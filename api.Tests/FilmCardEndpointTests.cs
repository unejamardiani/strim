using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Api.Tests.LibraryFilmCardTests;

namespace Api.Tests;

/// <summary>The film card hands the browser hotlinked TMDB poster URLs; Strim serves no image bytes itself.</summary>
public sealed class FilmCardEndpointTests
{
  [Fact]
  public async Task Card_ContainsHotlinkedPosterUrl()
  {
    const string details = """
      {"id":603,"title":"The Matrix","release_date":"1999-03-31","poster_path":"/m.jpg","vote_average":8.2,
       "similar":{"results":[{"id":5,"title":"Heat","release_date":"1995-12-15","poster_path":"/../h.jpg"}]}}
      """;
    await using var host = await TestHost.StartAsync(Token, request => request.AbsolutePath == "/3/search/movie"
      ? Json($$"""{"results":[{{Matrix}}]}""")
      : Json(details));

    var card = await host.CardAsync("The Matrix (1999)");

    var film = card.GetProperty("film");
    Assert.Equal("https://image.tmdb.org/t/p/w342/m.jpg", film.GetProperty("posterUrl").GetString());
    Assert.False(film.TryGetProperty("posterPath", out _));
    // An unsafe path from TMDB gives no poster at all.
    var similar = Assert.Single(film.GetProperty("similar").EnumerateArray());
    Assert.Equal(JsonValueKind.Null, similar.GetProperty("posterUrl").ValueKind);

    // No route of the app serves images: no poster or image route, and nothing that produces an image type. The one
    // exception is the Library's posters route, which answers poster addresses as JSON (LibraryPostersEndpointTests).
    var endpoints = host.Factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints)
      .OfType<RouteEndpoint>().ToList();
    Assert.NotEmpty(endpoints);
    foreach (var endpoint in endpoints)
    {
      var pattern = endpoint.RoutePattern.RawText ?? "";
      if (pattern != "/api/playlists/{id:guid}/library/posters")
        Assert.DoesNotContain("poster", pattern, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("image", pattern, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("tmdb", pattern, StringComparison.OrdinalIgnoreCase);
      var produces = endpoint.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>()
        .SelectMany(m => m.ContentTypes);
      Assert.DoesNotContain(produces, c => c.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
    }
  }

  [Fact]
  public async Task Card_FlagsTmdbDataPresence()
  {
    await using (var matched = await TestHost.StartAsync(Token, request => request.AbsolutePath == "/3/search/movie"
      ? Json($$"""{"results":[{{Matrix}}]}""")
      : Json(MatrixDetails)))
    {
      var card = await matched.CardAsync("The Matrix (1999)");
      Assert.Equal("matched", card.GetProperty("status").GetString());
      Assert.True(card.GetProperty("tmdbData").GetBoolean());
      // A channel's card has no TMDB data.
      Assert.False((await matched.CardAsync("The Matrix (1999)", "News", "Live")).GetProperty("tmdbData").GetBoolean());
    }

    // Without a token there is no card at all, so nothing to credit.
    await using (var unset = await TestHost.StartAsync(token: null, _ => Json("{}")))
    {
      var response = await unset.Client.Http.GetAsync(unset.CardPath("The Matrix (1999)"));
      Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    await using (var limited = await TestHost.StartAsync(Token, _ =>
      new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)))
    {
      var card = await limited.CardAsync("The Matrix (1999)");
      Assert.Equal("unavailable", card.GetProperty("status").GetString());
      Assert.False(card.GetProperty("tmdbData").GetBoolean());
    }

    await using (var none = await TestHost.StartAsync(Token, _ => Json("""{"results":[]}""")))
    {
      var card = await none.CardAsync("The Matrix (1999)");
      Assert.Equal("notFound", card.GetProperty("status").GetString());
      Assert.False(card.GetProperty("tmdbData").GetBoolean());
    }
  }
}
