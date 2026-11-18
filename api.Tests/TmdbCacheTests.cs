using System.Net;
using System.Text;
using Api.Configuration;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Tests;

public class TmdbCacheTests
{
  private const string Token = "test-token-5a9e3b71";
  private const string Details = """{"id":42,"title":"Some Film","similar":{"results":[{"id":43}]}}""";

  [Fact]
  public async Task RepeatedCall_ServedFromCache()
  {
    var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("search")
      ? Json("""{"results":[{"id":7,"title":"Some Film"}]}""") : Json(Details));
    var client = CreateClient(handler, new TmdbCache(new ManualTime()));

    var first = await client.SearchMovieAsync("Some Film", 2001, default);
    var second = await client.SearchMovieAsync("Some Film", 2001, default);
    Assert.Equal(TmdbStatus.Ok, second.Status);
    Assert.Equal(first.Value!.Results![0].Id, second.Value!.Results![0].Id);
    Assert.Equal(1, handler.Count);

    // A different query is a different entry.
    await client.SearchMovieAsync("Some Film", 2002, default);
    Assert.Equal(2, handler.Count);

    var details = await client.GetMovieWithSimilarAsync(42, default);
    var again = await client.GetMovieWithSimilarAsync(42, default);
    Assert.Equal(TmdbStatus.Ok, again.Status);
    Assert.Equal(details.Value!.Title, again.Value!.Title);
    Assert.Equal(43, again.Value.Similar!.Results![0].Id);
    Assert.Equal(3, handler.Count);
  }

  [Fact]
  public async Task Entry_ExpiresAfterLifetime()
  {
    var defaults = new TmdbCache(TimeProvider.System);
    Assert.True(defaults.Lifetime <= TimeSpan.FromDays(30));
    Assert.True(defaults.Lifetime > TimeSpan.Zero);

    var time = new ManualTime();
    var handler = new StubHandler(_ => Json(Details));
    var client = CreateClient(handler, new TmdbCache(time) { Lifetime = TimeSpan.FromDays(10) }, time);

    await client.GetMovieWithSimilarAsync(42, default);
    time.Advance(TimeSpan.FromDays(10) - TimeSpan.FromSeconds(1));
    await client.GetMovieWithSimilarAsync(42, default);
    Assert.Equal(1, handler.Count);

    time.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(TmdbStatus.Ok, (await client.GetMovieWithSimilarAsync(42, default)).Status);
    Assert.Equal(2, handler.Count);
  }

  [Fact]
  public void StaysWithinByteBudget()
  {
    var body = new byte[100];
    var entrySize = TmdbCache.SizeOf("a", body);
    var cache = new TmdbCache(new ManualTime()) { ByteBudget = entrySize * 2 };

    cache.Set("a", body);
    cache.Set("b", body);
    Assert.Equal(entrySize * 2, cache.Bytes);

    cache.Set("c", body);
    Assert.False(cache.TryGet("a", out _));
    Assert.True(cache.TryGet("b", out _));
    Assert.True(cache.TryGet("c", out _));
    Assert.True(cache.Bytes <= cache.ByteBudget);

    // An answer larger than the whole budget is not cached and evicts nothing.
    cache.Set("d", new byte[entrySize * 3]);
    Assert.False(cache.TryGet("d", out _));
    Assert.True(cache.TryGet("b", out _));
    Assert.True(cache.TryGet("c", out _));
    Assert.Equal(entrySize * 2, cache.Bytes);
  }

  [Fact]
  public async Task StaysWithinByteBudget_ThroughClient()
  {
    var handler = new StubHandler(_ => Json(Details));
    var client = CreateClient(handler, new TmdbCache(new ManualTime()) { ByteBudget = 10 });

    await client.GetMovieWithSimilarAsync(42, default);
    await client.GetMovieWithSimilarAsync(42, default);

    Assert.Equal(2, handler.Count);
  }

  [Fact]
  public async Task FailuresAreNotCached()
  {
    var time = new ManualTime();
    var calls = 0;
    var handler = new StubHandler(async (_, ct) => ++calls switch
    {
      1 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
      2 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
      3 => await Hang(ct),
      4 => Json("not json"),
      5 => throw new HttpRequestException("transport failed"),
      _ => Json(Details),
    });
    var cache = new TmdbCache(time);
    var client = CreateClient(handler, cache, time, TimeSpan.FromMilliseconds(100));

    for (var i = 0; i < 5; i++)
    {
      Assert.Equal(TmdbStatus.Unavailable, (await client.GetMovieWithSimilarAsync(42, default)).Status);
      Assert.Equal(0, cache.Bytes);
      time.Advance(TimeSpan.FromMinutes(2)); // past the rate-limit back-off
    }
    Assert.Equal(5, handler.Count);

    Assert.Equal(TmdbStatus.Ok, (await client.GetMovieWithSimilarAsync(42, default)).Status);
    Assert.Equal(TmdbStatus.Ok, (await client.GetMovieWithSimilarAsync(42, default)).Status);
    Assert.Equal(6, handler.Count);
  }

  [Fact]
  public void Cache_IsProcessLocalSingleton()
  {
    var provider = new ServiceCollection().AddStrimTmdb().BuildServiceProvider();
    Assert.Same(provider.GetRequiredService<TmdbCache>(), provider.GetRequiredService<TmdbCache>());
  }

  private static async Task<HttpResponseMessage> Hang(CancellationToken ct)
  {
    await Task.Delay(-1, ct);
    return Json("{}");
  }

  private static TmdbClient CreateClient(StubHandler handler, TmdbCache cache, TimeProvider? time = null,
    TimeSpan? timeout = null)
  {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = Token }).Build();
    return new TmdbClient(new StubFactory(handler), configuration, NullLogger<TmdbClient>.Instance,
      time ?? TimeProvider.System, cache) { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
  }

  private static HttpResponseMessage Json(string body) =>
    new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
  {
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
      : this((request, _) => Task.FromResult(respond(request))) { }

    private int _count;
    public int Count => _count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Interlocked.Increment(ref _count);
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
}
