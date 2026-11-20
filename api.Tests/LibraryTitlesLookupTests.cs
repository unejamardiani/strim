using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>The titles lookup (#417): where marked titles are in the visible list, by mark key, in one index scan.</summary>
public class LibraryTitlesLookupTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private static readonly string Playlist = LibraryTitlesEndpointTests.Playlist;
  private static readonly string Heat = MarkKey.From("Heat (1995)", MarkKind.Film)!.Key;
  private static readonly string Vertigo = MarkKey.From("Vertigo (1958)", MarkKind.Film)!.Key;
  private static readonly string Harbour = MarkKey.From("Harbour Lights S01E01", MarkKind.Series)!.Key;

  private async Task<ApiClient> SignedInAsync()
  {
    factory.UpstreamPlaylists[StrimAppFactory.UpstreamUrl] = Playlist;
    var client = new ApiClient(factory.CreateClient());
    (await client.RegisterAsync("lookup-" + Guid.NewGuid().ToString("N")[..10])).EnsureSuccessStatusCode();
    return client;
  }

  private async Task<(ApiClient Client, Guid Id, string CacheKey)> OwnerAsync(params string[] disabledGroups)
  {
    var client = await SignedInAsync();
    var created = await client.PostAsync("/api/playlists", new { name = "Sofa list", sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups });
    var cacheKey = (await Json(await client.PostAsync("/api/playlist/analyze", new { rawText = Playlist }))).GetProperty("cacheKey").GetString()!;
    return (client, (await Json(created)).GetProperty("id").GetGuid(), cacheKey);
  }

  private static async Task<JsonElement> Json(HttpResponseMessage response) =>
    await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();

  // Sent without a CSRF token: the lookup is read-only.
  private static Task<HttpResponseMessage> LookupAsync(ApiClient client, Guid id, object body) =>
    client.PostAsync($"/api/playlists/{id}/library/titles/lookup", body, withCsrf: false);

  private static async Task<Dictionary<string, JsonElement>> FoundAsync(ApiClient client, Guid id, object body) =>
    (await Json(await LookupAsync(client, id, body))).GetProperty("titles").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

  private static string Fields(JsonElement title, params string[] names) => string.Join(" ", names.Select(n => title.GetProperty(n).GetRawText()));

  [Fact]
  public async Task Lookup_ReturnsTitlesForKnownKeysOnly()
  {
    var (client, id, cacheKey) = await OwnerAsync();
    (await client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "Heat (1995)", kind = "film", watchlist = true, rating = 1 })).EnsureSuccessStatusCode();
    var found = await FoundAsync(client, id, new { cacheKey, keys = new[] { Heat, "film:2001|unknown title" } });
    Assert.Equal(new[] { Heat }, found.Keys);
    Assert.Equal($"\"Heat\" 1995 \"Movies\" \"{Heat}\" \"Films\" true 2 1 false 1 true",
      Fields(found[Heat], "title", "year", "contentType", "markKey", "group", "enabled", "entries", "groups", "seen", "rating", "watchlist"));
  }

  [Fact]
  public async Task Lookup_Series_ReportsSeasonsAndEpisodes()
  {
    var (client, id, cacheKey) = await OwnerAsync();
    var found = await FoundAsync(client, id, new { cacheKey, keys = new[] { Harbour } });
    Assert.Equal("\"Harbour Lights\" \"Series\" 3 1 3 3", Fields(found[Harbour], "title", "contentType", "entries", "seasonFirst", "seasonLast", "episodes"));
  }

  [Fact]
  public async Task Lookup_Viewer_NeverReturnsTitlesOfHiddenGroups()
  {
    var (owner, id, ownerKey) = await OwnerAsync("Old Films");
    var keys = new[] { Heat, Vertigo };
    Assert.Equal(new[] { Heat }, (await FoundAsync(owner, id, new { cacheKey = ownerKey, keys })).Keys);
    Assert.Equal("false", Fields((await FoundAsync(owner, id, new { cacheKey = ownerKey, includeDisabled = true, keys }))[Vertigo], "enabled"));

    var token = (await Json(await owner.PostAsync($"/api/playlists/{id}/shares", new { label = "Guest" }))).GetProperty("inviteToken").GetString();
    var viewer = await SignedInAsync();
    (await viewer.PostAsync("/api/shares/accept", new { token })).EnsureSuccessStatusCode();
    var libraryKey = (await Json(await viewer.PostAsync($"/api/playlists/{id}/library/open"))).GetProperty("libraryKey").GetString();
    Assert.Equal(new[] { Heat }, (await FoundAsync(viewer, id, new { libraryKey, includeDisabled = true, keys })).Keys);
  }

  [Fact]
  public async Task Lookup_Stranger_Gets404()
  {
    var (_, id, cacheKey) = await OwnerAsync();
    var stranger = await SignedInAsync();
    Assert.Equal(HttpStatusCode.NotFound, (await LookupAsync(stranger, id, new { cacheKey, keys = new[] { Heat } })).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await LookupAsync(stranger, Guid.NewGuid(), new { cacheKey, keys = new[] { Heat } })).StatusCode);
  }

  [Fact]
  public async Task Lookup_NoOrTooManyKeys_Gets400()
  {
    var (client, id, cacheKey) = await OwnerAsync();
    foreach (var keys in new[] { null, [], Enumerable.Repeat(Heat, 201).ToArray(), [new string('x', MarkKey.MaxKeyLength + 1)], [""] })
    {
      var response = await LookupAsync(client, id, new { cacheKey, keys });
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out _));
    }
    Assert.Equal(HttpStatusCode.OK, (await LookupAsync(client, id, new { cacheKey, keys = Enumerable.Repeat(Heat, 200) })).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await LookupAsync(client, id, new { keys = new[] { Heat } })).StatusCode);
  }

  [Fact]
  public async Task Lookup_WithoutSession_Gets401()
  {
    var (_, id, cacheKey) = await OwnerAsync();
    var response = await factory.CreateClient().PostAsJsonAsync($"/api/playlists/{id}/library/titles/lookup", new { cacheKey, keys = new[] { Heat } });
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Lookup_ScansTheIndexOnce()
  {
    var (client, id, cacheKey) = await OwnerAsync();
    var cache = factory.Services.GetRequiredService<PlaylistPipeline>().Cache;
    var index = cache.TryGetAnalysis(cache.TryGetSessionSource(cacheKey)!)!.SearchIndex!;
    var passes = index.PassCount;
    Assert.Equal(3, (await FoundAsync(client, id, new { cacheKey, keys = new[] { Heat, Vertigo, Harbour } })).Count);
    Assert.Equal(passes + 1, index.PassCount);
  }
}
