using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

public class PlaylistEndpointTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static object NewPlaylist(string name = "Living room", IEnumerable<string>? disabled = null) => new
  {
    name,
    sourceUrl = StrimAppFactory.UpstreamUrl,
    sourceName = "provider",
    disabledGroups = disabled ?? new[] { "Germany · Series" },
    totalChannels = 4,
    groupCount = 3,
  };

  [Fact]
  public async Task Anonymous_CannotListPlaylists()
  {
    var response = await factory.CreateClient().GetAsync("/api/playlists");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Create_Read_Update_Toggle_Delete()
  {
    var client = await SignedInAsync("crud");

    Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/playlists", new { name = "" })).StatusCode);

    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();
    Assert.False(string.IsNullOrWhiteSpace(created!.ShareCode));
    Assert.True(created.IsActive);

    var list = await client.Http.GetFromJsonAsync<List<PlaylistDto>>("/api/playlists");
    Assert.Contains(list!, p => p.Id == created.Id);

    var updated = await (await client.SendAsync(HttpMethod.Put, $"/api/playlists/{created.Id}",
      NewPlaylist("Kitchen", Array.Empty<string>()))).Content.ReadFromJsonAsync<PlaylistDto>();
    Assert.Equal("Kitchen", updated!.Name);
    Assert.Empty(updated.DisabledGroups);
    Assert.Equal(created.ShareCode, updated.ShareCode);

    var toggled = await (await client.SendAsync(HttpMethod.Patch, $"/api/playlists/{created.Id}/toggle-active")).Content.ReadFromJsonAsync<PlaylistDto>();
    Assert.False(toggled!.IsActive);

    var regenerated = await (await client.PostAsync($"/api/playlists/{created.Id}/regenerate-code")).Content.ReadFromJsonAsync<PlaylistDto>();
    Assert.NotEqual(created.ShareCode, regenerated!.ShareCode);

    var refresh = await client.SendAsync(HttpMethod.Patch, $"/api/playlists/{created.Id}/auto-refresh", new { enabled = true });
    refresh.EnsureSuccessStatusCode();
    var fetched = await client.Http.GetFromJsonAsync<PlaylistDto>($"/api/playlists/{created.Id}");
    Assert.True(fetched!.AutoRefreshEnabled);

    Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Delete, $"/api/playlists/{created.Id}")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await client.Http.GetAsync($"/api/playlists/{created.Id}")).StatusCode);
  }

  [Fact]
  public async Task Mutations_WithoutCsrfToken_AreRejected()
  {
    var client = await SignedInAsync("csrf");
    var response = await client.PostAsync("/api/playlists", NewPlaylist(), withCsrf: false);
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  }

  [Fact]
  public async Task Playlists_AreInvisibleToOtherUsers()
  {
    var owner = await SignedInAsync("owner");
    var created = await (await owner.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();

    var other = await SignedInAsync("intruder");
    Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/playlists/{created!.Id}")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(HttpMethod.Delete, $"/api/playlists/{created.Id}")).StatusCode);
    Assert.Empty((await other.Http.GetFromJsonAsync<List<PlaylistDto>>("/api/playlists"))!);
  }

  [Fact]
  public async Task HiddenGroups_AcceptTheGroupCapButNotMore()
  {
    var client = await SignedInAsync("limits");
    var atCap = Enumerable.Range(0, 10_000).Select(i => $"Group {i}").ToArray();
    Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/playlists", NewPlaylist("cap", atCap))).StatusCode);

    var overCap = atCap.Append("one more").ToArray();
    Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/playlists", NewPlaylist("over", overCap))).StatusCode);
  }

  private static object WithAccount(string name, string status, int max, int active, DateTimeOffset checkedUtc) => new
  {
    name,
    sourceUrl = StrimAppFactory.UpstreamUrl,
    disabledGroups = Array.Empty<string>(),
    accountStatus = status,
    accountMaxConnections = max,
    accountActiveConnections = active,
    accountCheckedUtc = checkedUtc,
  };

  [Fact]
  public async Task AccountFields_RoundTrip_AreKeptWhenOmitted_AndReplacedWhenSent()
  {
    var client = await SignedInAsync("account");
    var checkedAt = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);

    var created = await (await client.PostAsync("/api/playlists", WithAccount("Sofa", "Active", 2, 1, checkedAt)))
      .Content.ReadFromJsonAsync<PlaylistDto>();
    var read = await client.Http.GetFromJsonAsync<PlaylistDto>($"/api/playlists/{created!.Id}");
    Assert.Equal("Active", read!.AccountStatus);
    Assert.Equal(2, read.AccountMaxConnections);
    Assert.Equal(1, read.AccountActiveConnections);
    Assert.Equal(checkedAt, read.AccountCheckedUtc);

    (await client.SendAsync(HttpMethod.Put, $"/api/playlists/{created.Id}", NewPlaylist("Sofa"))).EnsureSuccessStatusCode();
    var kept = await client.Http.GetFromJsonAsync<PlaylistDto>($"/api/playlists/{created.Id}");
    Assert.Equal("Active", kept!.AccountStatus);
    Assert.Equal(2, kept.AccountMaxConnections);
    Assert.Equal(1, kept.AccountActiveConnections);
    Assert.Equal(checkedAt, kept.AccountCheckedUtc);

    var laterCheck = checkedAt.AddDays(3);
    (await client.SendAsync(HttpMethod.Put, $"/api/playlists/{created.Id}", WithAccount("Sofa", "Expired", 4, 0, laterCheck)))
      .EnsureSuccessStatusCode();
    var replaced = await client.Http.GetFromJsonAsync<PlaylistDto>($"/api/playlists/{created.Id}");
    Assert.Equal("Expired", replaced!.AccountStatus);
    Assert.Equal(4, replaced.AccountMaxConnections);
    Assert.Equal(0, replaced.AccountActiveConnections);
    Assert.Equal(laterCheck, replaced.AccountCheckedUtc);
  }

  [Fact]
  public async Task AccountFields_AreNull_WhenNotSaved()
  {
    var client = await SignedInAsync("noaccount");
    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();

    var read = await client.Http.GetFromJsonAsync<PlaylistDto>($"/api/playlists/{created!.Id}");
    Assert.Null(read!.AccountStatus);
    Assert.Null(read.AccountMaxConnections);
    Assert.Null(read.AccountActiveConnections);
    Assert.Null(read.AccountCheckedUtc);
  }

  [Fact]
  public async Task AccountStatus_LongerThanTheColumn_IsRejected()
  {
    var client = await SignedInAsync("longstatus");
    var response = await client.PostAsync("/api/playlists", WithAccount("Long", new string('x', 33), 1, 0, DateTimeOffset.UtcNow));
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  }

  [Fact]
  public async Task Startup_AddsAccountColumns_ToAnExistingSqliteDatabase_AndOldRowsStillLoad()
  {
    var root = Path.Combine(Path.GetTempPath(), $"strim-schema-{Guid.NewGuid():N}");
    var dbPath = Path.Combine(root, "strim.db");
    var accountColumns = new[] { "accountstatus", "accountmaxconnections", "accountactiveconnections", "accountcheckedutc" };
    var id = Guid.NewGuid();
    try
    {
      // A database written by the current app, then stripped back to the schema before these columns existed.
      using (var first = HostOn(dbPath))
      using (var scope = first.Services.CreateScope())
      {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Playlists.Add(new Playlist { Id = id, Name = "Before upgrade", ShareCode = "legacy-code", TotalChannels = 7 });
        await db.SaveChangesAsync();
      }
      SqliteConnection.ClearAllPools();
      using (var connection = new SqliteConnection($"Data Source={dbPath}"))
      {
        connection.Open();
        foreach (var column in accountColumns)
        {
          using var drop = connection.CreateCommand();
          drop.CommandText = $"ALTER TABLE playlists DROP COLUMN {column};";
          drop.ExecuteNonQuery();
        }
        Assert.Empty(accountColumns.Intersect(PlaylistColumns(connection)));
      }
      SqliteConnection.ClearAllPools();

      using (var second = HostOn(dbPath))
      using (var scope = second.Services.CreateScope())
      {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Playlists.SingleAsync(p => p.Id == id);
        Assert.Equal("Before upgrade", row.Name);
        Assert.Equal(7, row.TotalChannels);
        Assert.Null(row.AccountStatus);
        Assert.Null(row.AccountMaxConnections);
        Assert.Null(row.AccountActiveConnections);
        Assert.Null(row.AccountCheckedUtc);
      }
      SqliteConnection.ClearAllPools();
      using (var connection = new SqliteConnection($"Data Source={dbPath}"))
      {
        connection.Open();
        Assert.Subset(PlaylistColumns(connection), accountColumns.ToHashSet());
      }
    }
    finally
    {
      SqliteConnection.ClearAllPools();
      try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
  }

  private static WebApplicationFactory<Program> HostOn(string dbPath)
  {
    var host = new StrimAppFactory().WithWebHostBuilder(b => b
      .UseSetting("SQLITE_PATH", dbPath)
      .UseSetting("PlaylistCache:Directory", Path.Combine(Path.GetDirectoryName(dbPath)!, "cache")));
    _ = host.Services; // starts the app, which runs the schema patch
    return host;
  }

  private static HashSet<string> PlaylistColumns(SqliteConnection connection)
  {
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "PRAGMA table_info('playlists');";
    using var reader = cmd.ExecuteReader();
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (reader.Read()) columns.Add(reader.GetString(1));
    return columns;
  }
}

public sealed record PlaylistDto(
  Guid Id,
  string Name,
  string? SourceUrl,
  List<string> DisabledGroups,
  string? ShareCode,
  bool IsActive,
  bool AutoRefreshEnabled,
  long ViewCount,
  string? AccountStatus = null,
  int? AccountMaxConnections = null,
  int? AccountActiveConnections = null,
  DateTimeOffset? AccountCheckedUtc = null);
