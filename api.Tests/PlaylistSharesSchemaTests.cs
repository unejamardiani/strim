using Api.Configuration;
using Api.Data;
using Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests;

public sealed class PlaylistSharesSchemaTests : IDisposable
{
  private static readonly string[] Columns =
  {
    "acceptedutc", "createdutc", "id", "label", "lastopenedutc", "playlistid", "role", "tokenexpiresutc", "tokenhash", "vieweruserid",
  };

  private static readonly string[] Indexes =
  {
    "IX_playlistshares_playlistid", "IX_playlistshares_playlistid_vieweruserid", "IX_playlistshares_tokenhash", "IX_playlistshares_vieweruserid",
  };

  private readonly string _root = Path.Combine(Path.GetTempPath(), "strim-shares-" + Guid.NewGuid().ToString("N"));
  private readonly string _dbPath;
  private readonly WarningCollector _warnings = new();
  private readonly ServiceProvider _services;

  public PlaylistSharesSchemaTests()
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
  public void CreatesTableAndIndexes()
  {
    // A fresh database, then one from before this change (playlists and users, no shares table).
    _services.EnsureStrimSchema();
    AssertSchema();
    var playlistId = AddPlaylist();
    Execute("DROP TABLE playlistshares");
    Assert.Empty(ShareIndexes());

    _services.EnsureStrimSchema();

    AssertSchema();
    Assert.Equal(new[] { "1" }, Query("SELECT COUNT(*) FROM playlists"));
    AddShare(playlistId, viewerUserId: null);
    Assert.Equal(new[] { "viewer" }, Query("SELECT role FROM playlistshares"));
    Assert.Empty(_warnings.Messages);
  }

  [Fact]
  public void SecondStart_ChangesNothing()
  {
    _services.EnsureStrimSchema();
    var playlistId = AddPlaylist();
    AddUser("viewer-a");
    AddShare(playlistId, "viewer-a");
    var schema = Query("SELECT type || '|' || name || '|' || sql FROM sqlite_master ORDER BY name");
    var rows = Query("SELECT * FROM playlistshares");

    _services.EnsureStrimSchema();

    Assert.Equal(schema, Query("SELECT type || '|' || name || '|' || sql FROM sqlite_master ORDER BY name"));
    Assert.Equal(rows, Query("SELECT * FROM playlistshares"));
    Assert.Empty(_warnings.Messages);
  }

  [Fact]
  public void DeletingPlaylist_RemovesShares()
  {
    _services.EnsureStrimSchema();
    var kept = AddPlaylist();
    var deleted = AddPlaylist();
    AddUser("viewer-a");
    AddShare(deleted, "viewer-a");
    AddShare(deleted, viewerUserId: null);
    AddShare(kept, viewerUserId: null);

    ExecuteThroughContext($"DELETE FROM playlists WHERE Id = '{deleted.ToString().ToUpperInvariant()}'");

    Assert.Equal(new[] { kept.ToString().ToUpperInvariant() }, Query("SELECT upper(playlistid) FROM playlistshares"));
  }

  [Fact]
  public void DeletingUser_RemovesAcceptedShares()
  {
    _services.EnsureStrimSchema();
    var playlistId = AddPlaylist();
    AddUser("viewer-a");
    AddUser("viewer-b");
    AddShare(playlistId, "viewer-a");
    AddShare(playlistId, "viewer-b");
    AddShare(playlistId, viewerUserId: null);

    ExecuteThroughContext("DELETE FROM AspNetUsers WHERE Id = 'viewer-a'");

    Assert.Equal(new[] { "<null>", "viewer-b" }, Query("SELECT vieweruserid FROM playlistshares ORDER BY vieweruserid"));
  }

  [Fact]
  public void SameViewerTwiceOnOnePlaylist_Refused()
  {
    _services.EnsureStrimSchema();
    var first = AddPlaylist();
    var second = AddPlaylist();
    AddUser("viewer-a");
    AddShare(first, "viewer-a");
    AddShare(second, "viewer-a");
    AddShare(first, viewerUserId: null);
    AddShare(first, viewerUserId: null);

    Assert.Throws<DbUpdateException>(() => AddShare(first, "viewer-a"));
    Assert.Throws<DbUpdateException>(() => AddShare(second, viewerUserId: null, tokenHash: _lastTokenHash));
    Assert.Equal(new[] { "4" }, Query("SELECT COUNT(*) FROM playlistshares"));
  }

  [Fact]
  public void PostgresSql_IsIdempotent_WithQuotedIdentifiers()
  {
    var sql = Database.PostgresPlaylistSharesSql;

    Assert.Contains("CREATE TABLE IF NOT EXISTS \"playlistshares\" (", sql);
    Assert.Contains("\"id\" uuid NOT NULL PRIMARY KEY", sql);
    Assert.Contains("\"playlistid\" uuid NOT NULL", sql);
    Assert.Contains("\"label\" varchar(100) NOT NULL", sql);
    Assert.Contains("\"role\" varchar(16) NOT NULL DEFAULT 'viewer'", sql);
    Assert.Contains("\"tokenhash\" varchar(64) NULL", sql);
    Assert.Contains("\"tokenexpiresutc\" timestamp with time zone NULL", sql);
    Assert.Contains("\"vieweruserid\" varchar(450) NULL", sql);
    Assert.Contains("\"createdutc\" timestamp with time zone NOT NULL", sql);
    Assert.Contains("\"acceptedutc\" timestamp with time zone NULL", sql);
    Assert.Contains("\"lastopenedutc\" timestamp with time zone NULL", sql);
    Assert.Contains("FOREIGN KEY (\"playlistid\") REFERENCES \"playlists\"(\"Id\") ON DELETE CASCADE", sql);
    Assert.Contains("FOREIGN KEY (\"vieweruserid\") REFERENCES \"AspNetUsers\"(\"Id\") ON DELETE CASCADE", sql);
    Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_playlistshares_tokenhash\" ON \"playlistshares\" (\"tokenhash\") WHERE \"tokenhash\" IS NOT NULL;", sql);
    Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_playlistshares_playlistid_vieweruserid\" ON \"playlistshares\" (\"playlistid\", \"vieweruserid\") WHERE \"vieweruserid\" IS NOT NULL;", sql);
    foreach (var index in new[] { "IX_playlistshares_playlistid", "IX_playlistshares_vieweruserid" })
    {
      Assert.Contains($"CREATE INDEX IF NOT EXISTS \"{index}\" ON", sql);
    }
    Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DROP", Database.SqlitePlaylistSharesSql, StringComparison.OrdinalIgnoreCase);
  }

  private void AssertSchema()
  {
    Assert.Equal(Columns, Query("SELECT name FROM pragma_table_info('playlistshares') ORDER BY name"));
    Assert.Equal(Indexes, ShareIndexes());
    Assert.Equal(
      new[] { "AspNetUsers|vieweruserid|CASCADE", "playlists|playlistid|CASCADE" },
      Query("SELECT \"table\" || '|' || \"from\" || '|' || on_delete FROM pragma_foreign_key_list('playlistshares') ORDER BY \"table\""));
    Assert.Equal(
      new[] { "IX_playlistshares_playlistid_vieweruserid|1|1", "IX_playlistshares_tokenhash|1|1" },
      Query("SELECT name || '|' || \"unique\" || '|' || partial FROM pragma_index_list('playlistshares') WHERE \"unique\" = 1 AND origin = 'c' ORDER BY name"));
  }

  private string? _lastTokenHash;

  private Guid AddPlaylist()
  {
    using var scope = _services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var playlist = new Playlist { Name = "Sofa list", ShareCode = Guid.NewGuid().ToString("N") };
    db.Playlists.Add(playlist);
    db.SaveChanges();
    return playlist.Id;
  }

  private void AddShare(Guid playlistId, string? viewerUserId, string? tokenHash = null)
  {
    using var scope = _services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var now = DateTimeOffset.UtcNow;
    var accepted = viewerUserId != null;
    tokenHash ??= accepted ? null : Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    if (tokenHash != null) _lastTokenHash = tokenHash;
    db.PlaylistShares.Add(new PlaylistShare
    {
      Id = Guid.NewGuid(), PlaylistId = playlistId, Label = "Guest", TokenHash = tokenHash,
      TokenExpiresUtc = accepted ? null : now.AddDays(7), ViewerUserId = viewerUserId,
      CreatedUtc = now, AcceptedUtc = accepted ? now : null,
    });
    db.SaveChanges();
  }

  private void AddUser(string id) =>
    Execute($"INSERT INTO AspNetUsers (Id, UserName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('{id}', '{id}', 0, 0, 0, 0, 0)");

  private string[] ShareIndexes() =>
    Query("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'playlistshares' AND name NOT LIKE 'sqlite_%' ORDER BY name");

  // Through the context, so SQLite enforces the foreign keys as the app does.
  private void ExecuteThroughContext(string sql)
  {
    using var scope = _services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRaw(sql);
  }

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
