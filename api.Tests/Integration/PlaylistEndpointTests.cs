using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
  public async Task Create_IgnoresClientShareCode()
  {
    var client = await SignedInAsync("sharecode-create");

    var response = await client.PostAsync("/api/playlists", new
    {
      name = "Living room",
      sourceUrl = StrimAppFactory.UpstreamUrl,
      disabledGroups = Array.Empty<string>(),
      shareCode = "a",
    });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var created = await response.Content.ReadFromJsonAsync<PlaylistDto>();

    Assert.NotEqual("a", created!.ShareCode);
    Assert.Equal(43, created.ShareCode!.Length);
  }

  [Fact]
  public async Task Update_IgnoresClientShareCode_AndRegeneratedCodeRetiresTheOldOne()
  {
    var client = await SignedInAsync("sharecode-update");
    var anonymous = factory.CreateClient();
    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();
    var oldCode = created!.ShareCode;
    Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/playlists/{created.Id}/share/{oldCode}")).StatusCode);

    var regenerated = await (await client.PostAsync($"/api/playlists/{created.Id}/regenerate-code")).Content.ReadFromJsonAsync<PlaylistDto>();
    var currentCode = regenerated!.ShareCode;
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{created.Id}/share/{oldCode}")).StatusCode);

    var put = await client.SendAsync(HttpMethod.Put, $"/api/playlists/{created.Id}", new
    {
      name = "Living room",
      sourceUrl = StrimAppFactory.UpstreamUrl,
      disabledGroups = new[] { "Germany · Series" },
      shareCode = oldCode,
    });
    Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    Assert.Equal(currentCode, (await put.Content.ReadFromJsonAsync<PlaylistDto>())!.ShareCode);

    var fetched = await client.Http.GetFromJsonAsync<PlaylistDto>($"/api/playlists/{created.Id}");
    Assert.Equal(currentCode, fetched!.ShareCode);
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{created.Id}/share/{oldCode}")).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/playlists/{created.Id}/share/{currentCode}")).StatusCode);
  }

  [Fact]
  public async Task Playlist_CarriesWatchlistGroup_OffByDefault()
  {
    var client = await SignedInAsync("wlgroup-default");

    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<JsonElement>();
    Assert.False(created.GetProperty("watchlistGroup").GetBoolean());

    var fetched = await client.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{created.GetProperty("id").GetGuid()}");
    Assert.False(fetched.GetProperty("watchlistGroup").GetBoolean());
  }

  [Fact]
  public async Task Put_LeavesWatchlistGroupAsStored()
  {
    var client = await SignedInAsync("wlgroup-put");
    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();
    using (var scope = factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var stored = await db.Playlists.SingleAsync(p => p.Id == created!.Id);
      stored.WatchlistGroup = true;
      await db.SaveChangesAsync();
    }

    var response = await client.SendAsync(HttpMethod.Put, $"/api/playlists/{created!.Id}", NewPlaylist("Kitchen"));
    response.EnsureSuccessStatusCode();

    var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.True(updated.GetProperty("watchlistGroup").GetBoolean());
    var fetched = await client.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{created.Id}");
    Assert.True(fetched.GetProperty("watchlistGroup").GetBoolean());
  }

  private async Task<bool> StoredWatchlistGroupAsync(Guid id)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    return (await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == id)).WatchlistGroup;
  }

  [Fact]
  public async Task WatchlistGroup_SwitchesOnAndOff_AndIsReadBack()
  {
    var client = await SignedInAsync("wlgroup-switch");
    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();
    var path = $"/api/playlists/{created!.Id}";
    var before = (await client.Http.GetFromJsonAsync<JsonElement>(path)).GetProperty("updatedAt").GetDateTimeOffset();

    var on = await client.SendAsync(HttpMethod.Patch, $"{path}/watchlist-group", new { enabled = true });
    Assert.Equal(HttpStatusCode.OK, on.StatusCode);
    var onBody = await on.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(created.Id, onBody.GetProperty("id").GetGuid());
    Assert.True(onBody.GetProperty("watchlistGroup").GetBoolean());
    var fetched = await client.Http.GetFromJsonAsync<JsonElement>(path);
    Assert.True(fetched.GetProperty("watchlistGroup").GetBoolean());
    var afterOn = fetched.GetProperty("updatedAt").GetDateTimeOffset();
    Assert.True(afterOn > before);

    var again = await client.SendAsync(HttpMethod.Patch, $"{path}/watchlist-group", new { enabled = true });
    Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("watchlistGroup").GetBoolean());

    var off = await client.SendAsync(HttpMethod.Patch, $"{path}/watchlist-group", new { enabled = false });
    Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("watchlistGroup").GetBoolean());
    fetched = await client.Http.GetFromJsonAsync<JsonElement>(path);
    Assert.False(fetched.GetProperty("watchlistGroup").GetBoolean());
    Assert.True(fetched.GetProperty("updatedAt").GetDateTimeOffset() > afterOn);
  }

  [Fact]
  public async Task WatchlistGroup_WithoutCsrfToken_IsRejected()
  {
    var client = await SignedInAsync("wlgroup-csrf");
    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();

    var response = await client.SendAsync(HttpMethod.Patch, $"/api/playlists/{created!.Id}/watchlist-group", new { enabled = true }, withCsrf: false);
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.False(await StoredWatchlistGroupAsync(created.Id));
  }

  [Fact]
  public async Task WatchlistGroup_OfAnotherUsersPlaylist_Is404()
  {
    var owner = await SignedInAsync("wlgroup-owner");
    var created = await (await owner.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();

    var other = await SignedInAsync("wlgroup-intruder");
    var response = await other.SendAsync(HttpMethod.Patch, $"/api/playlists/{created!.Id}/watchlist-group", new { enabled = true });
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.False(await StoredWatchlistGroupAsync(created.Id));
  }

  [Fact]
  public async Task WatchlistGroup_WithoutASession_Is401()
  {
    var anonymous = new ApiClient(factory.CreateClient());
    var response = await anonymous.SendAsync(HttpMethod.Patch, $"/api/playlists/{Guid.NewGuid()}/watchlist-group", new { enabled = true });
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task WatchlistGroup_DoesNotContactTheProvider()
  {
    var client = await SignedInAsync("wlgroup-upstream");
    var created = await (await client.PostAsync("/api/playlists", NewPlaylist())).Content.ReadFromJsonAsync<PlaylistDto>();
    var before = factory.UpstreamRequests.ToArray();

    var response = await client.SendAsync(HttpMethod.Patch, $"/api/playlists/{created!.Id}/watchlist-group", new { enabled = true });
    response.EnsureSuccessStatusCode();
    Assert.Equal(before, factory.UpstreamRequests.ToArray());
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
