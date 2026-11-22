using Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Tests;

/// <summary>Outputs with a Watchlist group are cached under the watchlist's fingerprint (#520).</summary>
public sealed class WatchlistGroupPipelineTests : IDisposable
{
  private const string Source = """
    #EXTM3U
    #EXTINF:-1 group-title="News",News One
    http://line.example/live/u/p/1.ts
    #EXTINF:-1 group-title="Films",Little Motel (1995)
    http://line.example/movie/u/p/2.mp4
    #EXTINF:-1 group-title="Films",Other Film (2001)
    http://line.example/movie/u/p/3.mp4
    #EXTINF:-1 group-title="Films",Third Film (2010)
    http://line.example/movie/u/p/4.mp4

    """;

  private readonly string _directory = Path.Combine(Path.GetTempPath(), $"strim-cache-test-{Guid.NewGuid():N}");
  private readonly PlaylistFileCache _cache;
  private readonly PlaylistPipeline _pipeline;
  private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase) { "News" };

  public WatchlistGroupPipelineTests()
  {
    var options = Options.Create(new PlaylistCacheOptions
    {
      Directory = _directory, MaxSourceBytes = 1024 * 1024, MaxDiskBytes = 4 * 1024 * 1024, EntryTtlMinutes = 10, SourceTtlMinutes = 10,
    });
    _cache = new PlaylistFileCache(options, NullLogger<PlaylistFileCache>.Instance);
    // The fetcher is never used: the source is stored in the cache directly.
    _pipeline = new PlaylistPipeline(_cache, new PlaylistSourceFetcher(new NoHttp(), _cache, options, NullLogger<PlaylistSourceFetcher>.Instance));
  }

  public void Dispose()
  {
    if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
  }

  private static string Film(string name) => MarkKey.From(name, MarkKind.Film)!.Key;

  private static WatchlistGroup Watchlist(params string[] names) => new(names.Select(Film).ToList());

  private async Task<PlaylistSourceFile> SourceAsync() =>
    _cache.StoreDownloaded("http://line.example/get.php", await _cache.WriteRawTextAsync(Source, CancellationToken.None));

  /// <summary>The names in the Watchlist group of the output, in order.</summary>
  private async Task<string[]> GroupAsync(PlaylistOutputFile output)
  {
    await using var lease = _cache.TryLeaseOutput(output.OutputKey);
    return (await File.ReadAllLinesAsync(lease!.FilePath))
      .Where(line => line.StartsWith("#EXTINF:-1 group-title=\"Watchlist\",", StringComparison.Ordinal))
      .Select(line => line[(line.IndexOf(',') + 1)..])
      .ToArray();
  }

  [Fact]
  public async Task GetOutput_WithAWatchlist_WritesTheGroup_AndReusesItForTheSameWatchlist()
  {
    var source = await SourceAsync();

    var first = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None, Watchlist("Other Film (2001)", "Little Motel (1995)"));
    var second = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None, Watchlist("Other Film (2001)", "Little Motel (1995)"));

    Assert.Equal(first.OutputKey, second.OutputKey);
    Assert.Equal(new[] { "Other Film (2001)", "Little Motel (1995)" }, await GroupAsync(first));
  }

  [Fact]
  public async Task GetOutput_AfterTheWatchlistChanged_WritesANewOutput()
  {
    var source = await SourceAsync();
    var before = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None, Watchlist("Other Film (2001)", "Little Motel (1995)"));
    var keys = new HashSet<string> { before.OutputKey };

    foreach (var names in new[]
    {
      new[] { "Third Film (2010)", "Other Film (2001)", "Little Motel (1995)" }, // added
      new[] { "Other Film (2001)" }, // removed
      new[] { "Little Motel (1995)", "Other Film (2001)" }, // swapped
    })
    {
      var output = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None, Watchlist(names));
      Assert.True(keys.Add(output.OutputKey));
      Assert.Equal(names, await GroupAsync(output));
    }
  }

  [Fact]
  public async Task GetOutput_WithoutAWatchlist_NeverReturnsAnOutputWithTheGroup()
  {
    var source = await SourceAsync();
    var withGroup = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None, Watchlist("Other Film (2001)"));
    Assert.NotEmpty(await GroupAsync(withGroup));

    var plain = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None);
    var plainAgain = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None, null);

    Assert.NotEqual(withGroup.OutputKey, plain.OutputKey);
    Assert.Equal(plain.OutputKey, plainAgain.OutputKey);
    Assert.Empty(await GroupAsync(plain));
  }

  [Fact]
  public async Task GetOutput_WithAnEmptyWatchlist_IsThePlainOutput()
  {
    var source = await SourceAsync();

    var plain = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None);
    var empty = await _pipeline.GetOutputAsync(source, _disabled, CancellationToken.None, new WatchlistGroup([]));

    Assert.Equal(plain.OutputKey, empty.OutputKey);
    Assert.Empty(await GroupAsync(empty));
  }

  [Fact]
  public void Fingerprint_DependsOnKeysAndOrder()
  {
    var ab = new WatchlistGroup(["film:1995:a", "film:2001:b"]);

    Assert.Equal(ab.Fingerprint, new WatchlistGroup(["film:1995:a", "film:2001:b"]).Fingerprint);
    Assert.Matches("^[0-9a-f]{64}$", ab.Fingerprint);
    Assert.Equal(
      Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("film:1995:a\nfilm:2001:b"u8.ToArray())).ToLowerInvariant(),
      ab.Fingerprint);
    Assert.NotEqual(ab.Fingerprint, new WatchlistGroup(["film:2001:b", "film:1995:a"]).Fingerprint);
    Assert.NotEqual(ab.Fingerprint, new WatchlistGroup(["film:1995:a"]).Fingerprint);
    Assert.NotEqual(ab.Fingerprint, new WatchlistGroup(["film:1995:a", "film:2001:b", "series:c"]).Fingerprint);
    Assert.NotEqual(ab.Fingerprint, new WatchlistGroup(["film:1995:a", "film:2001:c"]).Fingerprint);
    Assert.Equal("", new WatchlistGroup([]).Fingerprint);
  }

  private sealed class NoHttp : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in this test.");
  }
}
