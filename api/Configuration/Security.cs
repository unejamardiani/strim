using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace Api.Configuration;

/// <summary>Proxy headers, CORS, rate limits and CSRF protection.</summary>
public static class Security
{
  public const string CorsPolicy = "default";

  /// <summary>
  /// The app runs behind a reverse proxy, so X-Forwarded-* is trusted from any hop unless
  /// TRUSTED_PROXIES (comma-separated IPs/CIDRs) narrows it down.
  /// </summary>
  public static IServiceCollection AddStrimForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
  {
    var trustedProxies = configuration["TRUSTED_PROXIES"];
    return services.Configure<ForwardedHeadersOptions>(options =>
    {
      options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

      if (string.IsNullOrWhiteSpace(trustedProxies))
      {
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        options.ForwardLimit = null;
        return;
      }

      foreach (var proxy in trustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      {
        if (IPAddress.TryParse(proxy, out var ipAddress))
        {
          options.KnownProxies.Add(ipAddress);
        }
        else if (proxy.Contains('/') && Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(proxy, out var network))
        {
          options.KnownNetworks.Add(network);
        }
        else
        {
          Console.WriteLine($"WARNING: Invalid proxy IP or CIDR in TRUSTED_PROXIES: {proxy}");
        }
      }
      options.ForwardLimit = 1;
    });
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
        policy.AllowAnyOrigin();
      }
      else
      {
        policy.SetIsOriginAllowed(_ => false);
      }
    }));
  }

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

  private static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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
      options.Cookie.SameSite = SameSiteMode.None;
      options.Cookie.HttpOnly = true;
    });
}
