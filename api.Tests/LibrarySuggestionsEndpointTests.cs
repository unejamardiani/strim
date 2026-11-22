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
    ("Serien | Drama", "Quiet Bay (2015) S01 E01", "series"), ("Serien | Neu", "Night Shift S01 E01", "series"),
    ("Serien | Neu", "Grey Coast S01 E01", "series"),
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

  private static Task<TestHost> StartAsync(Func<Uri, HttpResponseMessage> tmdb, string? token = Token, string[]? disabled = null,
    TimeProvider? time = null) =>
    TestHost.StartAsync(token, tmdb, disabled, Playlist(), time);

  private static string Path(TestHost host, string query) => $"/api/playlists/{host.PlaylistId}/library/suggestions?{query}";

  private static async Task<JsonElement> SuggestAsync(TestHost host, ApiClient? client = null, string extra = "", string source = "trending")
  {
    client ??= host.Client;
    var key = await host.OpenLibraryAsync(client);
    var response = await client.Http.GetAsync(Path(host, $"source={source}&libraryKey={key}{extra}"));
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

  /// <summary>A UTC midnight whose day number since the epoch divides by 60, so the first seed is the newest thumbs up.</summary>
  private static readonly DateTimeOffset Midnight =
    DateTimeOffset.UnixEpoch.AddDays(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400 / 60 * 60);

  private sealed class FakeTime(DateTimeOffset now) : TimeProvider
  { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }

  private static Task<TestHost> StartLikedAsync(Dictionary<string, string> answers, string[]? disabled = null, FakeTime? time = null) =>
    StartAsync(Tmdb([], [], more: answers), disabled: disabled, time: time ?? new FakeTime(Midnight.AddHours(12)));

  private static string Rec(string kind, int id, int page = 1) => $"/3/{kind}/{id}/recommendations" + (page == 2 ? "?page=2" : "");
  private static string Words3(int firstId, int count = 3, int totalPages = 1) => Page(totalPages, WordFilms(Words.Skip(firstId - 100).Take(count), firstId));

  /// <summary>A mark through PUT /api/marks; its change time (newest first by <paramref name="age"/>) and TMDB id set in the database.</summary>
  private static async Task RateAsync(TestHost host, string name, int rating = 1, string kind = "film", int? tmdbId = null,
    int age = 0, bool seen = false, bool watchlist = false, ApiClient? client = null)
  {
    Assert.Equal(HttpStatusCode.OK,
      (await (client ?? host.Client).SendAsync(HttpMethod.Put, "/api/marks", new { name, kind, seen, rating, watchlist })).StatusCode);
    using var scope = host.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var key = MarkKey.From(name, kind == "film" ? MarkKind.Film : MarkKind.Series)!.Key;
    var mark = db.TitleMarks.Single(m => m.TitleKey == key);
    (mark.UpdatedUtc, mark.FilmDbId) = (new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-age), tmdbId?.ToString());
    db.SaveChanges();
  }

  private static Task<JsonElement> LikedAsync(TestHost host, ApiClient? client = null, string extra = "") =>
    SuggestAsync(host, client, extra, "liked");

  private static string? SeedKey(JsonElement answer) =>
    answer.GetProperty("seed").ValueKind == JsonValueKind.Null ? null : answer.GetProperty("seed").GetProperty("key").GetString();

  private static List<string> Asked(TestHost host) => host.TmdbRequests.Select(u => u.PathAndQuery).ToList();

  private static void AssertEmpty(JsonElement answer)
  {
    Assert.Equal(JsonValueKind.Null, answer.GetProperty("seed").ValueKind);
    Assert.Empty(Ids(answer));
    Assert.False(answer.GetProperty("tmdbData").GetBoolean());
  }

  [Fact]
  public async Task Liked_ReturnsTheSeedAndItsRecommendationsInTheList()
  {
    await using var host = await StartLikedAsync(new()
    {
      [Rec("movie", 1)] = Page(1, [M(3, "Blue Harbour", "2003-02-01"), M(2, "Absent Film", "2000-01-01"), .. WordFilms(Words.Take(2), 100)]),
    });
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1);
    var answer = await LikedAsync(host);

    Assert.Equal([3, 100, 101], Ids(answer));
    Assert.Equal("liked", answer.GetProperty("source").GetString());
    var seed = answer.GetProperty("seed");
    Assert.Equal(("film:1995:little motel", "Little Motel", "film"),
      (seed.GetProperty("key").GetString(), seed.GetProperty("title").GetString(), seed.GetProperty("kind").GetString()));
    Assert.Empty(answer.GetProperty("providers").EnumerateArray());
    Assert.True(answer.GetProperty("tmdbData").GetBoolean());
    Assert.Equal("Blue Harbour (2003) 4K", answer.GetProperty("suggestions")[0].GetProperty("title").GetProperty("name").GetString());
    Assert.Equal([Rec("movie", 1)], Asked(host));
  }

  [Fact]
  public async Task Liked_SeedsOnlyFromThumbsUp_NotFromWatchlistOrSeen()
  {
    await using var host = await StartLikedAsync(new()
    {
      [Rec("movie", 1)] = Words3(100), [Rec("movie", 3)] = Words3(103), [Rec("movie", 4)] = Words3(106),
    });
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1, age: 2);
    await RateAsync(host, "Blue Harbour (2003)", rating: 0, tmdbId: 3, seen: true, age: 1);
    await RateAsync(host, "Old Mill (1980)", rating: 0, tmdbId: 4, watchlist: true);
    var answer = await LikedAsync(host);
    Assert.Equal("film:1995:little motel", SeedKey(answer));
    Assert.Equal([100, 101, 102], Ids(answer));
    Assert.Equal([Rec("movie", 1)], Asked(host));
  }

  [Fact]
  public async Task Liked_WithoutAThumbsUp_IsEmptyWithoutATmdbCall()
  {
    await using var host = await StartLikedAsync(new() { [Rec("movie", 1)] = Words3(100), [Rec("movie", 4)] = Words3(103) });
    await RateAsync(host, "Little Motel (1995)", rating: -1, tmdbId: 1);
    await RateAsync(host, "Old Mill (1980)", rating: 0, tmdbId: 4, watchlist: true);
    AssertEmpty(await LikedAsync(host));
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public async Task Liked_SeedChangesWithTheUtcDay_AndIsStableWithinIt()
  {
    var time = new FakeTime(Midnight.AddMinutes(30));
    await using var host = await StartLikedAsync(new()
    {
      [Rec("movie", 1)] = Words3(100), [Rec("movie", 3)] = Words3(103), [Rec("movie", 4)] = Words3(106),
    }, time: time);
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1);
    await RateAsync(host, "Blue Harbour (2003)", tmdbId: 3, age: 1);
    await RateAsync(host, "Old Mill (1980)", tmdbId: 4, age: 2);

    // Day number d with d mod 3 = 0 starts at the newest thumbs up, d + 1 at the next, d + 3 at the newest again.
    foreach (var (at, seed, first) in new[]
    {
      (TimeSpan.FromMinutes(30), "film:1995:little motel", 100), (TimeSpan.FromHours(23.5), "film:1995:little motel", 100),
      (TimeSpan.FromHours(24), "film:2003:blue harbour", 103), (TimeSpan.FromHours(47.9), "film:2003:blue harbour", 103),
      (TimeSpan.FromHours(48), "film:1980:old mill", 106), (TimeSpan.FromHours(72), "film:1995:little motel", 100),
    })
    {
      time.Now = Midnight + at;
      var answer = await LikedAsync(host);
      Assert.Equal(seed, SeedKey(answer));
      Assert.Equal(first, Ids(answer)[0]);
    }
  }

  [Fact]
  public async Task Liked_SeedWithFewerThanThree_HandsOverToTheNext_AtMostThreeSeeds()
  {
    var time = new FakeTime(Midnight.AddHours(12));
    await using var host = await StartLikedAsync(new()
    {
      [Rec("movie", 1)] = Words3(100, 2), [Rec("movie", 3)] = Words3(102, 1), [Rec("movie", 4)] = Words3(103, 2),
      [Rec("movie", 5)] = Words3(105, 5),
    }, time: time);
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1);
    await RateAsync(host, "Blue Harbour (2003)", tmdbId: 3, age: 1);
    await RateAsync(host, "Old Mill (1980)", tmdbId: 4, age: 2);
    await RateAsync(host, "Quiet Field (1999)", tmdbId: 5, age: 3);

    // Day d (d mod 4 = 0): 2, 1 and 2 suggestions; the fourth seed is never tried.
    AssertEmpty(await LikedAsync(host));
    Assert.Equal([Rec("movie", 1), Rec("movie", 3), Rec("movie", 4)], Asked(host));

    // Day d + 1 starts at the second seed and hands over to the fourth, which has 5.
    time.Now = time.Now.AddDays(1);
    var answer = await LikedAsync(host);
    Assert.Equal("film:1999:quiet field", SeedKey(answer));
    Assert.Equal([105, 106, 107, 108, 109], Ids(answer));
    Assert.Equal([Rec("movie", 1), Rec("movie", 3), Rec("movie", 4), Rec("movie", 5)], Asked(host));
  }

  [Fact]
  public async Task Liked_SeedsFromTheTenNewestThumbsUpOnly()
  {
    // Eleven thumbs up, newest first by index. The day is one on which a rotation over all eleven starts at the oldest.
    var day = Midnight.ToUnixTimeSeconds() / 86400;
    day += (10 - day % 11 + 11) % 11;
    var answers = Enumerable.Range(0, 11).ToDictionary(i => Rec("movie", 200 + i), _ => Words3(100));
    await using var host = await StartLikedAsync(answers, time: new FakeTime(DateTimeOffset.UnixEpoch.AddDays(day).AddHours(12)));
    for (var i = 0; i < 11; i++)
      await RateAsync(host, $"{Words[i]} seed (1990)", tmdbId: 200 + i, age: i);

    var expected = (int)(day % LibrarySuggestions.MaxSeedCandidates);
    var answer = await LikedAsync(host);
    Assert.Equal($"film:1990:{Words[expected]} seed", SeedKey(answer));
    Assert.Equal([Rec("movie", 200 + expected)], Asked(host));
  }

  [Fact]
  public async Task Liked_PageTwoCompletesTheSeed()
  {
    await using var host = await StartLikedAsync(new()
    {
      [Rec("movie", 1)] = Words3(100, 2, totalPages: 2), [Rec("movie", 1, 2)] = Words3(102, 1, totalPages: 2),
    });
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1);
    var answer = await LikedAsync(host);
    Assert.Equal("film:1995:little motel", SeedKey(answer));
    Assert.Equal([100, 101, 102], Ids(answer));
    Assert.Equal([Rec("movie", 1), Rec("movie", 1, 2)], Asked(host));
  }

  [Fact]
  public async Task Liked_SkipsNullItems_InSearchAndRecommendations()
  {
    const string search = "/3/search/movie?include_adult=false&query=little%20motel&year=1995";
    await using var host = await StartLikedAsync(new()
    {
      [search] = Page(1, ["null", M(1, "Little Motel", "1995-04-01")]),
      [Rec("movie", 1)] = Page(1, ["null", .. WordFilms(Words.Take(3), 100)]),
    });
    await RateAsync(host, "Little Motel (1995)");
    var answer = await LikedAsync(host);
    Assert.Equal("film:1995:little motel", SeedKey(answer));
    Assert.Equal([100, 101, 102], Ids(answer));
  }

  [Fact]
  public async Task Liked_SeedKeyIsLeftOutOfACandidate_NotTheCandidate()
  {
    // The seed "Quiet Bay" without a year shares the remake's yearless key; the remake is still found as "Quiet Bay (2015)".
    await using var host = await StartLikedAsync(new()
    {
      [Rec("tv", 11)] = Page(1, [T(20, "Quiet Bay", "2015-03-01"), T(13, "Night Shift", "2012-01-01"), T(14, "Grey Coast", "2014-01-01")]),
    });
    await RateAsync(host, "Quiet Bay", kind: "series", tmdbId: 11);
    var answer = await LikedAsync(host);
    Assert.Equal("series::quiet bay", SeedKey(answer));
    Assert.Equal([20, 13, 14], Ids(answer));
    Assert.Equal("Quiet Bay (2015) S01 E01", answer.GetProperty("suggestions")[0].GetProperty("title").GetProperty("name").GetString());
  }

  [Fact]
  public async Task Liked_UsesTheStoredTmdbId_ElseAClearSearchMatch_ElseSkipsTheSeed()
  {
    const string filmSearch = "/3/search/movie?include_adult=false&query=old%20mill&year=1980";
    const string tvSearch = "/3/search/tv?include_adult=false&query=harbour%20lights";
    await using var host = await StartLikedAsync(new()
    {
      [filmSearch] = Page(1, [M(40, "Old Mill", "1980-01-01"), M(41, "Old Mill", "1980-06-01")]),
      [Rec("movie", 40)] = Words3(110), [Rec("movie", 1)] = Words3(100, 2),
      [tvSearch] = Page(1, [T(10, "Harbour Lights", "2010-01-01"), T(12, "Harbour Lights Revisited", "2012-01-01")]),
      [Rec("tv", 10)] = Page(1, [T(11, "Quiet Bay", "2015-03-01"), T(10, "Harbour Lights", "2010-01-01"),
        T(13, "Night Shift", "2012-01-01"), T(14, "Grey Coast", "2014-01-01")]),
    });
    // Two films named "Old Mill" from 1980: no clear match, skipped. "Little Motel" stores its id: no search, 2 only.
    await RateAsync(host, "Old Mill (1980)");
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1, age: 1);
    await RateAsync(host, "Harbour Lights", kind: "series", age: 2);
    var answer = await LikedAsync(host);

    var seed = answer.GetProperty("seed");
    Assert.Equal(("series::harbour lights", "Harbour Lights", "series"),
      (seed.GetProperty("key").GetString(), seed.GetProperty("title").GetString(), seed.GetProperty("kind").GetString()));
    Assert.Equal([11, 13, 14], Ids(answer));
    Assert.Equal([filmSearch, Rec("movie", 1), tvSearch, Rec("tv", 10)], Asked(host));
  }

  [Fact]
  public async Task Liked_LeavesOutTheSeedItself_SeenAndThumbsDown()
  {
    await using var host = await StartLikedAsync(new()
    {
      [Rec("movie", 1)] = Page(1, [M(1, "Little Motel", "1995-04-01"), M(99, "Little Motel", "1995-04-01"),
        M(3, "Blue Harbour", "2003-02-01"), M(4, "Old Mill", "1980-01-01"), .. WordFilms(Words.Take(3), 100)]),
    });
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1);
    await RateAsync(host, "Blue Harbour (2003)", rating: 0, seen: true, age: 1);
    await RateAsync(host, "Old Mill (1980)", rating: -1, age: 2);
    var answer = await LikedAsync(host);
    Assert.Equal("film:1995:little motel", SeedKey(answer));
    Assert.Equal([100, 101, 102], Ids(answer));
  }

  [Fact]
  public async Task Liked_NeverMakesMoreThanEightTmdbCalls()
  {
    // Three seeds without a stored id, each a search and two pages: the third cannot ask its page 2 and is dropped.
    var answers = new Dictionary<string, string>();
    foreach (var (name, year, date, id, first) in new[]
    {
      ("little%20motel", 1995, "1995-04-01", 1, 100), ("blue%20harbour", 2003, "2003-02-01", 3, 102), ("old%20mill", 1980, "1980-01-01", 4, 104),
    })
    {
      answers[$"/3/search/movie?include_adult=false&query={name}&year={year}"] =
        Page(1, [M(id, Uri.UnescapeDataString(name), date)]);
      answers[Rec("movie", id)] = Words3(first, id == 4 ? 3 : 1, totalPages: 2);
      answers[Rec("movie", id, 2)] = Words3(first + 1, 1, totalPages: 2);
    }
    await using var host = await StartLikedAsync(answers);
    await RateAsync(host, "Little Motel (1995)");
    await RateAsync(host, "Blue Harbour (2003)", age: 1);
    await RateAsync(host, "Old Mill (1980)", age: 2);

    AssertEmpty(await LikedAsync(host));
    Assert.Equal(Api.Endpoints.LibrarySuggestionsEndpoints.MaxTmdbCalls, host.TmdbRequests.Count);
    Assert.Equal(Rec("movie", 4), Asked(host)[^1]);
  }

  [Fact]
  public async Task Liked_WhenTmdbIsRateLimited_IsEmptyOrPartial_AndMakesNoFurtherCall()
  {
    async Task<(JsonElement, List<string>)> RunAsync(Dictionary<string, string> answers, string failing, HttpStatusCode status, int? firstId)
    {
      var ok = Tmdb([], [], more: answers);
      await using var host = await StartAsync(uri => uri.PathAndQuery == failing ? new HttpResponseMessage(status) : ok(uri),
        time: new FakeTime(Midnight.AddHours(12)));
      // TmdbClient asks its cache before its back-off, so trying the second seed would bring its winning rail from the cache.
      host.Factory.Services.GetRequiredService<TmdbCache>().Set("movie/3/recommendations",
        System.Text.Encoding.UTF8.GetBytes(Words3(110)), TmdbClient.RecommendationsLifetime);
      await RateAsync(host, "Little Motel (1995)", tmdbId: firstId);
      await RateAsync(host, "Blue Harbour (2003)", tmdbId: 3, age: 1);
      return (await LikedAsync(host), Asked(host));
    }
    var next = new Dictionary<string, string> { [Rec("movie", 3)] = Words3(110) };

    // Page 1 rate limited: empty, and the second seed is not tried.
    var (answer, asked) = await RunAsync(next, Rec("movie", 1), HttpStatusCode.TooManyRequests, 1);
    AssertEmpty(answer);
    Assert.Equal([Rec("movie", 1)], asked);

    // Page 2 rate limited while page 1 gave 3 of the 12: the seed is unfinished, so empty, and the second seed is not tried.
    (answer, asked) = await RunAsync(new(next) { [Rec("movie", 1)] = Words3(100, 3, totalPages: 2) },
      Rec("movie", 1, 2), HttpStatusCode.TooManyRequests, 1);
    AssertEmpty(answer);
    Assert.Equal([Rec("movie", 1), Rec("movie", 1, 2)], asked);

    // A search TMDB cannot answer: empty, nothing further.
    const string search = "/3/search/movie?include_adult=false&query=little%20motel&year=1995";
    (answer, asked) = await RunAsync(next, search, HttpStatusCode.ServiceUnavailable, null);
    AssertEmpty(answer);
    Assert.Equal([search], asked);
  }

  [Fact]
  public async Task Liked_Viewer_UsesTheirOwnMarksAndVisibleGroups()
  {
    await using var host = await StartLikedAsync(new()
    {
      [Rec("movie", 3)] = Words3(110),
      [Rec("movie", 1)] = Page(1, [M(4, "Old Mill", "1980-01-01"), .. WordFilms(Words.Take(3), 100)]),
    }, disabled: ["Filme Alt"]);
    await RateAsync(host, "Blue Harbour (2003)", tmdbId: 3);
    var viewer = await host.ViewerAsync("suggest-viewer");
    await RateAsync(host, "Little Motel (1995)", tmdbId: 1, age: 1, client: viewer);

    var answer = await LikedAsync(host, viewer, "&includeDisabled=true");
    Assert.Equal("film:1995:little motel", SeedKey(answer));
    Assert.Equal([100, 101, 102], Ids(answer));
    Assert.Equal([Rec("movie", 1)], Asked(host));
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
    foreach (var query in new[] { "source=trending", "source=liked", $"libraryKey={key}", $"source=popular&libraryKey={key}", $"source=services&cacheKey={key}" })
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
