using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Data;
using Api.Endpoints;
using Api.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public class ShareAcceptTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static async Task<Guid> CreatePlaylistAsync(ApiClient client, string name = "Sofa list")
  {
    var created = await client.PostAsync("/api/playlists", new
    {
      name, sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups = Array.Empty<string>(),
    });
    created.EnsureSuccessStatusCode();
    return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
  }

  private static async Task<(Guid ShareId, string Token)> InviteAsync(ApiClient owner, Guid playlistId)
  {
    var response = await owner.PostAsync($"/api/playlists/{playlistId}/shares", new { label = "Guest" });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    return (body.GetProperty("share").GetProperty("id").GetGuid(), body.GetProperty("inviteToken").GetString()!);
  }

  private static Task<HttpResponseMessage> AcceptAsync(ApiClient client, string token, bool withCsrf = true) =>
    client.PostAsync("/api/shares/accept", new { token }, withCsrf);

  private static async Task<JsonElement> MineAsync(ApiClient client) =>
    await client.Http.GetFromJsonAsync<JsonElement>("/api/shares/mine");

  private T WithDb<T>(Func<AppDbContext, T> action)
  {
    using var scope = factory.Services.CreateScope();
    return action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
  }

  private string UserId(string userName) => WithDb(db => db.Users.Single(u => u.UserName == userName).Id);

  [Fact]
  public async Task Accept_BindsViewer_ClearsHash()
  {
    var owner = await SignedInAsync("accept-owner");
    var playlistId = await CreatePlaylistAsync(owner, "Living room");
    var (shareId, token) = await InviteAsync(owner, playlistId);
    var viewer = await SignedInAsync("accept-viewer");

    var response = await AcceptAsync(viewer, token);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(playlistId, body.GetProperty("playlistId").GetGuid());
    Assert.Equal("Living room", body.GetProperty("name").GetString());
    var row = WithDb(db => db.PlaylistShares.AsNoTracking().Single(s => s.Id == shareId));
    Assert.Equal(UserId("accept-viewer"), row.ViewerUserId);
    Assert.Null(row.TokenHash);
    Assert.NotNull(row.AcceptedUtc);
    var listed = Assert.Single((await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}/shares")).EnumerateArray());
    Assert.Equal("accepted", listed.GetProperty("status").GetString());
    Assert.Equal("accept-viewer", listed.GetProperty("viewerName").GetString());

    // A second invitation for someone who already has access: 200, and the spare invitation is removed.
    var (spareId, spare) = await InviteAsync(owner, playlistId);
    Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(viewer, spare)).StatusCode);
    Assert.False(WithDb(db => db.PlaylistShares.Any(s => s.Id == spareId)));
    Assert.Equal(1, WithDb(db => db.PlaylistShares.Count(s => s.PlaylistId == playlistId)));
  }

  [Fact]
  public async Task SecondUse_404()
  {
    var owner = await SignedInAsync("second-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var (shareId, token) = await InviteAsync(owner, playlistId);
    var first = await SignedInAsync("second-first");
    var other = await SignedInAsync("second-other");
    Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(first, token)).StatusCode);

    Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(other, token)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(first, token)).StatusCode);

    Assert.Equal(UserId("second-first"), WithDb(db => db.PlaylistShares.AsNoTracking().Single(s => s.Id == shareId).ViewerUserId));
    Assert.Equal(0, (await MineAsync(other)).GetArrayLength());
  }

  [Fact]
  public async Task UnknownExpiredDeleted_Same404()
  {
    var owner = await SignedInAsync("gone-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var (expiredId, expired) = await InviteAsync(owner, playlistId);
    var (deletedId, deleted) = await InviteAsync(owner, playlistId);
    WithDb(db =>
    {
      db.PlaylistShares.Single(s => s.Id == expiredId).TokenExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
      return db.SaveChanges();
    });
    Assert.Equal(HttpStatusCode.NoContent,
      (await owner.SendAsync(HttpMethod.Delete, $"/api/playlists/{playlistId}/shares/{deletedId}", null)).StatusCode);
    var viewer = await SignedInAsync("gone-viewer");

    var responses = new[]
    {
      await AcceptAsync(viewer, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
      await AcceptAsync(viewer, expired),
      await AcceptAsync(viewer, deleted),
      await AcceptAsync(viewer, ""),
    };

    var bodies = new List<string>();
    foreach (var response in responses)
    {
      Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
      bodies.Add(await response.Content.ReadAsStringAsync());
    }
    Assert.Single(bodies.Distinct());
    Assert.Null(WithDb(db => db.PlaylistShares.AsNoTracking().Single(s => s.Id == expiredId).ViewerUserId));
    Assert.Equal(0, (await MineAsync(viewer)).GetArrayLength());
  }

  [Fact]
  public async Task OwnInvitation_Refused()
  {
    var owner = await SignedInAsync("own-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var (shareId, token) = await InviteAsync(owner, playlistId);

    var response = await AcceptAsync(owner, token);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var row = WithDb(db => db.PlaylistShares.AsNoTracking().Single(s => s.Id == shareId));
    Assert.Null(row.ViewerUserId);
    Assert.Equal(ShareEndpoints.HashToken(token), row.TokenHash);
    Assert.Equal(0, (await MineAsync(owner)).GetArrayLength());
  }

  [Fact]
  public async Task Mine_HasNameAndOwnerOnly()
  {
    var owner = await SignedInAsync("mine-owner");
    var playlistId = await CreatePlaylistAsync(owner, "Kitchen");
    var (_, token) = await InviteAsync(owner, playlistId);
    var viewer = await SignedInAsync("mine-viewer");
    Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(viewer, token)).StatusCode);
    var totalChannels = WithDb(db => db.Playlists.Single(p => p.Id == playlistId).TotalChannels);

    var mine = await MineAsync(viewer);

    var item = Assert.Single(mine.EnumerateArray());
    var fields = item.EnumerateObject().Select(p => p.Name).Order().ToArray();
    Assert.Equal(new[] { "name", "ownerAvatar", "ownerName", "playlistId", "sharedSinceUtc", "totalChannels" }, fields);
    Assert.Equal(playlistId, item.GetProperty("playlistId").GetGuid());
    Assert.Equal("Kitchen", item.GetProperty("name").GetString());
    Assert.Equal("mine-owner", item.GetProperty("ownerName").GetString());
    Assert.Equal(totalChannels, item.GetProperty("totalChannels").GetInt32());
    Assert.Equal(JsonValueKind.String, item.GetProperty("sharedSinceUtc").ValueKind);
    var raw = mine.GetRawText();
    Assert.DoesNotContain(StrimAppFactory.UpstreamUrl, raw);
    Assert.DoesNotContain("Guest", raw);
    // The owner's own playlists are not listed as shared with them.
    Assert.Equal(0, (await MineAsync(owner)).GetArrayLength());
  }

  [Fact]
  public async Task Leave_EndsAccess()
  {
    var owner = await SignedInAsync("leave-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var (shareId, token) = await InviteAsync(owner, playlistId);
    var viewer = await SignedInAsync("leave-viewer");
    Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(viewer, token)).StatusCode);
    var stranger = await SignedInAsync("leave-stranger");

    Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Delete, $"/api/shares/mine/{playlistId}", null)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await viewer.SendAsync(HttpMethod.Delete, $"/api/shares/mine/{playlistId}", null, false)).StatusCode);
    Assert.Equal(1, (await MineAsync(viewer)).GetArrayLength());

    Assert.Equal(HttpStatusCode.NoContent, (await viewer.SendAsync(HttpMethod.Delete, $"/api/shares/mine/{playlistId}", null)).StatusCode);

    Assert.Equal(0, (await MineAsync(viewer)).GetArrayLength());
    Assert.False(WithDb(db => db.PlaylistShares.Any(s => s.Id == shareId)));
    Assert.Equal(HttpStatusCode.NotFound, (await viewer.SendAsync(HttpMethod.Delete, $"/api/shares/mine/{playlistId}", null)).StatusCode);
    Assert.True(WithDb(db => db.Playlists.Any(p => p.Id == playlistId)));
    Assert.Contains(factory.LogEntries.ToArray(), e => e.Contains("left playlist") && e.Contains(playlistId.ToString()));
  }

  [Fact]
  public async Task Accept_WithoutCsrf_Refused()
  {
    var owner = await SignedInAsync("csrf-accept-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var (shareId, token) = await InviteAsync(owner, playlistId);
    var viewer = await SignedInAsync("csrf-accept-viewer");

    var response = await AcceptAsync(viewer, token, withCsrf: false);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains(EndpointFilters.CsrfError, await response.Content.ReadAsStringAsync());
    Assert.Null(WithDb(db => db.PlaylistShares.AsNoTracking().Single(s => s.Id == shareId).ViewerUserId));
    var anonymous = new ApiClient(factory.CreateClient());
    Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/shares/mine")).StatusCode);
  }
}
