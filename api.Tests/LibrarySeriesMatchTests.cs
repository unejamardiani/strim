using System.Net;
using System.Text.Json;
using Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Api.Tests.LibraryFilmCardTests;

namespace Api.Tests;

/// <summary>PUT /api/marks/series-match: the TMDB tv id the user picked from a series card's "choose" answer.</summary>
public sealed class LibrarySeriesMatchTests
{
  private const string DarkDetails = """
    {"id":70523,"name":"Dark","first_air_date":"2017-12-01","status":"Ended","similar":{"results":[]}}
    """;

  private static HttpResponseMessage Tmdb(Uri request) => request.AbsolutePath switch
  {
    "/3/search/tv" => Json("""{"results":[{"id":70523,"name":"Dark","first_air_date":"2017-12-01"},{"id":9,"name":"Dark","first_air_date":"2005-01-01"}]}"""),
    "/3/tv/70523" => Json(DarkDetails),
    "/3/search/movie" => Json("""{"results":[{"id":949,"title":"Heat","release_date":"1995-12-15"}]}"""),
    "/3/movie/949" => Json("""
      {"id":949,"title":"Heat","release_date":"1995-12-15","genres":[],
       "similar":{"results":[{"id":603,"title":"The Matrix","release_date":"1999-03-31"}]}}
      """),
    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
  };

  private static Task<HttpResponseMessage> MatchAsync(TestHost host, string name, int? tmdbId, bool withCsrf = true) =>
    host.Client.SendAsync(HttpMethod.Put, "/api/marks/series-match", new { name, tmdbId }, withCsrf);

  private static List<Api.Models.TitleMark> Marks(TestHost host)
  {
    using var scope = host.Factory.Services.CreateScope();
    return scope.ServiceProvider.GetRequiredService<AppDbContext>().TitleMarks.AsNoTracking().ToList();
  }

  private static async Task<JsonElement> SeriesCardAsync(TestHost host, string name)
  {
    var response = await host.Client.Http.GetAsync(
      $"/api/playlists/{host.PlaylistId}/library/series-card?name={Uri.EscapeDataString(name)}&group=Shows&type=Series");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
  }

  [Fact]
  public async Task Store_Clear_EmptyMarkLeavesNoRow()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);
    Assert.Equal("choose", (await SeriesCardAsync(host, "Dark S01E01")).GetProperty("status").GetString());

    var stored = await MatchAsync(host, "Dark S01E01", 70523);
    Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
    var body = JsonDocument.Parse(await stored.Content.ReadAsStringAsync()).RootElement;
    Assert.Equal("series::dark|series|70523", $"{body.GetProperty("key")}|{body.GetProperty("kind")}|{body.GetProperty("tmdbId")}");
    var mark = Assert.Single(Marks(host));
    Assert.Equal(("series", "70523", false, 0), (mark.Kind, mark.FilmDbId, mark.Seen, mark.Rating));

    // Every episode row of the series now opens the stored match without a search.
    var requests = host.TmdbRequests.Count;
    Assert.Equal("matched", (await SeriesCardAsync(host, "Dark S02E03")).GetProperty("status").GetString());
    Assert.Equal(["/3/tv/70523"], host.TmdbRequests.Skip(requests).Select(u => u.AbsolutePath));

    // Clearing a mark that held only the match leaves no row.
    Assert.Equal(HttpStatusCode.NoContent, (await MatchAsync(host, "Dark S03", null)).StatusCode);
    Assert.Empty(Marks(host));
    Assert.Equal(HttpStatusCode.NoContent, (await MatchAsync(host, "Dark", null)).StatusCode);
    Assert.Empty(Marks(host));

    // A mark that is also seen keeps its row and its seen mark; only the id goes.
    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "Dark", kind = "series", seen = true });
    await MatchAsync(host, "Dark S01E01", 70523);
    Assert.Equal(HttpStatusCode.OK, (await MatchAsync(host, "Dark S01E02", null)).StatusCode);
    mark = Assert.Single(Marks(host));
    Assert.Equal((null, true), (mark.FilmDbId, mark.Seen));
    Assert.Equal(HttpStatusCode.BadRequest, (await MatchAsync(host, "Dark", 0)).StatusCode);
  }

  [Fact]
  public async Task WithoutCsrf_Refused()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);

    Assert.False((await MatchAsync(host, "Dark S01E01", 70523, withCsrf: false)).IsSuccessStatusCode);
    Assert.Empty(Marks(host));
    Assert.False((await host.Factory.CreateClient().PutAsync("/api/marks/series-match", null)).IsSuccessStatusCode);

    await using var noToken = await TestHost.StartAsync(token: null, Tmdb);
    Assert.Equal(HttpStatusCode.NotFound, (await MatchAsync(noToken, "Dark S01E01", 70523)).StatusCode);
    Assert.Empty(Marks(noToken));
  }

  [Fact]
  public async Task SeriesMatchId_NeverMarksAFilmSeen()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb);
    // A seen series whose TMDB tv id is the same number as a film's id.
    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "Matrix Reloaded Show", kind = "series", seen = true });
    Assert.Equal(HttpStatusCode.OK, (await MatchAsync(host, "Matrix Reloaded Show", 603)).StatusCode);

    var card = await host.CardAsync("Heat (1995)");

    Assert.Equal("matched", card.GetProperty("status").GetString());
    var similar = Assert.Single(card.GetProperty("film").GetProperty("similar").EnumerateArray());
    Assert.Equal(603, similar.GetProperty("id").GetInt32());
    Assert.False(similar.GetProperty("seen").GetBoolean());
    Assert.False(card.GetProperty("marks").GetProperty("seen").GetBoolean());
  }
}
