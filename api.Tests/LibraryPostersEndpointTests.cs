using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Api.Tests.LibraryFilmCardTests;

namespace Api.Tests;

/// <summary>POST /api/playlists/{id}/library/posters against a stubbed TMDB.</summary>
public sealed class LibraryPostersEndpointTests
{
  private static readonly string[] Words = "alpha bravo charlie delta echo foxtrot golf hotel india juliett kilo lima mike november oscar papa".Split(' ');
  private static readonly object Series = new { name = "Harbour Lights S01 E01", group = "Serien | Drama", type = "Series" };
  private static readonly object Channel = new { name = "Harbour News HD", group = "News", type = "Live" };
  private static readonly object OneFilm = new { items = new[] { Film("Little Motel (1995)") } };

  /// <summary>Every search finds exactly the title asked for, with a poster named after it.</summary>
  private static HttpResponseMessage Echo(Uri uri)
  {
    var query = HttpUtility.ParseQueryString(uri.Query);
    var (title, year) = (query["query"] ?? "", query["year"] ?? "2001");
    var hit = new { id = 1, title, name = title, release_date = $"{year}-05-01", first_air_date = "2010-01-01", poster_path = $"/{title.Replace(' ', '-')}.jpg" };
    return Json(JsonSerializer.Serialize(new { results = new[] { hit } }));
  }
  private static object Film(string name, string group = "Filme | Drama") => new { name, group, type = "Movies" };
  private static object[] Films(IEnumerable<string> words) => words.Select(w => Film($"{w} (2001)")).ToArray();
  private static string Path(TestHost host) => $"/api/playlists/{host.PlaylistId}/library/posters";
  private static LibraryPosterMemo Memo(TestHost host) => host.Factory.Services.GetRequiredService<LibraryPosterMemo>();
  private static Task<JsonElement> PostersAsync(TestHost host, params object[] items) => PostersAsync(host, host.Client, items);
  private static async Task<JsonElement> PostersAsync(TestHost host, ApiClient client, params object[] items)
  {
    var response = await client.PostAsync(Path(host), new { cacheKey = (string?)null, libraryKey = (string?)null, items });
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
  }
  private static List<string?> All(JsonElement answer, string property = "status") =>
    answer.GetProperty("posters").EnumerateArray().Select(p => p.GetProperty(property).GetString()).ToList();

  [Fact]
  public async Task Posters_ReturnHotlinkedUrlsInRequestOrder()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    var answer = await PostersAsync(host, Film("Little Motel (1995)"), Series);
    Assert.Equal(["Little Motel (1995)", "Harbour Lights S01 E01"], All(answer, "name"));
    Assert.Equal(["Movies", "Series"], All(answer, "type"));
    Assert.Equal(["ok", "ok"], All(answer));
    Assert.Equal(["https://image.tmdb.org/t/p/w342/little-motel.jpg", "https://image.tmdb.org/t/p/w342/harbour-lights.jpg"],
      All(answer, "posterUrl"));
    Assert.True(answer.GetProperty("tmdbData").GetBoolean());
  }
  [Fact]
  public async Task Posters_AmbiguousTitle_IsNone()
  {
    const string same = """{"title":"Little Motel","release_date":"1995-01-01","poster_path":"/a.jpg"}""";
    await using var host = await TestHost.StartAsync(Token, _ => Json($$"""{"results":[{"id":1,{{same[1..]}},{"id":2,{{same[1..]}}]}"""));
    var answer = await PostersAsync(host, Film("Little Motel (1995)"));
    Assert.Equal(["none"], All(answer));
    Assert.Equal([null], All(answer, "posterUrl"));
    Assert.False(answer.GetProperty("tmdbData").GetBoolean());
  }
  [Fact]
  public async Task Posters_AdultGroup_IsNoneWithoutATmdbCall()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    var answer = await PostersAsync(host, Film("Little Motel (1995)", "XXX | Movies"), Film("Little Motel (1995)", "Für Erwachsene"));
    Assert.Equal(["none", "none"], All(answer));
    Assert.Empty(host.TmdbRequests);
  }
  [Fact]
  public async Task Posters_ChannelOrUnknownType_IsNone()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    var answer = await PostersAsync(host, Channel,
      new { name = "Little Motel (1995)", group = "Filme", type = "Podcast" }, new { name = "Little Motel (1995)", group = "Filme" });
    Assert.Equal(["none", "none", "none"], All(answer));
    Assert.Empty(host.TmdbRequests);
  }
  [Fact]
  public async Task Posters_UseTheCallersStoredMatch()
  {
    await using var host = await TestHost.StartAsync(Token, uri => uri.AbsolutePath == "/3/movie/77"
      ? Json("""{"id":77,"title":"Little Motel","release_date":"1995-01-01","poster_path":"/stored.jpg","genres":[]}""") : Echo(uri));
    await host.AddMarkAsync("Little Motel (1995)", seen: true, rating: 1, filmDbId: "77");
    var answer = await PostersAsync(host, Film("DE - Little Motel (1995) HD"));
    Assert.Equal(["https://image.tmdb.org/t/p/w342/stored.jpg"], All(answer, "posterUrl"));
    Assert.Equal("/3/movie/77", Assert.Single(host.TmdbRequests).AbsolutePath);
    // A stored id is the caller's own, so its answer is not remembered for everyone.
    Assert.Equal(0, Memo(host).Count);
  }
  [Fact]
  public async Task Posters_SecondCall_IsServedFromTheMemoWithoutTmdbCalls()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    var first = Films(Words.Take(8));
    Assert.Equal(Enumerable.Repeat("ok", 8), All(await PostersAsync(host, first)));
    Assert.Equal(Enumerable.Repeat("ok", 8), All(await PostersAsync(host, first)));
    Assert.Equal(8, host.TmdbRequests.Count);
    // Memo hits cost nothing from the budget: eight remembered and eight new titles are all looked up.
    Assert.Equal(Enumerable.Repeat("ok", 16), All(await PostersAsync(host, [.. first, .. Films(Words.Skip(8))])));
    Assert.Equal(16, host.TmdbRequests.Count);
  }
  [Fact]
  public async Task Posters_BeyondEightLookups_ArePending()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    var answer = await PostersAsync(host, [.. Films(Words.Take(10)), Film("Alpha (2001)", "XXX")]);
    Assert.Equal([.. Enumerable.Repeat("ok", 8), "pending", "pending", "none"], All(answer));
    Assert.Null(All(answer, "posterUrl")[8]);
    Assert.Equal(8, host.TmdbRequests.Count);
  }
  [Fact]
  public async Task Posters_WhenTmdbIsRateLimited_RestIsPending_AndNoFurtherCall()
  {
    await using var host = await TestHost.StartAsync(Token, uri => uri.Query.Contains("charlie")
      ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Echo(uri));
    var answer = await PostersAsync(host, [.. Films(Words.Take(5)), Channel]);
    Assert.Equal(["ok", "ok", "pending", "pending", "pending", "none"], All(answer));
    Assert.Equal(3, host.TmdbRequests.Count);
    Assert.False(Memo(host).TryGet(MarkKey.From("charlie (2001)", MarkKind.Film)!.Key, out _));
  }
  [Fact]
  public async Task Posters_OnlyTitleAndYearReachTmdb()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    await PostersAsync(host, Film("DE - Little Motel (1995) [Multi-Sub] 4K"), Series);
    Assert.Equal(["/3/search/movie?include_adult=false&query=little%20motel&year=1995",
      "/3/search/tv?include_adult=false&query=harbour%20lights"], host.TmdbRequests.Select(u => u.PathAndQuery));
    var text = Uri.UnescapeDataString(string.Join(" ", host.TmdbRequests));
    foreach (var forbidden in new[] { host.PlaylistId.ToString(), "Filme", "Serien", "Drama", "Multi", "S01", "E01", host.UserName })
      Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
  }
  [Fact]
  public async Task Posters_Viewer_IsAllowed()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    var viewer = await host.ViewerAsync("poster-viewer");
    Assert.Equal(["ok"], All(await PostersAsync(host, viewer, Film("Little Motel (1995)"))));
  }
  private static async Task ExpectAsync(string? token, HttpStatusCode expected, Func<TestHost, Task<HttpResponseMessage>> send)
  {
    await using var host = await TestHost.StartAsync(token, Echo);
    Assert.Equal(expected, (await send(host)).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }
  [Fact]
  public Task Posters_Stranger_Gets404() => ExpectAsync(Token, HttpStatusCode.NotFound, async host =>
  {
    var stranger = new ApiClient(host.Factory.CreateClient());
    await stranger.RegisterAsync("poster-stranger");
    return await stranger.PostAsync(Path(host), OneFilm);
  });
  [Fact]
  public Task Posters_WithoutSession_Gets401() =>
    ExpectAsync(Token, HttpStatusCode.Unauthorized, host => host.Factory.CreateClient().PostAsJsonAsync(Path(host), OneFilm));
  [Fact]
  public Task Posters_WithoutTmdbToken_Gets404() =>
    ExpectAsync(null, HttpStatusCode.NotFound, host => host.Client.PostAsync(Path(host), OneFilm));
  [Fact]
  public async Task Posters_NoItemsOrTooMany_Gets400()
  {
    await using var host = await TestHost.StartAsync(Token, Echo);
    foreach (var body in new object[] { new { items = Array.Empty<object>() }, new { items = Enumerable.Repeat(Film("Little Motel (1995)"), 25) },
      new { cacheKey = "x" }, new { items = new object?[] { null } }, new { items = new[] { Film(new string('a', 1025)) } },
      new { items = new[] { Film("Little Motel (1995)", new string('g', 1025)) } } })
      Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync(Path(host), body)).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }
}
