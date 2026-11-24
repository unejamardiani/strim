using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Api.Configuration;
using Api.Data;
using Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>Failed automatic refreshes in a row, counted by the background refresh and shown by the playlist routes.</summary>
public class PlaylistRefreshFailureCountTests
{
  private const string SourceUrl = "http://93.184.215.14/failing-source.m3u";

  [Fact]
  public async Task FailedRefreshes_CountUp_AndARefreshThatWorks_SetsTheCountBackToZero()
  {
    using var factory = new StrimAppFactory();
    var upstreamWorks = false;
    factory.UpstreamResponses[SourceUrl] = () => upstreamWorks
      ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TestPlaylists.Mixed, Encoding.UTF8, "audio/x-mpegurl") }
      : new HttpResponseMessage(HttpStatusCode.BadGateway);
    await WaitForStartupPassAsync(factory);

    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync("refreshcount");
    var created = await (await client.PostAsync("/api/playlists", new { name = "Living room", sourceUrl = SourceUrl }))
      .Content.ReadFromJsonAsync<JsonElement>();
    var id = created.GetProperty("id").GetGuid();
    var enabled = await (await client.SendAsync(HttpMethod.Patch, $"/api/playlists/{id}/auto-refresh", new { enabled = true }))
      .Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(0, enabled.GetProperty("refreshFailureCount").GetInt32());

    await RunPassAsync(factory);
    await RunPassAsync(factory);

    Assert.Equal(2, await ListedCountAsync(client, "/api/playlists", id));
    Assert.Equal(2, await ListedCountAsync(client, "/api/playlists/overview", id));
    var toggled = await (await client.SendAsync(HttpMethod.Patch, $"/api/playlists/{id}/auto-refresh", new { enabled = true }))
      .Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(2, toggled.GetProperty("refreshFailureCount").GetInt32());

    upstreamWorks = true;
    await RunPassAsync(factory);

    Assert.Equal(0, await ListedCountAsync(client, "/api/playlists", id));
    Assert.Equal(0, await ListedCountAsync(client, "/api/playlists/overview", id));
  }

  [Fact]
  public void SqliteSchema_AddsTheColumnAsNotNullWithDefaultZero_OnEveryStart()
  {
    var root = Path.Combine(Path.GetTempPath(), "strim-failcount-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var connectionString = $"Data Source={Path.Combine(root, "strim.db")};Pooling=False";
    try
    {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
      using var provider = services.BuildServiceProvider();

      provider.EnsureStrimSchema();
      Execute(connectionString, "ALTER TABLE playlists DROP COLUMN refreshfailurecount");
      provider.EnsureStrimSchema();
      provider.EnsureStrimSchema();

      Assert.Equal("refreshfailurecount|INTEGER|1|0", Query(connectionString,
        "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('playlists') WHERE name = 'refreshfailurecount'"));
    }
    finally
    {
      try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
  }

  [Fact]
  public void PostgresPatch_AddsTheColumnOnlyWhenMissing()
  {
    Assert.Contains(
      "IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'refreshfailurecount') THEN\n" +
      "            ALTER TABLE playlists ADD COLUMN refreshfailurecount integer NOT NULL DEFAULT 0;\n" +
      "          END IF;",
      Database.PostgresPlaylistColumnsSql.ReplaceLineEndings("\n"));
  }

  private static async Task WaitForStartupPassAsync(StrimAppFactory factory)
  {
    // Starting the host runs the service's first pass; wait for it so it cannot race the passes under test.
    _ = factory.Services;
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!factory.LogEntries.Any(e => e.StartsWith("Refreshing ")) && DateTime.UtcNow < deadline)
    {
      await Task.Delay(20);
    }
  }

  private static Task RunPassAsync(StrimAppFactory factory) =>
    ActivatorUtilities.CreateInstance<PlaylistRefreshService>(factory.Services).RefreshPlaylistsAsync(CancellationToken.None);

  private static async Task<int> ListedCountAsync(ApiClient client, string path, Guid id)
  {
    var items = await client.Http.GetFromJsonAsync<JsonElement>(path);
    return items.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id).GetProperty("refreshFailureCount").GetInt32();
  }

  private static void Execute(string connectionString, string sql)
  {
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
  }

  private static string Query(string connectionString, string sql)
  {
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    using var reader = command.ExecuteReader();
    Assert.True(reader.Read());
    return string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString()));
  }
}
