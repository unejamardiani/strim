using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Api.Configuration;

/// <summary>The person behind a verified Cloudflare Access user token.</summary>
public sealed record CloudflareAccessIdentity(string Subject, string Email);

/// <summary>
/// Verifies the signed Cloudflare Access token of a request. Active only when the team domain and the Access
/// application's AUD tag are both configured. Plain identity headers such as Cf-Access-Authenticated-User-Email
/// are never trusted: anyone who reaches the app without Access could set them.
/// </summary>
public sealed class CloudflareAccess(IConfiguration configuration, IHttpClientFactory clients, ILogger<CloudflareAccess> logger)
{
  public const string LoginProvider = "cloudflare-access";
  public const string HttpClientName = "cloudflare-access-certs";
  public const string TokenHeader = "Cf-Access-Jwt-Assertion";

  // Bounds how often a token with an unknown key id can make Strim fetch the certs again.
  private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);

  private readonly string? _teamDomain = Setting(configuration, "TeamDomain", "CF_ACCESS_TEAM_DOMAIN");
  private readonly string? _audience = Setting(configuration, "Audience", "CF_ACCESS_AUD");
  private readonly JsonWebTokenHandler _handler = new();
  private readonly SemaphoreSlim _refresh = new(1, 1);
  private IList<SecurityKey> _keys = [];
  private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

  public bool Active => _teamDomain is not null && _audience is not null;

  /// <summary>The identity of a valid user token in the request; null when there is none or it fails any check.</summary>
  public async Task<CloudflareAccessIdentity?> VerifyAsync(HttpRequest request)
  {
    var token = request.Headers[TokenHeader].ToString();
    if (!Active || string.IsNullOrEmpty(token)) return null;

    try
    {
      var keys = await KeysAsync(_handler.ReadJsonWebToken(token).Kid);
      var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
      {
        ValidIssuer = $"https://{_teamDomain}",
        ValidAudience = _audience,
        IssuerSigningKeys = keys,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
      });
      if (!result.IsValid) return null;

      // Service tokens carry no e-mail; they never stand for a person.
      var subject = result.Claims.TryGetValue("sub", out var sub) ? sub as string : null;
      var email = result.Claims.TryGetValue("email", out var mail) ? mail as string : null;
      return string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email) ? null : new(subject, email);
    }
    catch (Exception ex) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
    {
      // A malformed token, unreachable certs or a certs timeout only mean no automatic sign-in.
      logger.LogWarning(ex, "Cloudflare Access token could not be verified");
      return null;
    }
  }

  private async Task<IList<SecurityKey>> KeysAsync(string? keyId)
  {
    if (!NeedsRefresh(keyId)) return _keys;
    await _refresh.WaitAsync();
    try
    {
      if (NeedsRefresh(keyId))
      {
        _fetchedAt = DateTimeOffset.UtcNow;
        var json = await clients.CreateClient(HttpClientName).GetStringAsync($"https://{_teamDomain}/cdn-cgi/access/certs");
        _keys = new JsonWebKeySet(json).GetSigningKeys();
      }
    }
    finally
    {
      _refresh.Release();
    }
    return _keys;
  }

  private bool NeedsRefresh(string? keyId) =>
    !_keys.Any(k => k.KeyId == keyId) && DateTimeOffset.UtcNow - _fetchedAt >= MinRefreshInterval;

  private static string? Setting(IConfiguration configuration, string key, string variable)
  {
    var value = configuration[$"Authentication:CloudflareAccess:{key}"] ?? configuration[variable];
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
  }
}

public static class CloudflareAccessSignIn
{
  /// <summary>
  /// Signs in the account linked to the request's Access identity and ends a session that belongs to anyone else,
  /// so a shared browser never stays in another person's account. Runs after UseAuthentication.
  /// </summary>
  public static IApplicationBuilder UseCloudflareAccessSignIn(this IApplicationBuilder app) =>
    app.Use(async (context, next) =>
    {
      var identity = await context.RequestServices.GetRequiredService<CloudflareAccess>().VerifyAsync(context.Request);
      if (identity is not null)
      {
        context.Features.Set(identity);
        await SignInLinkedAccountAsync(context, identity);
      }
      await next(context);
    });

  /// <summary>Links the request's Access identity to the account that just signed in, unless it is linked already.</summary>
  public static async Task LinkAsync(HttpContext context, UserManager<IdentityUser> users, IdentityUser user)
  {
    var identity = context.Features.Get<CloudflareAccessIdentity>();
    if (identity is null || await users.FindByLoginAsync(CloudflareAccess.LoginProvider, identity.Subject) is not null) return;
    await users.AddLoginAsync(user, new UserLoginInfo(CloudflareAccess.LoginProvider, identity.Subject, "Cloudflare Access"));
  }

  private static async Task SignInLinkedAccountAsync(HttpContext context, CloudflareAccessIdentity identity)
  {
    var signInManager = context.RequestServices.GetRequiredService<SignInManager<IdentityUser>>();
    var users = signInManager.UserManager;
    var linked = await users.FindByLoginAsync(CloudflareAccess.LoginProvider, identity.Subject);
    var current = users.GetUserId(context.User);
    if (current is not null && current == linked?.Id) return;

    if (current is not null)
    {
      await signInManager.SignOutAsync();
      context.User = new();
    }
    if (linked is null) return;

    var result = await signInManager.ExternalLoginSignInAsync(CloudflareAccess.LoginProvider, identity.Subject, isPersistent: false);
    if (!result.Succeeded) return;

    // The cookie carries the sign-in method; this request's principal needs it as well.
    var principal = await signInManager.CreateUserPrincipalAsync(linked);
    principal.Identities.First().AddClaim(new Claim(ClaimTypes.AuthenticationMethod, CloudflareAccess.LoginProvider));
    context.User = principal;
  }
}
