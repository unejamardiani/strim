using System.Net;
using System.Text.Json;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Api.Tests.LibraryFilmCardTests;

namespace Api.Tests;

/// <summary>GET /api/playlists/{id}/library/series-card against a stubbed TMDB.</summary>
public sealed class LibrarySeriesCardTests
{
  private const string Dark = """{"id":70523,"name":"Dark","first_air_date":"2017-12-01","poster_path":"/d.jpg"}""";

  private const string DarkDetails = """
    {"id":70523,"name":"Dark","overview":"A missing child.","first_air_date":"2017-12-01","last_air_date":"2020-06-27",
     "status":"Ended","number_of_seasons":3,"number_of_episodes":26,"poster_path":"/d.jpg","vote_average":8.4,
     "genres":[{"id":18,"name":"Drama"}],"networks":[{"id":213,"name":"Netflix"}],
     "seasons":[{"season_number":0,"episode_count":3},{"season_number":1,"episode_count":10},
       {"season_number":2,"episode_count":8},{"season_number":3,"episode_count":8}],
     "similar":{"results":[{"id":2,"name":"Heat","first_air_date":"2001-01-01"},{"id":4607,"name":"Lost","first_air_date":"2004-09-22"}]}}
    """;

  // "Shows Old" is switched off on the saved playlist; the adult group and the film never count.
  private const string SeriesList = """
    #EXTM3U
    #EXTINF:-1 group-title="Shows DE",Dark S01E01
    http://line.example/series/u/p/1.mkv
    #EXTINF:-1 group-title="Shows DE",Dark S01E02
    http://line.example/series/u/p/2.mkv
    #EXTINF:-1 group-title="Shows EN",Dark S01E01 FHD
    http://line.example/series/u/p/3.mkv
    #EXTINF:-1 group-title="Shows EN",Dark S03E02
    http://line.example/series/u/p/4.mkv
    #EXTINF:-1 group-title="Shows EN",Dark S05E01
    http://line.example/series/u/p/5.mkv
    #EXTINF:-1 group-title="Shows DE",Dark S02
    http://line.example/series/u/p/6.mkv
    #EXTINF:-1 group-title="Shows Old",Dark S04E01
    http://line.example/series/u/p/7.mkv
    #EXTINF:-1 group-title="XXX Shows",Dark S01E07
    http://line.example/series/u/p/8.mkv
    #EXTINF:-1 group-title="Films",Dark S01E09
    http://line.example/movie/u/p/9.mkv
    #EXTINF:-1 group-title="Shows DE",Lost S01E01
    http://line.example/series/u/p/10.mkv
    """;

  private static HttpResponseMessage Tmdb(Uri request) => request.AbsolutePath switch
  {
    "/3/search/tv" => Json($$"""{"results":[{{Dark}},{"id":9,"name":"Dark Matter","first_air_date":"2015-06-12"}]}"""),
    "/3/tv/70523" => Json(DarkDetails),
    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
  };

  private static string CardPath(TestHost host, string name, string group = "Shows DE", string type = "Series") =>
    $"/api/playlists/{host.PlaylistId}/library/series-card?name={Uri.EscapeDataString(name)}"
    + $"&group={Uri.EscapeDataString(group)}&type={type}";

  private static async Task<JsonElement> CardAsync(TestHost host, string name, string query = "", string group = "Shows DE", string type = "Series")
  {
    var response = await host.Client.Http.GetAsync(CardPath(host, name, group, type) + query);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
  }

  [Fact]
  public async Task NoToken_404_LibraryUnchanged()
  {
    await using var host = await TestHost.StartAsync(token: null, Tmdb, upstream: SeriesList);

    Assert.Equal(HttpStatusCode.NotFound, (await host.Client.Http.GetAsync(CardPath(host, "Dark S01E01"))).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"/api/playlists/{host.PlaylistId}/library/open", null)).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public async Task SendsOnlyCleanTitleAndYear()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json("""{"results":[]}"""));

    await CardAsync(host, "DE - Breaking Bad (2008) S02E05 [Multi-Sub] 4K", group: "Serien | Drama");

    var uri = Assert.Single(host.TmdbRequests);
    Assert.Equal("/3/search/tv?include_adult=false&query=breaking%20bad&first_air_date_year=2008", uri.PathAndQuery);
    var text = Uri.UnescapeDataString(uri.ToString());
    Assert.DoesNotContain(host.PlaylistId.ToString(), text);
    foreach (var forbidden in new[] { "S02", "E05", "Serien", "Multi", "username", "password", host.UserName })
      Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("Movies", "Shows DE")]
  [InlineData("Live", "Shows DE")]
  [InlineData("Series", "XXX | Series")]
  [InlineData("Series", "Für Erwachsene")]
  public async Task FilmChannelOrAdult_SendsNothing(string type, string group)
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);

    var card = await CardAsync(host, "Dark S01E01", group: group, type: type);

    Assert.Equal("notApplicable", card.GetProperty("status").GetString());
    Assert.Equal(JsonValueKind.Null, card.GetProperty("series").ValueKind);
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public async Task ClearMatch_ReturnsFacts()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);

    var card = await CardAsync(host, "Dark S01E01");

    Assert.Equal("matched", card.GetProperty("status").GetString());
    Assert.True(card.GetProperty("tmdbData").GetBoolean());
    var series = card.GetProperty("series");
    Assert.Equal(70523, series.GetProperty("id").GetInt32());
    Assert.Equal("Dark", series.GetProperty("title").GetString());
    Assert.Equal(2017, series.GetProperty("firstYear").GetInt32());
    Assert.Equal(2020, series.GetProperty("lastYear").GetInt32());
    Assert.Equal("ended", series.GetProperty("state").GetString());
    Assert.Equal(3, series.GetProperty("seasonCount").GetInt32());
    Assert.Equal(26, series.GetProperty("episodeCount").GetInt32());
    Assert.Equal("Netflix", series.GetProperty("network").GetString());
    Assert.Equal("https://image.tmdb.org/t/p/w342/d.jpg", series.GetProperty("posterUrl").GetString());
    Assert.Equal(8.4, series.GetProperty("rating").GetDouble());
    Assert.Equal(["Drama"], series.GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
    Assert.Equal("A missing child.", series.GetProperty("overview").GetString());
    Assert.Equal(["/3/search/tv", "/3/tv/70523"], host.TmdbRequests.Select(u => u.AbsolutePath));
    Assert.Equal("?append_to_response=similar", host.TmdbRequests.Last().Query);
    Assert.DoesNotContain(Token, card.GetRawText());

    // A second card is answered from the cache.
    await CardAsync(host, "Dark S02E01");
    Assert.Equal(2, host.TmdbRequests.Count);
  }

  [Fact]
  public async Task SeveralCandidates_ReturnsChoices()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json(
      $$"""{"results":[{{Dark}},{"id":9,"name":"Dark","first_air_date":"2005-01-01","poster_path":"/b.jpg"}]}"""));

    var card = await CardAsync(host, "Dark S01E01");

    Assert.Equal("choose", card.GetProperty("status").GetString());
    Assert.Equal(JsonValueKind.Null, card.GetProperty("series").ValueKind);
    var candidates = card.GetProperty("candidates").EnumerateArray().ToList();
    Assert.Equal([70523, 9], candidates.Select(c => c.GetProperty("id").GetInt32()));
    Assert.Equal(2005, candidates[1].GetProperty("year").GetInt32());
    Assert.Equal("Dark", candidates[1].GetProperty("title").GetString());
    Assert.Equal("https://image.tmdb.org/t/p/w342/b.jpg", candidates[1].GetProperty("posterUrl").GetString());
    Assert.Single(host.TmdbRequests);
  }

  [Fact]
  public async Task NoResult_NotFound()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json("""{"results":[]}"""));

    var card = await CardAsync(host, "Unknown Show S01E01");

    Assert.Equal("notFound", card.GetProperty("status").GetString());
    Assert.Single(host.TmdbRequests);
  }

  [Fact]
  public async Task StoredId_SkipsSearch()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json(
      DarkDetails.Replace("\"Ended\"", "\"Returning Series\"", StringComparison.Ordinal)));
    Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(HttpMethod.Put, "/api/marks",
      new { name = "Dark S01E01", kind = "series", seen = true, rating = 1 })).StatusCode);
    host.SetFilmDbId(host.UserName, "70523");

    var card = await CardAsync(host, "Dark S02E03");

    Assert.Equal("matched", card.GetProperty("status").GetString());
    Assert.True(card.GetProperty("marks").GetProperty("seen").GetBoolean());
    Assert.Equal(1, card.GetProperty("marks").GetProperty("rating").GetInt32());
    Assert.Equal("returning", card.GetProperty("series").GetProperty("state").GetString());
    Assert.Equal(JsonValueKind.Null, card.GetProperty("series").GetProperty("lastYear").ValueKind);
    Assert.Equal("/3/tv/70523", Assert.Single(host.TmdbRequests).AbsolutePath);
  }

  [Theory]
  [InlineData(HttpStatusCode.TooManyRequests)]
  [InlineData(HttpStatusCode.ServiceUnavailable)]
  public async Task TmdbDownOr429_Unavailable200(HttpStatusCode status)
  {
    await using var host = await TestHost.StartAsync(Token, _ => new HttpResponseMessage(status));

    var card = await CardAsync(host, "Dark S01E01");

    Assert.Equal("unavailable", card.GetProperty("status").GetString());
    Assert.Equal(JsonValueKind.Null, card.GetProperty("series").ValueKind);
    Assert.False(card.GetProperty("tmdbData").GetBoolean());
  }

  [Fact]
  public async Task OtherUsersPlaylist_404()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);
    var other = new ApiClient(host.Factory.CreateClient());
    await other.RegisterAsync("someone-else");

    Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync(CardPath(host, "Dark S01E01"))).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.CreateClient().GetAsync(CardPath(host, "Dark S01E01"))).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public async Task InList_SeasonsAndEpisodes_OnePass()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb, ["Shows Old"], SeriesList);
    var analyzed = await host.Client.PostAsync("/api/playlist/analyze", new { rawText = SeriesList });
    analyzed.EnsureSuccessStatusCode();
    var cacheKey = JsonDocument.Parse(await analyzed.Content.ReadAsStringAsync()).RootElement.GetProperty("cacheKey").GetString()!;
    var pipeline = host.Factory.Services.GetRequiredService<PlaylistPipeline>();
    var index = pipeline.Cache.TryGetAnalysis(pipeline.Cache.TryGetSessionSource(cacheKey)!)!.SearchIndex!;
    var before = index.PassCount;

    var series = (await CardAsync(host, "Dark S01E01", $"&cacheKey={cacheKey}")).GetProperty("series");

    Assert.Equal(before + 1, index.PassCount);
    var inList = series.GetProperty("inList");
    Assert.Equal(4, inList.GetProperty("episodes").GetInt32());
    Assert.Equal(1, inList.GetProperty("unnumbered").GetInt32());
    // TMDB's seasons 1 to 3 (not the specials the list lacks) and the list's season 5 that TMDB does not know.
    Assert.Equal(["1:10:2:1,2", "2:8:0:", "3:8:1:2", "5:null:1:1"], Seasons(inList));
    // In the list first, TMDB's order within each part.
    var similar = series.GetProperty("similar").EnumerateArray().ToList();
    Assert.Equal(["Lost", "Heat"], similar.Select(s => s.GetProperty("title").GetString()));
    Assert.True(similar[0].GetProperty("inList").GetBoolean());
    Assert.Equal("Lost S01E01", similar[0].GetProperty("entry").GetString());
    Assert.Equal("Shows DE", similar[0].GetProperty("group").GetString());
    Assert.False(similar[1].GetProperty("inList").GetBoolean());

    var all = await CardAsync(host, "Dark S01E01", $"&cacheKey={cacheKey}&includeDisabled=true");
    Assert.Contains("4:null:1:1", Seasons(all.GetProperty("series").GetProperty("inList")));
  }

  [Fact]
  public async Task NoIndex_InListNull()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb, upstream: SeriesList);

    var series = (await CardAsync(host, "Dark S01E01", "&cacheKey=expired")).GetProperty("series");

    Assert.Equal(JsonValueKind.Null, series.GetProperty("inList").ValueKind);
    Assert.All(series.GetProperty("similar").EnumerateArray(), s => Assert.False(s.GetProperty("inList").GetBoolean()));
  }

  [Theory]
  [InlineData("Dark Staffel 2 Folge 5", 2, 5)]
  [InlineData("Dark S03E07", 3, 7)]
  [InlineData("Dark S03", 3, null)]
  public async Task Context_FromEpisodeName(string name, int season, int? episode)
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);

    var context = (await CardAsync(host, name)).GetProperty("context");

    Assert.Equal(season, context.GetProperty("season").GetInt32());
    Assert.Equal(episode, context.GetProperty("episode").ValueKind == JsonValueKind.Null ? null : context.GetProperty("episode").GetInt32());
    Assert.Equal(JsonValueKind.Null, (await CardAsync(host, "Dark")).GetProperty("context").ValueKind);
  }

  private static List<string> Seasons(JsonElement inList) =>
    inList.GetProperty("seasons").EnumerateArray().Select(s =>
      $"{s.GetProperty("season").GetInt32()}:{s.GetProperty("total").GetRawText()}:{s.GetProperty("count").GetInt32()}:"
      + string.Join(',', s.GetProperty("episodes").EnumerateArray().Select(e => e.GetInt32()))).ToList();
}
