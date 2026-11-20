using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Endpoints;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>PUT /api/marks/film-match: the TMDB film the owner picked from a "choose" card.</summary>
public sealed class LibraryFilmMatchTests
{
  // A made-up value; only its presence matters.
  private const string Token = "test-tmdb-token-not-real-3c9d2e41";
  private const string MatrixKey = "film:1999:the matrix";

  private const string MatrixDetails = """
    {"id":603,"title":"The Matrix","overview":"A hacker learns the truth.","release_date":"1999-03-31",
     "poster_path":"/m.jpg","vote_average":8.2,"genres":[],"similar":{"results":[]}}
    """;

  [Fact]
  public async Task Confirm_StoresId()
  {
    await using var host = await Host.StartAsync(Token);

    var response = await host.ConfirmAsync("DE - The Matrix (1999) 4K", 603);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    Assert.Equal(MatrixKey, body.GetProperty("key").GetString());
    Assert.Equal(603, body.GetProperty("tmdbId").GetInt32());
    var mark = Assert.Single(host.Marks());
    Assert.Equal(("603", false, 0), (mark.FilmDbId, mark.Seen, mark.Rating));

    // An existing mark keeps its seen and thumbs marks, and a later thumbs change keeps the id.
    Assert.Equal(HttpStatusCode.OK, (await host.ConfirmAsync("The Matrix (1999)", 9)).StatusCode);
    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "The Matrix (1999)", kind = "film", seen = true });
    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "The Matrix (1999)", kind = "film", seen = false });
    Assert.Equal("9", Assert.Single(host.Marks()).FilmDbId);
  }

  [Fact]
  public async Task Confirm_WithoutCsrf_Rejected()
  {
    await using var host = await Host.StartAsync(Token);

    var response = await host.ConfirmAsync("The Matrix (1999)", 603, withCsrf: false);

    Assert.False(response.IsSuccessStatusCode);
    Assert.Empty(host.Marks());
  }

  [Fact]
  public async Task Clear_RemovesId()
  {
    await using var host = await Host.StartAsync(Token);
    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "The Matrix (1999)", kind = "film", seen = true });
    await host.ConfirmAsync("The Matrix (1999)", 603);

    var cleared = await host.ConfirmAsync("The Matrix (1999) HD", null);

    Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    var mark = Assert.Single(host.Marks());
    Assert.Null(mark.FilmDbId);
    Assert.True(mark.Seen);

    // A mark that held only the choice leaves no row behind.
    await host.ConfirmAsync("Heat (1995)", 949);
    Assert.Equal(HttpStatusCode.NoContent, (await host.ConfirmAsync("Heat (1995)", null)).StatusCode);
    Assert.Single(host.Marks());
  }

  [Theory]
  [InlineData("/api/marks/film-match", "film", "The Matrix (1999)")]
  [InlineData("/api/marks/series-match", "series", "Dark (2017)")]
  public async Task Clear_KeepsAWatchlistedMark(string route, string kind, string name)
  {
    await using var host = await Host.StartAsync(Token);
    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind, watchlist = true });
    Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(HttpMethod.Put, route, new { name, tmdbId = 603 })).StatusCode);

    var cleared = await host.Client.SendAsync(HttpMethod.Put, route, new { name, tmdbId = (int?)null });

    Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    var body = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()).RootElement;
    Assert.True(body.GetProperty("watchlist").GetBoolean());
    Assert.Equal(JsonValueKind.Null, body.GetProperty("tmdbId").ValueKind);
    var mark = Assert.Single(host.Marks());
    Assert.Equal((true, (string?)null), (mark.Watchlist, mark.FilmDbId));
  }

  [Fact]
  public async Task Confirm_PastTheCap_Refused()
  {
    await using var host = await Host.StartAsync(Token);
    await host.Client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "The Matrix (1999)", kind = "film", seen = true });
    host.WithDb(db => db.Database.ExecuteSqlRaw(
      """
      WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {1})
      INSERT INTO titlemarks (id, userid, kind, titlekey, year, displaytitle, seen, rating, filmdbid, watchlist, createdutc, updatedutc)
      SELECT printf('00000000-0000-0000-0000-%012d', i), {0}, 'film', 'film::filler ' || i, NULL, 'Filler ' || i,
             1, 0, NULL, 0, strftime('%Y-%m-%d %H:%M:%S+00:00', 'now'), strftime('%Y-%m-%d %H:%M:%S+00:00', 'now')
      FROM n
      """,
      db.Users.Single(u => u.UserName == host.UserName).Id, MarkEndpoints.MaxMarksPerUser - 1));

    var refused = await host.ConfirmAsync("Heat (1995)", 949);
    Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    Assert.Contains("limit", await refused.Content.ReadAsStringAsync());

    // An existing mark stays editable.
    Assert.Equal(HttpStatusCode.OK, (await host.ConfirmAsync("The Matrix (1999)", 603)).StatusCode);
    Assert.Equal(MarkEndpoints.MaxMarksPerUser, host.Marks().Count);
  }

  [Fact]
  public async Task Confirmed_CardUsesIdWithoutSearch()
  {
    await using var host = await Host.StartAsync(Token);
    await host.ConfirmAsync("The Matrix (1999)", 603);

    var card = await host.Client.Http.GetAsync(
      $"/api/playlists/{host.PlaylistId}/library/film-card?name={Uri.EscapeDataString("EN | The Matrix (1999)")}&group=Movies&type=Movies");

    Assert.Equal(HttpStatusCode.OK, card.StatusCode);
    var body = JsonDocument.Parse(await card.Content.ReadAsStringAsync()).RootElement;
    Assert.Equal("matched", body.GetProperty("status").GetString());
    Assert.Equal("/3/movie/603", Assert.Single(host.TmdbRequests).AbsolutePath);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-603)]
  public async Task Confirm_NonPositiveId_Rejected(int tmdbId)
  {
    await using var host = await Host.StartAsync(Token);

    Assert.Equal(HttpStatusCode.BadRequest, (await host.ConfirmAsync("The Matrix (1999)", tmdbId)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(
      HttpMethod.Put, "/api/marks/film-match", new { name = "The Matrix (1999)", tmdbId = "603abc" })).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(
      HttpMethod.Put, "/api/marks/film-match", new { name = "The Matrix (1999)", tmdbId = 1.5 })).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(
      HttpMethod.Put, "/api/marks/film-match", new { name = "The Matrix (1999)", tmdbId = 1L << 40 })).StatusCode);
    Assert.Empty(host.Marks());
  }

  [Fact]
  public async Task Confirm_OtherUsersMark_Unchanged()
  {
    await using var host = await Host.StartAsync(Token);
    await host.ConfirmAsync("The Matrix (1999)", 603);

    var other = new ApiClient(host.Factory.CreateClient());
    await other.RegisterAsync("someone-else");
    Assert.Equal(HttpStatusCode.OK, (await other.SendAsync(
      HttpMethod.Put, "/api/marks/film-match", new { name = "The Matrix (1999)", tmdbId = 9 })).StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, (await other.SendAsync(
      HttpMethod.Put, "/api/marks/film-match", new { name = "The Matrix (1999)", tmdbId = (int?)null })).StatusCode);

    Assert.Equal("603", Assert.Single(host.Marks()).FilmDbId);
    var anonymous = await host.Factory.CreateClient().PutAsync("/api/marks/film-match", null);
    Assert.False(anonymous.IsSuccessStatusCode);
    Assert.Equal("603", Assert.Single(host.Marks()).FilmDbId);
  }

  [Fact]
  public async Task NoToken_NotFound()
  {
    await using var host = await Host.StartAsync(token: null);

    Assert.Equal(HttpStatusCode.NotFound, (await host.ConfirmAsync("The Matrix (1999)", 603)).StatusCode);
    Assert.Empty(host.Marks());
  }

  private sealed class Host : IAsyncDisposable
  {
    public required StrimAppFactory Root { get; init; }
    public required WebApplicationFactory<Program> Factory { get; init; }
    public required ApiClient Client { get; init; }
    public required ConcurrentQueue<Uri> TmdbRequests { get; init; }
    public Guid PlaylistId { get; private set; }
    public string UserName { get; } = "match-owner";

    public static async Task<Host> StartAsync(string? token)
    {
      var requests = new ConcurrentQueue<Uri>();
      var root = new StrimAppFactory();
      var app = root.WithWebHostBuilder(builder =>
      {
        builder.ConfigureAppConfiguration((_, config) =>
          config.AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = token }));
        builder.ConfigureTestServices(services => services.AddHttpClient(TmdbClient.HttpClientName)
          .ConfigurePrimaryHttpMessageHandler(() => new StubTmdb(requests)));
      });
      var host = new Host { Root = root, Factory = app, Client = new ApiClient(app.CreateClient()), TmdbRequests = requests };
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

    public Task<HttpResponseMessage> ConfirmAsync(string name, int? tmdbId, bool withCsrf = true) =>
      Client.SendAsync(HttpMethod.Put, "/api/marks/film-match", new { name, tmdbId }, withCsrf);

    public T WithDb<T>(Func<AppDbContext, T> action)
    {
      using var scope = Factory.Services.CreateScope();
      return action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>The owner's marks.</summary>
    public List<Api.Models.TitleMark> Marks() => WithDb(db => db.TitleMarks.AsNoTracking()
      .Where(m => db.Users.Any(u => u.Id == m.UserId && u.UserName == UserName)).ToList());

    public async ValueTask DisposeAsync()
    {
      await Factory.DisposeAsync();
      await Root.DisposeAsync();
    }
  }

  private sealed class StubTmdb(ConcurrentQueue<Uri> requests) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      requests.Enqueue(request.RequestUri!);
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(MatrixDetails, Encoding.UTF8, "application/json"),
      });
    }
  }
}
