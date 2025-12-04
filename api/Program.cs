using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Npgsql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Data.Sqlite;
using System.Data.Common;

var builder = WebApplication.CreateBuilder(args);

// P1 fix: Input validation constants
const int MaxUrlLength = 2048;
const int MaxPlaylistNameLength = 200;
const int MaxPlaylistTextSize = 50 * 1024 * 1024; // 50 MB max for playlist text
const int MaxDisabledGroupsCount = 1000;

var configuredProvider = (builder.Configuration["DB_PROVIDER"] ?? builder.Configuration["DATABASE_PROVIDER"])?.ToLowerInvariant();
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres") ??
  builder.Configuration["POSTGRES_CONNECTION"];
var sqliteConnectionString = builder.Configuration.GetConnectionString("Sqlite") ??
  builder.Configuration["SQLITE_CONNECTION"];
var sqlitePathOverride = builder.Configuration["SQLITE_PATH"];

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
builder.Services.AddMemoryCache(options =>
{
  // Limit cache to 500MB to prevent memory exhaustion
  // Each cached playlist can be up to 50MB, so this allows ~10 concurrent cached playlists
  options.SizeLimit = 500 * 1024 * 1024;
});

// Rate limiting configuration to prevent abuse
builder.Services.AddRateLimiter(options =>
{
  options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

  // Strict rate limit for authentication endpoints (prevents brute force)
  options.AddPolicy("auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      factory: _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0
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
});

// Antiforgery for CSRF protection
builder.Services.AddAntiforgery(options =>
{
  options.HeaderName = "X-CSRF-TOKEN";
  options.Cookie.Name = "__Host-strim.csrf";
  options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
  options.Cookie.SameSite = SameSiteMode.Strict;
  options.Cookie.HttpOnly = true;
});

builder.Services.AddHttpClient("fetcher", client =>
{
  client.Timeout = TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("strim-fetch/1.0 (+https://github.com/)");
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-mpegurl"));
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
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
      o.Cookie.Name = "__Host-strim.auth";
      o.Cookie.SameSite = SameSiteMode.None;
      o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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
      o.Cookie.Name = "__Host-strim.external";
      o.Cookie.SameSite = SameSiteMode.None;
      o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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

builder.Services.AddAuthorization();

var app = builder.Build();

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

app.UseDefaultFiles();
app.UseStaticFiles();
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
    ["ownerid"] = "TEXT NULL"
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
  return Results.Ok(providers);
});

// Endpoint to get CSRF token for frontend (P0 security fix)
authGroup.MapGet("/csrf-token", (IAntiforgery antiforgery, HttpContext context) =>
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

async Task<string> FetchPlaylistText(string url, IHttpClientFactory httpClientFactory)
{
  if (string.IsNullOrWhiteSpace(url))
  {
    throw new ArgumentException("url is required", nameof(url));
  }

  if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
  {
    throw new InvalidOperationException("Only http/https URLs are allowed");
  }

  // SSRF protection: block requests to internal/private IPs (P0 security fix)
  if (SecurityHelpers.IsBlockedUrl(uri))
  {
    throw new InvalidOperationException("Access to internal or private network addresses is not allowed");
  }

  var client = httpClientFactory.CreateClient("fetcher");
  using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
  if (!res.IsSuccessStatusCode)
  {
    throw new HttpRequestException($"Upstream returned {(int)res.StatusCode}", null, res.StatusCode);
  }

  var bytes = await res.Content.ReadAsByteArrayAsync();
  return Encoding.UTF8.GetString(bytes);
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

app.MapPost("/api/playlist/analyze", async (AnalyzePlaylistRequest input, IHttpClientFactory httpClientFactory, IMemoryCache cache) =>
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

  try
  {
    var playlistText = string.IsNullOrWhiteSpace(input.RawText)
      ? await FetchPlaylistText(input.SourceUrl!, httpClientFactory)
      : input.RawText!;

    var (groupsMap, total) = PlaylistProcessor.CountGroups(playlistText);
    var cacheKey = $"pl-{Guid.NewGuid():N}";
    var cacheOptions = new MemoryCacheEntryOptions()
      .SetAbsoluteExpiration(TimeSpan.FromMinutes(15))
      .SetSize(playlistText.Length * 2); // Approximate memory size (UTF-16)
    cache.Set(cacheKey, playlistText, cacheOptions);

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
      total,
      groupsMap.Count,
      expiration,
      PlaylistProcessor.ToGroupResults(groupsMap));

    return Results.Ok(response);
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (InvalidOperationException ex)
  {
    // InvalidOperationException contains controlled messages (SSRF, URL validation) - safe to expose
    return Results.BadRequest(new { error = ex.Message });
  }
  catch (HttpRequestException ex)
  {
    // P1 fix: Don't expose raw HTTP error details
    app.Logger.LogWarning(ex, "HTTP request failed during playlist analyze");
    var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
    return Results.Problem("Failed to fetch the playlist from the source", statusCode: status);
  }
  catch (Exception ex)
  {
    app.Logger.LogError(ex, "Unexpected error during playlist analyze");
    return Results.Problem("Failed to analyze playlist", statusCode: (int)HttpStatusCode.BadGateway);
  }
}).RequireRateLimiting("fetch");

app.MapPost("/api/playlist/generate", async (GeneratePlaylistRequest input, IHttpClientFactory httpClientFactory, IMemoryCache cache) =>
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

  string? playlistText = null;
  if (!string.IsNullOrWhiteSpace(input.CacheKey))
  {
    cache.TryGetValue(input.CacheKey!, out playlistText);
  }

  try
  {
    if (playlistText is null && !string.IsNullOrWhiteSpace(input.SourceUrl))
    {
      playlistText = await FetchPlaylistText(input.SourceUrl!, httpClientFactory);
      if (!string.IsNullOrWhiteSpace(input.CacheKey))
      {
        var cacheOptions = new MemoryCacheEntryOptions()
          .SetAbsoluteExpiration(TimeSpan.FromMinutes(15))
          .SetSize(playlistText.Length * 2); // Approximate memory size (UTF-16)
        cache.Set(input.CacheKey!, playlistText, cacheOptions);
      }
    }
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (InvalidOperationException ex)
  {
    // InvalidOperationException contains controlled messages (SSRF, URL validation) - safe to expose
    return Results.BadRequest(new { error = ex.Message });
  }
  catch (HttpRequestException ex)
  {
    // P1 fix: Don't expose raw HTTP error details
    app.Logger.LogWarning(ex, "HTTP request failed during playlist generate");
    var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
    return Results.Problem("Failed to fetch the playlist from the source", statusCode: status);
  }
  catch (Exception ex)
  {
    app.Logger.LogError(ex, "Unexpected error during playlist generate");
    return Results.Problem("Failed to generate playlist", statusCode: (int)HttpStatusCode.BadGateway);
  }

  if (playlistText is null)
  {
    return Results.BadRequest(new { error = "Unable to load playlist from cache or sourceUrl." });
  }

  var disabled = new HashSet<string>(input.DisabledGroups ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
  var filtered = PlaylistProcessor.GenerateFiltered(playlistText, disabled);
  var response = new GeneratePlaylistResponse(filtered.Text, filtered.TotalChannels, filtered.KeptChannels);
  return Results.Ok(response);
}).RequireRateLimiting("fetch");

app.MapGet("/api/playlists/{id:guid}/share/{code}", async (Guid id, string code, AppDbContext db, IHttpClientFactory httpClientFactory) =>
{
  var playlist = await db.Playlists.FindAsync(id);
  if (playlist is null) return Results.NotFound();
  if (!string.Equals(playlist.ShareCode, code, StringComparison.Ordinal))
  {
    return Results.Unauthorized();
  }
  if (string.IsNullOrWhiteSpace(playlist.SourceUrl))
  {
    return Results.BadRequest(new { error = "Playlist is missing sourceUrl." });
  }

  try
  {
    var text = await FetchPlaylistText(playlist.SourceUrl, httpClientFactory);
    var disabled = new HashSet<string>(playlist.DisabledGroups ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    var filtered = PlaylistProcessor.GenerateFiltered(text, disabled);
    var fileName = $"{(playlist.SourceName ?? playlist.Name ?? "playlist")}-filtered.m3u";
    return Results.File(Encoding.UTF8.GetBytes(filtered.Text), "application/x-mpegurl", fileDownloadName: fileName);
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (HttpRequestException ex)
  {
    // P1 fix: Don't expose raw HTTP error details
    app.Logger.LogWarning(ex, "HTTP request failed during share download for playlist {PlaylistId}", id);
    var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
    return Results.Problem("Failed to fetch the playlist from the source", statusCode: status);
  }
  catch (Exception ex)
  {
    app.Logger.LogError(ex, "Unexpected error during share download for playlist {PlaylistId}", id);
    return Results.Problem("Failed to process shared playlist", statusCode: (int)HttpStatusCode.BadGateway);
  }
});

app.MapGet("/api/fetch", async (string url, IHttpClientFactory httpClientFactory) =>
{
  if (string.IsNullOrWhiteSpace(url))
  {
    return Results.BadRequest(new { error = "url is required" });
  }

  if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
  {
    return Results.BadRequest(new { error = "Only http/https URLs are allowed" });
  }

  // SSRF protection: block requests to internal/private IPs (P0 security fix)
  if (SecurityHelpers.IsBlockedUrl(uri))
  {
    return Results.BadRequest(new { error = "Access to internal or private network addresses is not allowed" });
  }

  try
  {
    var client = httpClientFactory.CreateClient("fetcher");
    using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
    if (!res.IsSuccessStatusCode)
    {
      return Results.StatusCode((int)res.StatusCode);
    }
    var bytes = await res.Content.ReadAsByteArrayAsync();
    // Return with a content-length to avoid proxy/protocol quirks.
    return Results.File(bytes, "application/x-mpegurl; charset=utf-8");
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (Exception ex)
  {
    return Results.Problem("Fetch failed", statusCode: (int)HttpStatusCode.BadGateway);
  }
}).RequireRateLimiting("fetch");

app.MapPost("/api/fetch", async (FetchRequest request, IHttpClientFactory httpClientFactory) =>
{
  var url = request.Url;
  if (string.IsNullOrWhiteSpace(url))
  {
    return Results.BadRequest(new { error = "url is required" });
  }

  if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
  {
    return Results.BadRequest(new { error = "Only http/https URLs are allowed" });
  }

  // SSRF protection: block requests to internal/private IPs (P0 security fix)
  if (SecurityHelpers.IsBlockedUrl(uri))
  {
    return Results.BadRequest(new { error = "Access to internal or private network addresses is not allowed" });
  }

  try
  {
    var client = httpClientFactory.CreateClient("fetcher");
    using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
    if (!res.IsSuccessStatusCode)
    {
      return Results.StatusCode((int)res.StatusCode);
    }
    var bytes = await res.Content.ReadAsByteArrayAsync();
    return Results.File(bytes, "application/x-mpegurl; charset=utf-8");
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (Exception ex)
  {
    return Results.Problem("Fetch failed", statusCode: (int)HttpStatusCode.BadGateway);
  }
}).RequireRateLimiting("fetch");

// SPA fallback
app.MapFallbackToFile("/index.html");

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
