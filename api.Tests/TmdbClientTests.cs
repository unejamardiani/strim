using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Api.Configuration;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    // The registered primary handler does not follow redirects on its own either.
    Assert.False(TmdbServices.CreatePrimaryHandler().AllowAutoRedirect);
    Assert.NotNull(new ServiceCollection().AddStrimTmdb().BuildServiceProvider()
      .GetRequiredService<IHttpClientFactory>().CreateClient(TmdbClient.HttpClientName));
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
  public async Task RateLimited_RespectsRetryAfter()
  {
    var time = new ManualTime();
    var withRetryAfter = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    withRetryAfter.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
    var answers = new Queue<HttpResponseMessage>(
      [withRetryAfter, new HttpResponseMessage(HttpStatusCode.TooManyRequests), Json("""{"results":[]}""")]);
    var handler = new StubHandler(_ => answers.Dequeue());
    var client = CreateClient(handler, time: time);

    Assert.Equal(TmdbStatus.Unavailable, (await client.SearchMovieAsync("A", null, default)).Status);
    time.Advance(TimeSpan.FromSeconds(9));
    Assert.Equal(TmdbStatus.Unavailable, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Single(handler.Requests);

    time.Advance(TimeSpan.FromSeconds(2));
    // No Retry-After: the default back-off applies.
    Assert.Equal(TmdbStatus.Unavailable, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Equal(2, handler.Requests.Count);
    time.Advance(TimeSpan.FromSeconds(59));
    Assert.Equal(TmdbStatus.Unavailable, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Equal(2, handler.Requests.Count);

    time.Advance(TimeSpan.FromSeconds(2));
    Assert.Equal(TmdbStatus.Ok, (await client.SearchMovieAsync("A", null, default)).Status);
    Assert.Equal(3, handler.Requests.Count);
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

  private static TmdbClient CreateClient(
    StubHandler handler,
    string? token = Token,
    TimeSpan? timeout = null,
    TimeProvider? time = null,
    ILogger<TmdbClient>? logger = null)
  {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = token }).Build();
    return new TmdbClient(new StubFactory(handler), configuration, logger ?? new CapturingTmdbLogger(),
      time ?? TimeProvider.System) { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
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
