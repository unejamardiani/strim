using System.Data.Common;
using System.Globalization;
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
    EnsureTitleMarksSchema(db, logger);
    EnsurePlaylistSharesSchema(db, logger);

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
      TryAddSqliteColumn(db, "accountstatus", "TEXT NULL", logger);
      TryAddSqliteColumn(db, "accountmaxconnections", "INTEGER NULL", logger);
      TryAddSqliteColumn(db, "accountactiveconnections", "INTEGER NULL", logger);
      TryAddSqliteColumn(db, "accountcheckedutc", "TEXT NULL", logger);
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

  /// <summary>
  /// The film and series marks table on SQLite (the one table the owner approved on 18 Nov 2026). The marks
  /// belong to an AspNetUsers row and go with it. Two partial unique indexes keep a null year its own value: one
  /// covers rows with a year, the other rows without, so a null year never collides with a real year (0 included).
  /// </summary>
  public const string SqliteTitleMarksSql = @"
        CREATE TABLE IF NOT EXISTS titlemarks (
          id TEXT NOT NULL PRIMARY KEY,
          userid TEXT NOT NULL,
          kind TEXT NOT NULL,
          titlekey TEXT NOT NULL,
          year INTEGER NULL,
          displaytitle TEXT NOT NULL,
          seen INTEGER NOT NULL,
          rating INTEGER NOT NULL,
          filmdbid TEXT NULL,
          watchlist INTEGER NOT NULL DEFAULT 0,
          watchlistedutc TEXT NULL,
          createdutc TEXT NOT NULL,
          updatedutc TEXT NOT NULL,
          CONSTRAINT FK_titlemarks_AspNetUsers_userid FOREIGN KEY (userid) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_titlemarks_userid ON titlemarks (userid);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_titlemarks_userid_kind_titlekey_year ON titlemarks (userid, kind, titlekey, year) WHERE year IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_titlemarks_userid_kind_titlekey_noyear ON titlemarks (userid, kind, titlekey) WHERE year IS NULL;
      ";

  /// <summary>The same table on PostgreSQL, with quoted identifiers as in <see cref="TryEnsurePostgresIdentity"/>.</summary>
  public const string PostgresTitleMarksSql = @"
        CREATE TABLE IF NOT EXISTS ""titlemarks"" (
          ""id"" uuid NOT NULL PRIMARY KEY,
          ""userid"" varchar(450) NOT NULL,
          ""kind"" varchar(16) NOT NULL,
          ""titlekey"" varchar(256) NOT NULL,
          ""year"" integer NULL,
          ""displaytitle"" varchar(256) NOT NULL,
          ""seen"" boolean NOT NULL,
          ""rating"" integer NOT NULL,
          ""filmdbid"" varchar(64) NULL,
          ""watchlist"" boolean NOT NULL DEFAULT false,
          ""watchlistedutc"" timestamp with time zone NULL,
          ""createdutc"" timestamp with time zone NOT NULL,
          ""updatedutc"" timestamp with time zone NOT NULL,
          CONSTRAINT ""FK_titlemarks_AspNetUsers_userid"" FOREIGN KEY (""userid"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ""IX_titlemarks_userid"" ON ""titlemarks"" (""userid"");
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_titlemarks_userid_kind_titlekey_year"" ON ""titlemarks"" (""userid"", ""kind"", ""titlekey"", ""year"") WHERE ""year"" IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_titlemarks_userid_kind_titlekey_noyear"" ON ""titlemarks"" (""userid"", ""kind"", ""titlekey"") WHERE ""year"" IS NULL;
        ALTER TABLE ""titlemarks"" ADD COLUMN IF NOT EXISTS ""watchlist"" boolean NOT NULL DEFAULT false;
        ALTER TABLE ""titlemarks"" ADD COLUMN IF NOT EXISTS ""watchlistedutc"" timestamp with time zone NULL;
      ";

  /// <summary>Columns added to <c>titlemarks</c> after it was created; SQLite has no ADD COLUMN IF NOT EXISTS.</summary>
  private static readonly (string Name, string Definition)[] SqliteTitleMarksPatches =
  [
    ("watchlist", "INTEGER NOT NULL DEFAULT 0"),
    ("watchlistedutc", "TEXT NULL"),
  ];

  private static void EnsureTitleMarksSchema(AppDbContext db, ILogger logger)
  {
    var isNpgsql = db.Database.IsNpgsql();
    if (!isNpgsql && !db.Database.IsSqlite()) return;
    try
    {
      db.Database.ExecuteSqlRaw(isNpgsql ? PostgresTitleMarksSql : SqliteTitleMarksSql);
      if (!isNpgsql) PatchSqliteTitleMarks(db);
      logger.LogDebug("Title marks schema ensured");
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to ensure the title marks schema: {Message}", ex.Message);
    }
  }

  private static void PatchSqliteTitleMarks(AppDbContext db)
  {
    var connection = db.Database.GetDbConnection();
    db.Database.OpenConnection();
    try
    {
      foreach (var (name, definition) in SqliteTitleMarksPatches)
      {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('titlemarks') WHERE name = '{name}';";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) continue;
        command.CommandText = $"ALTER TABLE titlemarks ADD COLUMN {name} {definition};";
        command.ExecuteNonQuery();
      }
    }
    finally
    {
      db.Database.CloseConnection();
    }
  }

  /// <summary>
  /// The playlist shares table on SQLite (epic #373, approved by the owner on 19 Nov 2026). A share goes with its
  /// playlist and with the account that accepted it. Two partial unique indexes keep a token hash single and one
  /// accepted share per viewer and playlist; open invitations (no viewer) and accepted ones (no hash) never collide.
  /// </summary>
  public const string SqlitePlaylistSharesSql = @"
        CREATE TABLE IF NOT EXISTS playlistshares (
          id TEXT NOT NULL PRIMARY KEY,
          playlistid TEXT NOT NULL,
          label TEXT NOT NULL,
          role TEXT NOT NULL DEFAULT 'viewer',
          tokenhash TEXT NULL,
          tokenexpiresutc TEXT NULL,
          vieweruserid TEXT NULL,
          createdutc TEXT NOT NULL,
          acceptedutc TEXT NULL,
          lastopenedutc TEXT NULL,
          CONSTRAINT FK_playlistshares_playlists_playlistid FOREIGN KEY (playlistid) REFERENCES playlists (Id) ON DELETE CASCADE,
          CONSTRAINT FK_playlistshares_AspNetUsers_vieweruserid FOREIGN KEY (vieweruserid) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_playlistshares_playlistid ON playlistshares (playlistid);
        CREATE INDEX IF NOT EXISTS IX_playlistshares_vieweruserid ON playlistshares (vieweruserid);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_playlistshares_tokenhash ON playlistshares (tokenhash) WHERE tokenhash IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_playlistshares_playlistid_vieweruserid ON playlistshares (playlistid, vieweruserid) WHERE vieweruserid IS NOT NULL;
      ";

  /// <summary>The same table on PostgreSQL, with quoted identifiers as in <see cref="TryEnsurePostgresIdentity"/>.</summary>
  public const string PostgresPlaylistSharesSql = @"
        CREATE TABLE IF NOT EXISTS ""playlistshares"" (
          ""id"" uuid NOT NULL PRIMARY KEY,
          ""playlistid"" uuid NOT NULL,
          ""label"" varchar(100) NOT NULL,
          ""role"" varchar(16) NOT NULL DEFAULT 'viewer',
          ""tokenhash"" varchar(64) NULL,
          ""tokenexpiresutc"" timestamp with time zone NULL,
          ""vieweruserid"" varchar(450) NULL,
          ""createdutc"" timestamp with time zone NOT NULL,
          ""acceptedutc"" timestamp with time zone NULL,
          ""lastopenedutc"" timestamp with time zone NULL,
          CONSTRAINT ""FK_playlistshares_playlists_playlistid"" FOREIGN KEY (""playlistid"") REFERENCES ""playlists""(""Id"") ON DELETE CASCADE,
          CONSTRAINT ""FK_playlistshares_AspNetUsers_vieweruserid"" FOREIGN KEY (""vieweruserid"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ""IX_playlistshares_playlistid"" ON ""playlistshares"" (""playlistid"");
        CREATE INDEX IF NOT EXISTS ""IX_playlistshares_vieweruserid"" ON ""playlistshares"" (""vieweruserid"");
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_playlistshares_tokenhash"" ON ""playlistshares"" (""tokenhash"") WHERE ""tokenhash"" IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_playlistshares_playlistid_vieweruserid"" ON ""playlistshares"" (""playlistid"", ""vieweruserid"") WHERE ""vieweruserid"" IS NOT NULL;
      ";

  private static void EnsurePlaylistSharesSchema(AppDbContext db, ILogger logger)
  {
    var isNpgsql = db.Database.IsNpgsql();
    if (!isNpgsql && !db.Database.IsSqlite()) return;
    try
    {
      db.Database.ExecuteSqlRaw(isNpgsql ? PostgresPlaylistSharesSql : SqlitePlaylistSharesSql);
      logger.LogDebug("Playlist shares schema ensured");
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to ensure the playlist shares schema: {Message}", ex.Message);
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
          IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'accountstatus') THEN
            ALTER TABLE playlists ADD COLUMN accountstatus varchar(32) NULL;
          END IF;
          IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'accountmaxconnections') THEN
            ALTER TABLE playlists ADD COLUMN accountmaxconnections integer NULL;
          END IF;
          IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'accountactiveconnections') THEN
            ALTER TABLE playlists ADD COLUMN accountactiveconnections integer NULL;
          END IF;
          IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'accountcheckedutc') THEN
            ALTER TABLE playlists ADD COLUMN accountcheckedutc timestamptz NULL;
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
      ["sourcecheckedutc"] = "TEXT NULL",
      ["accountstatus"] = "TEXT NULL",
      ["accountmaxconnections"] = "INTEGER NULL",
      ["accountactiveconnections"] = "INTEGER NULL",
      ["accountcheckedutc"] = "TEXT NULL"
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
