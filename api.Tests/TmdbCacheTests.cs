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
  public void Lifetime_IsClampedToThirtyDays()
  {
    Assert.Equal(TimeSpan.FromDays(30), TmdbCache.MaxLifetime);
    Assert.Equal(TmdbCache.MaxLifetime, new TmdbCache(new ManualTime()).Lifetime);
    Assert.Equal(TimeSpan.FromDays(30), new TmdbCache(new ManualTime()) { Lifetime = TimeSpan.FromDays(31) }.Lifetime);
    Assert.Equal(TimeSpan.FromDays(30), new TmdbCache(new ManualTime()) { Lifetime = TimeSpan.MaxValue }.Lifetime);
    Assert.Equal(TimeSpan.FromDays(7), new TmdbCache(new ManualTime()) { Lifetime = TimeSpan.FromDays(7) }.Lifetime);

    // A clamped cache really drops the entry after 30 days.
    var time = new ManualTime();
    var cache = new TmdbCache(time) { Lifetime = TimeSpan.FromDays(365) };
    cache.Set("a", new byte[10]);
    time.Advance(TimeSpan.FromDays(30));
    Assert.False(cache.TryGet("a", out _));
  }

  [Fact]
  public void ExpiredEntry_ReleasesBudget()
  {
    var time = new ManualTime();
    var body = new byte[100];
    var entrySize = TmdbCache.SizeOf("a", body);
    var cache = new TmdbCache(time) { Lifetime = TimeSpan.FromDays(1), ByteBudget = entrySize * 2 };

    // Removed on lookup: an expired entry is a miss and frees its bytes.
    cache.Set("a", body);
    time.Advance(TimeSpan.FromDays(1));
    Assert.False(cache.TryGet("a", out _));
    Assert.Equal(0, cache.Bytes);

    // Replaced: refreshing an expired entry counts it once, not twice.
    cache.Set("b", body);
    time.Advance(TimeSpan.FromDays(1));
    cache.Set("b", body);
    Assert.Equal(entrySize, cache.Bytes);
    Assert.True(cache.TryGet("b", out _));

    // The freed bytes are usable: two fresh entries fit again.
    cache.Set("c", body);
    Assert.True(cache.TryGet("b", out _));
    Assert.True(cache.TryGet("c", out _));
    Assert.Equal(entrySize * 2, cache.Bytes);
  }

  [Fact]
  public void Cache_IsBuiltOnlyFromInMemoryState()
  {
    var type = typeof(TmdbCache);
    var constructor = Assert.Single(type.GetConstructors());
    Assert.Equal([typeof(TimeProvider)], constructor.GetParameters().Select(p => p.ParameterType));

    var allowed = new[] { "System.Object", "System.Int64", "System.TimeSpan", "System.TimeProvider",
      "System.Collections.Generic." };
    var fields = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
      | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    Assert.NotEmpty(fields);
    foreach (var field in fields)
    {
      var name = field.FieldType.FullName ?? field.FieldType.Name;
      Assert.True(allowed.Any(name.StartsWith), $"{field.Name} has type {name}");
      Assert.DoesNotContain("System.IO", name);
      Assert.DoesNotContain("EntityFramework", name);
      Assert.DoesNotContain("Data", name);
    }
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
