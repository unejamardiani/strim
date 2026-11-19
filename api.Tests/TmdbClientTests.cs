using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Api.Configuration;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Tests;

public class TmdbClientTests
{
  // A made-up value; only its absence from logs, errors and URIs matters.
  private const string Token = "test-token-0d6f1c2e";

  [Fact]
  public async Task Disabled_WhenTokenMissing_SendsNoRequest()
  {
    var handler = new StubHandler(_ => Json("{}"));
    var client = CreateClient(handler, token: null);

    Assert.False(client.IsEnabled);
    var result = await client.SearchMovieAsync("Some Film", 2001, CancellationToken.None);

    Assert.Equal(TmdbStatus.Disabled, result.Status);
    Assert.Empty(handler.Requests);
  }

  [Fact]
  public async Task ReadsTokenFromTmdbReadAccessToken()
  {
    Assert.Equal("TMDB_READ_ACCESS_TOKEN", TmdbClient.TokenVariable);
    var handler = new StubHandler(_ => Json("""{"results":[]}"""));
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["TMDB_READ_ACCESS_TOKEN"] = Token }).Build();
    var client = new TmdbClient(new StubFactory(handler), configuration, new CapturingTmdbLogger(), TimeProvider.System);

    Assert.True(client.IsEnabled);
    await client.SearchMovieAsync("Some Film", null, CancellationToken.None);

    Assert.Equal(Token, Assert.Single(handler.Requests).Authorization?.Parameter);
  }

  [Fact]
  public async Task SendsTokenAsHeaderNotInUri()
  {
    var handler = new StubHandler(_ => Json("""{"results":[{"id":7,"title":"Some Film","release_date":"2001-05-04"}]}"""));
    var client = CreateClient(handler);

    var result = await client.SearchMovieAsync("Some Film", 2001, CancellationToken.None);

    Assert.Equal(TmdbStatus.Ok, result.Status);
    Assert.Equal(7, Assert.Single(result.Value!.Results!).Id);
    var request = Assert.Single(handler.Requests);
    Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);
    Assert.Equal("api.themoviedb.org", request.Uri.Host);
    Assert.Equal("/3/search/movie", request.Uri.AbsolutePath);
    Assert.Contains("query=Some%20Film", request.Uri.Query);
    Assert.Contains("year=2001", request.Uri.Query);
    Assert.Equal("Bearer", request.Authorization?.Scheme);
    Assert.Equal(Token, request.Authorization?.Parameter);
    Assert.DoesNotContain(Token, request.Uri.OriginalString);
  }

  [Fact]
  public async Task DoesNotFollowRedirects()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
      { Headers = { Location = new Uri("https://elsewhere.example/3/movie/1") } });
    var client = CreateClient(handler);

    var result = await client.GetMovieWithSimilarAsync(1, CancellationToken.None);

    Assert.Equal(TmdbStatus.Unavailable, result.Status);
    Assert.Single(handler.Requests);

    // The primary handler the named client is actually built with does not follow redirects either.
    var options = new ServiceCollection().AddStrimTmdb().BuildServiceProvider()
      .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(TmdbClient.HttpClientName);
    var builder = new InspectingHandlerBuilder();
    foreach (var configure in options.HttpMessageHandlerBuilderActions)
    {
      configure(builder);
    }
    var primary = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
    Assert.False(primary.AllowAutoRedirect);
  }

  private sealed class InspectingHandlerBuilder : HttpMessageHandlerBuilder
  {
    public override string? Name { get; set; } = TmdbClient.HttpClientName;
    public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
    public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];
    public override HttpMessageHandler Build() => PrimaryHandler;
  }

  [Fact]
  public async Task SlowAnswer_ReturnsUnavailable()
  {
    var handler = new StubHandler(async (_, ct) => { await Task.Delay(-1, ct); return Json("{}"); });
    var client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(200));

    var result = await client.GetMovieWithSimilarAsync(5, CancellationToken.None);

    Assert.Equal(TmdbStatus.Unavailable, result.Status);
  }

  [Fact]
  public async Task RateLimited_BlocksCallsUntilRetryAfter()
  {
    var time = new ManualTime();
    var logger = new CapturingTmdbLogger();
    var answers = new Queue<HttpResponseMessage>([TooMany(r => r.RetryAfter = new(TimeSpan.FromSeconds(10))),
      Json("""{"results":[]}""")]);
    var handler = new StubHandler(_ => answers.Dequeue());
    var client = CreateClient(handler, time: time, logger: logger);

    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("Secret Title", 1999, default)).Status);
    time.Advance(TimeSpan.FromSeconds(9));
    // Every call is blocked, whatever it asks for.
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("Other", null, default)).Status);
    Assert.Equal(TmdbStatus.RateLimited, (await client.GetMovieWithSimilarAsync(7, default)).Status);
    Assert.Single(handler.Requests);

    time.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(TmdbStatus.Ok, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Equal(2, handler.Requests.Count);

    var line = Assert.Single(logger.Lines);
    Assert.Contains("rate limit", line);
    foreach (var forbidden in new[] { Token, "Secret", "secret", "1999", "search", "query" })
      Assert.DoesNotContain(forbidden, line);
  }

  [Fact]
  public async Task RateLimited_HttpDateAndMissingHeader_UseDateOrDefault()
  {
    var time = new ManualTime();
    var answers = new Queue<HttpResponseMessage>([
      TooMany(r => r.RetryAfter = new(time.GetUtcNow().AddSeconds(120))),
      TooMany(_ => { }),
      TooMany(r => r.TryAddWithoutValidation("Retry-After", "soon")),
      Json("""{"results":[]}"""),
    ]);
    var handler = new StubHandler(_ => answers.Dequeue());
    var client = CreateClient(handler, time: time);

    // An HTTP date: blocked until that moment.
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("A", null, default)).Status);
    time.Advance(TimeSpan.FromSeconds(119));
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Single(handler.Requests);
    time.Advance(TimeSpan.FromSeconds(1));

    // No header, then one that cannot be read: the default applies each time.
    Assert.Equal(TimeSpan.FromSeconds(60), TmdbClient.DefaultBackoff);
    for (var expected = 2; expected <= 3; expected++)
    {
      Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("A", null, default)).Status);
      time.Advance(TmdbClient.DefaultBackoff - TimeSpan.FromSeconds(1));
      Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("A", null, default)).Status);
      Assert.Equal(expected, handler.Requests.Count);
      time.Advance(TimeSpan.FromSeconds(1));
    }

    Assert.Equal(TmdbStatus.Ok, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Equal(4, handler.Requests.Count);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task RateLimited_RetryAfterIsCapped(bool asDate)
  {
    var time = new ManualTime();
    var days = TimeSpan.FromDays(3);
    var answers = new Queue<HttpResponseMessage>([
      TooMany(r => r.RetryAfter = asDate ? new(time.GetUtcNow() + days) : new(days)),
      Json("""{"results":[]}"""),
    ]);
    var handler = new StubHandler(_ => answers.Dequeue());
    var client = CreateClient(handler, time: time);

    Assert.Equal(TimeSpan.FromHours(1), TmdbClient.MaxBackoff);
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("A", null, default)).Status);
    time.Advance(TmdbClient.MaxBackoff - TimeSpan.FromSeconds(1));
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Single(handler.Requests);

    time.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(TmdbStatus.Ok, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Equal(2, handler.Requests.Count);
  }

  [Fact]
  public async Task RateLimited_DoesNotRetry()
  {
    // Every answer is a 429 with no wait at all, so a retry loop would be free to run.
    var handler = new StubHandler(_ => TooMany(r => r.RetryAfter = new(TimeSpan.Zero)));
    var client = CreateClient(handler, time: new ManualTime());

    var result = await client.GetMovieWithSimilarAsync(3, default);

    Assert.Equal(TmdbStatus.RateLimited, result.Status);
    Assert.Single(handler.Requests);
  }

  private static HttpResponseMessage TooMany(Action<HttpResponseHeaders> headers)
  {
    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    headers(response.Headers);
    return response;
  }

  [Fact]
  public async Task NeverLogsOrThrowsToken()
  {
    var logger = new CapturingTmdbLogger();
    var calls = 0;
    var handler = new StubHandler(request => ++calls switch
    {
      1 => throw new HttpRequestException("transport failed for " + request.Headers.Authorization),
      2 => throw new InvalidOperationException("bad state " + request.Headers.Authorization),
      3 => new HttpResponseMessage(HttpStatusCode.Unauthorized)
      {
        Content = new StringContent("{\"status_message\":\"" + request.Headers.Authorization + "\"}"),
      },
      _ => Json("not json " + request.Headers.Authorization),
    });
    var client = CreateClient(handler, logger: logger);

    for (var i = 0; i < 4; i++)
    {
      var result = await client.GetMovieWithSimilarAsync(3, CancellationToken.None);
      Assert.Equal(TmdbStatus.Unavailable, result.Status);
    }

    Assert.Equal(4, handler.Requests.Count);
    Assert.NotEmpty(logger.Lines);
    Assert.All(logger.Lines, line => Assert.DoesNotContain(Token, line));
    var argument = await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SearchMovieAsync(" ", null, default));
    Assert.DoesNotContain(Token, argument.ToString());

    // The caller cancels while the transport throws a cancellation that echoes the header.
    using var caller = new CancellationTokenSource();
    var cancelling = CreateClient(new StubHandler(request =>
    {
      caller.Cancel();
      throw new OperationCanceledException("cancelled " + request.Headers.Authorization,
        new HttpRequestException("inner " + request.Headers.Authorization), caller.Token);
    }), logger: logger);
    var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
      () => cancelling.GetMovieWithSimilarAsync(3, caller.Token));
    Assert.DoesNotContain(Token, cancelled.ToString());
    Assert.Null(cancelled.InnerException);
    Assert.Equal(caller.Token, cancelled.CancellationToken);
    Assert.All(logger.Lines, line => Assert.DoesNotContain(Token, line));
  }

  [Fact]
  public async Task DetailsAndSimilar_InOneRequest()
  {
    var handler = new StubHandler(_ => Json("""
      {"id":42,"title":"Some Film","overview":"A story.","release_date":"1999-03-31","poster_path":"/p.jpg",
       "vote_average":7.5,"genres":[{"id":1,"name":"Drama"}],
       "similar":{"results":[{"id":43,"title":"Other Film"},{"id":44,"title":"Third Film"}]}}
      """));
    var client = CreateClient(handler);

    var result = await client.GetMovieWithSimilarAsync(42, CancellationToken.None);

    Assert.Equal(TmdbStatus.Ok, result.Status);
    var details = result.Value!;
    Assert.Equal("Some Film", details.Title);
    Assert.Equal("1999-03-31", details.ReleaseDate);
    Assert.Equal(7.5, details.VoteAverage);
    Assert.Equal("Drama", Assert.Single(details.Genres!).Name);
    Assert.Equal(new[] { 43, 44 }, details.Similar!.Results!.Select(r => r.Id));
    var request = Assert.Single(handler.Requests);
    Assert.Equal("/3/movie/42", request.Uri.AbsolutePath);
    Assert.Contains("append_to_response=similar", request.Uri.Query);
  }

  [Fact]
  public async Task SearchTv_SendsTitleAndFirstAirYearOnly()
  {
    var handler = new StubHandler(_ => Json("""
      {"results":[{"id":9,"name":"Some Series","first_air_date":"2008-01-20","poster_path":"/s.jpg","vote_average":8.9}]}
      """));
    var client = CreateClient(handler);

    var result = await client.SearchTvAsync(" Some Series ", 2008, CancellationToken.None);
    await client.SearchTvAsync("Some Series", null, CancellationToken.None);

    Assert.Equal(TmdbStatus.Ok, result.Status);
    var hit = Assert.Single(result.Value!.Results!);
    Assert.Equal((9, "Some Series", "2008-01-20", "/s.jpg", 8.9),
      (hit.Id, hit.Name, hit.FirstAirDate, hit.PosterPath, hit.VoteAverage));
    Assert.Equal(2, handler.Requests.Count);
    Assert.All(handler.Requests, r => Assert.Equal("/3/search/tv", r.Uri.AbsolutePath));
    Assert.Equal("?include_adult=false&query=Some%20Series&first_air_date_year=2008", handler.Requests[0].Uri.Query);
    Assert.Equal("?include_adult=false&query=Some%20Series", handler.Requests[1].Uri.Query);
    await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SearchTvAsync(" ", 2008, default));
    Assert.Equal(2, handler.Requests.Count);
  }

  [Fact]
  public async Task TvDetails_OneRequestWithSimilarAndSeasons()
  {
    var handler = new StubHandler(_ => Json("""
      {"id":42,"name":"Some Series","overview":"A story.","first_air_date":"2008-01-20",
       "last_air_date":"2013-09-29","status":"Ended","number_of_seasons":2,"number_of_episodes":20,
       "poster_path":"/p.jpg","vote_average":8.5,"genres":[{"id":1,"name":"Drama"}],
       "networks":[{"id":5,"name":"Some Network"}],
       "seasons":[{"season_number":0,"episode_count":3},{"season_number":1,"episode_count":7},
                  {"season_number":2,"episode_count":13}],
       "similar":{"results":[{"id":43,"name":"Other Series","first_air_date":"2010-04-01"},{"id":44,"name":"Third"}]}}
      """));
    var client = CreateClient(handler);

    var result = await client.GetTvWithSimilarAsync(42, CancellationToken.None);

    Assert.Equal(TmdbStatus.Ok, result.Status);
    var tv = result.Value!;
    Assert.Equal(("Some Series", "A story.", "2008-01-20", "2013-09-29", "Ended"),
      (tv.Name, tv.Overview, tv.FirstAirDate, tv.LastAirDate, tv.Status));
    Assert.Equal((2, 20, "/p.jpg", 8.5), (tv.NumberOfSeasons, tv.NumberOfEpisodes, tv.PosterPath, tv.VoteAverage));
    Assert.Equal("Drama", Assert.Single(tv.Genres!).Name);
    Assert.Equal("Some Network", Assert.Single(tv.Networks!).Name);
    Assert.Equal(new[] { (0, (int?)3), (1, 7), (2, 13) }, tv.Seasons!.Select(s => (s.SeasonNumber, s.EpisodeCount)));
    Assert.Equal(new[] { 43, 44 }, tv.Similar!.Results!.Select(r => r.Id));
    Assert.Equal("2010-04-01", tv.Similar.Results![0].FirstAirDate);
    var request = Assert.Single(handler.Requests);
    Assert.Equal("/3/tv/42", request.Uri.AbsolutePath);
    Assert.Equal("?append_to_response=similar", request.Uri.Query);
    await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => client.GetTvWithSimilarAsync(0, default));
  }

  [Fact]
  public async Task Tv_UsesCacheAndBackoff()
  {
    var time = new ManualTime();
    var cache = new TmdbCache(time);
    var answers = new Queue<HttpResponseMessage>([
      Json("""{"results":[{"id":9,"name":"Some Series"}]}"""),
      Json("""{"id":9,"name":"Some Series","seasons":[{"season_number":1,"episode_count":6}]}"""),
      TooMany(r => r.RetryAfter = new(TimeSpan.FromSeconds(30))),
      Json("""{"id":10,"name":"Later"}"""),
    ]);
    var handler = new StubHandler(_ => answers.Dequeue());
    var client = CreateClient(handler, time: time, cache: cache);

    Assert.Equal(TmdbStatus.Ok, (await client.SearchTvAsync("Some Series", 2008, default)).Status);
    Assert.Equal(TmdbStatus.Ok, (await client.GetTvWithSimilarAsync(9, default)).Status);
    // Cache hits: no request, and the tv paths do not collide with the film paths.
    Assert.Equal(9, Assert.Single((await client.SearchTvAsync("Some Series", 2008, default)).Value!.Results!).Id);
    Assert.Equal(6, Assert.Single((await client.GetTvWithSimilarAsync(9, default)).Value!.Seasons!).EpisodeCount);
    Assert.True(cache.TryGet("search/tv?include_adult=false&query=Some%20Series&first_air_date_year=2008", out _));
    Assert.True(cache.TryGet("tv/9?append_to_response=similar", out _));
    Assert.False(cache.TryGet("movie/9?append_to_response=similar", out _));
    Assert.Equal(2, handler.Requests.Count);

    // A 429 blocks every call, films and series alike, until its Retry-After has passed.
    Assert.Equal(TmdbStatus.RateLimited, (await client.GetTvWithSimilarAsync(10, default)).Status);
    time.Advance(TimeSpan.FromSeconds(29));
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchTvAsync("Other", null, default)).Status);
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchMovieAsync("Other", null, default)).Status);
    Assert.Equal(3, handler.Requests.Count);
    // A cached answer is still served while blocked.
    Assert.Equal(TmdbStatus.Ok, (await client.GetTvWithSimilarAsync(9, default)).Status);
    time.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(TmdbStatus.Ok, (await client.GetTvWithSimilarAsync(10, default)).Status);
    Assert.Equal(4, handler.Requests.Count);
  }

  [Fact]
  public async Task Tv_TokenNeverInUriOrLog()
  {
    var logger = new CapturingTmdbLogger();
    var calls = 0;
    var handler = new StubHandler(request => ++calls switch
    {
      1 => Json("""{"results":[]}"""),
      2 => throw new HttpRequestException("transport failed for " + request.Headers.Authorization),
      3 => new HttpResponseMessage(HttpStatusCode.Unauthorized)
      {
        Content = new StringContent("{\"status_message\":\"" + request.Headers.Authorization + "\"}"),
      },
      4 => Json("not json " + request.Headers.Authorization),
      _ => TooMany(r => r.RetryAfter = new(TimeSpan.FromSeconds(5))),
    });
    var client = CreateClient(handler, logger: logger, time: new ManualTime());

    Assert.Equal(TmdbStatus.Ok, (await client.SearchTvAsync("Secret Series", 1999, default)).Status);
    for (var i = 0; i < 3; i++)
    {
      Assert.Equal(TmdbStatus.Unavailable, (await client.GetTvWithSimilarAsync(3, default)).Status);
    }
    Assert.Equal(TmdbStatus.RateLimited, (await client.SearchTvAsync("Secret Series", 1999, default)).Status);

    Assert.Equal(5, handler.Requests.Count);
    Assert.All(handler.Requests, r =>
    {
      Assert.Equal("Bearer", r.Authorization?.Scheme);
      Assert.Equal(Token, r.Authorization?.Parameter);
      Assert.DoesNotContain(Token, r.Uri.OriginalString);
    });
    Assert.Equal(4, logger.Lines.Count);
    foreach (var line in logger.Lines)
      foreach (var forbidden in new[] { Token, "Secret", "1999", "search", "query", "tv/" })
        Assert.DoesNotContain(forbidden, line);
  }

  private static TmdbClient CreateClient(
    StubHandler handler,
    string? token = Token,
    TimeSpan? timeout = null,
    TimeProvider? time = null,
    ILogger<TmdbClient>? logger = null,
    TmdbCache? cache = null)
  {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = token }).Build();
    return new TmdbClient(new StubFactory(handler), configuration, logger ?? new CapturingTmdbLogger(),
      time ?? TimeProvider.System, cache) { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
  }

  private static HttpResponseMessage Json(string body) =>
    new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  private sealed record SeenRequest(Uri Uri, AuthenticationHeaderValue? Authorization);

  private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
  {
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
      : this((request, _) => Task.FromResult(respond(request))) { }

    public List<SeenRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Requests.Add(new SeenRequest(request.RequestUri!, request.Headers.Authorization));
      return respond(request, ct);
    }
  }

  private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
  }

  private sealed class ManualTime : TimeProvider
  {
    private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
  }

  private sealed class CapturingTmdbLogger : ILogger<TmdbClient>
  {
    public ConcurrentQueue<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Lines.Enqueue(formatter(state, exception) + " " + exception);
  }
}
