using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Endpoints;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Api.Tests;

/// <summary>Names shown to other people never contain a whole e-mail address; the invite hint note comes from config.</summary>
public class ShareNamesTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static async Task<Guid> CreatePlaylistAsync(ApiClient client, string name)
  {
    var created = await client.PostAsync("/api/playlists", new
    {
      name, sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups = Array.Empty<string>(),
    });
    created.EnsureSuccessStatusCode();
    return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
  }

  private static async Task ShareAsync(ApiClient owner, Guid playlistId, ApiClient viewer)
  {
    var invited = await owner.PostAsync($"/api/playlists/{playlistId}/shares", new { label = "Guest" });
    Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
    var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteToken").GetString();
    Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsync("/api/shares/accept", new { token })).StatusCode);
  }

  private static async Task<JsonElement> OpenLibraryAsync(ApiClient client, Guid playlistId)
  {
    var response = await client.PostAsync($"/api/playlists/{playlistId}/library/open");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return await response.Content.ReadFromJsonAsync<JsonElement>();
  }

  [Theory]
  [InlineData("sofa.viewer@example.org", "sofa.viewer")]
  [InlineData("plain-viewer", "plain-viewer")]
  public async Task OwnerList_ShowsViewerNameBeforeAt(string viewerName, string shown)
  {
    var owner = await SignedInAsync($"list-owner-{shown}");
    var playlistId = await CreatePlaylistAsync(owner, "Living room");
    await ShareAsync(owner, playlistId, await SignedInAsync(viewerName));

    var listed = await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}/shares");

    var share = Assert.Single(listed.EnumerateArray());
    Assert.Equal(shown, share.GetProperty("viewerName").GetString());
    Assert.DoesNotContain("@", listed.GetRawText());
  }

  [Theory]
  [InlineData("mine.owner@example.org", "mine.owner")]
  [InlineData("mine-plain-owner", "mine-plain-owner")]
  public async Task SharedWithMe_ShowsOwnerNameBeforeAt(string ownerName, string shown)
  {
    var owner = await SignedInAsync(ownerName);
    var playlistId = await CreatePlaylistAsync(owner, "Bedroom");
    var viewer = await SignedInAsync($"mine-viewer-{shown}");
    await ShareAsync(owner, playlistId, viewer);

    var mine = await viewer.Http.GetFromJsonAsync<JsonElement>("/api/shares/mine");

    var row = Assert.Single(mine.EnumerateArray());
    Assert.Equal(playlistId, row.GetProperty("playlistId").GetGuid());
    Assert.Equal("Bedroom", row.GetProperty("name").GetString());
    Assert.Equal(shown, row.GetProperty("ownerName").GetString());
    Assert.True(row.TryGetProperty("totalChannels", out _));
    Assert.True(row.TryGetProperty("sharedSinceUtc", out _));
    Assert.DoesNotContain("@", mine.GetRawText());
  }

  [Theory]
  [InlineData("open.owner@example.org", "open.owner")]
  [InlineData("open-plain-owner", "open-plain-owner")]
  public async Task LibraryOpen_ReturnsOwnerName(string ownerName, string shown)
  {
    var owner = await SignedInAsync(ownerName);
    var playlistId = await CreatePlaylistAsync(owner, "Kitchen");
    var viewer = await SignedInAsync($"open-viewer-{shown}");
    await ShareAsync(owner, playlistId, viewer);

    var asOwner = await OpenLibraryAsync(owner, playlistId);
    var asViewer = await OpenLibraryAsync(viewer, playlistId);

    Assert.Equal("owner", asOwner.GetProperty("role").GetString());
    Assert.Equal(shown, asOwner.GetProperty("ownerName").GetString());
    Assert.Equal("viewer", asViewer.GetProperty("role").GetString());
    Assert.Equal(shown, asViewer.GetProperty("ownerName").GetString());
  }

  [Theory]
  [InlineData(null, "")]
  [InlineData("Ask the owner to add your e-mail to the access list.", "Ask the owner to add your e-mail to the access list.")]
  public async Task Config_ReturnsShareAccessNote(string? note, string expected)
  {
    using var app = new StrimAppFactory();
    using var host = app.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
      config.AddInMemoryCollection(new Dictionary<string, string?> { ["Sharing:AccessNote"] = note })));

    var body = await host.CreateClient().GetFromJsonAsync<JsonElement>("/api/config");

    Assert.Equal(expected, body.GetProperty("shareAccessNote").GetString());
    Assert.True(body.TryGetProperty("tmdbEnabled", out _));
  }

  [Theory]
  [InlineData("someone@example.org", "someone")]
  [InlineData("plain", "plain")]
  [InlineData("@leading", "@leading")]
  [InlineData(null, null)]
  public void DisplayName_TrimsTheDomain(string? userName, string? expected) =>
    Assert.Equal(expected, ShareEndpoints.DisplayName(userName));
}
