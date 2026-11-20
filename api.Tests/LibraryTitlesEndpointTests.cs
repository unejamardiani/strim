using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>The Library titles route (#416): collapsed titles with the caller's marks, filters, paging and counts.</summary>
public class LibraryTitlesEndpointTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  internal static readonly string Playlist = "#EXTM3U\n" + string.Concat(new[]
  {
    ("Films", "Heat (1995)", "movie"), ("Films", "Alien (1979)", "movie"), ("Films", "Arrival (2016)", "movie"),
    ("Films", "DE - Heat (1995) 4K", "movie"), ("Serien", "Harbour Lights S01E01", "series"),
    ("Serien", "Harbour Lights S01E02", "series"), ("Serien", "Harbour Lights S03E01", "series"),
    ("Serien", "Night Shift S01E01", "series"), ("News", "Harbour TV", "live"), ("Old Films", "Vertigo (1958)", "movie"),
  }.Select((e, i) => $"#EXTINF:-1 group-title=\"{e.Item1}\",{e.Item2}\nhttp://line.example/{e.Item3}/u/p/{i}.mkv\n"));

  private async Task<ApiClient> SignedInAsync()
  {
    factory.UpstreamPlaylists[StrimAppFactory.UpstreamUrl] = Playlist;
    var client = new ApiClient(factory.CreateClient());
    (await client.RegisterAsync("titles-" + Guid.NewGuid().ToString("N")[..10])).EnsureSuccessStatusCode();
    return client;
  }

  private async Task<(ApiClient Client, Guid Id, string Key)> OwnerAsync(params string[] disabledGroups)
  {
    var client = await SignedInAsync();
    var created = await client.PostAsync("/api/playlists", new { name = "Sofa list", sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups });
    return (client, (await Json(created)).GetProperty("id").GetGuid(), await AnalyzeAsync(client));
  }

  private static async Task<string> AnalyzeAsync(ApiClient client, string? rawText = null) => "cacheKey=" + Uri.EscapeDataString(
    (await Json(await client.PostAsync("/api/playlist/analyze", new { rawText = rawText ?? Playlist }))).GetProperty("cacheKey").GetString()!);

  private async Task<(ApiClient Viewer, string Key)> ViewerAsync(ApiClient owner, Guid id)
  {
    var token = (await Json(await owner.PostAsync($"/api/playlists/{id}/shares", new { label = "Guest" }))).GetProperty("inviteToken").GetString();
    var viewer = await SignedInAsync();
    (await viewer.PostAsync("/api/shares/accept", new { token })).EnsureSuccessStatusCode();
    var opened = await Json(await viewer.PostAsync($"/api/playlists/{id}/library/open"));
    return (viewer, "libraryKey=" + Uri.EscapeDataString(opened.GetProperty("libraryKey").GetString()!));
  }

  private static async Task<JsonElement> Json(HttpResponseMessage response) =>
    await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
  private static async Task MarkAsync(ApiClient client, object mark) =>
    (await client.SendAsync(HttpMethod.Put, "/api/marks", mark)).EnsureSuccessStatusCode();
  private static Task<HttpResponseMessage> GetAsync(ApiClient client, Guid id, string query) =>
    client.Http.GetAsync($"/api/playlists/{id}/library/titles?{query}");
  private static async Task<JsonElement> OkAsync(ApiClient client, Guid id, string query) => await Json(await GetAsync(client, id, query));

  private static Dictionary<string, JsonElement> ByTitle(JsonElement body) =>
    body.GetProperty("titles").EnumerateArray().ToDictionary(t => t.GetProperty("title").GetString()!);

  private static string Titles(JsonElement body) =>
    string.Join(", ", body.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("title").GetString()));

  /// <summary>movies series live total hiddenCount seenHiddenCount hasMore.</summary>
  private static string Counts(JsonElement body) => string.Join(" ", new[] { "movies", "series", "live", "total", "hiddenCount", "seenHiddenCount", "hasMore" }
    .Select(p => (p is "movies" or "series" or "live" ? body.GetProperty("counts") : body).GetProperty(p).GetRawText()));

  private static string Field(JsonElement title, string name) => title.GetProperty(name).GetRawText();

  [Fact]
  public async Task Titles_OwnerWithCacheKey_ReturnsCollapsedSeriesWithMarks()
  {
    var (client, id, key) = await OwnerAsync();
    await MarkAsync(client, new { name = "Harbour Lights S02E01", kind = "series", seen = true, rating = 1 });
    var body = await OkAsync(client, id, key);
    var titles = ByTitle(body);
    Assert.Equal("Heat, Alien, Arrival, Harbour Lights, Night Shift, Harbour TV, Vertigo", Titles(body));
    Assert.Equal("4 2 1 7 0 0 false", Counts(body));
    var series = titles["Harbour Lights"];
    Assert.Equal("\"Harbour Lights S01E01\" \"Series\" 3 1 3 3 true 1 false", string.Join(" ",
      new[] { "name", "contentType", "entries", "seasonFirst", "seasonLast", "episodes", "seen", "rating", "watchlist" }.Select(f => Field(series, f))));
    Assert.Equal("2 1995 null", $"{Field(titles["Heat"], "entries")} {Field(titles["Heat"], "year")} {Field(titles["Heat"], "seen")}");
    Assert.Equal("null", Field(titles["Harbour TV"], "markKey"));
    Assert.Equal("0 30", $"{Field(body, "offset")} {Field(body, "limit")}");
  }

  [Fact]
  public async Task Titles_PerType_ReturnsUpToNOfEachTypeAndAllCounts()
  {
    var (client, id, key) = await OwnerAsync();
    var body = await OkAsync(client, id, key + "&perType=1&offset=5&limit=1");
    Assert.Equal("Heat, Harbour Lights, Harbour TV", Titles(body));
    Assert.Equal("4 2 1 7 0 0 true", Counts(body));
    var films = await OkAsync(client, id, key + "&perType=2&types=movies");
    Assert.Equal("Heat, Alien", Titles(films));
    Assert.Equal("4 2 1 4 0 0 true", Counts(films));
  }

  [Fact]
  public async Task Titles_Group_PagesWithOffsetAndLimit()
  {
    var (client, id, key) = await OwnerAsync();
    var group = "&group=" + Uri.EscapeDataString(ByTitle(await OkAsync(client, id, key))["Alien"].GetProperty("groupKey").GetString()!);
    var first = await OkAsync(client, id, key + group + "&limit=2");
    Assert.Equal("Heat, Alien", Titles(first));
    Assert.Equal("3 0 0 3 0 0 true", Counts(first));
    var second = await OkAsync(client, id, key + group + "&limit=2&offset=2");
    Assert.Equal("Arrival", Titles(second));
    Assert.Equal("3 0 0 3 0 0 false", Counts(second));
    Assert.Equal("2", Field(second, "offset"));
  }

  [Fact]
  public async Task Titles_HideSeen_LeavesSeenTitlesOutAndCountsThem()
  {
    var (client, id, key) = await OwnerAsync();
    await MarkAsync(client, new { name = "Heat (1995)", kind = "film", seen = true, rating = 0 });
    var body = await OkAsync(client, id, key + "&hideSeen=true");
    Assert.Equal("Alien, Arrival, Harbour Lights, Night Shift, Harbour TV, Vertigo", Titles(body));
    Assert.Equal("3 2 1 6 0 1 false", Counts(body));
  }

  [Fact]
  public async Task Titles_Liked_KeepsOnlyThumbsUpTitles()
  {
    var (client, id, key) = await OwnerAsync();
    await MarkAsync(client, new { name = "Alien (1979)", kind = "film", seen = false, rating = 1 });
    await MarkAsync(client, new { name = "Heat (1995)", kind = "film", seen = true, rating = -1 });
    var body = await OkAsync(client, id, key + "&liked=true");
    Assert.Equal("Alien", Titles(body));
    Assert.Equal("1 0 0 1 0 0 false", Counts(body));
  }

  [Fact]
  public async Task Titles_CarryTheWatchlistFlag()
  {
    var (client, id, key) = await OwnerAsync();
    await MarkAsync(client, new { name = "Arrival (2016)", kind = "film", watchlist = true });
    await MarkAsync(client, new { name = "Heat (1995)", kind = "film", seen = true, rating = 0 });
    var titles = ByTitle(await OkAsync(client, id, key));
    Assert.Equal("true false null", string.Join(" ", new[] { "Arrival", "Heat", "Alien" }.Select(t => Field(titles[t], "watchlist"))));
  }

  [Fact]
  public async Task Titles_HiddenCount_CountsTitlesInSwitchedOffGroups()
  {
    var (client, id, key) = await OwnerAsync("Old Films");
    var body = await OkAsync(client, id, key);
    Assert.DoesNotContain("Vertigo", Titles(body));
    Assert.Equal("3 2 1 6 1 0 false", Counts(body));
  }

  [Fact]
  public async Task Titles_IncludeDisabled_ShowsThemWithEnabledFalse()
  {
    var (client, id, key) = await OwnerAsync("Old Films");
    var body = await OkAsync(client, id, key + "&includeDisabled=true");
    Assert.Equal("false true", $"{Field(ByTitle(body)["Vertigo"], "enabled")} {Field(ByTitle(body)["Heat"], "enabled")}");
    Assert.Equal("4 2 1 7 0 0 false", Counts(body));
  }

  [Fact]
  public async Task Titles_Viewer_SeesOnlyVisibleGroupsWhateverTheRequestAsks()
  {
    var (owner, id, ownerKey) = await OwnerAsync("Old Films");
    var oldFilms = ByTitle(await OkAsync(owner, id, ownerKey + "&includeDisabled=true"))["Vertigo"].GetProperty("groupKey").GetString()!;
    var (viewer, key) = await ViewerAsync(owner, id);
    var all = await OkAsync(viewer, id, key + "&includeDisabled=true");
    Assert.DoesNotContain("Vertigo", Titles(all));
    Assert.Equal("3 2 1 6 0 0 false", Counts(all));
    var hidden = await OkAsync(viewer, id, key + "&includeDisabled=true&group=" + Uri.EscapeDataString(oldFilms));
    Assert.Equal("", Titles(hidden));
    Assert.Equal("0 0 0 0 0 0 false", Counts(hidden));
  }

  [Fact]
  public async Task Titles_Stranger_Gets404()
  {
    var (_, id, ownerKey) = await OwnerAsync();
    var stranger = await SignedInAsync();
    Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(stranger, id, await AnalyzeAsync(stranger))).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(stranger, id, ownerKey)).StatusCode);
  }

  [Fact]
  public async Task Titles_ViewerWithEditorCacheKey_Gets404()
  {
    var (owner, id, ownerKey) = await OwnerAsync();
    var (viewer, _) = await ViewerAsync(owner, id);
    Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(viewer, id, await AnalyzeAsync(viewer))).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(viewer, id, ownerKey)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(viewer, id, "libraryKey=lib-unknown")).StatusCode);
  }

  [Theory]
  [InlineData("&q=a")]
  [InlineData("&q=--")]
  [InlineData("&q=long")]
  [InlineData("&types=Movies,Radio")]
  [InlineData("&limit=0")]
  [InlineData("&limit=61")]
  [InlineData("&perType=11")]
  [InlineData("&offset=-1")]
  [InlineData("")]
  public async Task Titles_BadParameters_Get400(string extra)
  {
    var (client, id, key) = await OwnerAsync();
    var response = await GetAsync(client, id, extra == "" ? "" : key + (extra == "&q=long" ? "&q=" + new string('x', 101) : extra));
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out _));
  }

  [Fact]
  public async Task Titles_ExpiredOwnerKey_GetsTheSearchMessage()
  {
    var (client, id, _) = await OwnerAsync();
    var response = await GetAsync(client, id, "cacheKey=expired");
    Assert.Equal("{\"error\":\"The playlist session expired. Analyze the source again.\"}", await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Titles_SecondPage_DoesNotScanTheIndexAgain()
  {
    var (client, id, key) = await OwnerAsync();
    var cache = factory.Services.GetRequiredService<PlaylistPipeline>().Cache;
    var index = cache.TryGetAnalysis(cache.TryGetSessionSource(Uri.UnescapeDataString(key["cacheKey=".Length..]))!)!.SearchIndex!;
    Assert.Equal("Harbour Lights", Titles(await OkAsync(client, id, key + "&q=harbour&limit=1")));
    var passes = index.PassCount;
    Assert.Equal("Harbour TV", Titles(await OkAsync(client, id, key + "&q=harbour&limit=1&offset=1")));
    Assert.Equal(passes, index.PassCount);
    Assert.Equal("Harbour TV", Titles(await OkAsync(client, id, key + "&q=harbour&sort=title&limit=1&offset=1")));
    Assert.Equal(passes, index.PassCount);
    await OkAsync(client, id, key + "&q=heat");
    Assert.Equal(passes + 1, index.PassCount);
  }

  [Fact]
  public async Task Titles_SortTitle_OrdersByTitleIgnoringCase()
  {
    var (client, id, _) = await OwnerAsync();
    var key = await AnalyzeAsync(client, "#EXTM3U\n" + string.Concat(new[] { "zulu (2013)", "Bravo (2001)", "alpha (2000)", "Bravo (2001) 4K", "Charlie (1999)" }
      .Select((n, i) => $"#EXTINF:-1 group-title=\"Films\",{n}\nhttp://line.example/movie/u/p/{i}.mkv\n")));
    Assert.Equal("alpha, Bravo, Charlie, zulu", Titles(await OkAsync(client, id, key + "&sort=title")));
    Assert.Equal("zulu, Bravo, alpha, Charlie", Titles(await OkAsync(client, id, key + "&sort=playlist")));
  }

  [Fact]
  public async Task Titles_SortYear_NewestFirstUnknownLast()
  {
    var (client, id, key) = await OwnerAsync();
    Assert.Equal("Arrival, Heat, Alien, Vertigo, Harbour Lights, Night Shift, Harbour TV", Titles(await OkAsync(client, id, key + "&sort=year")));
  }

  [Fact]
  public async Task Titles_SortUnknown_Gets400()
  {
    var (client, id, key) = await OwnerAsync();
    var response = await GetAsync(client, id, key + "&sort=rating");
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains("playlist, title, year", await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Titles_Sort_AppliesBeforePaging()
  {
    var (client, id, key) = await OwnerAsync();
    Assert.Equal("Alien, Arrival, Harbour Lights", Titles(await OkAsync(client, id, key + "&sort=title&limit=3")));
    var second = await OkAsync(client, id, key + "&sort=title&limit=3&offset=3");
    Assert.Equal("Harbour TV, Heat, Night Shift", Titles(second));
    Assert.Equal("4 2 1 7 0 0 true", Counts(second));
  }

  [Fact]
  public async Task Titles_Lang_KeepsTitlesWithOneOfTheLanguages()
  {
    var (client, id, key) = await OwnerAsync();
    var body = await OkAsync(client, id, key + "&lang=en,DE");
    Assert.Equal("Heat", Titles(body));
    Assert.Equal("\"DE - Heat (1995) 4K\" 1", $"{Field(ByTitle(body)["Heat"], "name")} {Field(ByTitle(body)["Heat"], "entries")}");
  }

  [Theory]
  [InlineData("d")]
  [InlineData("de,e1")]
  [InlineData("a-b-c")]
  [InlineData("de,en,fr,it,es,pt,nl,pl,cs,sk,hu,ro,bg,el,tr,ru,uk,sv,da,no,fi")]
  public async Task Titles_Lang_Invalid_Gets400(string lang)
  {
    var (client, id, key) = await OwnerAsync();
    var response = await GetAsync(client, id, key + "&lang=" + lang);
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal("Give at most 20 languages or tags, separated by commas, each 2 to 5 letters with at most one hyphen (de, UK, EX-YU).",
      (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
  }

  [Fact]
  public async Task Titles_ReturnLanguageCounts()
  {
    var (client, id, key) = await OwnerAsync();
    var languages = (await OkAsync(client, id, key)).GetProperty("languages");
    Assert.Equal("[{\"value\":\"de\",\"kind\":\"language\",\"count\":1}]", languages.GetRawText());
  }

  [Fact]
  public async Task Titles_WorkWithoutTmdbToken()
  {
    Assert.False(factory.Services.GetRequiredService<TmdbClient>().IsEnabled);
    var (client, id, key) = await OwnerAsync();
    Assert.Equal("Harbour Lights, Night Shift", Titles(await OkAsync(client, id, key + "&types=Series")));
  }
}
