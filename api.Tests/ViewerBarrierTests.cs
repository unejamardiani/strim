using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>
/// The barrier between a playlist's owner and the people it is shared with (story #380, epic #373): every route under
/// <c>/api/playlists</c> is classified, every owner route answers a viewer exactly as it answers a stranger, and no
/// response a viewer can obtain carries the source address, its host, the credentials, a stream URL or the share code.
/// </summary>
public class ViewerBarrierTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private const string BrokenUpstreamUrl = "http://93.184.215.14/get.php?username=u&password=p&type=missing";

  /// <summary>Routes only the owner may use: a viewer gets the stranger's 404. Sample path and body per route.</summary>
  private static readonly (string Method, string Pattern, string Path, object? Body)[] OwnerRoutes =
  [
    ("GET", "/api/playlists/{id:guid}", "/api/playlists/{id}", null),
    ("PUT", "/api/playlists/{id:guid}", "/api/playlists/{id}", new { name = "Taken over", sourceUrl = StrimAppFactory.UpstreamUrl }),
    ("DELETE", "/api/playlists/{id:guid}", "/api/playlists/{id}", null),
    ("GET", "/api/playlists/{id:guid}/stats", "/api/playlists/{id}/stats", null),
    ("GET", "/api/playlists/{id:guid}/provider-language-probe", "/api/playlists/{id}/provider-language-probe?kind=vod&titleId=1", null),
    ("PATCH", "/api/playlists/{id:guid}/toggle-active", "/api/playlists/{id}/toggle-active", null),
    ("POST", "/api/playlists/{id:guid}/regenerate-code", "/api/playlists/{id}/regenerate-code", null),
    ("PATCH", "/api/playlists/{id:guid}/auto-refresh", "/api/playlists/{id}/auto-refresh", new { enabled = true }),
    ("GET", "/api/playlists/{id:guid}/shares", "/api/playlists/{id}/shares", null),
    ("POST", "/api/playlists/{id:guid}/shares", "/api/playlists/{id}/shares", new { label = "Intruder" }),
    ("DELETE", "/api/playlists/{id:guid}/shares/{shareId:guid}", "/api/playlists/{id}/shares/{shareId}", null),
  ];

  /// <summary>Routes the owner and accepted viewers may use; access is checked per request.</summary>
  private static readonly string[] ViewerRoutes =
  [
    "POST /api/playlists/{id:guid}/library/open",
    "POST /api/playlists/{id:guid}/library/search",
    "GET /api/playlists/{id:guid}/library/group-entries",
    "GET /api/playlists/{id:guid}/library/film-card",
    "GET /api/playlists/{id:guid}/library/series-card",
  ];

  /// <summary>Routes not bound to one playlist: the caller's own lists, the sample and the anonymous player link.</summary>
  private static readonly string[] OtherRoutes =
  [
    "GET /api/playlists",
    "GET /api/playlists/overview",
    "POST /api/playlists",
    "GET /api/playlists/sample",
    "GET /api/playlists/{id:guid}/share/{code}",
  ];

  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static async Task<Guid> CreatePlaylistAsync(ApiClient client, string sourceUrl = StrimAppFactory.UpstreamUrl)
  {
    var created = await client.PostAsync("/api/playlists", new { name = "Sofa list", sourceUrl, disabledGroups = new[] { "Kids" } });
    created.EnsureSuccessStatusCode();
    return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
  }

  private async Task<(ApiClient Viewer, string Token)> ViewerOfAsync(ApiClient owner, Guid playlistId, string userName)
  {
    var invited = await owner.PostAsync($"/api/playlists/{playlistId}/shares", new { label = "Guest" });
    Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
    var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteToken").GetString()!;
    var viewer = await SignedInAsync(userName);
    Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsync("/api/shares/accept", new { token })).StatusCode);
    return (viewer, token);
  }

  private static string Describe(RouteEndpoint endpoint)
  {
    var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
    return $"{string.Join(",", methods)} {endpoint.RoutePattern.RawText!.TrimEnd('/')}";
  }

  [Fact]
  public void UnclassifiedPlaylistRoute_FailsTheBuild()
  {
    var mapped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
      .Where(e => e.RoutePattern.RawText?.StartsWith("/api/playlists", StringComparison.OrdinalIgnoreCase) == true)
      .Select(Describe).ToHashSet();
    var classified = OwnerRoutes.Select(r => $"{r.Method} {r.Pattern}").Concat(ViewerRoutes).Concat(OtherRoutes).ToHashSet();

    // A new route under /api/playlists must be added to one of the lists above, with a deliberate decision about viewers.
    Assert.Empty(mapped.Except(classified).Order());
    // A classified route that no longer exists means the lists above went stale.
    Assert.Empty(classified.Except(mapped).Order());
  }

  [Fact]
  public async Task EveryOwnerRoute_404ForViewer()
  {
    var owner = await SignedInAsync("barrier-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var (viewer, _) = await ViewerOfAsync(owner, playlistId, "barrier-viewer");
    var stranger = await SignedInAsync("barrier-stranger");
    var before = await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}");
    var shareId = (await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}/shares"))
      .EnumerateArray().Single().GetProperty("id").GetGuid();

    foreach (var (method, _, template, body) in OwnerRoutes)
    {
      var path = template.Replace("{id}", playlistId.ToString()).Replace("{shareId}", shareId.ToString());
      var asStranger = await stranger.SendAsync(new HttpMethod(method), path, body);
      var asViewer = await viewer.SendAsync(new HttpMethod(method), path, body);

      Assert.True(asViewer.StatusCode == HttpStatusCode.NotFound, $"{method} {path} answered a viewer with {asViewer.StatusCode}");
      Assert.Equal(asStranger.StatusCode, asViewer.StatusCode);
      Assert.Equal(await asStranger.Content.ReadAsStringAsync(), await asViewer.Content.ReadAsStringAsync());
    }

    // Nothing on the list changed: same playlist, same player link, the share still there.
    var after = await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}");
    Assert.Equal(before.ToString(), after.ToString());
    var shares = await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}/shares");
    Assert.Equal("Guest", shares.EnumerateArray().Single().GetProperty("label").GetString());
  }

  [Fact]
  public async Task NoViewerResponse_HoldsSourceHostCredentialsOrShareCode()
  {
    var owner = await SignedInAsync("secret-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var brokenId = await CreatePlaylistAsync(owner, BrokenUpstreamUrl);
    var (viewer, token) = await ViewerOfAsync(owner, playlistId, "secret-viewer");
    var (brokenViewer, _) = await ViewerOfAsync(owner, brokenId, "secret-broken-viewer");
    var shareCode = (await owner.Http.GetFromJsonAsync<JsonElement>($"/api/playlists/{playlistId}")).GetProperty("shareCode").GetString()!;
    Assert.False(string.IsNullOrWhiteSpace(shareCode));

    var responses = new List<(string Request, HttpResponseMessage Response)>();
    async Task<HttpResponseMessage> Record(string request, Task<HttpResponseMessage> call)
    {
      var response = await call;
      responses.Add((request, response));
      return response;
    }

    await Record("accept again", viewer.PostAsync("/api/shares/accept", new { token }));
    await Record("mine", viewer.Http.GetAsync("/api/shares/mine"));
    var opened = await Record("open", viewer.PostAsync($"/api/playlists/{playlistId}/library/open"));
    Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
    var libraryKey = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libraryKey").GetString()!;
    foreach (var query in new[] { "ARD", "ZDF", "Tatort" })
    {
      var searched = await Record($"search {query}", viewer.PostAsync($"/api/playlists/{playlistId}/library/search", new { libraryKey, query }));
      Assert.Equal(HttpStatusCode.OK, searched.StatusCode);
    }
    foreach (var group in new[] { "Germany · Live", "Germany · Series" })
    {
      var entries = await Record($"group {group}", viewer.Http.GetAsync(
        $"/api/playlists/{playlistId}/library/group-entries?libraryKey={libraryKey}&group={Uri.EscapeDataString(group)}"));
      Assert.Equal(HttpStatusCode.OK, entries.StatusCode);
    }
    await Record("film card", viewer.Http.GetAsync(
      $"/api/playlists/{playlistId}/library/film-card?libraryKey={libraryKey}&name=Tatort&group=Germany&type=Movies"));
    await Record("series card", viewer.Http.GetAsync(
      $"/api/playlists/{playlistId}/library/series-card?libraryKey={libraryKey}&name=Tatort%20S01E01&group=Germany&type=Series"));
    foreach (var (method, _, template, body) in OwnerRoutes)
    {
      var path = template.Replace("{id}", playlistId.ToString()).Replace("{shareId}", Guid.NewGuid().ToString());
      await Record(path, viewer.SendAsync(new HttpMethod(method), path, body));
    }
    // A provider failure reaches the viewer as one neutral message, never the upstream's wording.
    var failed = await Record("open broken", brokenViewer.PostAsync($"/api/playlists/{brokenId}/library/open"));
    Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
    Assert.Contains("The list cannot be loaded right now", await failed.Content.ReadAsStringAsync());

    string[] secrets =
    [
      StrimAppFactory.UpstreamUrl, BrokenUpstreamUrl, "93.184.215.14", "username=u", "password=p", "line.example", "/u/p/",
      shareCode, Uri.EscapeDataString(shareCode),
    ];
    foreach (var (request, response) in responses)
    {
      var text = await response.Content.ReadAsStringAsync() + string.Join("\n", response.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
      foreach (var secret in secrets)
      {
        Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{request} gave the viewer \"{secret}\"");
      }
    }
  }

  [Fact]
  public async Task PlaylistsAndOverview_OmitSharedLists()
  {
    var owner = await SignedInAsync("lists-owner");
    var playlistId = await CreatePlaylistAsync(owner);
    var (viewer, _) = await ViewerOfAsync(owner, playlistId, "lists-viewer");

    foreach (var path in new[] { "/api/playlists", "/api/playlists/overview" })
    {
      var ownList = await owner.Http.GetFromJsonAsync<JsonElement>(path);
      Assert.Equal(playlistId, ownList.EnumerateArray().Single().GetProperty("id").GetGuid());
      var viewerList = await viewer.Http.GetFromJsonAsync<JsonElement>(path);
      Assert.Empty(viewerList.EnumerateArray());
    }

    // The shared list reaches the viewer only through their own share list.
    var mine = await viewer.Http.GetFromJsonAsync<JsonElement>("/api/shares/mine");
    Assert.Equal(playlistId, mine.EnumerateArray().Single().GetProperty("playlistId").GetGuid());
  }
}
