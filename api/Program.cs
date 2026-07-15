using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Npgsql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Configure forwarded headers for proxy/load balancer scenarios
// This allows the app to correctly identify HTTPS requests when behind a reverse proxy
// NOTE: We trust all proxies by default since this app is designed to run behind a reverse proxy.
// Network-level security (firewall/VPC rules) should restrict direct access to the application.
// Set TRUSTED_PROXIES env var to restrict to specific IPs/CIDRs if needed.
var trustedProxiesConfig = builder.Configuration["TRUSTED_PROXIES"];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

  if (!string.IsNullOrWhiteSpace(trustedProxiesConfig))
  {
    // Explicit trusted proxies configured - use restricted mode
    var proxies = trustedProxiesConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var proxy in proxies)
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
    // Limit to 1 proxy hop when explicit proxies are configured
    options.ForwardLimit = 1;
  }
  else
  {
    // Default: Trust all proxies - app is designed to run behind a reverse proxy
    // Clear the default restrictions to accept X-Forwarded-* from any source
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();
    // Allow unlimited proxy hops (null = no limit)
    options.ForwardLimit = null;
  }
});

// P1 fix: Input validation constants
const int MaxUrlLength = 2048;
const int MaxPlaylistNameLength = 200;
const int MaxPlaylistTextSize = 50 * 1024 * 1024; // 50 MB max for playlist text
const long MaxAnalyzeRequestBodySize = MaxPlaylistTextSize + (1L * 1024 * 1024); // JSON envelope/headroom
const int MaxDisabledGroupsCount = 1000;

var configuredProvider = (builder.Configuration["DB_PROVIDER"] ?? builder.Configuration["DATABASE_PROVIDER"])?.ToLowerInvariant();
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres") ??
  builder.Configuration["POSTGRES_CONNECTION"];
var sqliteConnectionString = builder.Configuration.GetConnectionString("Sqlite") ??
  builder.Configuration["SQLITE_CONNECTION"];
var sqlitePathOverride = builder.Configuration["SQLITE_PATH"];
var publicShareBaseUrl = builder.Configuration["PUBLIC_SHARE_BASE_URL"]?.Trim().TrimEnd('/');

if (!string.IsNullOrWhiteSpace(publicShareBaseUrl) &&
    (!Uri.TryCreate(publicShareBaseUrl, UriKind.Absolute, out var shareBaseUri) ||
     (shareBaseUri.Scheme != Uri.UriSchemeHttps && shareBaseUri.Scheme != Uri.UriSchemeHttp)))
{
  throw new InvalidOperationException("PUBLIC_SHARE_BASE_URL must be an absolute HTTP or HTTPS URL.");
}

// Default to SQLite when no Postgres connection string is provided unless explicitly overridden.
var useSqlite = string.Equals(configuredProvider, "sqlite", StringComparison.OrdinalIgnoreCase) ||
  (string.IsNullOrWhiteSpace(configuredProvider) && string.IsNullOrWhiteSpace(postgresConnectionString));

if (useSqlite)
{
  var sqliteBuilder = new SqliteConnectionStringBuilder(
    string.IsNullOrWhiteSpace(sqliteConnectionString)
      ? $"Data Source={Path.Combine(AppContext.BaseDirectory, "data", "strim.db")}"
      : sqliteConnectionString);

  if (!string.IsNullOrWhiteSpace(sqlitePathOverride))
  {
    sqliteBuilder.DataSource = sqlitePathOverride;
  }

  if (!Path.IsPathRooted(sqliteBuilder.DataSource))
  {
    sqliteBuilder.DataSource = Path.GetFullPath(sqliteBuilder.DataSource, AppContext.BaseDirectory);
  }

  var sqliteDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
  if (!string.IsNullOrWhiteSpace(sqliteDirectory))
  {
    Directory.CreateDirectory(sqliteDirectory);
  }

  builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(sqliteBuilder.ConnectionString));
}
else
{
  if (string.IsNullOrWhiteSpace(postgresConnectionString))
  {
    throw new InvalidOperationException(
      "PostgreSQL connection string is required when DB_PROVIDER is set to 'postgres'. " +
      "Set POSTGRES_CONNECTION environment variable or ConnectionStrings:Postgres in configuration.");
  }

  // Configure Npgsql data source with dynamic JSON enabled so we can store List<string> as jsonb.
  var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
  dataSourceBuilder.EnableDynamicJson();
  var dataSource = dataSourceBuilder.Build();

  builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
}

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ??
  Array.Empty<string>();
var envOrigins = builder.Configuration["ALLOWED_ORIGINS"];
if (!string.IsNullOrWhiteSpace(envOrigins))
{
  var parsed = envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
  allowedOrigins = allowedOrigins.Concat(parsed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

// P1 fix: CORS configuration - require explicit origins in production
var isDevelopment = builder.Environment.IsDevelopment();
builder.Services.AddCors(options =>
{
  options.AddPolicy("default", policy =>
  {
    policy.AllowAnyHeader().AllowAnyMethod();
    if (allowedOrigins.Any())
    {
      policy.WithOrigins(allowedOrigins).AllowCredentials();
    }
    else if (isDevelopment)
    {
      // Only allow permissive CORS in development mode
      policy.AllowAnyOrigin();
      Console.WriteLine("WARNING: CORS is configured to allow any origin. This is only acceptable in development.");
    }
    else
    {
      // Production: require explicit CORS origins configuration
      // Default to same-origin only (no cross-origin requests allowed)
      policy.SetIsOriginAllowed(_ => false);
      Console.WriteLine("WARNING: No CORS origins configured. Cross-origin requests will be blocked. " +
        "Set ALLOWED_ORIGINS environment variable or Cors:AllowedOrigins in configuration.");
    }
  });
});
// Large playlists are deliberately spooled to private disk files. Metadata stays small in memory.
builder.Services.Configure<PlaylistCacheOptions>(builder.Configuration.GetSection(PlaylistCacheOptions.SectionName));
builder.Services.AddSingleton<PlaylistFileCache>();
builder.Services.AddSingleton<PlaylistSourceFetcher>();
builder.Services.AddSingleton<PlaylistJobGate>();
builder.Services.AddHostedService<PlaylistCacheCleanupService>();

// Rate limiting configuration to prevent abuse
builder.Services.AddRateLimiter(options =>
{
  options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

  // Strict rate limit for authentication endpoints (prevents brute force)
  // Allow 20 requests per minute with a small queue to handle legitimate retries
  options.AddPolicy("auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      factory: _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 20,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 2
      }));

  // Moderate rate limit for fetch/analyze endpoints (prevents abuse)
  options.AddPolicy("fetch", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      factory: _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 2
      }));

  // Public share URLs can otherwise trigger an expensive upstream fetch and filter on every hit.
  options.AddPolicy("share", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      factory: _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 1
      }));

  // General rate limit for other API endpoints
  options.AddPolicy("general", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      factory: _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 100,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 5
      }));

  // Strict rate limit for sensitive operations (regenerate codes, toggle status)
  // Prevents abuse from compromised accounts disrupting legitimate access
  // Use stable internal user ID (NameIdentifier claim) to prevent bypass via username rotation
  options.AddPolicy("sensitive", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      partitionKey: context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      factory: _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0 // No queue - reject immediately if over limit
      }));
});

// Antiforgery for CSRF protection
// Note: SameSite.None is required for cross-origin clients (CORS with credentials).
// Strict/Lax cookies are not sent with cross-site requests, breaking CSRF validation.
// Security is maintained via: Secure cookie, HttpOnly, __Host- prefix, and origin validation.
builder.Services.AddAntiforgery(options =>
{
  options.HeaderName = "X-CSRF-TOKEN";
  options.Cookie.Name = isDevelopment ? "strim.csrf" : "__Host-strim.csrf";
  // Always require HTTPS in production; allow HTTP in development for local testing
  options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
  options.Cookie.SameSite = SameSiteMode.None;
  options.Cookie.HttpOnly = true;
});

var playlistHeaderTimeout = TimeSpan.FromSeconds(Math.Max(
  1,
  builder.Configuration.GetValue<int?>($"{PlaylistCacheOptions.SectionName}:HeaderTimeoutSeconds") ?? 15));
builder.Services.AddHttpClient("fetcher", client =>
{
  // With ResponseHeadersRead, this bounds DNS/connect/header stalls; PlaylistSourceFetcher owns
  // the longer, cancellable body-read timeout for legitimately large downloads.
  client.Timeout = playlistHeaderTimeout;

  // Use a realistic browser user agent to avoid upstream blocks that reject
  // generic/unknown clients. Some IPTV providers respond with 403 to the
  // default .NET agent string.
  client.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-mpegurl"));
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
  // Disable auto-redirects to validate each redirect target for SSRF protection
  AllowAutoRedirect = false
});
builder.Services.AddIdentityCore<IdentityUser>(options =>
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

builder.Services.AddAuthentication(options =>
{
  options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
  options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
  options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
})
  .AddIdentityCookies(options =>
  {
    options.ApplicationCookie?.Configure(o =>
    {
      o.SlidingExpiration = true;
      o.Cookie.Name = isDevelopment ? "strim.auth" : "__Host-strim.auth";
      o.Cookie.SameSite = SameSiteMode.None;
      o.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
      o.Cookie.HttpOnly = true;
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
      o.Cookie.Name = isDevelopment ? "strim.external" : "__Host-strim.external";
      o.Cookie.SameSite = SameSiteMode.None;
      o.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
      o.Cookie.HttpOnly = true;
    });
  });

var googleClientId = builder.Configuration["Authentication:Google:ClientId"] ?? builder.Configuration["GOOGLE_CLIENT_ID"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? builder.Configuration["GOOGLE_CLIENT_SECRET"];
var googleEnabled = !string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret);
if (googleEnabled)
{
  builder.Services.AddAuthentication().AddGoogle(options =>
  {
    options.ClientId = googleClientId!;
    options.ClientSecret = googleClientSecret!;
    options.SignInScheme = IdentityConstants.ExternalScheme;
  });
}

var msClientId = builder.Configuration["Authentication:Microsoft:ClientId"] ?? builder.Configuration["MICROSOFT_CLIENT_ID"];
var msClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"] ?? builder.Configuration["MICROSOFT_CLIENT_SECRET"];
var tenantId = builder.Configuration["Authentication:Microsoft:TenantId"] ?? builder.Configuration["MICROSOFT_TENANT_ID"] ?? "common";
var microsoftEnabled = !string.IsNullOrWhiteSpace(msClientId) && !string.IsNullOrWhiteSpace(msClientSecret);
if (microsoftEnabled)
{
  builder.Services.AddAuthentication().AddOpenIdConnect("microsoft", options =>
  {
    options.SignInScheme = IdentityConstants.ExternalScheme;
    options.ClientId = msClientId;
    options.ClientSecret = msClientSecret;
    options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    options.ResponseType = "code";
    options.SaveTokens = true;
    options.CallbackPath = "/signin-microsoft";
    options.Scope.Add("email");
    options.Scope.Add("profile");
  });
}

var githubClientId = builder.Configuration["Authentication:GitHub:ClientId"] ?? builder.Configuration["GITHUB_CLIENT_ID"];
var githubClientSecret = builder.Configuration["Authentication:GitHub:ClientSecret"] ?? builder.Configuration["GITHUB_CLIENT_SECRET"];
var githubEnabled = !string.IsNullOrWhiteSpace(githubClientId) && !string.IsNullOrWhiteSpace(githubClientSecret);
if (githubEnabled)
{
  builder.Services.AddAuthentication().AddGitHub(options =>
  {
    options.ClientId = githubClientId!;
    options.ClientSecret = githubClientSecret!;
    options.SignInScheme = IdentityConstants.ExternalScheme;
    options.Scope.Add("user:email");
    options.SaveTokens = true;
  });
}

builder.Services.AddAuthorization();

// P4: Health checks for monitoring and container orchestration
builder.Services.AddHealthChecks()
  .AddDbContextCheck<AppDbContext>("database");

builder.Services.AddHostedService<PlaylistRefreshService>();

// P4: OpenAPI documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
  options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
  {
    Title = "Strim API",
    Version = "v1",
    Description = "IPTV playlist management and filtering API"
  });
});

// Keep JSON/text responses compressed. Large M3U output is streamed as-is: compression is CPU
// intensive and is better handled by an edge proxy when a deployment explicitly needs it.
builder.Services.AddResponseCompression(options =>
{
  options.EnableForHttps = true;
  options.MimeTypes = new[] {
    "application/json",
    "text/plain"
  };
});

var app = builder.Build();
var analyzeRequestBodyGate = new SemaphoreSlim(1, 1);

// Use forwarded headers middleware (must be before other middleware)
// This reads X-Forwarded-Proto and X-Forwarded-For headers from the proxy
app.UseForwardedHeaders();

// Model binding turns RawText JSON into a managed string before the endpoint handler and its
// job gate run. Bound the body before binding and admit only one analyze request at a time, so a
// burst of raw uploads cannot allocate several 50 MiB UTF-16 strings concurrently. Remote URL
// sources remain streamed to disk in the handler.
app.Use(async (context, next) =>
{
  var isPlaylistAnalyze = HttpMethods.IsPost(context.Request.Method) &&
    string.Equals(context.Request.Path.Value, "/api/playlist/analyze", StringComparison.OrdinalIgnoreCase);
  if (!isPlaylistAnalyze)
  {
    await next();
    return;
  }

  if (context.Request.ContentLength is > MaxAnalyzeRequestBodySize)
  {
    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
    await context.Response.WriteAsJsonAsync(new { error = $"Analyze request exceeds the {MaxAnalyzeRequestBodySize / (1024 * 1024)} MiB body limit." });
    return;
  }

  var maxBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
  if (maxBodySize is { IsReadOnly: false })
  {
    maxBodySize.MaxRequestBodySize = MaxAnalyzeRequestBodySize;
  }

  if (!analyzeRequestBodyGate.Wait(0))
  {
    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    context.Response.Headers["Retry-After"] = "30";
    await context.Response.WriteAsJsonAsync(new { error = "Playlist analysis is already in progress. Please retry shortly." });
    return;
  }

  try
  {
    await next();
  }
  finally
  {
    analyzeRequestBodyGate.Release();
  }
});

// Azure App Service specific: Handle X-ARR-SSL header for HTTPS detection
// Azure may not always set X-Forwarded-Proto, but always sets X-ARR-SSL for HTTPS
app.Use(async (context, next) =>
{
  var arrSsl = context.Request.Headers["X-ARR-SSL"].FirstOrDefault();
  if (!string.IsNullOrEmpty(arrSsl))
  {
    // X-ARR-SSL is set by Azure when request comes through HTTPS
    context.Request.Scheme = "https";
  }
  await next();
});

// Diagnostic logging: Log proxy IP on startup (remove after confirming it works)
app.Use(async (context, next) =>
{
  if (context.Request.Path.StartsWithSegments("/health/live"))
  {
    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
    var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
    var arrSsl = context.Request.Headers["X-ARR-SSL"].ToString();
    app.Logger.LogInformation(
      "Connection from {RemoteIP} | X-Forwarded-For: {ForwardedFor} | X-Forwarded-Proto: {ForwardedProto} | X-ARR-SSL: {ArrSsl}",
      remoteIp, forwardedFor, forwardedProto, arrSsl);
  }
  await next();
});

// Response compression should be early in the pipeline
app.UseResponseCompression();

// P4: OpenAPI documentation (available in all environments at /swagger)
app.UseSwagger();
if (app.Environment.IsDevelopment())
{
  app.UseSwaggerUI(options =>
  {
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Strim API v1");
    options.RoutePrefix = "swagger";
  });
}

// Correlation ID middleware for request tracing
app.Use(async (context, next) =>
{
  const string correlationIdHeader = "X-Correlation-ID";
  var correlationId = context.Request.Headers[correlationIdHeader].FirstOrDefault()
    ?? Guid.NewGuid().ToString("N");

  context.Response.Headers[correlationIdHeader] = correlationId;

  using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
  {
    await next();
  }
});

app.UseCors("default");

// Rewrite root path to index.html. UseDefaultFiles() should handle this,
// but a known issue with implicit routing in .NET 8 Minimal API
// can prevent the default-document lookup from matching "/".
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/")
    {
        context.Request.Path = "/index.html";
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Status code pages for error handling
app.UseWhen(
  context => !context.Request.Path.StartsWithSegments("/api"),
  nonApi => nonApi.UseStatusCodePagesWithReExecute("/error/{0}"));

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

// Ensure database exists and matches the model. For now we use EnsureCreated for simplicity.
using (var scope = app.Services.CreateScope())
{
  var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
  var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSchema");
  EnsureSchema(db, logger);
}

void EnsureSchema(AppDbContext db, ILogger logger)
{
  db.Database.EnsureCreated();
  EnsureIdentitySchema(db, logger);

  if (db.Database.IsNpgsql())
  {
    TryPatchPostgresSchema(db, logger);
    TryBackfillPostgresShareCodes(db, logger);
  }
  else if (db.Database.IsSqlite())
  {
    TryAddSqliteColumn(db, "totalchannels", "INTEGER NOT NULL DEFAULT 0", logger);
    TryAddSqliteColumn(db, "groupcount", "INTEGER NOT NULL DEFAULT 0", logger);
    TryAddSqliteColumn(db, "expirationutc", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "sharecode", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "ownerid", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "isactive", "INTEGER NOT NULL DEFAULT 1", logger);
    TryAddSqliteColumn(db, "viewcount", "INTEGER NOT NULL DEFAULT 0", logger);
    TryAddSqliteColumn(db, "lastviewedutc", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "autorefreshenabled", "INTEGER NOT NULL DEFAULT 0", logger);
    TryAddSqliteColumn(db, "lastrefreshedutc", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "sourcehash", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "sourceetag", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "sourcelastmodifiedutc", "TEXT NULL", logger);
    TryAddSqliteColumn(db, "sourcelengthbytes", "INTEGER NULL", logger);
    TryAddSqliteColumn(db, "sourcecheckedutc", "TEXT NULL", logger);
    TryBackfillSqliteShareCodes(db, logger);
  }
}

void EnsureIdentitySchema(AppDbContext db, ILogger logger)
{
  if (db.Database.IsNpgsql())
  {
    TryEnsurePostgresIdentity(db, logger);
  }
  else if (db.Database.IsSqlite())
  {
    TryEnsureSqliteIdentity(db, logger);
  }
}

void TryEnsureSqliteIdentity(AppDbContext db, ILogger logger)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      CREATE TABLE IF NOT EXISTS AspNetRoles (
        Id TEXT NOT NULL PRIMARY KEY,
        Name TEXT NULL,
        NormalizedName TEXT NULL,
        ConcurrencyStamp TEXT NULL
      );
      CREATE TABLE IF NOT EXISTS AspNetUsers (
        Id TEXT NOT NULL PRIMARY KEY,
        UserName TEXT NULL,
        NormalizedUserName TEXT NULL,
        Email TEXT NULL,
        NormalizedEmail TEXT NULL,
        EmailConfirmed INTEGER NOT NULL DEFAULT 0,
        PasswordHash TEXT NULL,
        SecurityStamp TEXT NULL,
        ConcurrencyStamp TEXT NULL,
        PhoneNumber TEXT NULL,
        PhoneNumberConfirmed INTEGER NOT NULL DEFAULT 0,
        TwoFactorEnabled INTEGER NOT NULL DEFAULT 0,
        LockoutEnd TEXT NULL,
        LockoutEnabled INTEGER NOT NULL DEFAULT 0,
        AccessFailedCount INTEGER NOT NULL DEFAULT 0
      );
      CREATE TABLE IF NOT EXISTS AspNetRoleClaims (
        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
        RoleId TEXT NOT NULL,
        ClaimType TEXT NULL,
        ClaimValue TEXT NULL,
        CONSTRAINT FK_AspNetRoleClaims_Roles_RoleId FOREIGN KEY (RoleId) REFERENCES AspNetRoles (Id) ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS AspNetUserClaims (
        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
        UserId TEXT NOT NULL,
        ClaimType TEXT NULL,
        ClaimValue TEXT NULL,
        CONSTRAINT FK_AspNetUserClaims_Users_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS AspNetUserLogins (
        LoginProvider TEXT NOT NULL,
        ProviderKey TEXT NOT NULL,
        ProviderDisplayName TEXT NULL,
        UserId TEXT NOT NULL,
        PRIMARY KEY (LoginProvider, ProviderKey),
        CONSTRAINT FK_AspNetUserLogins_Users_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS AspNetUserRoles (
        UserId TEXT NOT NULL,
        RoleId TEXT NOT NULL,
        PRIMARY KEY (UserId, RoleId),
        CONSTRAINT FK_AspNetUserRoles_Roles_RoleId FOREIGN KEY (RoleId) REFERENCES AspNetRoles (Id) ON DELETE CASCADE,
        CONSTRAINT FK_AspNetUserRoles_Users_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS AspNetUserTokens (
        UserId TEXT NOT NULL,
        LoginProvider TEXT NOT NULL,
        Name TEXT NOT NULL,
        Value TEXT NULL,
        PRIMARY KEY (UserId, LoginProvider, Name),
        CONSTRAINT FK_AspNetUserTokens_Users_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
      );
      CREATE INDEX IF NOT EXISTS IX_AspNetRoleClaims_RoleId ON AspNetRoleClaims (RoleId);
      CREATE INDEX IF NOT EXISTS IX_AspNetUserClaims_UserId ON AspNetUserClaims (UserId);
      CREATE INDEX IF NOT EXISTS IX_AspNetUserLogins_UserId ON AspNetUserLogins (UserId);
      CREATE INDEX IF NOT EXISTS IX_AspNetUserRoles_RoleId ON AspNetUserRoles (RoleId);
      CREATE INDEX IF NOT EXISTS IX_AspNetUsers_NormalizedEmail ON AspNetUsers (NormalizedEmail);
      CREATE UNIQUE INDEX IF NOT EXISTS IX_AspNetUsers_NormalizedUserName ON AspNetUsers (NormalizedUserName);
    ");
    logger.LogDebug("SQLite Identity schema ensured");
  }
  catch (Exception ex)
  {
    // P1 fix: Log the error instead of silently swallowing
    logger.LogWarning(ex, "Failed to ensure SQLite Identity schema: {Message}", ex.Message);
  }
}

void TryEnsurePostgresIdentity(AppDbContext db, ILogger logger)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      CREATE TABLE IF NOT EXISTS ""AspNetRoles"" (
        ""Id"" varchar(450) NOT NULL PRIMARY KEY,
        ""Name"" varchar(256) NULL,
        ""NormalizedName"" varchar(256) NULL,
        ""ConcurrencyStamp"" text NULL
      );
      CREATE TABLE IF NOT EXISTS ""AspNetUsers"" (
        ""Id"" varchar(450) NOT NULL PRIMARY KEY,
        ""UserName"" varchar(256) NULL,
        ""NormalizedUserName"" varchar(256) NULL,
        ""Email"" varchar(256) NULL,
        ""NormalizedEmail"" varchar(256) NULL,
        ""EmailConfirmed"" boolean NOT NULL DEFAULT false,
        ""PasswordHash"" text NULL,
        ""SecurityStamp"" text NULL,
        ""ConcurrencyStamp"" text NULL,
        ""PhoneNumber"" text NULL,
        ""PhoneNumberConfirmed"" boolean NOT NULL DEFAULT false,
        ""TwoFactorEnabled"" boolean NOT NULL DEFAULT false,
        ""LockoutEnd"" timestamp with time zone NULL,
        ""LockoutEnabled"" boolean NOT NULL DEFAULT false,
        ""AccessFailedCount"" integer NOT NULL DEFAULT 0
      );
      CREATE TABLE IF NOT EXISTS ""AspNetRoleClaims"" (
        ""Id"" serial NOT NULL PRIMARY KEY,
        ""RoleId"" varchar(450) NOT NULL,
        ""ClaimType"" text NULL,
        ""ClaimValue"" text NULL,
        CONSTRAINT ""FK_AspNetRoleClaims_Roles_RoleId"" FOREIGN KEY (""RoleId"") REFERENCES ""AspNetRoles""(""Id"") ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS ""AspNetUserClaims"" (
        ""Id"" serial NOT NULL PRIMARY KEY,
        ""UserId"" varchar(450) NOT NULL,
        ""ClaimType"" text NULL,
        ""ClaimValue"" text NULL,
        CONSTRAINT ""FK_AspNetUserClaims_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS ""AspNetUserLogins"" (
        ""LoginProvider"" varchar(128) NOT NULL,
        ""ProviderKey"" varchar(128) NOT NULL,
        ""ProviderDisplayName"" text NULL,
        ""UserId"" varchar(450) NOT NULL,
        PRIMARY KEY (""LoginProvider"", ""ProviderKey""),
        CONSTRAINT ""FK_AspNetUserLogins_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS ""AspNetUserRoles"" (
        ""UserId"" varchar(450) NOT NULL,
        ""RoleId"" varchar(450) NOT NULL,
        PRIMARY KEY (""UserId"", ""RoleId""),
        CONSTRAINT ""FK_AspNetUserRoles_Roles_RoleId"" FOREIGN KEY (""RoleId"") REFERENCES ""AspNetRoles""(""Id"") ON DELETE CASCADE,
        CONSTRAINT ""FK_AspNetUserRoles_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS ""AspNetUserTokens"" (
        ""UserId"" varchar(450) NOT NULL,
        ""LoginProvider"" varchar(128) NOT NULL,
        ""Name"" varchar(128) NOT NULL,
        ""Value"" text NULL,
        PRIMARY KEY (""UserId"", ""LoginProvider"", ""Name""),
        CONSTRAINT ""FK_AspNetUserTokens_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE CASCADE
      );
      CREATE INDEX IF NOT EXISTS ""IX_AspNetRoleClaims_RoleId"" ON ""AspNetRoleClaims"" (""RoleId"");
      CREATE INDEX IF NOT EXISTS ""IX_AspNetUserClaims_UserId"" ON ""AspNetUserClaims"" (""UserId"");
      CREATE INDEX IF NOT EXISTS ""IX_AspNetUserLogins_UserId"" ON ""AspNetUserLogins"" (""UserId"");
      CREATE INDEX IF NOT EXISTS ""IX_AspNetUserRoles_RoleId"" ON ""AspNetUserRoles"" (""RoleId"");
      CREATE INDEX IF NOT EXISTS ""IX_AspNetUsers_NormalizedEmail"" ON ""AspNetUsers"" (""NormalizedEmail"");
      CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AspNetUsers_NormalizedUserName"" ON ""AspNetUsers"" (""NormalizedUserName"");
    ");
    logger.LogDebug("PostgreSQL Identity schema ensured");
  }
  catch (Exception ex)
  {
    // P1 fix: Log the error instead of silently swallowing
    logger.LogWarning(ex, "Failed to ensure PostgreSQL Identity schema: {Message}", ex.Message);
  }
}

void TryPatchPostgresSchema(AppDbContext db, ILogger logger)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      DO $$
      BEGIN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'totalchannels') THEN
          ALTER TABLE playlists ADD COLUMN totalchannels integer NOT NULL DEFAULT 0;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'groupcount') THEN
          ALTER TABLE playlists ADD COLUMN groupcount integer NOT NULL DEFAULT 0;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'expirationutc') THEN
          ALTER TABLE playlists ADD COLUMN expirationutc timestamptz NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'sharecode') THEN
          ALTER TABLE playlists ADD COLUMN sharecode varchar(64) NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'ownerid') THEN
          ALTER TABLE playlists ADD COLUMN ownerid varchar(450) NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'isactive') THEN
          ALTER TABLE playlists ADD COLUMN isactive boolean NOT NULL DEFAULT true;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'viewcount') THEN
          ALTER TABLE playlists ADD COLUMN viewcount bigint NOT NULL DEFAULT 0;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'lastviewedutc') THEN
          ALTER TABLE playlists ADD COLUMN lastviewedutc timestamptz NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'autorefreshenabled') THEN
          ALTER TABLE playlists ADD COLUMN autorefreshenabled boolean NOT NULL DEFAULT false;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'lastrefreshedutc') THEN
          ALTER TABLE playlists ADD COLUMN lastrefreshedutc timestamptz NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'sourcehash') THEN
          ALTER TABLE playlists ADD COLUMN sourcehash varchar(64) NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'sourceetag') THEN
          ALTER TABLE playlists ADD COLUMN sourceetag varchar(512) NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'sourcelastmodifiedutc') THEN
          ALTER TABLE playlists ADD COLUMN sourcelastmodifiedutc timestamptz NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'sourcelengthbytes') THEN
          ALTER TABLE playlists ADD COLUMN sourcelengthbytes bigint NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'sourcecheckedutc') THEN
          ALTER TABLE playlists ADD COLUMN sourcecheckedutc timestamptz NULL;
        END IF;
      END $$;
    ");
    logger.LogDebug("PostgreSQL schema patched successfully");
  }
  catch (Exception ex)
  {
    // P1 fix: Log the error instead of silently swallowing
    logger.LogWarning(ex, "Failed to patch PostgreSQL schema: {Message}", ex.Message);
  }
}

void TryBackfillPostgresShareCodes(AppDbContext db, ILogger logger)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      UPDATE playlists
      SET sharecode = md5(random()::text || clock_timestamp()::text)
      WHERE sharecode IS NULL OR sharecode = '';
    ");
    logger.LogDebug("PostgreSQL share codes backfilled");
  }
  catch (Exception ex)
  {
    // P1 fix: Log the error instead of silently swallowing
    logger.LogWarning(ex, "Failed to backfill PostgreSQL share codes: {Message}", ex.Message);
  }
}

void TryAddSqliteColumn(AppDbContext db, string columnName, string definition, ILogger logger)
{
  // P1 fix: Validate column name against allowed list to prevent SQL injection
  var allowedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
  {
    ["totalchannels"] = "INTEGER NOT NULL DEFAULT 0",
    ["groupcount"] = "INTEGER NOT NULL DEFAULT 0",
    ["expirationutc"] = "TEXT NULL",
    ["sharecode"] = "TEXT NULL",
    ["ownerid"] = "TEXT NULL",
    ["isactive"] = "INTEGER NOT NULL DEFAULT 1",
    ["viewcount"] = "INTEGER NOT NULL DEFAULT 0",
    ["lastviewedutc"] = "TEXT NULL",
    ["autorefreshenabled"] = "INTEGER NOT NULL DEFAULT 0",
    ["lastrefreshedutc"] = "TEXT NULL",
    ["sourcehash"] = "TEXT NULL",
    ["sourceetag"] = "TEXT NULL",
    ["sourcelastmodifiedutc"] = "TEXT NULL",
    ["sourcelengthbytes"] = "INTEGER NULL",
    ["sourcecheckedutc"] = "TEXT NULL"
  };

  if (!allowedColumns.TryGetValue(columnName, out var expectedDefinition))
  {
    logger.LogWarning("Attempted to add unauthorized column: {ColumnName}", columnName);
    return;
  }

  try
  {
    var connection = db.Database.GetDbConnection();
    db.Database.OpenConnection();

    var columnExists = false;
    using (var cmd = connection.CreateCommand())
    {
      cmd.CommandText = "PRAGMA table_info('playlists');";
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
      {
        if (reader.FieldCount > 1 && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
        {
          columnExists = true;
          break;
        }
      }
    }

    if (!columnExists)
    {
      // Use the validated column name and expected definition from the whitelist
      using var alter = connection.CreateCommand();
      alter.CommandText = $"ALTER TABLE playlists ADD COLUMN {columnName} {expectedDefinition};";
      alter.ExecuteNonQuery();
      logger.LogInformation("Added SQLite column: {ColumnName}", columnName);
    }
  }
  catch (Exception ex)
  {
    // P1 fix: Log the error instead of silently swallowing
    logger.LogWarning(ex, "Failed to add SQLite column {ColumnName}: {Message}", columnName, ex.Message);
  }
  finally
  {
    db.Database.CloseConnection();
  }
}

void TryBackfillSqliteShareCodes(AppDbContext db, ILogger logger)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      UPDATE playlists
      SET sharecode = lower(hex(randomblob(16)))
      WHERE sharecode IS NULL OR sharecode = '';
    ");
    logger.LogDebug("SQLite share codes backfilled");
  }
  catch (Exception ex)
  {
    // P1 fix: Log the error instead of silently swallowing
    logger.LogWarning(ex, "Failed to backfill SQLite share codes: {Message}", ex.Message);
  }
}

string BuildReturnUrl(string? returnUrl)
{
  if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
  if (Uri.TryCreate(returnUrl, UriKind.Relative, out _) && returnUrl.StartsWith('/'))
  {
    return returnUrl;
  }
  return "/";
}

bool ProviderEnabled(string provider) =>
  provider.ToLowerInvariant() switch
  {
    "google" => googleEnabled,
    "microsoft" => microsoftEnabled,
    _ => false
  };

string? GetUserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

// Security helpers extracted to Api.Services.SecurityHelpers

// P4: Health check endpoints for monitoring and k8s probes
app.MapHealthChecks("/health", new HealthCheckOptions
{
  ResponseWriter = async (context, report) =>
  {
    context.Response.ContentType = "application/json";
    var result = new
    {
      status = report.Status.ToString(),
      checks = report.Entries.Select(e => new
      {
        name = e.Key,
        status = e.Value.Status.ToString(),
        description = e.Value.Description
      }),
      duration = report.TotalDuration.TotalMilliseconds
    };
    await context.Response.WriteAsJsonAsync(result);
  }
});

// Simple liveness probe (always returns healthy if app is running)
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));

// Readiness probe (checks database connectivity)
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
  Predicate = check => check.Tags.Contains("ready") || check.Name == "database"
});

var authGroup = app.MapGroup("/api/auth").RequireRateLimiting("auth");
authGroup.MapGet("/me", async (UserManager<IdentityUser> userManager, ClaimsPrincipal principal) =>
{
  var user = await userManager.GetUserAsync(principal);
  if (user is null) return Results.Unauthorized();
  return Results.Ok(new { userName = user.UserName, email = user.Email });
});

authGroup.MapPost("/register", async (RegisterRequest request, UserManager<IdentityUser> userManager, SignInManager<IdentityUser> signInManager, IAntiforgery antiforgery, HttpContext context) =>
{
  // CSRF protection for state-changing operation (P0 security fix)
  try
  {
    await antiforgery.ValidateRequestAsync(context);
  }
  catch (AntiforgeryValidationException)
  {
    return Results.BadRequest(new { error = "Invalid or missing CSRF token" });
  }

  if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
  {
    return Results.BadRequest(new { error = "Username and password are required." });
  }

  try
  {
    var userName = request.UserName.Trim();
    var existing = await userManager.FindByNameAsync(userName);
    if (existing is not null)
    {
      return Results.BadRequest(new { error = "Username is already taken." });
    }

    var newUser = new IdentityUser
    {
      UserName = userName,
      Email = request.Email?.Trim(),
      LockoutEnabled = true,
    };

    var createResult = await userManager.CreateAsync(newUser, request.Password);
    if (!createResult.Succeeded)
    {
      return Results.BadRequest(new { error = string.Join("; ", createResult.Errors.Select(e => e.Description)) });
    }

    await signInManager.SignInAsync(newUser, isPersistent: false);
    return Results.Ok(new { userName = newUser.UserName, email = newUser.Email });
  }
  catch (Exception ex)
  {
    // P1 fix: Don't expose exception details to clients - log internally instead
    app.Logger.LogError(ex, "Registration failed for user attempt");
    return Results.Problem("Registration failed due to an internal error", statusCode: 500);
  }
});

app.MapGet("/api/config", () => Results.Ok(new { publicShareBaseUrl }));

authGroup.MapPost("/login", async (LoginRequest request, SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager, IAntiforgery antiforgery, HttpContext context) =>
{
  // CSRF protection for state-changing operation (P0 security fix)
  try
  {
    await antiforgery.ValidateRequestAsync(context);
  }
  catch (AntiforgeryValidationException)
  {
    return Results.BadRequest(new { error = "Invalid or missing CSRF token" });
  }

  if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
  {
    return Results.BadRequest(new { error = "Username and password are required." });
  }

  try
  {
    var userName = request.UserName.Trim();
    var user = await userManager.FindByNameAsync(userName);
    if (user is null)
    {
      return Results.Unauthorized();
    }

    var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
    if (result.IsLockedOut)
    {
      return Results.StatusCode(StatusCodes.Status423Locked);
    }
    if (!result.Succeeded)
    {
      return Results.Unauthorized();
    }

    return Results.Ok(new { userName = user.UserName, email = user.Email });
  }
  catch (Exception ex)
  {
    // P1 fix: Don't expose exception details to clients - log internally instead
    app.Logger.LogError(ex, "Login failed for user attempt");
    return Results.Problem("Login failed due to an internal error", statusCode: 500);
  }
});

authGroup.MapPost("/logout", async (SignInManager<IdentityUser> signInManager) =>
{
  await signInManager.SignOutAsync();
  return Results.Ok();
}).RequireAuthorization();

authGroup.MapGet("/providers", () =>
{
  var providers = new List<object>();
  if (googleEnabled) providers.Add(new { name = "google", displayName = "Google" });
  if (microsoftEnabled) providers.Add(new { name = "microsoft", displayName = "Microsoft" });
  if (githubEnabled) providers.Add(new { name = "github", displayName = "GitHub" });
  return Results.Ok(providers);
});

// Endpoint to get CSRF token for frontend (P0 security fix)
// Not rate-limited since it's needed before each auth operation
app.MapGet("/api/auth/csrf-token", (IAntiforgery antiforgery, HttpContext context) =>
{
  var tokens = antiforgery.GetAndStoreTokens(context);
  return Results.Ok(new { token = tokens.RequestToken });
});

authGroup.MapGet("/external/{provider}", (string provider, string? returnUrl, SignInManager<IdentityUser> signInManager) =>
{
  var normalized = provider.ToLowerInvariant();
  var scheme = normalized switch
  {
    "google" => "Google",
    "microsoft" => "microsoft",
    _ => null
  };

  if (scheme is null || !ProviderEnabled(normalized))
  {
    return Results.NotFound();
  }

  var redirectUri = BuildReturnUrl(returnUrl);
  var props = signInManager.ConfigureExternalAuthenticationProperties(
    scheme,
    $"/api/auth/external-callback?provider={normalized}&returnUrl={Uri.EscapeDataString(redirectUri)}");
  return Results.Challenge(props, new[] { scheme });
});

authGroup.MapGet("/external-callback", async (string provider, string? returnUrl, SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager, HttpContext context) =>
{
  var info = await signInManager.GetExternalLoginInfoAsync();
  if (info is null)
  {
    return Results.Redirect("/?auth=failed");
  }

  var signInResult = await signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false);
  IdentityUser? user = null;
  if (!signInResult.Succeeded)
  {
    var email = info.Principal.FindFirstValue(ClaimTypes.Email);
    user = !string.IsNullOrWhiteSpace(email) ? await userManager.FindByEmailAsync(email) : null;
    var userName = email ?? $"{info.LoginProvider}-{info.ProviderKey}";
    user ??= new IdentityUser
    {
      UserName = userName,
      Email = email,
    };

    if (user.Id == default)
    {
      var create = await userManager.CreateAsync(user);
      if (!create.Succeeded)
      {
        return Results.Redirect("/?auth=failed");
      }
    }

    await userManager.AddLoginAsync(user, info);
    await signInManager.SignInAsync(user, isPersistent: false);
  }

  await context.SignOutAsync(IdentityConstants.ExternalScheme);
  var safeReturn = BuildReturnUrl(returnUrl);
  return Results.Redirect(safeReturn);
});

async Task<PlaylistSourceFile> FetchAndCacheSourceAsync(
  string url,
  PlaylistFileCache cache,
  PlaylistSourceFetcher fetcher,
  CancellationToken cancellationToken)
{
  // A hash can skip parsing/filtering, but it cannot skip a network download. Reuse a recent
  // source first; after the configurable interval, validators (when supported) cheaply confirm
  // freshness and providers without validators are re-read at most once per interval.
  var fresh = cache.TryGetFreshSource(url);
  if (fresh is not null)
  {
    return fresh;
  }

  var existing = cache.TryGetSource(url);
  var fetched = await fetcher.FetchAsync(url, existing, cancellationToken);
  if (fetched.NotModified)
  {
    if (existing is null)
    {
      throw new HttpRequestException("The playlist source returned an unusable cache validation response.");
    }

    cache.TouchNotModified(existing, fetched.ETag, fetched.LastModifiedUtc);
    return existing;
  }

  return cache.StoreDownloaded(url, fetched.DownloadedFile
    ?? throw new InvalidOperationException("Playlist source did not return a body."));
}

static void CopySourceMetadata(Playlist playlist, PlaylistSourceFile source)
{
  playlist.SourceHash = source.ContentHash;
  // Keep the full validator in the ephemeral cache, but do not let a nonconforming upstream
  // ETag exceed the database column and turn an otherwise valid download into a 5xx response.
  playlist.SourceETag = ClampDatabaseString(source.ETag, 512);
  playlist.SourceLastModifiedUtc = source.LastModifiedUtc;
  playlist.SourceLengthBytes = source.LengthBytes;
  playlist.SourceCheckedUtc = DateTimeOffset.UtcNow;
}

static string? ClampDatabaseString(string? value, int maxLength) =>
  string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

async Task<IResult> FetchPlaylistFileAsync(
  string? url,
  HttpContext context,
  PlaylistFileCache cache,
  PlaylistSourceFetcher fetcher,
  PlaylistJobGate jobGate)
{
  if (string.IsNullOrWhiteSpace(url))
  {
    return Results.BadRequest(new { error = "url is required" });
  }
  if (url.Length > MaxUrlLength)
  {
    return Results.BadRequest(new { error = $"Source URL must be {MaxUrlLength} characters or less." });
  }

  var job = await jobGate.TryEnterAsync(context.RequestAborted);
  if (job is null)
  {
    return Results.Problem("Playlist processing is busy. Please retry shortly.", statusCode: StatusCodes.Status429TooManyRequests);
  }

  await using (job)
  {
    try
    {
      var fetched = await fetcher.FetchAsync(url, null, context.RequestAborted);
      var downloaded = fetched.DownloadedFile
        ?? throw new InvalidOperationException("Playlist source did not return a body.");
      return new PlaylistFileResult(
        cache.LeaseTransientFile(
          downloaded.TemporaryPath,
          associatedResource: downloaded,
          knownLengthBytes: downloaded.LengthBytes),
        "application/x-mpegurl; charset=utf-8",
        download: false);
    }
    catch (PlaylistSizeExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (PlaylistDiskCapacityExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status507InsufficientStorage);
    }
    catch (OperationCanceledException)
    {
      return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
    }
    catch (InvalidOperationException ex)
    {
      return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
      app.Logger.LogWarning(ex, "HTTP request failed during playlist fetch");
      var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
      return Results.Problem(GetPlaylistFetchErrorMessage(ex), statusCode: status);
    }
    catch (Exception ex)
    {
      app.Logger.LogError(ex, "Unexpected error during playlist fetch");
      return Results.Problem("Fetch failed", statusCode: (int)HttpStatusCode.BadGateway);
    }
  }
}

static string GetPlaylistFetchErrorMessage(HttpRequestException ex)
{
  if (ex.Message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase))
  {
    return ex.Message;
  }

  if (ex.StatusCode == HttpStatusCode.Forbidden)
  {
    return "The playlist source rejected Strim's backend request (HTTP 403). The provider may block cloud/proxy servers or require access from your own network.";
  }

  if (ex.StatusCode.HasValue)
  {
    return $"The playlist source returned HTTP {(int)ex.StatusCode.Value}.";
  }

  return "Failed to fetch the playlist from the source";
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/playlists", async (ClaimsPrincipal user, AppDbContext db) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  try
  {
    var items = await db.Playlists
      .Where(p => p.OwnerId == userId)
      .ToListAsync();

    // SQLite cannot ORDER BY DateTimeOffset; sort in-memory for portability.
    items = items
      .OrderByDescending(p => p.UpdatedAt)
      .ThenByDescending(p => p.CreatedAt)
      .ToList();

    return Results.Ok(items);
  }
  catch (Exception ex)
  {
    // P1 fix: Don't expose exception details - log internally
    app.Logger.LogError(ex, "Failed to load playlists for user");
    return Results.Problem("Failed to load playlists due to an internal error", statusCode: 500);
  }
}).RequireAuthorization();

app.MapGet("/api/playlists/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  try
  {
    var item = await db.Playlists
      .Where(p => p.OwnerId == userId && p.Id == id)
      .FirstOrDefaultAsync();
    return item is null ? Results.NotFound() : Results.Ok(item);
  }
  catch (Exception ex)
  {
    // P1 fix: Don't expose exception details - log internally
    app.Logger.LogError(ex, "Failed to load playlist {PlaylistId}", id);
    return Results.Problem("Failed to load playlist due to an internal error", statusCode: 500);
  }
}).RequireAuthorization();

app.MapPost("/api/playlists", async (PlaylistRequest input, ClaimsPrincipal user, AppDbContext db) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  // P1 fix: Input validation
  if (string.IsNullOrWhiteSpace(input.Name))
  {
    return Results.BadRequest(new { error = "Name is required." });
  }
  if (input.Name.Length > MaxPlaylistNameLength)
  {
    return Results.BadRequest(new { error = $"Name must be {MaxPlaylistNameLength} characters or less." });
  }
  if (!string.IsNullOrEmpty(input.SourceUrl) && input.SourceUrl.Length > MaxUrlLength)
  {
    return Results.BadRequest(new { error = $"Source URL must be {MaxUrlLength} characters or less." });
  }
  if (input.DisabledGroups?.Count > MaxDisabledGroupsCount)
  {
    return Results.BadRequest(new { error = $"Disabled groups list cannot exceed {MaxDisabledGroupsCount} items." });
  }

  try
  {
    var entity = new Playlist
    {
      Name = input.Name.Trim(),
      SourceUrl = input.SourceUrl,
      SourceName = input.SourceName,
      DisabledGroups = input.DisabledGroups ?? new List<string>(),
      TotalChannels = input.TotalChannels ?? 0,
      GroupCount = input.GroupCount ?? 0,
      ExpirationUtc = input.ExpirationUtc,
      ShareCode = string.IsNullOrWhiteSpace(input.ShareCode) ? SecurityHelpers.GenerateSecureShareCode() : input.ShareCode.Trim(),
      OwnerId = userId,
    };

    db.Playlists.Add(entity);
    await db.SaveChangesAsync();
    return Results.Created($"/api/playlists/{entity.Id}", entity);
  }
  catch (Exception ex)
  {
    // P1 fix: Don't expose exception details - log internally
    app.Logger.LogError(ex, "Failed to save playlist");
    return Results.Problem("Failed to save playlist due to an internal error", statusCode: 500);
  }
}).RequireAuthorization();

app.MapPut("/api/playlists/{id:guid}", async (Guid id, PlaylistRequest input, ClaimsPrincipal user, AppDbContext db) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  var entity = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);
  if (entity is null) return Results.NotFound();

  // P1 fix: Input validation
  if (string.IsNullOrWhiteSpace(input.Name))
  {
    return Results.BadRequest(new { error = "Name is required." });
  }
  if (input.Name.Length > MaxPlaylistNameLength)
  {
    return Results.BadRequest(new { error = $"Name must be {MaxPlaylistNameLength} characters or less." });
  }
  if (!string.IsNullOrEmpty(input.SourceUrl) && input.SourceUrl.Length > MaxUrlLength)
  {
    return Results.BadRequest(new { error = $"Source URL must be {MaxUrlLength} characters or less." });
  }
  if (input.DisabledGroups?.Count > MaxDisabledGroupsCount)
  {
    return Results.BadRequest(new { error = $"Disabled groups list cannot exceed {MaxDisabledGroupsCount} items." });
  }

  try
  {
    entity.Name = input.Name.Trim();
    entity.SourceUrl = input.SourceUrl;
    entity.SourceName = input.SourceName;
    entity.DisabledGroups = input.DisabledGroups ?? new List<string>();
    entity.TotalChannels = input.TotalChannels ?? entity.TotalChannels;
    entity.GroupCount = input.GroupCount ?? entity.GroupCount;
    entity.ExpirationUtc = input.ExpirationUtc ?? entity.ExpirationUtc;
    if (!string.IsNullOrWhiteSpace(input.ShareCode))
    {
      entity.ShareCode = input.ShareCode.Trim();
    }
    if (string.IsNullOrWhiteSpace(entity.ShareCode))
    {
      entity.ShareCode = SecurityHelpers.GenerateSecureShareCode();
    }

    await db.SaveChangesAsync();
    return Results.Ok(entity);
  }
  catch (Exception ex)
  {
    // P1 fix: Don't expose exception details - log internally
    app.Logger.LogError(ex, "Failed to update playlist {PlaylistId}", id);
    return Results.Problem("Failed to update playlist due to an internal error", statusCode: 500);
  }
}).RequireAuthorization();

app.MapDelete("/api/playlists/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  var entity = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);
  if (entity is null) return Results.NotFound();
  db.Playlists.Remove(entity);
  await db.SaveChangesAsync();
  return Results.NoContent();
}).RequireAuthorization();

app.MapPatch("/api/playlists/{id:guid}/toggle-active", async (Guid id, ClaimsPrincipal user, AppDbContext db, ILogger<Program> logger) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  var entity = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);
  if (entity is null) return Results.NotFound();

  var previousState = entity.IsActive;
  entity.IsActive = !entity.IsActive;
  entity.UpdatedAt = DateTimeOffset.UtcNow;
  await db.SaveChangesAsync();

  // Audit log for security monitoring
  logger.LogInformation(
    "User {UserId} toggled playlist {PlaylistId} share link from {PreviousState} to {NewState}",
    userId, id, previousState ? "active" : "inactive", entity.IsActive ? "active" : "inactive");

  return Results.Ok(new { id = entity.Id, isActive = entity.IsActive });
}).RequireAuthorization().RequireRateLimiting("sensitive");

app.MapPost("/api/playlists/{id:guid}/regenerate-code", async (Guid id, ClaimsPrincipal user, AppDbContext db, ILogger<Program> logger) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  var entity = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);
  if (entity is null) return Results.NotFound();

  var oldCode = entity.ShareCode;
  entity.ShareCode = SecurityHelpers.GenerateSecureShareCode();
  entity.UpdatedAt = DateTimeOffset.UtcNow;
  await db.SaveChangesAsync();

  // Audit log for security monitoring - track regenerations to detect abuse
  // Log only truncated code to prevent exposure in logs (first/last 4 chars for correlation)
  var oldCodeTruncated = oldCode?.Length > 8
    ? $"{oldCode.Substring(0, 4)}...{oldCode.Substring(oldCode.Length - 4)}"
    : "****";
  logger.LogWarning(
    "User {UserId} regenerated share code for playlist {PlaylistId}. Old code invalidated (truncated): {OldCodeTruncated}",
    userId, id, oldCodeTruncated);

  return Results.Ok(new { id = entity.Id, shareCode = entity.ShareCode });
}).RequireAuthorization().RequireRateLimiting("sensitive");

app.MapPatch("/api/playlists/{id:guid}/auto-refresh", async (Guid id, AutoRefreshRequest input, ClaimsPrincipal user, AppDbContext db) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

  var entity = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);
  if (entity is null) return Results.NotFound();

  entity.AutoRefreshEnabled = input.Enabled;
  entity.UpdatedAt = DateTimeOffset.UtcNow;
  await db.SaveChangesAsync();

  return Results.Ok(new { id = entity.Id, autoRefreshEnabled = entity.AutoRefreshEnabled, lastRefreshedUtc = entity.LastRefreshedUtc });
}).RequireAuthorization().RequireRateLimiting("general");

app.MapPost("/api/playlist/analyze", async (
  AnalyzePlaylistRequest input,
  HttpContext context,
  PlaylistFileCache cache,
  PlaylistSourceFetcher fetcher,
  PlaylistJobGate jobGate) =>
{
  if (string.IsNullOrWhiteSpace(input.SourceUrl) && string.IsNullOrWhiteSpace(input.RawText))
  {
    return Results.BadRequest(new { error = "Provide a sourceUrl or rawText." });
  }

  // P1 fix: Input validation
  if (!string.IsNullOrEmpty(input.SourceUrl) && input.SourceUrl.Length > MaxUrlLength)
  {
    return Results.BadRequest(new { error = $"Source URL must be {MaxUrlLength} characters or less." });
  }
  if (!string.IsNullOrEmpty(input.RawText) && input.RawText.Length > MaxPlaylistTextSize)
  {
    return Results.BadRequest(new { error = $"Playlist text exceeds maximum size of {MaxPlaylistTextSize / (1024 * 1024)} MB." });
  }

  var job = await jobGate.TryEnterAsync(context.RequestAborted);
  if (job is null)
  {
    return Results.Problem("Playlist processing is busy. Please retry shortly.", statusCode: StatusCodes.Status429TooManyRequests);
  }

  await using (job)
  {
    try
    {
      PlaylistSourceFile source;
      if (string.IsNullOrWhiteSpace(input.RawText))
      {
        source = await FetchAndCacheSourceAsync(input.SourceUrl!, cache, fetcher, context.RequestAborted);
      }
      else
      {
        var raw = await cache.WriteRawTextAsync(input.RawText!, context.RequestAborted);
        source = cache.StoreRawText(Guid.NewGuid().ToString("N"), raw);
      }

      var analysis = cache.TryGetAnalysis(source)
        ?? await PlaylistProcessor.AnalyzeFileAsync(
          source.FilePath,
          context.RequestAborted,
          cache.MaxLineLengthChars,
          cache.MaxGroupCount,
          cache.MaxGroupTitleLengthChars,
          cache.MaxGroupMetadataBytes);
      cache.StoreAnalysis(source, analysis);
      var cacheKey = cache.CreateSession(source);

      DateTimeOffset? expiration = null;
      if (!string.IsNullOrWhiteSpace(input.SourceUrl) && Uri.TryCreate(input.SourceUrl, UriKind.Absolute, out var parsedUri))
      {
        expiration = PlaylistProcessor.TryExtractExpiration(parsedUri);
      }

      var friendlyName = string.IsNullOrWhiteSpace(input.SourceName)
        ? PlaylistProcessor.DeriveNameFromUrl(input.SourceUrl)
        : input.SourceName!.Trim();

      var response = new AnalyzePlaylistResponse(
        cacheKey,
        input.SourceUrl,
        friendlyName,
        analysis.TotalChannels,
        analysis.Groups.Count,
        expiration,
        PlaylistProcessor.ToGroupResults(analysis.Groups));

      return Results.Ok(response);
    }
    catch (PlaylistSizeExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (PlaylistDiskCapacityExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status507InsufficientStorage);
    }
    catch (OperationCanceledException)
    {
      return Results.Problem("Fetch or processing timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
    }
    catch (InvalidOperationException ex)
    {
      // InvalidOperationException contains controlled messages (SSRF, URL validation) - safe to expose
      return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
      app.Logger.LogWarning(ex, "HTTP request failed during playlist analyze");
      var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
      return Results.Problem(GetPlaylistFetchErrorMessage(ex), statusCode: status);
    }
    catch (Exception ex)
    {
      app.Logger.LogError(ex, "Unexpected error during playlist analyze");
      return Results.Problem("Failed to analyze playlist", statusCode: (int)HttpStatusCode.BadGateway);
    }
  }
}).RequireRateLimiting("fetch");

app.MapPost("/api/playlist/generate", async (
  GeneratePlaylistRequest input,
  HttpContext context,
  PlaylistFileCache cache,
  PlaylistSourceFetcher fetcher,
  PlaylistJobGate jobGate) =>
{
  if (string.IsNullOrWhiteSpace(input.CacheKey) && string.IsNullOrWhiteSpace(input.SourceUrl))
  {
    return Results.BadRequest(new { error = "Provide a cacheKey or sourceUrl." });
  }

  // P1 fix: Input validation
  if (!string.IsNullOrEmpty(input.SourceUrl) && input.SourceUrl.Length > MaxUrlLength)
  {
    return Results.BadRequest(new { error = $"Source URL must be {MaxUrlLength} characters or less." });
  }
  if (input.DisabledGroups?.Count > MaxDisabledGroupsCount)
  {
    return Results.BadRequest(new { error = $"Disabled groups list cannot exceed {MaxDisabledGroupsCount} items." });
  }
  if (input.DisabledGroups?.Any(group => string.IsNullOrWhiteSpace(group) || group.Length > cache.MaxGroupTitleLengthChars) == true)
  {
    return Results.BadRequest(new { error = $"Each disabled group name must be between 1 and {cache.MaxGroupTitleLengthChars} characters." });
  }

  var job = await jobGate.TryEnterAsync(context.RequestAborted);
  if (job is null)
  {
    return Results.Problem("Playlist processing is busy. Please retry shortly.", statusCode: StatusCodes.Status429TooManyRequests);
  }

  await using (job)
  {
    try
    {
      PlaylistSourceFile? source = !string.IsNullOrWhiteSpace(input.CacheKey)
        ? cache.TryGetSessionSource(input.CacheKey!)
        : null;
      if (source is null && !string.IsNullOrWhiteSpace(input.SourceUrl))
      {
        source = await FetchAndCacheSourceAsync(input.SourceUrl, cache, fetcher, context.RequestAborted);
      }
      if (source is null)
      {
        return Results.BadRequest(new { error = "The playlist session expired. Analyze the source again." });
      }

      var disabled = new HashSet<string>(input.DisabledGroups ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
      var output = cache.TryGetOutput(source.ContentHash, disabled);
      if (output is null)
      {
        var temporaryOutputPath = cache.CreateOutputTemporaryPath();
        using var outputReservation = cache.ReserveOutputCapacity(source);
        try
        {
          var result = await PlaylistProcessor.GenerateFilteredFileAsync(
            source.FilePath,
            temporaryOutputPath,
            disabled,
            context.RequestAborted,
            cache.MaxLineLengthChars,
            cache.MaxGeneratedBytes);
          output = cache.StoreOutput(source, disabled, temporaryOutputPath, result);
        }
        catch
        {
          cache.DeleteTemporaryFile(temporaryOutputPath, outputReservation.ReservedBytes);
          throw;
        }
      }

      return Results.Ok(new GeneratePlaylistResponse(output.OutputKey, output.TotalChannels, output.KeptChannels));
    }
    catch (PlaylistSizeExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (PlaylistGeneratedSizeExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (PlaylistDiskCapacityExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status507InsufficientStorage);
    }
    catch (OperationCanceledException)
    {
      return Results.Problem("Fetch or processing timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
    }
    catch (InvalidOperationException ex)
    {
      return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
      app.Logger.LogWarning(ex, "HTTP request failed during playlist generate");
      var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
      return Results.Problem(GetPlaylistFetchErrorMessage(ex), statusCode: status);
    }
    catch (Exception ex)
    {
      app.Logger.LogError(ex, "Unexpected error during playlist generate");
      return Results.Problem("Failed to generate playlist", statusCode: (int)HttpStatusCode.BadGateway);
    }
  }
}).RequireRateLimiting("fetch");

app.MapGet("/api/playlist/output/{outputKey}", IResult (string outputKey, PlaylistFileCache cache) =>
{
  // The random, short-lived output key is a capability generated only after an analyzed source
  // has been processed. Keep this unauthenticated so anonymous source loads can download too.
  var lease = cache.TryLeaseOutput(outputKey);
  return lease is null
    ? Results.NotFound()
    : new PlaylistFileResult(lease, "application/x-mpegurl; charset=utf-8");
}).RequireRateLimiting("fetch");

app.MapGet("/api/playlists/sample", () =>
{
  var groups = new[] { "News", "Sports", "Entertainment", "Documentary", "Kids", "Music", "Regional", "Premium", "4K Ultra HD", "Radio" };
  var channels = new (string Name, string Group, string Logo)[]
  {
    ("CNN International", "News", "https://placehold.co/40x40/ff4444/fff?text=CNN"),
    ("BBC World News", "News", "https://placehold.co/40x40/bb0000/fff?text=BBC"),
    ("Sky News", "News", "https://placehold.co/40x40/0044cc/fff?text=SKY"),
    ("Al Jazeera English", "News", "https://placehold.co/40x40/008800/fff?text=AJ"),
    ("Fox News", "News", "https://placehold.co/40x40/004488/fff?text=FN"),
    ("Euronews", "News", "https://placehold.co/40x40/222244/fff?text=EN"),
    ("Bloomberg TV", "News", "https://placehold.co/40x40/115511/fff?text=BLM"),
    ("France 24 English", "News", "https://placehold.co/40x40/112244/fff?text=F24"),
    ("Deutsche Welle", "News", "https://placehold.co/40x40/cc0000/fff?text=DW"),
    ("RT News", "News", "https://placehold.co/40x40/004488/fff?text=RT"),
    ("ESPN 1", "Sports", "https://placehold.co/40x40/cc0000/fff?text=ESPN"),
    ("ESPN 2", "Sports", "https://placehold.co/40x40/cc4400/fff?text=ES2"),
    ("Sky Sports Main Event", "Sports", "https://placehold.co/40x40/004488/fff?text=SS1"),
    ("Sky Sports Football", "Sports", "https://placehold.co/40x40/004488/fff?text=SSF"),
    ("Eurosport 1", "Sports", "https://placehold.co/40x40/004400/fff?text=ES1"),
    ("beIN Sports HD", "Sports", "https://placehold.co/40x40/660000/fff?text=BE1"),
    ("DAZN 1", "Sports", "https://placehold.co/40x40/111111/fff?text=DAZ"),
    ("NBA TV", "Sports", "https://placehold.co/40x40/cc6600/fff?text=NBA"),
    ("NFL Network", "Sports", "https://placehold.co/40x40/004400/fff?text=NFL"),
    ("Fox Sports 1", "Sports", "https://placehold.co/40x40/002266/fff?text=FS1"),
    ("Netflix Originals", "Entertainment", "https://placehold.co/40x40/cc0000/fff?text=NF"),
    ("HBO", "Entertainment", "https://placehold.co/40x40/000000/fff?text=HBO"),
    ("HBO 2", "Entertainment", "https://placehold.co/40x40/111111/fff?text=HB2"),
    ("HBO Comedy", "Entertainment", "https://placehold.co/40x40/222222/fff?text=HBC"),
    ("AMC", "Entertainment", "https://placehold.co/40x40/004400/fff?text=AMC"),
    ("FX", "Entertainment", "https://placehold.co/40x40/222244/fff?text=FX"),
    ("Comedy Central", "Entertainment", "https://placehold.co/40x40/ff8800/fff?text=CC"),
    ("MTV", "Entertainment", "https://placehold.co/40x40/00cc00/fff?text=MTV"),
    ("TLC", "Entertainment", "https://placehold.co/40x40/cc4488/fff?text=TLC"),
    ("Discovery Channel", "Entertainment", "https://placehold.co/40x40/886600/fff?text=DSC"),
    ("National Geographic", "Documentary", "https://placehold.co/40x40/ffcc00/000?text=NG"),
    ("Discovery Science", "Documentary", "https://placehold.co/40x40/004488/fff?text=DSC"),
    ("History Channel", "Documentary", "https://placehold.co/40x40/886622/fff?text=HST"),
    ("Animal Planet", "Documentary", "https://placehold.co/40x40/448844/fff?text=AP"),
    ("BBC Earth", "Documentary", "https://placehold.co/40x40/004488/fff?text=BBC"),
    ("Smithsonian Channel", "Documentary", "https://placehold.co/40x40/224466/fff?text=SMI"),
    ("Curiosity Stream", "Documentary", "https://placehold.co/40x40/004466/fff?text=CUR"),
    ("Nat Geo Wild", "Documentary", "https://placehold.co/40x40/88aa00/fff?text=NGW"),
    ("Travel Channel", "Documentary", "https://placehold.co/40x40/448866/fff?text=TRV"),
    ("Cartoon Network", "Kids", "https://placehold.co/40x40/00aaff/fff?text=CN"),
    ("Nickelodeon", "Kids", "https://placehold.co/40x40/ff8800/fff?text=NCK"),
    ("Disney Channel", "Kids", "https://placehold.co/40x40/0066cc/fff?text=DIS"),
    ("Disney Junior", "Kids", "https://placehold.co/40x40/66aaff/fff?text=DJR"),
    ("Boomerang", "Kids", "https://placehold.co/40x40/22cc44/fff?text=BOM"),
    ("CBeebies", "Kids", "https://placehold.co/40x40/44aa88/fff?text=CBB"),
    ("PBS Kids", "Kids", "https://placehold.co/40x40/4488aa/fff?text=PBS"),
    ("Nick Jr.", "Kids", "https://placehold.co/40x40/ffaa00/fff?text=NJR"),
    ("MTV Hits", "Music", "https://placehold.co/40x40/ff00ff/fff?text=MTH"),
    ("VH1", "Music", "https://placehold.co/40x40/ccaa00/fff?text=VH1"),
    ("MTV Base", "Music", "https://placehold.co/40x40/00cc88/fff?text=MTB"),
    ("MTV Rocks", "Music", "https://placehold.co/40x40/cc4400/fff?text=MTR"),
    ("CMT", "Music", "https://placehold.co/40x40/448800/fff?text=CMT"),
    ("BBC Radio 1 (Video)", "Music", "https://placehold.co/40x40/bb0000/fff?text=BR1"),
    ("Trace Urban", "Music", "https://placehold.co/40x40/004488/fff?text=TRC"),
    ("Star Plus", "Regional", "https://placehold.co/40x40/002288/fff?text=SPL"),
    ("Zee TV", "Regional", "https://placehold.co/40x40/0088cc/fff?text=ZEE"),
    ("Colors TV", "Regional", "https://placehold.co/40x40/ff0088/fff?text=COL"),
    ("Sony TV", "Regional", "https://placehold.co/40x40/0044aa/fff?text=SNY"),
    ("TV Globo", "Regional", "https://placehold.co/40x40/008800/fff?text=GLB"),
    ("Telefe", "Regional", "https://placehold.co/40x40/4488cc/fff?text=TLF"),
    ("CCTV 4", "Regional", "https://placehold.co/40x40/cc0000/fff?text=CCTV"),
    ("NHK World", "Regional", "https://placehold.co/40x40/880000/fff?text=NHK"),
    ("HBO Premium", "Premium", "https://placehold.co/40x40/111111/fff?text=HBP"),
    ("Showtime HD", "Premium", "https://placehold.co/40x40/004400/fff?text=SHW"),
    ("Starz", "Premium", "https://placehold.co/40x40/004488/fff?text=STZ"),
    ("Cinemax", "Premium", "https://placehold.co/40x40/000044/fff?text=CMX"),
    ("Sky Cinema", "Premium", "https://placehold.co/40x40/002244/fff?text=SKC"),
    ("FOX Movies", "Premium", "https://placehold.co/40x40/ccaa00/fff?text=FXM"),
    ("Paramount+", "Premium", "https://placehold.co/40x40/0044cc/fff?text=PAR"),
    ("Peacock", "Premium", "https://placehold.co/40x40/0088cc/fff?text=PEA"),
    ("UHD Discovery", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KD"),
    ("UHD Sports", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KS"),
    ("UHD Movies", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KM"),
    ("UHD Nature", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KN"),
    ("UHD Events", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KE"),
    ("BBC Radio 1", "Radio", "https://placehold.co/40x40/bb0000/fff?text=R1"),
    ("BBC Radio 2", "Radio", "https://placehold.co/40x40/cc4400/fff?text=R2"),
    ("BBC Radio 4", "Radio", "https://placehold.co/40x40/aa6600/fff?text=R4"),
    ("NPR", "Radio", "https://placehold.co/40x40/004488/fff?text=NPR"),
    ("Classic FM", "Radio", "https://placehold.co/40x40/224466/fff?text=CLS"),
    ("Radio 538", "Radio", "https://placehold.co/40x40/ff6600/fff?text=538"),
  };

  var sb = new StringBuilder();
  sb.AppendLine("#EXTM3U");
  sb.AppendLine("# Created with Strim (https://strim.plis.dev)");

  var id = 1001;
  foreach (var (name, group, logo) in channels)
  {
    var tvgId = $"channel.{id}";
    var url = $"http://example.com/streams/{id}.m3u8";
    sb.AppendLine($"#EXTINF:-1 tvg-id=\"{tvgId}\" tvg-name=\"{name}\" tvg-logo=\"{logo}\" group-title=\"{group}\",{name}");
    sb.AppendLine(url);
    id++;
  }

  return Results.Ok(new { text = sb.ToString() });
}).RequireRateLimiting("general");

app.MapGet("/api/playlists/{id:guid}/stats", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
  var userId = GetUserId(user);
  if (string.IsNullOrWhiteSpace(userId))
    return Results.Unauthorized();

  var playlist = await db.Playlists.FindAsync(id);
  if (playlist is null || playlist.OwnerId != userId)
    return Results.NotFound();

  return Results.Ok(new { viewCount = playlist.ViewCount, lastViewedUtc = playlist.LastViewedUtc });
}).RequireAuthorization().RequireRateLimiting("general");

app.MapGet("/api/playlists/{id:guid}/share/{code}", async Task<IResult> (
  Guid id,
  string code,
  HttpContext context,
  AppDbContext db,
  PlaylistFileCache cache,
  PlaylistSourceFetcher fetcher,
  PlaylistJobGate jobGate) =>
{
  var playlist = await db.Playlists.FindAsync(id);

  // Use uniform 404 response for all invalid access attempts to prevent user enumeration
  // This prevents attackers from probing which playlist IDs exist via differential responses
  if (playlist is null) return Results.NotFound();
  if (!string.Equals(playlist.ShareCode, code, StringComparison.Ordinal))
  {
    return Results.NotFound();
  }
  if (!playlist.IsActive)
  {
    return Results.NotFound();
  }
  if (string.IsNullOrWhiteSpace(playlist.SourceUrl))
  {
    return Results.BadRequest(new { error = "Playlist is missing sourceUrl." });
  }

  var job = await jobGate.TryEnterAsync(context.RequestAborted);
  if (job is null)
  {
    return Results.Problem("Playlist processing is busy. Please retry shortly.", statusCode: StatusCodes.Status429TooManyRequests);
  }

  await using (job)
  {
    try
    {
      // Conditional GET reuses an unchanged source and hash-keyed generated variant.
      var source = await FetchAndCacheSourceAsync(playlist.SourceUrl, cache, fetcher, context.RequestAborted);
      var disabled = new HashSet<string>(playlist.DisabledGroups ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
      var output = cache.TryGetOutput(source.ContentHash, disabled);
      var fileName = $"{(playlist.SourceName ?? playlist.Name ?? "playlist")}-filtered.m3u";
      if (output is null)
      {
        var temporaryOutputPath = cache.CreateOutputTemporaryPath();
        using var outputReservation = cache.ReserveOutputCapacity(source);
        try
        {
          var result = await PlaylistProcessor.GenerateFilteredFileAsync(
            source.FilePath,
            temporaryOutputPath,
            disabled,
            context.RequestAborted,
            cache.MaxLineLengthChars,
            cache.MaxGeneratedBytes);
          output = cache.StoreOutput(source, disabled, temporaryOutputPath, result);
        }
        catch
        {
          cache.DeleteTemporaryFile(temporaryOutputPath, outputReservation.ReservedBytes);
          throw;
        }
      }

      CopySourceMetadata(playlist, source);
      playlist.ViewCount++;
      playlist.LastViewedUtc = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync(context.RequestAborted);

      var lease = cache.TryLeaseOutput(output.OutputKey, fileName);
      return lease is null
        ? Results.Problem("The generated playlist expired before it could be downloaded.", statusCode: StatusCodes.Status503ServiceUnavailable)
        : new PlaylistFileResult(lease, "application/x-mpegurl; charset=utf-8");
    }
    catch (PlaylistSizeExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (PlaylistGeneratedSizeExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (PlaylistDiskCapacityExceededException ex)
    {
      return Results.Problem(ex.Message, statusCode: StatusCodes.Status507InsufficientStorage);
    }
    catch (OperationCanceledException)
    {
      return Results.Problem("Fetch or processing timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
    }
    catch (HttpRequestException ex)
    {
      app.Logger.LogWarning(ex, "HTTP request failed during share download for playlist {PlaylistId}", id);
      var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
      return Results.Problem(GetPlaylistFetchErrorMessage(ex), statusCode: status);
    }
    catch (InvalidOperationException ex)
    {
      return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
      app.Logger.LogError(ex, "Unexpected error during share download for playlist {PlaylistId}", id);
      return Results.Problem("Failed to process shared playlist", statusCode: (int)HttpStatusCode.BadGateway);
    }
  }
}).RequireRateLimiting("share");

app.MapGet("/api/fetch", async Task<IResult> (
  string url,
  HttpContext context,
  PlaylistFileCache cache,
  PlaylistSourceFetcher fetcher,
  PlaylistJobGate jobGate) =>
{
  return await FetchPlaylistFileAsync(url, context, cache, fetcher, jobGate);
}).RequireRateLimiting("fetch");

app.MapPost("/api/fetch", async Task<IResult> (
  FetchRequest request,
  HttpContext context,
  PlaylistFileCache cache,
  PlaylistSourceFetcher fetcher,
  PlaylistJobGate jobGate) =>
{
  return await FetchPlaylistFileAsync(request.Url, context, cache, fetcher, jobGate);
}).RequireRateLimiting("fetch");

// Error page handling
app.MapGet("/error/{statusCode:int}", async (int statusCode, HttpContext context) =>
{
  var filePath = statusCode switch
  {
    404 => "wwwroot/404.html",
    500 => "wwwroot/500.html",
    502 or 503 or 504 => "wwwroot/50x.html",
    _ when statusCode >= 500 && statusCode < 600 => "wwwroot/50x.html",
    _ => "wwwroot/404.html" // Default to 404 for unknown status codes
  };

  if (File.Exists(filePath))
  {
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(filePath);
    return Results.Empty;
  }

  // Fallback if error page doesn't exist
  return Results.Problem($"Error {statusCode}", statusCode: statusCode);
});

// Endpoint routing runs before the static-file middleware in this Minimal API
// pipeline, so handle the default document explicitly instead of relying only
// on the earlier path rewrite.
var indexFilePath = Path.Combine(
  app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"),
  "index.html");
app.MapGet("/", () => Results.File(indexFilePath, "text/html; charset=utf-8"));

// SPA fallback for client-side routing - set 404 status and serve error page
app.MapFallback(async (HttpContext context) =>
{
  // Only fallback for non-API routes
  if (!context.Request.Path.StartsWithSegments("/api"))
  {
    context.Response.StatusCode = 404;
    context.Response.ContentType = "text/html; charset=utf-8";

    if (File.Exists("wwwroot/404.html"))
    {
      await context.Response.SendFileAsync("wwwroot/404.html");
    }
    else
    {
      await context.Response.WriteAsync("404 - Not Found");
    }
  }
});

app.Run();

public record PlaylistRequest(
  string Name,
  string? SourceUrl,
  string? SourceName,
  List<string>? DisabledGroups,
  int? TotalChannels,
  int? GroupCount,
  DateTimeOffset? ExpirationUtc,
  string? ShareCode);

public record FetchRequest(string Url);

public record RegisterRequest(string UserName, string Password, string? Email);

public record LoginRequest(string UserName, string Password);

public record AutoRefreshRequest(bool Enabled);
