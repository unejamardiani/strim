using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Api.Configuration;

/// <summary>Proxy headers, CORS, rate limits and CSRF protection.</summary>
public static class Security
{
  public const string CorsPolicy = "default";

  private const string PeerAddressKey = "Strim.PeerAddress";

  /// <summary>
  /// X-Forwarded-For and X-Forwarded-Proto count only from the proxies in TRUSTED_PROXIES
  /// (comma-separated IPs/CIDRs). Without it they are ignored, so a client cannot choose its own
  /// rate-limit partition.
  /// </summary>
  public static IServiceCollection AddStrimForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
  {
    var trustedProxies = configuration["TRUSTED_PROXIES"];
    return services.Configure<ForwardedHeadersOptions>(options =>
    {
      if (string.IsNullOrWhiteSpace(trustedProxies))
      {
        options.ForwardedHeaders = ForwardedHeaders.None;
        return;
      }

      options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

      var parsed = ParseTrustedProxies(trustedProxies);
      foreach (var proxy in parsed.Proxies)
      {
        options.KnownProxies.Add(proxy);
      }
      foreach (var network in parsed.Networks)
      {
        options.KnownIPNetworks.Add(network);
      }
      foreach (var rejected in parsed.Rejected)
      {
        Console.WriteLine($"WARNING: Invalid proxy IP or CIDR in TRUSTED_PROXIES: {rejected}");
      }
      options.ForwardLimit = 1;
    });
  }

  /// <summary>The addresses and CIDRs parsed from TRUSTED_PROXIES, plus the entries that were neither.</summary>
  public sealed record TrustedProxies(
    IReadOnlyList<IPAddress> Proxies,
    IReadOnlyList<System.Net.IPNetwork> Networks,
    IReadOnlyList<string> Rejected);

  /// <summary>
  /// Parses a comma-separated list of IPs and CIDRs. Entries are trimmed and empty ones ignored.
  /// A CIDR may have host bits set (10.1.2.3/8); they are masked off, as the HttpOverrides parser did.
  /// </summary>
  public static TrustedProxies ParseTrustedProxies(string? value)
  {
    var proxies = new List<IPAddress>();
    var networks = new List<System.Net.IPNetwork>();
    var rejected = new List<string>();
    if (string.IsNullOrWhiteSpace(value)) return new TrustedProxies(proxies, networks, rejected);

    foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      if (IPAddress.TryParse(entry, out var address))
      {
        proxies.Add(address);
      }
      else if (TryParseCidr(entry, out var network))
      {
        networks.Add(network);
      }
      else
      {
        rejected.Add(entry);
      }
    }
    return new TrustedProxies(proxies, networks, rejected);
  }

  private static bool TryParseCidr(string entry, out System.Net.IPNetwork network)
  {
    network = default;
    var slash = entry.IndexOf('/');
    if (slash < 0) return false;
    if (!IPAddress.TryParse(entry.AsSpan(0, slash), out var address)) return false;
    if (!int.TryParse(entry.AsSpan(slash + 1), out var prefixLength)) return false;

    var bytes = address.GetAddressBytes();
    if (prefixLength < 0 || prefixLength > bytes.Length * 8) return false;

    for (var i = 0; i < bytes.Length; i++)
    {
      var bitsInByte = Math.Clamp(prefixLength - i * 8, 0, 8);
      bytes[i] &= (byte)(0xFF << (8 - bitsInByte));
    }
    network = new System.Net.IPNetwork(new IPAddress(bytes), prefixLength);
    return true;
  }

  /// <summary>
  /// Explicit origins (ALLOWED_ORIGINS or Cors:AllowedOrigins) get credentialed CORS. Without them,
  /// development allows any origin and production allows none (the UI is served same-origin).
  /// </summary>
  public static IServiceCollection AddStrimCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
  {
    var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    var envOrigins = configuration["ALLOWED_ORIGINS"];
    if (!string.IsNullOrWhiteSpace(envOrigins))
    {
      var parsed = envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      allowedOrigins = allowedOrigins.Concat(parsed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    return services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    {
      policy.AllowAnyHeader().AllowAnyMethod();
      if (allowedOrigins.Length > 0)
      {
        policy.WithOrigins(allowedOrigins).AllowCredentials();
      }
      else if (environment.IsDevelopment())
      {
        // Local static servers run on other localhost ports and auth uses cookies.
        policy.SetIsOriginAllowed(IsLocalDevelopmentOrigin).AllowCredentials();
      }
      else
      {
        policy.SetIsOriginAllowed(_ => false);
      }
    }));
  }

  private static bool IsLocalDevelopmentOrigin(string origin)
  {
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
    if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
    return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
  }

  /// <summary>
  /// SameSite=None for cross-origin clients in production. Browsers reject SameSite=None without
  /// Secure, so local HTTP development uses Lax.
  /// </summary>
  public static SameSiteMode CookieSameSite(IHostEnvironment environment) =>
    environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None;

  public static class RateLimits
  {
    public const string Auth = "auth";
    public const string Fetch = "fetch";
    public const string Share = "share";
    public const string General = "general";
    public const string Sensitive = "sensitive";
  }

  public static IServiceCollection AddStrimRateLimits(this IServiceCollection services) =>
    services.AddRateLimiter(options =>
    {
      options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

      // Brute-force protection for sign-in and registration.
      options.AddPolicy(RateLimits.Auth, context => PerIp(context, permits: 20, queue: 2));
      // Analyze/generate fetch and process whole playlists.
      options.AddPolicy(RateLimits.Fetch, context => PerIp(context, permits: 30, queue: 2));
      // Public share links can trigger an upstream fetch and filter on every hit.
      options.AddPolicy(RateLimits.Share, context => PerIp(context, permits: 30, queue: 1));
      options.AddPolicy(RateLimits.General, context => PerIp(context, permits: 100, queue: 5));
      // Link toggles and regeneration, keyed by user so rotating names cannot bypass it.
      options.AddPolicy(RateLimits.Sensitive, context =>
        RateLimitPartition.GetFixedWindowLimiter(
          partitionKey: context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ClientIp(context),
          factory: _ => Window(permits: 10, queue: 0)));
    });

  private static RateLimitPartition<string> PerIp(HttpContext context, int permits, int queue) =>
    RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ => Window(permits, queue));

  /// <summary>
  /// Behind Cloudflare the trusted proxy's own peer is a Cloudflare edge, so CF-Connecting-IP names
  /// the client. It counts only when the immediate peer is a trusted proxy; anyone else can forge it.
  /// </summary>
  private static string ClientIp(HttpContext context)
  {
    if (IsFromTrustedProxy(context) &&
        IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString(), out var cloudflareClient))
    {
      return cloudflareClient.ToString();
    }
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
  }

  /// <summary>
  /// Remembers the immediate peer before UseForwardedHeaders replaces RemoteIpAddress with the
  /// forwarded client, so <see cref="IsFromTrustedProxy"/> still checks the hop that sent the request.
  /// </summary>
  public static IApplicationBuilder UseStrimPeerAddress(this IApplicationBuilder app) =>
    app.Use((context, next) =>
    {
      context.Items[PeerAddressKey] = context.Connection.RemoteIpAddress;
      return next();
    });

  /// <summary>
  /// True when the immediate peer is in TRUSTED_PROXIES or is loopback (the forwarded-headers
  /// options keep their loopback defaults next to the configured proxies).
  /// </summary>
  public static bool IsFromTrustedProxy(HttpContext context)
  {
    if (context.Items[PeerAddressKey] is not IPAddress peer) return false;
    if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
    var options = context.RequestServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    return options.KnownProxies.Contains(peer) || options.KnownIPNetworks.Any(network => network.Contains(peer));
  }

  private static FixedWindowRateLimiterOptions Window(int permits, int queue) => new()
  {
    PermitLimit = permits,
    Window = TimeSpan.FromMinutes(1),
    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    QueueLimit = queue,
  };

  /// <summary>
  /// CSRF tokens travel in X-CSRF-TOKEN. Cookies are SameSite=None (cross-origin clients), so the
  /// token is what protects state-changing requests; see <see cref="Endpoints.EndpointFilters"/>.
  /// </summary>
  public static IServiceCollection AddStrimAntiforgery(this IServiceCollection services, IHostEnvironment environment) =>
    services.AddAntiforgery(options =>
    {
      var development = environment.IsDevelopment();
      options.HeaderName = "X-CSRF-TOKEN";
      options.Cookie.Name = development ? "strim.csrf" : "__Host-strim.csrf";
      options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
      options.Cookie.SameSite = CookieSameSite(environment);
      options.Cookie.HttpOnly = true;
    });
}
