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
  private static readonly string SeriesList = "#EXTM3U\n" + string.Concat(new[]
  {
    ("Shows DE", "Dark S01E01"), ("Shows DE", "Dark S01E02"), ("Shows EN", "Dark S01E01 FHD"), ("Shows EN", "Dark S03E02"),
    ("Shows EN", "Dark S05E01"), ("Shows DE", "Dark S02"), ("Shows Old", "Dark S04E01"), ("XXX Shows", "Dark S01E07"),
    ("Films", "Dark S01E09"), ("Shows DE", "Lost S01E01"),
  }.Select((e, i) => $"#EXTINF:-1 group-title=\"{e.Item1}\",{e.Item2}\nhttp://line.example/{(e.Item1 == "Films" ? "movie" : "series")}/u/p/{i}.mkv\n"));

  private static HttpResponseMessage Tmdb(Uri request) => request.AbsolutePath switch
  {
    "/3/search/tv" => Json($$"""{"results":[{{Dark}},{"id":9,"name":"Dark Matter","first_air_date":"2015-06-12"}]}"""),
    "/3/tv/70523" => Json(DarkDetails),
    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
  };

  private static string CardPath(TestHost host, string name, string group = "Shows DE", string type = "Series") =>
    $"/api/playlists/{host.PlaylistId}/library/series-card?name={Uri.EscapeDataString(name)}&group={Uri.EscapeDataString(group)}&type={type}";

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
    foreach (var forbidden in new[] { "S02", "E05", "Serien", "Multi", "username", "password", host.UserName, host.PlaylistId.ToString() })
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
    Assert.Equal("70523|Dark|2017|2020|ended|3|26|Netflix|https://image.tmdb.org/t/p/w342/d.jpg|8.4|A missing child.", string.Join('|',
      new[] { "id", "title", "firstYear", "lastYear", "state", "seasonCount", "episodeCount", "network", "posterUrl", "rating", "overview" }
        .Select(p => series.GetProperty(p).ToString())));
    Assert.Equal(["Drama"], series.GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
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
    Assert.Equal("Dark|2005|https://image.tmdb.org/t/p/w342/b.jpg", string.Join('|',
      new[] { "title", "year", "posterUrl" }.Select(p => candidates[1].GetProperty(p).ToString())));
    Assert.Single(host.TmdbRequests);
  }

  [Fact]
  public async Task NoResult_NotFound()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json("""{"results":[]}"""));
    Assert.Equal("notFound", (await CardAsync(host, "Unknown Show S01E01")).GetProperty("status").GetString());
    Assert.Single(host.TmdbRequests);
  }

  [Fact]
  public async Task Card_MarksCarryTheWatchlistFlag()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);
    Assert.False((await CardAsync(host, "Dark S01E01")).GetProperty("marks").GetProperty("watchlist").GetBoolean());

    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "Dark S01E01", kind = "series", watchlist = true });
    Assert.True((await CardAsync(host, "Dark S02E03")).GetProperty("marks").GetProperty("watchlist").GetBoolean());
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
    Assert.Equal("""{"seen":true,"rating":1,"watchlist":false}""", card.GetProperty("marks").GetRawText());
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
    Assert.Equal(["Lost|True|Lost S01E01|Shows DE", "Heat|False||"], similar.Select(s => string.Join('|',
      new[] { "title", "inList", "entry", "group" }.Select(p => s.TryGetProperty(p, out var v) ? v.ToString() : ""))));
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
  [InlineData("Dark Staffel 2 Folge 5", "{\"season\":2,\"episode\":5}")]
  [InlineData("Dark S03E07", "{\"season\":3,\"episode\":7}")]
  [InlineData("Dark S03", "{\"season\":3,\"episode\":null}")]
  [InlineData("Dark", "null")]
  public async Task Context_FromEpisodeName(string name, string context)
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);
    Assert.Equal(context, (await CardAsync(host, name)).GetProperty("context").GetRawText());
  }

  private static List<string> Seasons(JsonElement inList) =>
    inList.GetProperty("seasons").EnumerateArray().Select(s =>
      $"{s.GetProperty("season").GetInt32()}:{s.GetProperty("total").GetRawText()}:{s.GetProperty("count").GetInt32()}:"
      + string.Join(',', s.GetProperty("episodes").EnumerateArray().Select(e => e.GetInt32()))).ToList();
}
