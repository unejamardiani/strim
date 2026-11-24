using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Data;
using Api.Endpoints;
using Api.Models;
using Api.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public class ShareManagementTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static async Task<Guid> CreatePlaylistAsync(ApiClient client)
  {
    var created = await client.PostAsync("/api/playlists", new
    {
      name = "Sofa list", sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups = Array.Empty<string>(),
    });
    created.EnsureSuccessStatusCode();
    return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
  }

  private static Task<HttpResponseMessage> CreateShareAsync(ApiClient client, Guid playlistId, string label = "Guest", bool withCsrf = true) =>
    client.PostAsync($"/api/playlists/{playlistId}/shares", new { label }, withCsrf);

  private static Task<HttpResponseMessage> DeleteShareAsync(ApiClient client, Guid playlistId, Guid shareId, bool withCsrf = true) =>
    client.SendAsync(HttpMethod.Delete, $"/api/playlists/{playlistId}/shares/{shareId}", null, withCsrf);

  private static async Task<JsonElement> ListAsync(ApiClient client, Guid playlistId) =>
    await client.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}/shares");

  private T WithDb<T>(Func<AppDbContext, T> action)
  {
    using var scope = factory.Services.CreateScope();
    return action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
  }

  [Fact]
  public async Task Create_ReturnsTokenOnce_StoresOnlyHash()
  {
    var owner = await SignedInAsync("share-create");
    var playlistId = await CreatePlaylistAsync(owner);

    var response = await CreateShareAsync(owner, playlistId, "Sam");

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    var token = body.GetProperty("inviteToken").GetString()!;
    Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
    var share = body.GetProperty("share");
    Assert.Equal("Sam", share.GetProperty("label").GetString());
    Assert.Equal("viewer", share.GetProperty("role").GetString());
    Assert.Equal("invited", share.GetProperty("status").GetString());

    var row = WithDb(db => db.PlaylistShares.AsNoTracking().Single(s => s.PlaylistId == playlistId));
    Assert.Equal(ShareEndpoints.HashToken(token), row.TokenHash);
    Assert.Matches("^[0-9a-f]{64}$", row.TokenHash);
    Assert.NotEqual(token, row.TokenHash);
    var lifetime = row.TokenExpiresUtc!.Value - row.CreatedUtc;
    Assert.Equal(ShareEndpoints.InvitationLifetime, lifetime);
    Assert.Null(row.ViewerUserId);

    // The token is shown only once: neither the list nor a second create gives it again.
    Assert.DoesNotContain(token, (await ListAsync(owner, playlistId)).GetRawText());
    var second = await (await CreateShareAsync(owner, playlistId, "Sam")).Content.ReadFromJsonAsync<JsonElement>();
    Assert.NotEqual(token, second.GetProperty("inviteToken").GetString());
  }

  [Fact]
  public async Task List_HasNoTokenOrHash()
  {
    var owner = await SignedInAsync("share-list");
    var playlistId = await CreatePlaylistAsync(owner);
    var token = (await (await CreateShareAsync(owner, playlistId, "Robin")).Content.ReadFromJsonAsync<JsonElement>())
      .GetProperty("inviteToken").GetString()!;

    var list = await ListAsync(owner, playlistId);

    var item = Assert.Single(list.EnumerateArray());
    var fields = item.EnumerateObject().Select(p => p.Name).Order().ToArray();
    Assert.Equal(new[] { "acceptedUtc", "createdUtc", "expiresUtc", "id", "label", "lastOpenedUtc", "role", "status", "viewerName" }, fields);
    Assert.Equal("Robin", item.GetProperty("label").GetString());
    Assert.Equal("invited", item.GetProperty("status").GetString());
    var raw = list.GetRawText();
    Assert.DoesNotContain(token, raw);
    Assert.DoesNotContain(ShareEndpoints.HashToken(token), raw);
    Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task AnotherUsersPlaylist_404()
  {
    var owner = await SignedInAsync("share-owner-a");
    var playlistId = await CreatePlaylistAsync(owner);
    var shareId = (await (await CreateShareAsync(owner, playlistId)).Content.ReadFromJsonAsync<JsonElement>())
      .GetProperty("share").GetProperty("id").GetGuid();
    var stranger = await SignedInAsync("share-stranger-b");

    Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/playlists/{playlistId}/shares")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await CreateShareAsync(stranger, playlistId)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await DeleteShareAsync(stranger, playlistId, shareId)).StatusCode);
    // A share id under the stranger's own playlist does not reach the owner's share either.
    var own = await CreatePlaylistAsync(stranger);
    Assert.Equal(HttpStatusCode.NotFound, (await DeleteShareAsync(stranger, own, shareId)).StatusCode);

    Assert.Equal(1, WithDb(db => db.PlaylistShares.Count(s => s.PlaylistId == playlistId)));
    var anonymous = new ApiClient(factory.CreateClient());
    Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync($"/api/playlists/{playlistId}/shares")).StatusCode);
  }

  [Fact]
  public async Task Mutations_WithoutCsrf_Refused()
  {
    var owner = await SignedInAsync("share-csrf");
    var playlistId = await CreatePlaylistAsync(owner);
    var shareId = (await (await CreateShareAsync(owner, playlistId)).Content.ReadFromJsonAsync<JsonElement>())
      .GetProperty("share").GetProperty("id").GetGuid();

    var create = await CreateShareAsync(owner, playlistId, withCsrf: false);
    var delete = await DeleteShareAsync(owner, playlistId, shareId, withCsrf: false);

    Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    Assert.Contains(EndpointFilters.CsrfError, await create.Content.ReadAsStringAsync());
    Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    Assert.Contains(EndpointFilters.CsrfError, await delete.Content.ReadAsStringAsync());
    Assert.Equal(1, WithDb(db => db.PlaylistShares.Count(s => s.PlaylistId == playlistId)));
  }

  [Fact]
  public async Task ParallelCreations_KeepTheShareLimit()
  {
    var owner = await SignedInAsync("share-race");
    var playlistId = await CreatePlaylistAsync(owner);
    // Five directly, so the ten requests the per-user rate limit lets through race for the last five places.
    WithDb(db =>
    {
      var now = DateTimeOffset.UtcNow;
      for (var i = 0; i < ShareEndpoints.MaxSharesPerPlaylist - 5; i++)
      {
        db.PlaylistShares.Add(new PlaylistShare
        {
          Id = Guid.NewGuid(), PlaylistId = playlistId, Label = "Guest " + i, CreatedUtc = now,
          TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), TokenExpiresUtc = now.AddDays(7),
        });
      }
      return db.SaveChanges();
    });
    var token = await owner.CsrfTokenAsync();

    var responses = await Task.WhenAll(Enumerable.Range(0, 11).Select(async i =>
    {
      using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/playlists/{playlistId}/shares")
      {
        Content = JsonContent.Create(new { label = "Racer " + i }),
      };
      request.Headers.Add("X-CSRF-TOKEN", token);
      return await owner.Http.SendAsync(request);
    }));

    Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
    Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
      r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.TooManyRequests }));
    Assert.Equal(ShareEndpoints.MaxSharesPerPlaylist, WithDb(db => db.PlaylistShares.Count(s => s.PlaylistId == playlistId)));
  }

  [Fact]
  public async Task EleventhShare_409()
  {
    var owner = await SignedInAsync("share-limit");
    var playlistId = await CreatePlaylistAsync(owner);
    Assert.Equal(HttpStatusCode.Created, (await CreateShareAsync(owner, playlistId)).StatusCode);
    // The other nine directly, so the per-user rate limit (10 a minute) does not decide the test. One is accepted:
    // open and accepted shares both count.
    await SignedInAsync("share-limit-viewer");
    var viewerId = WithDb(db => db.Users.Single(u => u.UserName == "share-limit-viewer").Id);
    WithDb(db =>
    {
      var now = DateTimeOffset.UtcNow;
      for (var i = 0; i < ShareEndpoints.MaxSharesPerPlaylist - 1; i++)
      {
        var accepted = i == 0;
        db.PlaylistShares.Add(new PlaylistShare
        {
          Id = Guid.NewGuid(), PlaylistId = playlistId, Label = "Guest " + i, CreatedUtc = now,
          TokenHash = accepted ? null : Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
          TokenExpiresUtc = accepted ? null : now.AddDays(7), ViewerUserId = accepted ? viewerId : null,
          AcceptedUtc = accepted ? now : null,
        });
      }
      return db.SaveChanges();
    });

    var refused = await CreateShareAsync(owner, playlistId, "One too many");

    Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    Assert.Equal(ShareEndpoints.MaxSharesPerPlaylist, WithDb(db => db.PlaylistShares.Count(s => s.PlaylistId == playlistId)));
    var list = await ListAsync(owner, playlistId);
    Assert.Equal(1, list.EnumerateArray().Count(s => s.GetProperty("status").GetString() == "accepted"));
    Assert.Equal("share-limit-viewer", list.EnumerateArray().Single(s => s.GetProperty("status").GetString() == "accepted").GetProperty("viewerName").GetString());
  }

  [Fact]
  public async Task Delete_RemovesShare()
  {
    var owner = await SignedInAsync("share-delete");
    var playlistId = await CreatePlaylistAsync(owner);
    var shareId = (await (await CreateShareAsync(owner, playlistId, "Kim")).Content.ReadFromJsonAsync<JsonElement>())
      .GetProperty("share").GetProperty("id").GetGuid();
    await CreateShareAsync(owner, playlistId, "Lee");

    Assert.Equal(HttpStatusCode.NoContent, (await DeleteShareAsync(owner, playlistId, shareId)).StatusCode);

    Assert.Equal(new[] { "Lee" }, (await ListAsync(owner, playlistId)).EnumerateArray().Select(s => s.GetProperty("label").GetString()));
    Assert.False(WithDb(db => db.PlaylistShares.Any(s => s.Id == shareId)));
    Assert.Equal(HttpStatusCode.NotFound, (await DeleteShareAsync(owner, playlistId, shareId)).StatusCode);
  }

  [Fact]
  public async Task Logs_HoldNoTokenOrLabel()
  {
    const string Label = "Neighbour-Label-7f3a";
    var owner = await SignedInAsync("share-logs");
    var playlistId = await CreatePlaylistAsync(owner);

    var body = await (await CreateShareAsync(owner, playlistId, Label)).Content.ReadFromJsonAsync<JsonElement>();
    var token = body.GetProperty("inviteToken").GetString()!;
    var shareId = body.GetProperty("share").GetProperty("id").GetGuid();
    await DeleteShareAsync(owner, playlistId, shareId);

    var entries = factory.LogEntries.ToArray();
    Assert.Contains(entries, e => e.Contains("created invitation") && e.Contains(shareId.ToString()) && e.Contains(playlistId.ToString()));
    Assert.Contains(entries, e => e.Contains("deleted share") && e.Contains(shareId.ToString()));
    foreach (var secret in new[] { token, ShareEndpoints.HashToken(token), Label })
    {
      Assert.DoesNotContain(entries, e => e.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }
  }
}
