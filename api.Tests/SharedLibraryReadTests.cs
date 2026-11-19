using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// <c>POST /api/playlists/{id}/library/search</c> and <c>GET /api/playlists/{id}/library/group-entries</c> for the
/// owner and accepted viewers (story #378). In the Mixed fixture "Germany" splits into "Germany · Live" (ARD, ZDF)
/// and "Germany · Series" (Tatort S01E01); "Kids" holds KiKA.
/// </summary>
public class SharedLibraryReadTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static async Task<Guid> CreatePlaylistAsync(ApiClient client, params string[] disabledGroups)
  {
    var created = await client.PostAsync("/api/playlists", new
    {
      name = "Sofa list", sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups,
    });
    created.EnsureSuccessStatusCode();
    return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
  }

  private async Task<ApiClient> ViewerOfAsync(ApiClient owner, Guid playlistId, string userName)
  {
    var invited = await owner.PostAsync($"/api/playlists/{playlistId}/shares", new { label = "Guest" });
    Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
    var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteToken").GetString();
    var viewer = await SignedInAsync(userName);
    Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsync("/api/shares/accept", new { token })).StatusCode);
    return viewer;
  }

  private static async Task<string> OpenAsync(ApiClient client, Guid playlistId)
  {
    var response = await client.PostAsync($"/api/playlists/{playlistId}/library/open");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libraryKey").GetString()!;
  }

  private static Task<HttpResponseMessage> SearchAsync(ApiClient client, Guid playlistId, string libraryKey, string query, object? extra = null) =>
    client.PostAsync($"/api/playlists/{playlistId}/library/search", extra ?? new { libraryKey, query });

  private static Task<HttpResponseMessage> GroupEntriesAsync(ApiClient client, Guid playlistId, string libraryKey, string group) =>
    client.Http.GetAsync($"/api/playlists/{playlistId}/library/group-entries?libraryKey={libraryKey}&group={Uri.EscapeDataString(group)}");

  private static async Task<JsonElement> OkJsonAsync(Task<HttpResponseMessage> request)
  {
    var response = await request;
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return await response.Content.ReadFromJsonAsync<JsonElement>();
  }

  private static JsonElement[] Matches(JsonElement body) => body.GetProperty("matches").EnumerateArray().ToArray();

  [Fact]
  public async Task Viewer_Search_CarriesOwnMarksOnly()
  {
    var owner = await SignedInAsync("marks-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var viewer = await ViewerOfAsync(owner, playlistId, "marks-viewer");
    var owned = await owner.SendAsync(HttpMethod.Put, "/api/marks", new { name = "Tatort S01E01", kind = "series", seen = true, rating = -1 });
    Assert.Equal(HttpStatusCode.OK, owned.StatusCode);
    var key = await OpenAsync(viewer, playlistId);

    var before = Matches(await OkJsonAsync(SearchAsync(viewer, playlistId, key, "Tatort"))).Single();
    Assert.Equal(JsonValueKind.Null, before.GetProperty("seen").ValueKind);
    Assert.Equal(JsonValueKind.Null, before.GetProperty("rating").ValueKind);

    var marked = await viewer.SendAsync(HttpMethod.Put, "/api/marks", new { name = "Tatort S01E01", kind = "series", rating = 1 });
    Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
    var after = Matches(await OkJsonAsync(SearchAsync(viewer, playlistId, key, "Tatort"))).Single();
    Assert.False(after.GetProperty("seen").GetBoolean());
    Assert.Equal(1, after.GetProperty("rating").GetInt32());
  }

  [Fact]
  public async Task Viewer_AskingForSwitchedOffGroups_GetsNone()
  {
    var owner = await SignedInAsync("hidden-owner");
    var playlistId = await CreatePlaylistAsync(owner, "Kids");
    var viewer = await ViewerOfAsync(owner, playlistId, "hidden-viewer");
    var key = await OpenAsync(viewer, playlistId);

    // The request's disabledGroups and hideDisabled are not the viewer's to choose.
    var body = await OkJsonAsync(SearchAsync(viewer, playlistId, key, "KiKA",
      new { libraryKey = key, query = "KiKA", disabledGroups = Array.Empty<string>(), hideDisabled = false }));

    Assert.Empty(Matches(body));
    Assert.Equal(0, body.GetProperty("total").GetInt32());
    Assert.Equal(0, body.GetProperty("hiddenCount").GetInt32());
    Assert.Single(Matches(await OkJsonAsync(SearchAsync(viewer, playlistId, key, "ZDF"))));
  }

  [Fact]
  public async Task Viewer_GroupEntriesOfHiddenGroup_404()
  {
    var owner = await SignedInAsync("group-owner");
    var playlistId = await CreatePlaylistAsync(owner, "Germany · Series");
    var viewer = await ViewerOfAsync(owner, playlistId, "group-viewer");
    var key = await OpenAsync(viewer, playlistId);

    Assert.Equal(HttpStatusCode.NotFound, (await GroupEntriesAsync(viewer, playlistId, key, "Germany · Series")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await GroupEntriesAsync(viewer, playlistId, key, "No such group")).StatusCode);
    var live = await OkJsonAsync(GroupEntriesAsync(viewer, playlistId, key, "Germany · Live"));
    Assert.Equal(new[] { "ARD", "ZDF" }, live.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToArray());
  }

  [Fact]
  public async Task Viewer_Results_ShowTheirGroup()
  {
    var owner = await SignedInAsync("where-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var viewer = await ViewerOfAsync(owner, playlistId, "where-viewer");
    var key = await OpenAsync(viewer, playlistId);

    var match = Matches(await OkJsonAsync(SearchAsync(viewer, playlistId, key, "Tatort"))).Single();
    Assert.Equal("Germany · Series", match.GetProperty("group").GetString());

    var entries = (await OkJsonAsync(GroupEntriesAsync(viewer, playlistId, key, "Kids"))).GetProperty("entries").EnumerateArray().ToArray();
    Assert.Equal("KiKA", entries.Single().GetProperty("name").GetString());
    Assert.Equal("Kids", entries.Single().GetProperty("group").GetString());
  }

  [Fact]
  public async Task Revoked_NextRequest404_WithLiveKey()
  {
    var owner = await SignedInAsync("revoke-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var viewer = await ViewerOfAsync(owner, playlistId, "revoke-viewer");
    var key = await OpenAsync(viewer, playlistId);
    await OkJsonAsync(SearchAsync(viewer, playlistId, key, "ARD"));

    var shares = await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}/shares");
    var shareId = shares.EnumerateArray().Single().GetProperty("id").GetGuid();
    Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/playlists/{playlistId}/shares/{shareId}")).StatusCode);

    Assert.Equal(HttpStatusCode.NotFound, (await SearchAsync(viewer, playlistId, key, "ARD")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await GroupEntriesAsync(viewer, playlistId, key, "Kids")).StatusCode);
  }

  [Fact]
  public async Task KeyOfAnotherUserOrPlaylist_404()
  {
    var owner = await SignedInAsync("keys-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var otherPlaylistId = await CreatePlaylistAsync(owner);
    var viewer = await ViewerOfAsync(owner, playlistId, "keys-viewer");
    await ViewerOfAsync(owner, otherPlaylistId, "keys-viewer-2");
    var ownerKey = await OpenAsync(owner, playlistId);
    var otherKey = await OpenAsync(owner, otherPlaylistId);
    var stranger = await SignedInAsync("keys-stranger");

    // The owner's key for this playlist, and a key for another playlist, are not the viewer's.
    Assert.Equal(HttpStatusCode.NotFound, (await SearchAsync(viewer, playlistId, ownerKey, "ARD")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await SearchAsync(owner, playlistId, otherKey, "ARD")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await GroupEntriesAsync(viewer, playlistId, ownerKey, "Kids")).StatusCode);
    // A stranger holding a valid key gets the same 404, and an editor cacheKey is no Library key.
    Assert.Equal(HttpStatusCode.NotFound, (await SearchAsync(stranger, playlistId, ownerKey, "ARD")).StatusCode);
    var analyzed = await OkJsonAsync(owner.PostAsync("/api/playlist/analyze", new { sourceUrl = StrimAppFactory.UpstreamUrl }));
    var cacheKey = analyzed.GetProperty("cacheKey").GetString()!;
    Assert.Equal(HttpStatusCode.NotFound, (await SearchAsync(owner, playlistId, cacheKey, "ARD")).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await GroupEntriesAsync(owner, playlistId, cacheKey, "Kids")).StatusCode);
  }

  [Fact]
  public async Task EditorAndOwnerRoutes_AnswerAsBefore()
  {
    var owner = await SignedInAsync("before-owner");
    var playlistId = await CreatePlaylistAsync(owner, "Kids");
    var libraryKey = await OpenAsync(owner, playlistId);

    // The owner's playlist-scoped Library covers the whole list and takes the hidden groups from the request.
    var ownerSearch = await OkJsonAsync(SearchAsync(owner, playlistId, libraryKey, "KiKA",
      new { libraryKey, query = "KiKA", disabledGroups = new[] { "Kids" } }));
    Assert.False(Matches(ownerSearch).Single().GetProperty("enabled").GetBoolean());
    var ownerKids = await OkJsonAsync(GroupEntriesAsync(owner, playlistId, libraryKey, "Kids"));
    Assert.Equal(1, ownerKids.GetProperty("total").GetInt32());

    // The editor's and the cacheKey Library routes answer as before.
    var cacheKey = (await OkJsonAsync(owner.PostAsync("/api/playlist/analyze", new { sourceUrl = StrimAppFactory.UpstreamUrl })))
      .GetProperty("cacheKey").GetString()!;
    var editor = await OkJsonAsync(owner.PostAsync("/api/playlist/search", new { cacheKey, query = "KiKA", disabledGroups = new[] { "Kids" } }));
    Assert.False(Matches(editor).Single().GetProperty("enabled").GetBoolean());
    var library = await OkJsonAsync(owner.PostAsync("/api/library/search", new { cacheKey, query = "KiKA" }));
    Assert.Equal("Kids", Matches(library).Single().GetProperty("group").GetString());
    var entries = await OkJsonAsync(owner.Http.GetAsync($"/api/library/group-entries?cacheKey={cacheKey}&group=Kids"));
    Assert.Equal(1, entries.GetProperty("total").GetInt32());
    Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/library/search", new { cacheKey = libraryKey, query = "KiKA" })).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await owner.Http.GetAsync("/api/playlists")).StatusCode);

    // Signed out: 401.
    var anonymous = new ApiClient(factory.CreateClient());
    Assert.Equal(HttpStatusCode.Unauthorized, (await SearchAsync(anonymous, playlistId, libraryKey, "KiKA")).StatusCode);
  }
}
