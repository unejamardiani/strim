using System.Security.Claims;
using Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Api.Configuration;

/// <summary>An external sign-in option that is configured with credentials.</summary>
public sealed record ExternalLoginProvider(string Name, string DisplayName, string Scheme);

/// <summary>The configured external providers, looked up by their lower-case name.</summary>
public sealed class ExternalLoginProviders(IReadOnlyList<ExternalLoginProvider> providers)
{
  public IReadOnlyList<ExternalLoginProvider> All { get; } = providers;

  public ExternalLoginProvider? Find(string name) =>
    All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>ASP.NET Core Identity with cookie sessions plus optional Google, Microsoft, GitHub and Cloudflare Access sign-in.</summary>
public static class Authentication
{
  public static IServiceCollection AddStrimAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
  {
    var development = environment.IsDevelopment();

    // The keys sign the session and antiforgery cookies. The image runs as a non-root user without
    // access to /root/.aspnet, so it names a directory that user owns and a volume can keep.
    var keysPath = configuration["DATA_PROTECTION_KEYS_PATH"];
    if (!string.IsNullOrWhiteSpace(keysPath))
    {
      services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    }

    services.AddIdentityCore<IdentityUser>(options =>
      {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = true;
        options.Password.RequiredLength = 10;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
        options.Lockout.MaxFailedAccessAttempts = 5;
      })
      .AddSignInManager()
      .AddEntityFrameworkStores<AppDbContext>()
      .AddDefaultTokenProviders();

    var authentication = services.AddAuthentication(options =>
    {
      options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
      options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
      options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });

    authentication.AddIdentityCookies(options =>
    {
      options.ApplicationCookie?.Configure(o =>
      {
        o.SlidingExpiration = true;
        o.Cookie.Name = development ? "strim.auth" : "__Host-strim.auth";
        o.Cookie.SameSite = Security.CookieSameSite(environment);
        o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.Cookie.HttpOnly = true;
        // An API: answer with status codes instead of redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx =>
        {
          ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
          return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
          ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
          return Task.CompletedTask;
        };
      });
      options.ExternalCookie?.Configure(o =>
      {
        o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        o.Cookie.Name = development ? "strim.external" : "__Host-strim.external";
        o.Cookie.SameSite = Security.CookieSameSite(environment);
        o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.Cookie.HttpOnly = true;
      });
    });

    // The security-stamp check rebuilds the session's principal every 30 minutes from the user alone;
    // keep the sign-in method that the profile reports (#579).
    services.Configure<SecurityStampValidatorOptions>(options => options.OnRefreshingPrincipal = context =>
    {
      var method = context.CurrentPrincipal?.FindFirst(ClaimTypes.AuthenticationMethod);
      if (method is not null) context.NewPrincipal?.Identities.First().AddClaim(new Claim(method.Type, method.Value));
      return Task.CompletedTask;
    });

    var providers = new List<ExternalLoginProvider>();

    var (googleId, googleSecret) = Credentials(configuration, "Google", "GOOGLE");
    if (googleId is not null && googleSecret is not null)
    {
      authentication.AddGoogle(options =>
      {
        options.ClientId = googleId;
        options.ClientSecret = googleSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
      });
      providers.Add(new("google", "Google", "Google"));
    }

    var (microsoftId, microsoftSecret) = Credentials(configuration, "Microsoft", "MICROSOFT");
    if (microsoftId is not null && microsoftSecret is not null)
    {
      var tenantId = configuration["Authentication:Microsoft:TenantId"] ?? configuration["MICROSOFT_TENANT_ID"] ?? "common";
      authentication.AddOpenIdConnect("microsoft", options =>
      {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ClientId = microsoftId;
        options.ClientSecret = microsoftSecret;
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.ResponseType = "code";
        options.SaveTokens = true;
        options.CallbackPath = "/signin-microsoft";
        options.Scope.Add("email");
        options.Scope.Add("profile");
      });
      providers.Add(new("microsoft", "Microsoft", "microsoft"));
    }

    var (githubId, githubSecret) = Credentials(configuration, "GitHub", "GITHUB");
    if (githubId is not null && githubSecret is not null)
    {
      authentication.AddGitHub(options =>
      {
        options.ClientId = githubId;
        options.ClientSecret = githubSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.Scope.Add("user:email");
        options.SaveTokens = true;
      });
      providers.Add(new("github", "GitHub", "GitHub"));
    }

    services.AddSingleton(new ExternalLoginProviders(providers));
    services.AddSingleton<CloudflareAccess>();
    services.AddHttpClient(CloudflareAccess.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
    services.AddAuthorization();
    return services;
  }

  private static (string? Id, string? Secret) Credentials(IConfiguration configuration, string section, string envPrefix)
  {
    var id = configuration[$"Authentication:{section}:ClientId"] ?? configuration[$"{envPrefix}_CLIENT_ID"];
    var secret = configuration[$"Authentication:{section}:ClientSecret"] ?? configuration[$"{envPrefix}_CLIENT_SECRET"];
    return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ? (null, null) : (id, secret);
  }
}
