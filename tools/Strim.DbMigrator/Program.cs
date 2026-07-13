using System.Data;
using System.Globalization;
using System.Text.Json;
using Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

try
{
  await SqliteToPostgresMigrator.RunAsync();
  return 0;
}
catch (Exception ex)
{
  Console.Error.WriteLine($"Migration failed: {ex.Message}");
  return 1;
}

internal static class SqliteToPostgresMigrator
{
  private static readonly IReadOnlyList<TableMapping> Tables =
  [
    new("AspNetRoles",
    [
      Text("Id"), Text("Name"), Text("NormalizedName"), Text("ConcurrencyStamp")
    ]),
    new("AspNetUsers",
    [
      Text("Id"), Text("UserName"), Text("NormalizedUserName"), Text("Email"),
      Text("NormalizedEmail"), Bool("EmailConfirmed"), Text("PasswordHash"),
      Text("SecurityStamp"), Text("ConcurrencyStamp"), Text("PhoneNumber"),
      Bool("PhoneNumberConfirmed"), Bool("TwoFactorEnabled"), Timestamp("LockoutEnd"),
      Bool("LockoutEnabled"), Int("AccessFailedCount")
    ]),
    new("playlists",
    [
      Uuid("Id"), Text("Name"), Text("SourceUrl"), Text("SourceName"), Json("DisabledGroups"),
      Int("totalchannels"), Int("groupcount"), Timestamp("expirationutc"), Text("sharecode"),
      Timestamp("CreatedAt"), Timestamp("UpdatedAt"), Text("ownerid"), Bool("isactive"),
      Long("viewcount"), Timestamp("lastviewedutc"), Bool("autorefreshenabled"),
      Timestamp("lastrefreshedutc")
    ]),
    new("AspNetRoleClaims",
    [
      Int("Id"), Text("RoleId"), Text("ClaimType"), Text("ClaimValue")
    ]),
    new("AspNetUserClaims",
    [
      Int("Id"), Text("UserId"), Text("ClaimType"), Text("ClaimValue")
    ]),
    new("AspNetUserLogins",
    [
      Text("LoginProvider"), Text("ProviderKey"), Text("ProviderDisplayName"), Text("UserId")
    ]),
    new("AspNetUserRoles",
    [
      Text("UserId"), Text("RoleId")
    ]),
    new("AspNetUserTokens",
    [
      Text("UserId"), Text("LoginProvider"), Text("Name"), Text("Value")
    ])
  ];

  public static async Task RunAsync()
  {
    var sourcePath = RequireEnvironmentVariable("SQLITE_MIGRATION_SOURCE");
    var postgresConnectionString = RequireEnvironmentVariable("POSTGRES_CONNECTION");
    var expectedDatabase = Environment.GetEnvironmentVariable("MIGRATION_EXPECTED_DATABASE") ?? "strim";

    if (!File.Exists(sourcePath))
    {
      throw new FileNotFoundException("SQLite source file does not exist.", sourcePath);
    }

    var sqliteConnectionString = new SqliteConnectionStringBuilder
    {
      DataSource = Path.GetFullPath(sourcePath),
      Mode = SqliteOpenMode.ReadOnly,
      Cache = SqliteCacheMode.Private
    }.ConnectionString;

    var postgresBuilder = new NpgsqlConnectionStringBuilder(postgresConnectionString);
    if (!string.Equals(postgresBuilder.Database, expectedDatabase, StringComparison.Ordinal))
    {
      throw new InvalidOperationException(
        $"Target database must be '{expectedDatabase}', but the connection string selects '{postgresBuilder.Database}'.");
    }

    await using var sqlite = new SqliteConnection(sqliteConnectionString);
    await sqlite.OpenAsync();
    await VerifySqliteIntegrityAsync(sqlite);
    await VerifySourceSchemaAsync(sqlite);

    var options = new DbContextOptionsBuilder<AppDbContext>()
      .UseNpgsql(postgresConnectionString)
      .Options;
    await using (var schemaContext = new AppDbContext(options))
    {
      await schemaContext.Database.EnsureCreatedAsync();
    }

    await using var postgres = new NpgsqlConnection(postgresConnectionString);
    await postgres.OpenAsync();
    await VerifyTargetDatabaseAsync(postgres, expectedDatabase);
    await VerifyTargetSchemaAsync(postgres);

    await using var transaction = await postgres.BeginTransactionAsync(IsolationLevel.Serializable);
    await AcquireMigrationLockAsync(postgres, transaction);
    await RequireEmptyTargetAsync(postgres, transaction);

    var migratedCounts = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var table in Tables)
    {
      var count = await CopyTableAsync(sqlite, postgres, transaction, table);
      migratedCounts[table.Name] = count;
      Console.WriteLine($"Migrated {table.Name}: {count} row(s)");
    }

    await ResetIdentitySequencesAsync(postgres, transaction);
    await VerifyTargetCountsAsync(postgres, transaction, migratedCounts);
    await transaction.CommitAsync();

    Console.WriteLine("SQLite to PostgreSQL migration completed and verified.");
  }

  private static async Task VerifySqliteIntegrityAsync(SqliteConnection connection)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA integrity_check;";
    var result = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException($"SQLite integrity check failed: {result ?? "no result"}");
    }
  }

  private static async Task VerifySourceSchemaAsync(SqliteConnection connection)
  {
    foreach (var table in Tables)
    {
      var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      await using var command = connection.CreateCommand();
      command.CommandText = $"PRAGMA table_info({QuoteSqliteIdentifier(table.Name)});";
      await using var reader = await command.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        existingColumns.Add(reader.GetString(1));
      }

      var missing = table.Columns
        .Select(column => column.Name)
        .Where(column => !existingColumns.Contains(column))
        .ToArray();
      if (missing.Length > 0)
      {
        throw new InvalidOperationException(
          $"SQLite table '{table.Name}' is missing required columns: {string.Join(", ", missing)}");
      }
    }
  }

  private static async Task VerifyTargetDatabaseAsync(NpgsqlConnection connection, string expectedDatabase)
  {
    await using var command = new NpgsqlCommand("SELECT current_database();", connection);
    var actualDatabase = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.Ordinal))
    {
      throw new InvalidOperationException(
        $"Connected to database '{actualDatabase}', expected '{expectedDatabase}'.");
    }
  }

  private static async Task VerifyTargetSchemaAsync(NpgsqlConnection connection)
  {
    foreach (var table in Tables)
    {
      await using var command = new NpgsqlCommand(
        "SELECT to_regclass(@qualified_name) IS NOT NULL;", connection);
      command.Parameters.AddWithValue("qualified_name", $"public.{QuotePostgresIdentifier(table.Name)}");
      var exists = (bool)(await command.ExecuteScalarAsync() ?? false);
      if (!exists)
      {
        throw new InvalidOperationException($"PostgreSQL target table is missing: {table.Name}");
      }
    }
  }

  private static async Task AcquireMigrationLockAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction)
  {
    await using var command = new NpgsqlCommand(
      "SELECT pg_advisory_xact_lock(hashtext('strim-sqlite-migration'));", connection, transaction);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task RequireEmptyTargetAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction)
  {
    foreach (var table in Tables)
    {
      await using var command = new NpgsqlCommand(
        $"SELECT COUNT(*) FROM {QuotePostgresIdentifier(table.Name)};", connection, transaction);
      var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
      if (count != 0)
      {
        throw new InvalidOperationException(
          $"PostgreSQL target is not empty: {table.Name} contains {count} row(s). No data was changed.");
      }
    }
  }

  private static async Task<long> CopyTableAsync(
    SqliteConnection sqlite,
    NpgsqlConnection postgres,
    NpgsqlTransaction transaction,
    TableMapping table)
  {
    var columns = string.Join(", ", table.Columns.Select(column => QuoteSqliteIdentifier(column.Name)));
    await using var select = sqlite.CreateCommand();
    select.CommandText = $"SELECT {columns} FROM {QuoteSqliteIdentifier(table.Name)};";

    await using var reader = await select.ExecuteReaderAsync();
    long count = 0;
    while (await reader.ReadAsync())
    {
      await using var insert = new NpgsqlCommand
      {
        Connection = postgres,
        Transaction = transaction,
        CommandText = BuildInsertSql(table)
      };

      for (var index = 0; index < table.Columns.Count; index++)
      {
        var column = table.Columns[index];
        var rawValue = reader.IsDBNull(index) ? null : reader.GetValue(index);
        insert.Parameters.Add(new NpgsqlParameter($"p{index}", column.PostgresType)
        {
          Value = rawValue is null ? DBNull.Value : column.Convert(rawValue)
        });
      }

      await insert.ExecuteNonQueryAsync();
      count++;
    }

    return count;
  }

  private static string BuildInsertSql(TableMapping table)
  {
    var columns = string.Join(", ", table.Columns.Select(column => QuotePostgresIdentifier(column.Name)));
    var parameters = string.Join(", ", table.Columns.Select((_, index) => $"@p{index}"));
    return $"INSERT INTO {QuotePostgresIdentifier(table.Name)} ({columns}) VALUES ({parameters});";
  }

  private static async Task ResetIdentitySequencesAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction)
  {
    foreach (var tableName in new[] { "AspNetRoleClaims", "AspNetUserClaims" })
    {
      var quotedTable = QuotePostgresIdentifier(tableName);
      var sql = $"""
        SELECT setval(
          pg_get_serial_sequence('{quotedTable}', 'Id'),
          COALESCE((SELECT MAX("Id") FROM {quotedTable}), 1),
          EXISTS (SELECT 1 FROM {quotedTable})
        );
        """;
      await using var command = new NpgsqlCommand(sql, connection, transaction);
      await command.ExecuteNonQueryAsync();
    }
  }

  private static async Task VerifyTargetCountsAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    IReadOnlyDictionary<string, long> expectedCounts)
  {
    foreach (var (tableName, expectedCount) in expectedCounts)
    {
      await using var command = new NpgsqlCommand(
        $"SELECT COUNT(*) FROM {QuotePostgresIdentifier(tableName)};", connection, transaction);
      var actualCount = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
      if (actualCount != expectedCount)
      {
        throw new InvalidOperationException(
          $"Verification failed for {tableName}: expected {expectedCount}, found {actualCount}.");
      }
    }
  }

  private static string RequireEnvironmentVariable(string name)
  {
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value)
      ? throw new InvalidOperationException($"Environment variable {name} is required.")
      : value;
  }

  private static ColumnMapping Text(string name) =>
    new(name, NpgsqlDbType.Text, value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);

  private static ColumnMapping Bool(string name) =>
    new(name, NpgsqlDbType.Boolean, value => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0);

  private static ColumnMapping Int(string name) =>
    new(name, NpgsqlDbType.Integer, value => Convert.ToInt32(value, CultureInfo.InvariantCulture));

  private static ColumnMapping Long(string name) =>
    new(name, NpgsqlDbType.Bigint, value => Convert.ToInt64(value, CultureInfo.InvariantCulture));

  private static ColumnMapping Uuid(string name) =>
    new(name, NpgsqlDbType.Uuid, value => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!));

  private static ColumnMapping Json(string name) =>
    new(name, NpgsqlDbType.Jsonb, value => NormalizeJson(Convert.ToString(value, CultureInfo.InvariantCulture)));

  private static ColumnMapping Timestamp(string name) =>
    new(name, NpgsqlDbType.TimestampTz, value => ParseTimestamp(value, name));

  private static object ParseTimestamp(object value, string columnName)
  {
    var text = Convert.ToString(value, CultureInfo.InvariantCulture);
    if (!DateTimeOffset.TryParse(
      text,
      CultureInfo.InvariantCulture,
      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
      out var timestamp))
    {
      throw new FormatException($"Invalid timestamp in column '{columnName}'.");
    }

    return timestamp;
  }

  private static object NormalizeJson(string? value)
  {
    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "[]" : value);
    return document.RootElement.GetRawText();
  }

  private static string QuoteSqliteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

  private static string QuotePostgresIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

  private sealed record TableMapping(string Name, IReadOnlyList<ColumnMapping> Columns);

  private sealed record ColumnMapping(
    string Name,
    NpgsqlDbType PostgresType,
    Func<object, object> Convert);
}
