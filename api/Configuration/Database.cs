using System.Data.Common;
using Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Configuration;

/// <summary>
/// Database provider selection and schema upkeep. There are no EF migrations: the schema is created
/// with EnsureCreated and later columns are added idempotently here (see AGENTS.md).
/// </summary>
public static class Database
{
  /// <summary>SQLite by default; PostgreSQL when DB_PROVIDER=postgres or a Postgres connection is configured.</summary>
  public static IServiceCollection AddStrimDatabase(this IServiceCollection services, IConfiguration configuration)
  {
    var configuredProvider = (configuration["DB_PROVIDER"] ?? configuration["DATABASE_PROVIDER"])?.ToLowerInvariant();
    var postgresConnectionString = configuration.GetConnectionString("Postgres") ?? configuration["POSTGRES_CONNECTION"];
    var sqliteConnectionString = configuration.GetConnectionString("Sqlite") ?? configuration["SQLITE_CONNECTION"];
    var sqlitePathOverride = configuration["SQLITE_PATH"];

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

      return services.AddDbContext<AppDbContext>(options => options.UseSqlite(sqliteBuilder.ConnectionString));
    }

    if (string.IsNullOrWhiteSpace(postgresConnectionString))
    {
      throw new InvalidOperationException(
        "PostgreSQL connection string is required when DB_PROVIDER is set to 'postgres'. " +
        "Set POSTGRES_CONNECTION environment variable or ConnectionStrings:Postgres in configuration.");
    }

    // Dynamic JSON lets Npgsql store List<string> as jsonb.
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
    dataSourceBuilder.EnableDynamicJson();
    var dataSource = dataSourceBuilder.Build();
    return services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
  }

  /// <summary>Creates the database if needed and adds columns introduced after the first release.</summary>
  public static void EnsureStrimSchema(this IServiceProvider services)
  {
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSchema");
    EnsureSchema(db, logger);
  }

  private static void EnsureSchema(AppDbContext db, ILogger logger)
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

  private static void EnsureIdentitySchema(AppDbContext db, ILogger logger)
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

  private static void TryEnsureSqliteIdentity(AppDbContext db, ILogger logger)
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

  private static void TryEnsurePostgresIdentity(AppDbContext db, ILogger logger)
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

  private static void TryPatchPostgresSchema(AppDbContext db, ILogger logger)
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

  private static void TryBackfillPostgresShareCodes(AppDbContext db, ILogger logger)
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

  private static void TryAddSqliteColumn(AppDbContext db, string columnName, string definition, ILogger logger)
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

  private static void TryBackfillSqliteShareCodes(AppDbContext db, ILogger logger)
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
}
