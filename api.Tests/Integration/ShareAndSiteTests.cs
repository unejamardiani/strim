using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Api.Tests.Integration;

public class ShareAndSiteTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private async Task<(ApiClient Owner, PlaylistDto Playlist)> CreateSharedAsync(string userName, params string[] disabled)
  {
    var owner = new ApiClient(factory.CreateClient());
    await owner.RegisterAsync(userName);
    var response = await owner.PostAsync("/api/playlists", new
    {
      name = "Shared",
      sourceUrl = StrimAppFactory.UpstreamUrl,
      disabledGroups = disabled,
    });
    response.EnsureSuccessStatusCode();
    return (owner, (await response.Content.ReadFromJsonAsync<PlaylistDto>())!);
  }

  [Fact]
  public async Task Share_ServesTheFilteredPlaylistAnonymously_AndCountsViews()
  {
    var (owner, playlist) = await CreateSharedAsync("sharer", "Germany · Series");

    var anonymous = factory.CreateClient();
    var text = await anonymous.GetStringAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}");
    Assert.Contains("ARD", text);
    Assert.DoesNotContain("Tatort", text);

    var stats = await owner.Http.GetFromJsonAsync<StatsDto>($"/api/playlists/{playlist.Id}/stats");
    Assert.Equal(1, stats!.ViewCount);
  }

  [Fact]
  public async Task Share_WithWrongCodeOrInactiveLink_IsNotFound()
  {
    var (owner, playlist) = await CreateSharedAsync("inactive");
    var anonymous = factory.CreateClient();

    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{playlist.Id}/share/wrong")).StatusCode);

    (await owner.SendAsync(HttpMethod.Patch, $"/api/playlists/{playlist.Id}/toggle-active")).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}")).StatusCode);
  }

  [Fact]
  public async Task Health_And_Config_Respond()
  {
    var client = factory.CreateClient();
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    var config = await client.GetStringAsync("/api/config");
    Assert.Contains("publicShareBaseUrl", config);
  }

  [Fact]
  public async Task UnknownApiRoute_IsNotFound()
  {
    var response = await factory.CreateClient().GetAsync("/api/does-not-exist");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task UnknownPage_IsNotFound()
  {
    var response = await factory.CreateClient().GetAsync("/no-such-page");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  private sealed record StatsDto(long ViewCount);
}
