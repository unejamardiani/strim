using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

  private static async Task SetProfileAsync(ApiClient client, object profile) =>
    Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Put, "/api/profile", profile)).StatusCode);

  [Fact]
  public async Task Viewer_SeesOwnersDisplayNameAndAvatar()
  {
    var owner = await SignedInAsync("profiled.owner@example.org");
    await SetProfileAsync(owner, new { displayName = "Sofa Captain", avatar = "tangerine:tv" });
    var playlistId = await CreatePlaylistAsync(owner, "Den");
    var viewer = await SignedInAsync("profiled-viewer");
    await ShareAsync(owner, playlistId, viewer);

    var row = Assert.Single((await viewer.Http.GetFromJsonAsync<JsonElement>("/api/shares/mine")).EnumerateArray());
    var opened = await OpenLibraryAsync(viewer, playlistId);

    foreach (var body in new[] { row, opened })
    {
      Assert.Equal("Sofa Captain", body.GetProperty("ownerName").GetString());
      Assert.Equal("tangerine:tv", body.GetProperty("ownerAvatar").GetString());
    }
  }

  [Fact]
  public async Task Viewer_OfOwnerWithoutProfile_SeesLocalPartAndNoAvatar()
  {
    var owner = await SignedInAsync("bare.owner@example.org");
    var playlistId = await CreatePlaylistAsync(owner, "Attic");
    var viewer = await SignedInAsync("bare-viewer");
    await ShareAsync(owner, playlistId, viewer);

    var row = Assert.Single((await viewer.Http.GetFromJsonAsync<JsonElement>("/api/shares/mine")).EnumerateArray());

    Assert.Equal("bare.owner", row.GetProperty("ownerName").GetString());
    Assert.Equal(JsonValueKind.Null, row.GetProperty("ownerAvatar").ValueKind);
  }

  [Fact]
  public async Task OwnerList_AcceptedShareHasViewersAvatar_OpenInvitationNone()
  {
    var owner = await SignedInAsync("avatar-list-owner");
    var playlistId = await CreatePlaylistAsync(owner, "Garden");
    var viewer = await SignedInAsync("avatar-list-viewer");
    await SetProfileAsync(viewer, new { displayName = "Couch Guest", avatar = "violet:star" });
    await ShareAsync(owner, playlistId, viewer);
    Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/playlists/{playlistId}/shares", new { label = "Later" })).StatusCode);

    var shares = (await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}/shares")).EnumerateArray().ToList();

    var accepted = Assert.Single(shares, s => s.GetProperty("status").GetString() == "accepted");
    Assert.Equal("violet:star", accepted.GetProperty("avatar").GetString());
    Assert.Equal("Couch Guest", accepted.GetProperty("viewerName").GetString());
    Assert.Equal("Guest", accepted.GetProperty("label").GetString());
    var open = Assert.Single(shares, s => s.GetProperty("status").GetString() == "invited");
    Assert.Equal(JsonValueKind.Null, open.GetProperty("avatar").ValueKind);
  }

  [Fact]
  public async Task ShareResponses_NeverContainAnotherUsersEmail()
  {
    const string ownerEmail = "mail.owner@example.org", viewerEmail = "mail.viewer@example.org";
    var owner = await SignedInAsync(ownerEmail);
    var playlistId = await CreatePlaylistAsync(owner, "Porch");
    var viewer = await SignedInAsync(viewerEmail);
    await ShareAsync(owner, playlistId, viewer);

    var ownerSees = new[]
    {
      await owner.Http.GetStringAsync($"/api/playlists/{playlistId}/shares"),
      await (await owner.PostAsync($"/api/playlists/{playlistId}/shares", new { label = "Another" })).Content.ReadAsStringAsync(),
    };
    var viewerSees = new[]
    {
      await viewer.Http.GetStringAsync("/api/shares/mine"),
      (await OpenLibraryAsync(viewer, playlistId)).GetRawText(),
    };

    Assert.All(ownerSees, body => Assert.DoesNotContain(viewerEmail, body));
    Assert.All(viewerSees, body => Assert.DoesNotContain(ownerEmail, body));
  }
}
