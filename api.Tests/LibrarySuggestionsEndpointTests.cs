using System.Net;
using System.Text.Json;
using Api.Data;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Api.Tests.LibraryFilmCardTests;

namespace Api.Tests;

/// <summary>GET /api/playlists/{id}/library/suggestions against a stubbed TMDB; the matching rules are in LibrarySuggestionsTests.</summary>
public sealed class LibrarySuggestionsEndpointTests
{
  private static readonly string[] Words = "alpha bravo charlie delta echo foxtrot golf hotel india juliett kilo lima mike november oscar papa".Split(' ');

  // "Filme Alt" is switched off when a test asks for it.
  private static readonly (string Group, string Name, string Kind)[] Entries =
  [
    ("Filme | Drama", "Little Motel (1995)", "movie"), ("Filme | Drama", "Blue Harbour (2003) 4K", "movie"),
    ("Filme Alt", "Old Mill (1980)", "movie"), ("Serien | Drama", "Harbour Lights S01 E01", "series"),
    ("Serien | Drama", "Quiet Bay (2015) S01 E01", "series"),
    .. Words.Select(w => ("Filme | Neu", $"{w} (2001)", "movie")),
  ];

  private static string Playlist() => "#EXTM3U\n" + string.Concat(Entries.Select((e, i) =>
    $"#EXTINF:-1 group-title=\"{e.Group}\",{e.Name}\nhttp://line.example/{e.Kind}/u/p/{i}.mkv\n"));

  private static string M(int id, string title, string date) =>
    JsonSerializer.Serialize(new { id, title, original_title = title, release_date = date, poster_path = $"/f{id}.jpg", adult = false });
  private static string T(int id, string name, string date) =>
    JsonSerializer.Serialize(new { id, name, original_name = name, first_air_date = date, poster_path = $"/s{id}.jpg" });
  private static string Page(int totalPages, IEnumerable<string> items) => $$"""{"total_pages":{{totalPages}},"results":[{{string.Join(",", items)}}]}""";
  private static string[] WordFilms(IEnumerable<string> words, int firstId) => words.Select((w, i) => M(firstId + i, w, "2001-05-01")).ToArray();

  /// <summary>TMDB answering trending pages; an unknown page answers 404.</summary>
  private static Func<Uri, HttpResponseMessage> Tmdb(string[] films, string[] series, int filmPages = 1, int seriesPages = 1,
    Dictionary<string, string>? more = null) => uri => uri.PathAndQuery switch
  {
    "/3/trending/movie/week" => Json(Page(filmPages, films)),
    "/3/trending/tv/week" => Json(Page(seriesPages, series)),
    var other when more?.GetValueOrDefault(other) is { } body => Json(body),
    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
  };

  private static Task<TestHost> StartAsync(Func<Uri, HttpResponseMessage> tmdb, string? token = Token, string[]? disabled = null) =>
    TestHost.StartAsync(token, tmdb, disabled, Playlist());

  private static string Path(TestHost host, string query) => $"/api/playlists/{host.PlaylistId}/library/suggestions?{query}";

  private static async Task<JsonElement> SuggestAsync(TestHost host, ApiClient? client = null, string extra = "")
  {
    client ??= host.Client;
    var key = await host.OpenLibraryAsync(client);
    var response = await client.Http.GetAsync(Path(host, $"source=trending&libraryKey={key}{extra}"));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
  }

  private static List<int> Ids(JsonElement answer) =>
    answer.GetProperty("suggestions").EnumerateArray().Select(s => s.GetProperty("tmdbId").GetInt32()).ToList();

  /// <summary>A mark through PUT /api/marks (an empty mark is deleted, so a stored id goes on a watchlisted one).</summary>
  private static async Task MarkAsync(TestHost host, string name, bool seen = false, bool watchlist = false, string? filmDbId = null)
  {
    Assert.Equal(HttpStatusCode.OK,
      (await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind = "film", seen, rating = 0, watchlist })).StatusCode);
    if (filmDbId is null) return;
    using var scope = host.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var key = MarkKey.From(name, MarkKind.Film)!.Key;
    db.TitleMarks.Single(m => m.TitleKey == key).FilmDbId = filmDbId;
    db.SaveChanges();
  }

  [Fact]
  public async Task Trending_ReturnsTheTitlesInTheList_InterleavedWithPosterAndMarks()
  {
    await using var host = await StartAsync(Tmdb(
      [M(1, "Little Motel", "1995-04-01"), M(2, "Absent Film", "2000-01-01"), M(3, "Blue Harbour", "2003-02-01"),
        M(603, "Nowhere Inn", "1995-04-01")],
      [T(10, "Harbour Lights", "2010-01-01"), T(11, "Quiet Bay", "2015-03-01"), T(12, "Absent Show", "2011-01-01")]));
    await MarkAsync(host, "Blue Harbour (2003)", seen: true);
    // "Old Mill" has another name, but its mark stores film 603.
    await MarkAsync(host, "Old Mill (1980)", watchlist: true, filmDbId: "603");
    var answer = await SuggestAsync(host);

    // Ranks: film 1, series 10, film 2 (absent), series 11, film 3 (seen), series 12 (absent), film 603.
    Assert.Equal([1, 10, 11, 603], Ids(answer));
    Assert.Equal("trending", answer.GetProperty("source").GetString());
    Assert.Equal(JsonValueKind.Null, answer.GetProperty("seed").ValueKind);
    Assert.Empty(answer.GetProperty("providers").EnumerateArray());
    Assert.True(answer.GetProperty("tmdbData").GetBoolean());
    var first = answer.GetProperty("suggestions")[0];
    Assert.Equal("https://image.tmdb.org/t/p/w342/f1.jpg", first.GetProperty("posterUrl").GetString());
    var title = first.GetProperty("title");
    Assert.Equal("Little Motel (1995)", title.GetProperty("name").GetString());
    Assert.Equal("Little Motel", title.GetProperty("title").GetString());
    Assert.Equal(1995, title.GetProperty("year").GetInt32());
    Assert.Equal("Movies", title.GetProperty("contentType").GetString());
    Assert.Equal("Filme | Drama", title.GetProperty("group").GetString());
    Assert.True(title.GetProperty("enabled").GetBoolean());
    Assert.Equal(JsonValueKind.Null, title.GetProperty("seen").ValueKind);
    Assert.Equal("Series", answer.GetProperty("suggestions")[1].GetProperty("title").GetProperty("contentType").GetString());
    var stored = answer.GetProperty("suggestions")[3].GetProperty("title");
    Assert.Equal("Old Mill (1980)", stored.GetProperty("name").GetString());
    Assert.True(stored.GetProperty("watchlist").GetBoolean());
  }

  [Fact]
  public async Task Trending_StopsAtTwelve_AndAsksFurtherPagesOnlyWhileShort()
  {
    await using (var full = await StartAsync(Tmdb(WordFilms(Words.Take(14), 100), [], filmPages: 3, seriesPages: 3)))
    {
      Assert.Equal(Enumerable.Range(100, 12), Ids(await SuggestAsync(full)));
      Assert.Equal(["/3/trending/movie/week", "/3/trending/tv/week"], full.TmdbRequests.Select(u => u.PathAndQuery));
    }

    // Five matches a page: page 2 is asked with 5, page 3 with 10; series stop after their only page.
    var more = new Dictionary<string, string>
    {
      ["/3/trending/movie/week?page=2"] = Page(3, WordFilms(Words.Skip(5).Take(5), 105)),
      ["/3/trending/movie/week?page=3"] = Page(3, WordFilms(Words.Skip(10).Take(5), 110)),
    };
    await using var shortHost = await StartAsync(Tmdb(WordFilms(Words.Take(5), 100), [T(50, "Absent Show", "2011-01-01")], 3, 1, more));
    Assert.Equal(Enumerable.Range(100, 12), Ids(await SuggestAsync(shortHost)));
    Assert.Equal(["/3/trending/movie/week", "/3/trending/tv/week", "/3/trending/movie/week?page=2", "/3/trending/movie/week?page=3"],
      shortHost.TmdbRequests.Select(u => u.PathAndQuery));

    // Both kinds advertise four pages and only three titles match: pages 1 to 3 of each, six requests, never page 4.
    var four = new Dictionary<string, string>();
    foreach (var kind in new[] { "movie", "tv" })
      foreach (var page in new[] { 2, 3, 4 })
        four[$"/3/trending/{kind}/week?page={page}"] = Page(4, kind == "movie" ? WordFilms(Words.Skip(page).Take(1), 200 + page) : []);
    await using var capped = await StartAsync(Tmdb([M(1, "Little Motel", "1995-04-01")], [], 4, 4, four));
    Assert.Equal([1, 202, 203], Ids(await SuggestAsync(capped)));
    Assert.Equal(["/3/trending/movie/week", "/3/trending/tv/week", "/3/trending/movie/week?page=2", "/3/trending/tv/week?page=2",
      "/3/trending/movie/week?page=3", "/3/trending/tv/week?page=3"], capped.TmdbRequests.Select(u => u.PathAndQuery));
  }

  [Fact]
  public async Task Trending_WhenTmdbIsRateLimited_AnswersWhatItHas_AndMakesNoFurtherCall()
  {
    var films = Tmdb([M(1, "Little Motel", "1995-04-01")], [], filmPages: 3, seriesPages: 3);
    await using (var host = await StartAsync(uri => uri.AbsolutePath == "/3/trending/tv/week"
      ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : films(uri)))
    {
      // TmdbClient asks its cache before its back-off, so a further call would bring film 3 from the cache.
      host.Factory.Services.GetRequiredService<TmdbCache>().Set("trending/movie/week?page=2",
        System.Text.Encoding.UTF8.GetBytes(Page(3, [M(3, "Blue Harbour", "2003-02-01")])), TmdbClient.TrendingLifetime);
      Assert.Equal([1], Ids(await SuggestAsync(host)));
      Assert.Equal(2, host.TmdbRequests.Count);
    }
    await using var down = await StartAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    var answer = await SuggestAsync(down);
    Assert.Empty(Ids(answer));
    Assert.False(answer.GetProperty("tmdbData").GetBoolean());
    Assert.Single(down.TmdbRequests);
  }

  [Fact]
  public async Task Trending_OnlyTheFixedPathsReachTmdb()
  {
    await using var host = await StartAsync(Tmdb([M(1, "Little Motel", "1995-04-01")], [T(10, "Harbour Lights", "2010-01-01")], 3, 3));
    await MarkAsync(host, "Blue Harbour (2003)", watchlist: true, filmDbId: "3");
    Assert.Equal([1, 10], Ids(await SuggestAsync(host)));
    Assert.All(host.TmdbRequests, u => Assert.Matches("^/3/trending/(movie|tv)/week(\\?page=[23])?$", u.PathAndQuery));
    var text = Uri.UnescapeDataString(string.Join(" ", host.TmdbRequests));
    foreach (var forbidden in new[] { "Motel", "Harbour", "Filme", "Drama", host.PlaylistId.ToString(), host.UserName })
      Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Trending_Viewer_SeesOnlyVisibleGroups()
  {
    await using var host = await StartAsync(Tmdb([M(4, "Old Mill", "1980-01-01"), M(1, "Little Motel", "1995-04-01")], []),
      disabled: ["Filme Alt"]);
    var viewer = await host.ViewerAsync("suggest-viewer");
    Assert.Equal([1], Ids(await SuggestAsync(host, viewer, "&includeDisabled=true")));
  }

  [Fact]
  public async Task Trending_OwnerIncludeDisabled_FindsSwitchedOffGroups()
  {
    await using var host = await StartAsync(Tmdb([M(4, "Old Mill", "1980-01-01"), M(1, "Little Motel", "1995-04-01")], []),
      disabled: ["Filme Alt"]);
    Assert.Equal([1], Ids(await SuggestAsync(host)));
    Assert.Equal([4, 1], Ids(await SuggestAsync(host, extra: "&includeDisabled=true")));
  }

  private static async Task ExpectAsync(string? token, HttpStatusCode expected, Func<TestHost, Task<HttpResponseMessage>> send)
  {
    await using var host = await StartAsync(Tmdb([M(1, "Little Motel", "1995-04-01")], []), token);
    Assert.Equal(expected, (await send(host)).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public Task Suggestions_Stranger_Gets404() => ExpectAsync(Token, HttpStatusCode.NotFound, async host =>
  {
    var stranger = new ApiClient(host.Factory.CreateClient());
    await stranger.RegisterAsync("suggest-stranger");
    return await stranger.Http.GetAsync(Path(host, "source=trending&libraryKey=abc"));
  });

  [Fact]
  public Task Suggestions_WithoutSession_Gets401() => ExpectAsync(Token, HttpStatusCode.Unauthorized,
    host => host.Factory.CreateClient().GetAsync(Path(host, "source=trending&libraryKey=abc")));

  [Fact]
  public Task Suggestions_WithoutTmdbToken_Gets404() => ExpectAsync(null, HttpStatusCode.NotFound, async host =>
    await host.Client.Http.GetAsync(Path(host, $"source=trending&libraryKey={await host.OpenLibraryAsync(host.Client)}")));

  [Fact]
  public async Task Suggestions_WithoutKeyOrWithUnknownSource_Gets400()
  {
    await using var host = await StartAsync(Tmdb([M(1, "Little Motel", "1995-04-01")], []));
    var key = await host.OpenLibraryAsync(host.Client);
    foreach (var query in new[] { "source=trending", $"libraryKey={key}", $"source=liked&libraryKey={key}", $"source=services&cacheKey={key}" })
      Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.Http.GetAsync(Path(host, query))).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public async Task Suggestions_WithoutAnIndex_AreEmpty()
  {
    await using var host = await StartAsync(Tmdb([M(1, "Little Motel", "1995-04-01")], []));
    var response = await host.Client.Http.GetAsync(Path(host, "source=trending&cacheKey=expired-key"));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    Assert.Empty(answer.GetProperty("suggestions").EnumerateArray());
    Assert.False(answer.GetProperty("tmdbData").GetBoolean());
    Assert.Empty(host.TmdbRequests);
  }
}
