using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>GET /api/playlists/{id}/library/film-card against a stubbed TMDB.</summary>
public sealed class LibraryFilmCardTests
{
  // A made-up value; only its absence from responses matters.
  internal const string Token = "test-tmdb-token-not-real-5b1e7a90";

  internal const string Matrix = """{"id":603,"title":"The Matrix","release_date":"1999-03-31","poster_path":"/m.jpg"}""";

  internal const string MatrixDetails = """
    {"id":603,"title":"The Matrix","overview":"A hacker learns the truth.","release_date":"1999-03-31",
     "poster_path":"/m.jpg","vote_average":8.2,"genres":[{"id":28,"name":"Action"},{"id":878,"name":"Science Fiction"}],
     "similar":{"results":[]}}
    """;

  [Fact]
  public async Task NoToken_CardDisabled_LibraryUnchanged()
  {
    await using var without = await TestHost.StartAsync(token: null, _ => Json("{}"));
    await using var with = await TestHost.StartAsync(Token, _ => Json("{}"));

    var card = await without.Client.Http.GetAsync(without.CardPath("The Matrix (1999)"));
    Assert.Equal(HttpStatusCode.NotFound, card.StatusCode);
    Assert.Empty(without.TmdbRequests);

    // The Library's search answers the same with and without a token.
    Assert.Equal(await SearchBodyAsync(with), await SearchBodyAsync(without));
    Assert.Empty(with.TmdbRequests);
  }

  [Fact]
  public async Task SendsOnlyCleanTitleAndYear()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json("""{"results":[]}"""));

    await host.CardAsync("DE - The Matrix (1999) [Multi-Sub] 4K", "Filme | Action");

    var uri = Assert.Single(host.TmdbRequests);
    Assert.Equal("/3/search/movie?include_adult=false&query=the%20matrix&year=1999", uri.PathAndQuery);
    var text = Uri.UnescapeDataString(uri.ToString());
    Assert.DoesNotContain(host.PlaylistId.ToString(), text);
    foreach (var forbidden in new[] { "93.184.215.14", "username", "password", "Filme", "Multi", host.UserName })
      Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("Live", "Movies")]
  [InlineData("Series", "Movies")]
  [InlineData("Movies", "XXX | Movies")]
  [InlineData("Movies", "Für Erwachsene")]
  public async Task ChannelOrAdult_SendsNothing(string type, string group)
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json($$"""{"results":[{{Matrix}}]}"""));

    var card = await host.CardAsync("The Matrix (1999)", group, type);

    Assert.Equal("notApplicable", card.GetProperty("status").GetString());
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public async Task ClearMatch_ReturnsDetails()
  {
    await using var host = await TestHost.StartAsync(Token, request => request.AbsolutePath == "/3/search/movie"
      ? Json($$"""{"results":[{{Matrix}},{"id":604,"title":"The Matrix Reloaded","release_date":"2003-05-15"}]}""")
      : Json(MatrixDetails));

    var card = await host.CardAsync("The Matrix (1999)");

    Assert.Equal("matched", card.GetProperty("status").GetString());
    var film = card.GetProperty("film");
    Assert.Equal(603, film.GetProperty("id").GetInt32());
    Assert.Equal("https://image.tmdb.org/t/p/w342/m.jpg", film.GetProperty("posterUrl").GetString());
    Assert.Equal("The Matrix", film.GetProperty("title").GetString());
    Assert.Equal(1999, film.GetProperty("year").GetInt32());
    Assert.Equal(8.2, film.GetProperty("rating").GetDouble());
    Assert.Equal(["Action", "Science Fiction"], film.GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
    Assert.Equal("A hacker learns the truth.", film.GetProperty("overview").GetString());
    Assert.Equal(2, host.TmdbRequests.Count);
    Assert.Equal("/3/movie/603", host.TmdbRequests.Last().AbsolutePath);
  }

  [Theory]
  // Two results with the same title and year, a title that differs, and a year that differs.
  [InlineData("""{"id":603,"title":"The Matrix","release_date":"1999-03-31"},{"id":9,"title":"The Matrix","release_date":"1999-01-01","poster_path":"/b.jpg"}""")]
  [InlineData("""{"id":603,"title":"Matrix","release_date":"1999-03-31"},{"id":9,"title":"The Matrix Code","release_date":"1999-01-01","poster_path":"/b.jpg"}""")]
  [InlineData("""{"id":603,"title":"The Matrix","release_date":"2000-03-31"},{"id":9,"title":"Another","release_date":"1999-01-01","poster_path":"/b.jpg"}""")]
  public async Task SeveralCandidates_ReturnsChoices(string results)
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json($$"""{"results":[{{results}}]}"""));

    var card = await host.CardAsync("The Matrix (1999)");

    Assert.Equal("choose", card.GetProperty("status").GetString());
    Assert.Equal(JsonValueKind.Null, card.GetProperty("film").ValueKind);
    var candidates = card.GetProperty("candidates").EnumerateArray().ToList();
    Assert.Equal([603, 9], candidates.Select(c => c.GetProperty("id").GetInt32()));
    Assert.Equal(1999, candidates[1].GetProperty("year").GetInt32());
    Assert.Equal("https://image.tmdb.org/t/p/w342/b.jpg", candidates[1].GetProperty("posterUrl").GetString());
    Assert.False(string.IsNullOrEmpty(candidates[1].GetProperty("title").GetString()));
    Assert.Single(host.TmdbRequests);
  }

  [Fact]
  public async Task NoResult_ReturnsNotFound()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json("""{"results":[]}"""));

    var card = await host.CardAsync("Unknown Film (2004)");

    Assert.Equal("notFound", card.GetProperty("status").GetString());
    Assert.Single(host.TmdbRequests);
  }

  [Fact]
  public async Task StoredId_SkipsSearch()
  {
    await using var host = await TestHost.StartAsync(Token, _ => Json(MatrixDetails));
    await host.AddMarkAsync("DE - The Matrix (1999)", seen: true, rating: 1, filmDbId: "603");

    var card = await host.CardAsync("The Matrix (1999) HD");

    Assert.Equal("matched", card.GetProperty("status").GetString());
    Assert.True(card.GetProperty("marks").GetProperty("seen").GetBoolean());
    Assert.Equal("/3/movie/603", Assert.Single(host.TmdbRequests).AbsolutePath);
  }

  [Theory]
  [InlineData(HttpStatusCode.TooManyRequests)]
  [InlineData(HttpStatusCode.ServiceUnavailable)]
  [InlineData(HttpStatusCode.InternalServerError)]
  public async Task TmdbDown_ReturnsUnavailable(HttpStatusCode status)
  {
    await using var host = await TestHost.StartAsync(Token, _ => new HttpResponseMessage(status));

    var response = await host.Client.Http.GetAsync(host.CardPath("The Matrix (1999)"));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadAsStringAsync();
    Assert.Equal("unavailable", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
  }

  [Fact]
  public async Task TmdbRateLimited_ReturnsCardWithoutTmdbData()
  {
    await using var host = await TestHost.StartAsync(Token, _ =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
      response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
      return response;
    });
    await host.AddMarkAsync("The Matrix (1999)", seen: true, rating: 1, filmDbId: null);

    var first = await host.CardAsync("The Matrix (1999)");
    Assert.Single(host.TmdbRequests);

    // While Strim backs off, the card still answers with the entry's own marks and sends nothing to TMDB.
    var card = await host.CardAsync("The Matrix (1999) HD");
    Assert.Single(host.TmdbRequests);
    foreach (var answer in new[] { first, card })
    {
      Assert.Equal("unavailable", answer.GetProperty("status").GetString());
      Assert.Equal(JsonValueKind.Null, answer.GetProperty("film").ValueKind);
      Assert.Equal(JsonValueKind.Null, answer.GetProperty("candidates").ValueKind);
      Assert.True(answer.GetProperty("marks").GetProperty("seen").GetBoolean());
      Assert.Equal(1, answer.GetProperty("marks").GetProperty("rating").GetInt32());
    }
  }

  [Fact]
  public async Task OtherUsersPlaylist_NotFound_AndTokenNeverReturned()
  {
    await using var host = await TestHost.StartAsync(Token, request => request.AbsolutePath == "/3/search/movie"
      ? Json($$"""{"results":[{{Matrix}}]}""") : Json(MatrixDetails));

    var other = new ApiClient(host.Factory.CreateClient());
    await other.RegisterAsync("someone-else");
    var foreign = await other.Http.GetAsync(host.CardPath("The Matrix (1999)"));
    Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    Assert.Empty(host.TmdbRequests);

    var anonymous = await host.Factory.CreateClient().GetAsync(host.CardPath("The Matrix (1999)"));
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

    var own = await host.Client.Http.GetAsync(host.CardPath("The Matrix (1999)"));
    var body = await own.Content.ReadAsStringAsync();
    Assert.Contains("matched", body);
    Assert.DoesNotContain(Token, body);
    Assert.DoesNotContain(Token[..12], body);
  }

  [Fact]
  public async Task Card_IncludesSeenAndThumbsMarks()
  {
    await using var host = await TestHost.StartAsync(Token, request => request.AbsolutePath == "/3/search/movie"
      ? Json($$"""{"results":[{{Matrix}}]}""") : Json(MatrixDetails));

    var unmarked = (await host.CardAsync("The Matrix (1999)")).GetProperty("marks");
    Assert.False(unmarked.GetProperty("seen").GetBoolean());
    Assert.Equal(0, unmarked.GetProperty("rating").GetInt32());

    await host.AddMarkAsync("The Matrix (1999) 4K", seen: true, rating: -1, filmDbId: null);
    var marks = (await host.CardAsync("The Matrix (1999)")).GetProperty("marks");
    Assert.True(marks.GetProperty("seen").GetBoolean());
    Assert.Equal(-1, marks.GetProperty("rating").GetInt32());
  }

  private static async Task<string> SearchBodyAsync(TestHost host)
  {
    var analyzed = await host.Client.PostAsync("/api/playlist/analyze", new { rawText = TestPlaylists.Mixed });
    analyzed.EnsureSuccessStatusCode();
    var cacheKey = JsonDocument.Parse(await analyzed.Content.ReadAsStringAsync()).RootElement.GetProperty("cacheKey").GetString();
    var search = await host.Client.PostAsync("/api/playlist/search", new { cacheKey, query = "zdf" });
    Assert.Equal(HttpStatusCode.OK, search.StatusCode);
    return await search.Content.ReadAsStringAsync();
  }

  internal static HttpResponseMessage Json(string body) =>
    new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  internal sealed class TestHost : IAsyncDisposable
  {
    /// <summary>The base factory; it owns the throwaway database and cache directory.</summary>
    public required StrimAppFactory Root { get; init; }

    /// <summary>The host with the token and the stubbed TMDB.</summary>
    public required WebApplicationFactory<Program> Factory { get; init; }
    public required ApiClient Client { get; init; }
    public required ConcurrentQueue<Uri> TmdbRequests { get; init; }
    public Guid PlaylistId { get; private set; }
    public string UserName { get; } = "card-owner";

    public static async Task<TestHost> StartAsync(string? token, Func<Uri, HttpResponseMessage> tmdb)
    {
      var requests = new ConcurrentQueue<Uri>();
      var root = new StrimAppFactory();
      var app = root.WithWebHostBuilder(builder =>
      {
        builder.ConfigureAppConfiguration((_, config) =>
          config.AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = token }));
        builder.ConfigureTestServices(services => services.AddHttpClient(TmdbClient.HttpClientName)
          .ConfigurePrimaryHttpMessageHandler(() => new StubTmdb(tmdb, requests)));
      });
      var host = new TestHost { Root = root, Factory = app, Client = new ApiClient(app.CreateClient()), TmdbRequests = requests };
      await host.Client.RegisterAsync(host.UserName);
      var created = await host.Client.PostAsync("/api/playlists", new
      {
        name = "Living room", sourceUrl = StrimAppFactory.UpstreamUrl, sourceName = "provider",
        disabledGroups = Array.Empty<string>(), totalChannels = 4, groupCount = 2,
      });
      created.EnsureSuccessStatusCode();
      host.PlaylistId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
      return host;
    }

    public string CardPath(string name, string group = "Movies", string type = "Movies") =>
      $"/api/playlists/{PlaylistId}/library/film-card?name={Uri.EscapeDataString(name)}"
      + $"&group={Uri.EscapeDataString(group)}&type={type}";

    public async Task<JsonElement> CardAsync(string name, string group = "Movies", string type = "Movies")
    {
      var response = await Client.Http.GetAsync(CardPath(name, group, type));
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Marks a film through PUT /api/marks; the TMDB id has no route yet (S5), so it is set in the database.</summary>
    public async Task AddMarkAsync(string name, bool seen, int rating, string? filmDbId)
    {
      Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind = "film", seen, rating })).StatusCode);
      using var scope = Factory.Services.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      Assert.Single(db.TitleMarks.ToList()).FilmDbId = filmDbId;
      db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
      await Factory.DisposeAsync();
      await Root.DisposeAsync();
    }
  }

  private sealed class StubTmdb(Func<Uri, HttpResponseMessage> respond, ConcurrentQueue<Uri> requests) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      requests.Enqueue(request.RequestUri!);
      return Task.FromResult(respond(request.RequestUri!));
    }
  }
}
