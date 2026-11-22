using Api.Configuration;
using Api.Data;
using Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests;

public sealed class PlaylistWatchlistGroupSchemaTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "strim-wlgroup-" + Guid.NewGuid().ToString("N"));
  private readonly string _dbPath;
  private readonly ServiceProvider _services;

  public PlaylistWatchlistGroupSchemaTests()
  {
    Directory.CreateDirectory(_root);
    _dbPath = Path.Combine(_root, "strim.db");
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath};Pooling=False"));
    _services = services.BuildServiceProvider();
  }

  public void Dispose()
  {
    _services.Dispose();
    try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
  }

  [Fact]
  public void Schema_HasTheWatchlistGroupColumn()
  {
    _services.EnsureStrimSchema();

    Assert.Equal(new[] { "watchlistgroup|INTEGER|1" }, Query(
      "SELECT name, type, \"notnull\" FROM pragma_table_info('playlists') WHERE name = 'watchlistgroup'"));
  }

  [Fact]
  public void Schema_AddsTheColumnToAnExistingTable_AndExistingRowsAreOff()
  {
    var id = Guid.NewGuid();
    _services.EnsureStrimSchema();
    using (var scope = _services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      db.Playlists.Add(new Playlist { Id = id, Name = "Sofa list", ShareCode = "code1", WatchlistGroup = true });
      db.SaveChanges();
    }
    Execute("ALTER TABLE playlists DROP COLUMN watchlistgroup");
    Assert.Empty(Query("SELECT name FROM pragma_table_info('playlists') WHERE name = 'watchlistgroup'"));

    _services.EnsureStrimSchema();
    _services.EnsureStrimSchema();

    Assert.Equal(new[] { "1" }, Query("SELECT COUNT(*) FROM pragma_table_info('playlists') WHERE name = 'watchlistgroup'"));
    using (var scope = _services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      Assert.False(db.Playlists.Single(p => p.Id == id).WatchlistGroup);
    }
  }

  [Fact]
  public void PostgresPatch_AddsTheColumnOnlyWhenMissing()
  {
    Assert.Contains(
      "IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'watchlistgroup') THEN\n" +
      "            ALTER TABLE playlists ADD COLUMN watchlistgroup boolean NOT NULL DEFAULT false;\n" +
      "          END IF;",
      Database.PostgresPlaylistColumnsSql.ReplaceLineEndings("\n"));
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
}
