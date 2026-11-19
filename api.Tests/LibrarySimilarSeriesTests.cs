using System.Net;
using System.Text.Json;
using Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Api.Tests.LibraryFilmCardTests;

namespace Api.Tests;

/// <summary>The similar series of the series card: from the details request, found in the same pass as the list's episodes.</summary>
public sealed class LibrarySimilarSeriesTests
{
  // TMDB's order: Heat (not in the list), Lost (only in the switched-off group), Fargo (in the list by its title and
  // year), Mindhunter (in the list under another name, found by the id stored on that series' mark).
  private const string DarkDetails = """
    {"id":70523,"name":"Dark","first_air_date":"2017-12-01","status":"Ended","seasons":[{"season_number":1,"episode_count":10}],
     "similar":{"results":[{"id":2,"name":"Heat","first_air_date":"2001-01-01"},{"id":4607,"name":"Lost","first_air_date":"2004-09-22"},
       {"id":60622,"name":"Fargo","first_air_date":"2014-04-15","poster_path":"/f.jpg"},{"id":67744,"name":"Mindhunter","first_air_date":"2017-10-13"}]}}
    """;

  private static readonly string SeriesList = "#EXTM3U\n" + string.Concat(new[]
  {
    ("Shows DE", "Dark S01E01"), ("Shows Old", "Lost S01E01"), ("Shows EN", "Fargo (2014) S01E01"),
    ("Shows EN", "Mind Hunter S02E01"),
  }.Select((e, i) => $"#EXTINF:-1 group-title=\"{e.Item1}\",{e.Item2}\nhttp://line.example/series/u/p/{i}.mkv\n"));

  private static HttpResponseMessage Tmdb(Uri request) => request.AbsolutePath switch
  {
    "/3/search/tv" => Json("""{"results":[{"id":70523,"name":"Dark","first_air_date":"2017-12-01"}]}"""),
    "/3/tv/70523" => Json(DarkDetails),
    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
  };

  private static async Task<(TestHost Host, string CacheKey)> StartAsync()
  {
    var host = await TestHost.StartAsync(Token, Tmdb, ["Shows Old"], SeriesList);
    var analyzed = await host.Client.PostAsync("/api/playlist/analyze", new { rawText = SeriesList });
    Assert.Equal(HttpStatusCode.OK, analyzed.StatusCode);
    return (host, JsonDocument.Parse(await analyzed.Content.ReadAsStringAsync()).RootElement.GetProperty("cacheKey").GetString()!);
  }

  private static async Task<JsonElement> SeriesAsync(TestHost host, string query)
  {
    var response = await host.Client.Http.GetAsync(
      $"/api/playlists/{host.PlaylistId}/library/series-card?name=Dark%20S01E01&group=Shows%20DE&type=Series{query}");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var card = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    Assert.Equal("matched", card.GetProperty("status").GetString());
    return card.GetProperty("series");
  }

  private static List<string> Similar(JsonElement series, params string[] fields) =>
    series.GetProperty("similar").EnumerateArray().Select(s => string.Join('|',
      fields.Select(p => s.TryGetProperty(p, out var v) ? v.ToString() : ""))).ToList();

  private static async Task MarkAsync(TestHost host, string name, bool seen, int? tmdbId = null)
  {
    if (seen)
      Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind = "series", seen })).StatusCode);
    if (tmdbId is not null)
      Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(HttpMethod.Put, "/api/marks/series-match", new { name, tmdbId })).StatusCode);
  }

  [Fact]
  public async Task Similar_NoExtraRequest()
  {
    var (host, cacheKey) = await StartAsync();
    await using var _ = host;
    var series = await SeriesAsync(host, $"&cacheKey={cacheKey}");
    Assert.Equal(4, series.GetProperty("similar").GetArrayLength());
    // Search and details with similar; nothing per similar series, season or episode, and nothing on a cache hit.
    Assert.Equal(["/3/search/tv", "/3/tv/70523"], host.TmdbRequests.Select(u => u.AbsolutePath));
    await SeriesAsync(host, $"&cacheKey={cacheKey}&hideSeen=true");
    Assert.Equal(2, host.TmdbRequests.Count);
  }

  [Fact]
  public async Task Similar_InListFirst_WithGroupAndEnabled()
  {
    var (host, cacheKey) = await StartAsync();
    await using var _ = host;
    await MarkAsync(host, "Mind Hunter", seen: false, tmdbId: 67744);
    var fields = new[] { "title", "inList", "entry", "group", "groupKey", "enabled" };

    Assert.Equal([
      "Lost|True|Lost S01E01|Shows Old|Shows Old|False", "Fargo|True|Fargo (2014) S01E01|Shows EN|Shows EN|True",
      "Mindhunter|True|Mind Hunter S02E01|Shows EN|Shows EN|True", "Heat|False||||",
    ], Similar(await SeriesAsync(host, $"&cacheKey={cacheKey}&includeDisabled=true"), fields));
    // Without includeDisabled the switched-off group does not count, so Lost is not in the list.
    Assert.Equal(["Fargo|True", "Mindhunter|True", "Heat|False", "Lost|False"],
      Similar(await SeriesAsync(host, $"&cacheKey={cacheKey}"), "title", "inList"));
    var fargo = (await SeriesAsync(host, $"&cacheKey={cacheKey}")).GetProperty("similar")[0];
    Assert.Equal("60622|2014|https://image.tmdb.org/t/p/w342/f.jpg", string.Join('|',
      new[] { "id", "year", "posterUrl" }.Select(p => fargo.GetProperty(p).ToString())));
  }

  [Fact]
  public async Task Similar_SamePassAsInList()
  {
    var (host, cacheKey) = await StartAsync();
    await using var _ = host;
    var pipeline = host.Factory.Services.GetRequiredService<PlaylistPipeline>();
    var index = pipeline.Cache.TryGetAnalysis(pipeline.Cache.TryGetSessionSource(cacheKey)!)!.SearchIndex!;
    var before = index.PassCount;

    var series = await SeriesAsync(host, $"&cacheKey={cacheKey}");

    Assert.Equal(before + 1, index.PassCount);
    Assert.Equal(1, series.GetProperty("inList").GetProperty("episodes").GetInt32());
    Assert.Equal(["Fargo|True", "Heat|False", "Lost|False", "Mindhunter|False"],
      Similar(series, "title", "inList"));
  }

  [Fact]
  public async Task Similar_HideSeen()
  {
    var (host, cacheKey) = await StartAsync();
    await using var _ = host;
    // Heat is seen under its title, Mindhunter under the id stored on another name's mark.
    await MarkAsync(host, "Heat", seen: true);
    await MarkAsync(host, "Mind Hunter", seen: true, tmdbId: 67744);

    Assert.Equal(["Fargo|True|False", "Mindhunter|True|True", "Heat|False|True", "Lost|False|False"],
      Similar(await SeriesAsync(host, $"&cacheKey={cacheKey}"), "title", "inList", "seen"));
    Assert.Equal(["Fargo", "Lost"], Similar(await SeriesAsync(host, $"&cacheKey={cacheKey}&hideSeen=true"), "title"));
    // Without an index nothing is in the list, and seen still counts.
    Assert.Equal(["Lost", "Fargo"], Similar(await SeriesAsync(host, "&cacheKey=expired&hideSeen=true"), "title"));
  }
}
