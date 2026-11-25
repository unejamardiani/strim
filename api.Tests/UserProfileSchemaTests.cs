using Api.Configuration;
using Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests;

/// <summary>The profile columns on AspNetUsers (#579).</summary>
public sealed class UserProfileSchemaTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "strim-profile-" + Guid.NewGuid().ToString("N"));
  private readonly string _dbPath;
  private readonly TitleMarksSchemaTests.WarningCollector _warnings = new();
  private readonly ServiceProvider _services;

  public UserProfileSchemaTests()
  {
    Directory.CreateDirectory(_root);
    _dbPath = Path.Combine(_root, "strim.db");
    var services = new ServiceCollection();
    services.AddLogging(logging => logging.AddProvider(_warnings));
    services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath};Pooling=False"));
    _services = services.BuildServiceProvider();
  }

  public void Dispose()
  {
    _services.Dispose();
    try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
  }

  [Fact]
  public void FreshDatabase_HasNullableProfileColumns()
  {
    _services.EnsureStrimSchema();

    Assert.Equal(new[] { "avatar|TEXT|0", "display_name|TEXT|0", "theme|TEXT|0" }, ProfileColumns());
    Assert.Empty(_warnings.Messages);
  }

  [Fact]
  public void ExistingUsersTable_GetsTheColumns_KeepsItsUsers_AndRunsTwiceWithoutWarning()
  {
    // AspNetUsers as it was before #579, with one account in it.
    _services.EnsureStrimSchema();
    Execute("INSERT INTO AspNetUsers (Id, UserName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('user-a', 'user-a', 0, 0, 0, 0, 0)");
    Execute("ALTER TABLE AspNetUsers DROP COLUMN display_name");
    Execute("ALTER TABLE AspNetUsers DROP COLUMN avatar");
    Execute("ALTER TABLE AspNetUsers DROP COLUMN theme");
    Assert.Empty(ProfileColumns());

    _services.EnsureStrimSchema();
    _services.EnsureStrimSchema();

    Assert.Equal(new[] { "avatar|TEXT|0", "display_name|TEXT|0", "theme|TEXT|0" }, ProfileColumns());
    Assert.Equal(new[] { "user-a|<null>|<null>|<null>" }, Query("SELECT UserName, display_name, avatar, theme FROM AspNetUsers"));
    Assert.Empty(_warnings.Messages);
  }

  [Fact]
  public void PostgresSql_AddsEachColumnIdempotently()
  {
    var sql = Database.PostgresUserProfileColumnsSql;

    Assert.Contains("ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"display_name\" varchar(40) NULL;", sql);
    Assert.Contains("ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"avatar\" varchar(32) NULL;", sql);
    Assert.Contains("ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"theme\" varchar(16) NULL;", sql);
    Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("RENAME", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void PostgresModel_UsesTheSameColumnsAsThePatch()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost").Options;
    using var db = new AppDbContext(options);
    var script = db.Database.GenerateCreateScript();

    Assert.Contains("display_name character varying(40),", script);
    Assert.Contains("avatar character varying(32),", script);
    Assert.Contains("theme character varying(16),", script);
  }

  private string[] ProfileColumns() => Query(
    "SELECT name, type, \"notnull\" FROM pragma_table_info('AspNetUsers') WHERE name IN ('display_name', 'avatar', 'theme') ORDER BY name");

  private void Execute(string sql)
  {
    using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
  }

  private string[] Query(string sql)
  {
    using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    using var reader = command.ExecuteReader();
    var rows = new List<string>();
    while (reader.Read())
    {
      rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString())));
    }
    return rows.ToArray();
  }
}
