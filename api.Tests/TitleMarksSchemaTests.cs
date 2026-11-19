using Api.Configuration;
using Api.Data;
using Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests;

public sealed class TitleMarksSchemaTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "strim-marks-" + Guid.NewGuid().ToString("N"));
  private readonly string _dbPath;
  private readonly WarningCollector _warnings = new();
  private readonly ServiceProvider _services;

  public TitleMarksSchemaTests()
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
  public void FreshDatabase_GetsTableAndIndexes()
  {
    _services.EnsureStrimSchema();

    Assert.Equal(
      new[] { "createdutc", "displaytitle", "filmdbid", "id", "kind", "rating", "seen", "titlekey", "updatedutc", "userid", "year" },
      Query("SELECT name FROM pragma_table_info('titlemarks') ORDER BY name"));
    Assert.Contains("IX_titlemarks_userid", TitleMarkIndexes());
    Assert.Contains("IX_titlemarks_userid_kind_titlekey_year", TitleMarkIndexes());
    Assert.Contains("IX_titlemarks_userid_kind_titlekey_noyear", TitleMarkIndexes());
    Assert.Equal(new[] { "AspNetUsers|CASCADE" }, Query("SELECT \"table\" || '|' || on_delete FROM pragma_foreign_key_list('titlemarks')"));
    Assert.Empty(_warnings.Messages);
  }

  [Fact]
  public void ExistingDatabaseWithoutMarks_GetsTable_AndKeepsPlaylists()
  {
    // A database as it was before this change: playlists and the Identity tables, no marks table.
    _services.EnsureStrimSchema();
    Execute("DROP TABLE titlemarks");
    using (var scope = _services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      db.Playlists.Add(new Playlist { Name = "Sofa list", DisabledGroups = new List<string> { "News" }, TotalChannels = 12, ShareCode = "code1" });
      db.SaveChanges();
    }
    var before = Query("SELECT * FROM playlists");
    Assert.Empty(TitleMarkIndexes());

    _services.EnsureStrimSchema();

    Assert.Equal(before, Query("SELECT * FROM playlists"));
    Assert.Contains("IX_titlemarks_userid", TitleMarkIndexes());
    Assert.Contains("IX_titlemarks_userid_kind_titlekey_year", TitleMarkIndexes());
    Assert.Contains("IX_titlemarks_userid_kind_titlekey_noyear", TitleMarkIndexes());
    Assert.Empty(_warnings.Messages);
  }

  [Fact]
  public void RunningTwice_RaisesNoErrorAndLogsNoWarning()
  {
    _services.EnsureStrimSchema();
    _services.EnsureStrimSchema();

    Assert.Empty(_warnings.Messages);
  }

  [Fact]
  public void SameUserKindKeyAndYear_IsRejected_OtherYearOrNullYearIsAccepted()
  {
    _services.EnsureStrimSchema();
    AddUser("user-a");

    AddMark("user-a", "film", "film|1999|the matrix", 1999);
    AddMark("user-a", "film", "film|1999|the matrix", 2003);
    AddMark("user-a", "film", "film|1999|the matrix", null);
    AddMark("user-a", "series", "film|1999|the matrix", 1999);

    Assert.Throws<DbUpdateException>(() => AddMark("user-a", "film", "film|1999|the matrix", 1999));
    Assert.Throws<DbUpdateException>(() => AddMark("user-a", "film", "film|1999|the matrix", null));
    Assert.Equal(new[] { "4" }, Query("SELECT COUNT(*) FROM titlemarks"));
  }

  [Fact]
  public void NullYear_DoesNotCollideWithYearZero()
  {
    _services.EnsureStrimSchema();
    AddUser("user-a");

    AddMark("user-a", "film", "film||untitled", null);
    AddMark("user-a", "film", "film||untitled", 0);

    Assert.Throws<DbUpdateException>(() => AddMark("user-a", "film", "film||untitled", 0));
    Assert.Throws<DbUpdateException>(() => AddMark("user-a", "film", "film||untitled", null));
    Assert.Equal(new[] { "2" }, Query("SELECT COUNT(*) FROM titlemarks"));
  }

  [Fact]
  public void DeletingUser_RemovesOnlyThatUsersMarks()
  {
    _services.EnsureStrimSchema();
    AddUser("user-a");
    AddUser("user-b");
    AddMark("user-a", "film", "film|1999|the matrix", 1999);
    AddMark("user-a", "series", "series||dark", null);
    AddMark("user-b", "film", "film|1999|the matrix", 1999);

    using (var scope = _services.CreateScope())
    {
      scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
        .ExecuteSqlRaw("DELETE FROM AspNetUsers WHERE Id = 'user-a'");
    }

    Assert.Equal(new[] { "user-b" }, Query("SELECT userid FROM titlemarks"));
  }

  [Fact]
  public void PostgresSql_IsIdempotent_WithQuotedIdentifiers()
  {
    var sql = Database.PostgresTitleMarksSql;

    Assert.Contains("CREATE TABLE IF NOT EXISTS \"titlemarks\" (", sql);
    foreach (var column in new[] { "id", "userid", "kind", "titlekey", "year", "displaytitle", "seen", "rating", "filmdbid", "createdutc", "updatedutc" })
    {
      Assert.Contains($"\"{column}\" ", sql);
    }
    Assert.Contains("\"year\" integer NULL", sql);
    Assert.Contains("\"filmdbid\" varchar(64) NULL", sql);
    Assert.Contains("CONSTRAINT \"FK_titlemarks_AspNetUsers_userid\" FOREIGN KEY (\"userid\") REFERENCES \"AspNetUsers\"(\"Id\") ON DELETE CASCADE", sql);
    Assert.Contains("CREATE INDEX IF NOT EXISTS \"IX_titlemarks_userid\" ON \"titlemarks\" (\"userid\");", sql);
    Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_titlemarks_userid_kind_titlekey_year\" ON \"titlemarks\" (\"userid\", \"kind\", \"titlekey\", \"year\") WHERE \"year\" IS NOT NULL;", sql);
    Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_titlemarks_userid_kind_titlekey_noyear\" ON \"titlemarks\" (\"userid\", \"kind\", \"titlekey\") WHERE \"year\" IS NULL;", sql);
    Assert.DoesNotContain("COALESCE", sql);
    Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("RENAME", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void NoEfMigrationExists()
  {
    Assert.DoesNotContain(typeof(AppDbContext).Assembly.GetTypes(), t => typeof(Migration).IsAssignableFrom(t));
    Assert.DoesNotContain("DROP", Database.SqliteTitleMarksSql, StringComparison.OrdinalIgnoreCase);
  }

  private void AddUser(string id) =>
    Execute($"INSERT INTO AspNetUsers (Id, UserName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('{id}', '{id}', 0, 0, 0, 0, 0)");

  private void AddMark(string userId, string kind, string titleKey, int? year)
  {
    using var scope = _services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var now = DateTimeOffset.UtcNow;
    db.TitleMarks.Add(new TitleMark
    {
      Id = Guid.NewGuid(), UserId = userId, Kind = kind, TitleKey = titleKey, Year = year,
      DisplayTitle = "The Matrix", Seen = true, Rating = 1, CreatedUtc = now, UpdatedUtc = now,
    });
    db.SaveChanges();
  }

  private string[] TitleMarkIndexes() =>
    Query("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'titlemarks' ORDER BY name");

  private void Execute(string sql)
  {
    using var connection = Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
  }

  private string[] Query(string sql)
  {
    using var connection = Open();
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

  private SqliteConnection Open()
  {
    var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
    connection.Open();
    return connection;
  }

  private sealed class WarningCollector : ILoggerProvider, ILogger
  {
    public List<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      if (logLevel >= LogLevel.Warning) lock (Messages) Messages.Add(formatter(state, exception));
    }
    public void Dispose() { }
  }
}
