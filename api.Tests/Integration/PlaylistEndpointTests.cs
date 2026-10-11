using System.Net;
using System.Net.Http.Json;
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
}

public sealed record PlaylistDto(
  Guid Id,
  string Name,
  string? SourceUrl,
  List<string> DisabledGroups,
  string? ShareCode,
  bool IsActive,
  bool AutoRefreshEnabled,
  long ViewCount);
