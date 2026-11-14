using Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Tests;

public class PlaylistFileCacheTests
{
  [Fact]
  public async Task GeneratedOutput_IsReusedForTheSameSourceHashAndDisabledGroups()
  {
    var directory = Path.Combine(Path.GetTempPath(), $"strim-cache-test-{Guid.NewGuid():N}");
    var options = Options.Create(new PlaylistCacheOptions
    {
      Directory = directory,
      MaxSourceBytes = 1024 * 1024,
      MaxDiskBytes = 4 * 1024 * 1024,
      EntryTtlMinutes = 10,
      SourceTtlMinutes = 10,
    });
    var cache = new PlaylistFileCache(options, NullLogger<PlaylistFileCache>.Instance);

    try
    {
      var downloaded = await cache.WriteRawTextAsync(
        "#EXTM3U\n#EXTINF:-1 group-title=\"News\",News\nhttps://example.com/news.m3u8\n",
        CancellationToken.None);
      var source = cache.StoreDownloaded("https://example.com/playlist.m3u", downloaded);
      Assert.Same(source, cache.TryGetFreshSource("https://example.com/playlist.m3u"));
      var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var temporaryOutput = cache.CreateOutputTemporaryPath();
      using var outputReservation = cache.ReserveOutputCapacity(source);
      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        source.FilePath,
        temporaryOutput,
        disabled,
        CancellationToken.None,
        cache.MaxLineLengthChars,
        cache.MaxGeneratedBytes);
      var output = cache.StoreOutput(source, disabled, temporaryOutput, result);

      var cached = cache.TryGetOutput(source.ContentHash, disabled);
      Assert.NotNull(cached);
      Assert.Equal(output.OutputKey, cached!.OutputKey);

      await using var lease = cache.TryLeaseOutput(output.OutputKey);
      Assert.NotNull(lease);
      Assert.True(File.Exists(lease!.FilePath));

      await using var renamedLease = cache.TryLeaseOutput(output.OutputKey, "other\r\nsource.m3u");
      Assert.NotNull(renamedLease);
      Assert.Equal("other__source.m3u", renamedLease!.FileName);
    }
    finally
    {
      if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void OutputFileName_IsSafeForContentDisposition()
  {
    var safe = PlaylistFileCache.SanitizeFileName("report\r\nX-Injected: yes\".m3u");

    Assert.DoesNotContain('\r', safe);
    Assert.DoesNotContain('\n', safe);
    Assert.DoesNotContain('"', safe);
    Assert.EndsWith(".m3u", safe, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void SourceReservation_RejectsAConfigurationThatCannotFitItsMaximumSource()
  {
    var directory = Path.Combine(Path.GetTempPath(), $"strim-cache-test-{Guid.NewGuid():N}");
    var options = Options.Create(new PlaylistCacheOptions
    {
      Directory = directory,
      MaxSourceBytes = 2 * 1024 * 1024,
      MaxDiskBytes = 1 * 1024 * 1024,
    });
    var cache = new PlaylistFileCache(options, NullLogger<PlaylistFileCache>.Instance);

    try
    {
      Assert.Throws<PlaylistDiskCapacityExceededException>(() => cache.ReserveSourceCapacity(null));
    }
    finally
    {
      if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void FailedStartupCleanup_RemainsAccountedUntilARetrySucceeds()
  {
    var directory = Path.Combine(Path.GetTempPath(), $"strim-cache-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var staleFile = Path.Combine(directory, "source-stale.m3u");
    File.WriteAllText(staleFile, "0123456789");
    var allowDelete = false;
    var options = Options.Create(new PlaylistCacheOptions
    {
      Directory = directory,
      MaxSourceBytes = 10,
      MaxDiskBytes = 10,
    });
    var cache = new PlaylistFileCache(
      options,
      NullLogger<PlaylistFileCache>.Instance,
      path => allowDelete && PlaylistFileCache.TryDelete(path));

    try
    {
      Assert.Throws<PlaylistDiskCapacityExceededException>(() => cache.ReserveSourceCapacity(null));
      Assert.True(File.Exists(staleFile));

      allowDelete = true;
      cache.Cleanup();

      Assert.False(File.Exists(staleFile));
      using var reservation = cache.ReserveSourceCapacity(null);
    }
    finally
    {
      allowDelete = true;
      if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task FailedEviction_DoesNotReleaseDiskCapacityUntilDeletionSucceeds()
  {
    var directory = Path.Combine(Path.GetTempPath(), $"strim-cache-test-{Guid.NewGuid():N}");
    var allowDelete = true;
    var options = Options.Create(new PlaylistCacheOptions
    {
      Directory = directory,
      MaxSourceBytes = 10,
      MaxDiskBytes = 10,
    });
    var cache = new PlaylistFileCache(
      options,
      NullLogger<PlaylistFileCache>.Instance,
      path => allowDelete && PlaylistFileCache.TryDelete(path));

    try
    {
      var downloaded = await cache.WriteRawTextAsync("0123456789", CancellationToken.None);
      var source = cache.StoreRawText("failed-eviction", downloaded);
      Assert.True(File.Exists(source.FilePath));

      allowDelete = false;
      Assert.Throws<PlaylistDiskCapacityExceededException>(() => cache.ReserveSourceCapacity(null));
      Assert.True(File.Exists(source.FilePath));

      allowDelete = true;
      cache.Cleanup();

      Assert.False(File.Exists(source.FilePath));
      using var reservation = cache.ReserveSourceCapacity(null);
    }
    finally
    {
      allowDelete = true;
      if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task LastGood_SurvivesExpiryOfTheNormalOutput()
  {
    using var fixture = new LastGoodFixture(maxDiskBytes: 4096);
    var source = await fixture.StoreSourceAsync(new string('s', 100));
    var output = fixture.StoreOutput(source, "Sports", "#EXTM3U\nkept\n");
    var playlistId = Guid.NewGuid();

    Assert.True(fixture.Cache.RememberLastGood(playlistId, output));
    fixture.Time.Advance(TimeSpan.FromMinutes(31));
    fixture.Cache.Cleanup();

    Assert.Null(fixture.Cache.TryLeaseOutput(output.OutputKey));
    await using var lease = fixture.Cache.TryLeaseLastGood(playlistId, "list.m3u");
    Assert.NotNull(lease);
    Assert.Equal("#EXTM3U\nkept\n", await File.ReadAllTextAsync(lease!.FilePath));
  }

  [Fact]
  public async Task LastGood_LeaseReturnsRememberedBytes_OrNullWhenUnknown()
  {
    using var fixture = new LastGoodFixture(maxDiskBytes: 4096);
    var source = await fixture.StoreSourceAsync(new string('s', 100));
    var output = fixture.StoreOutput(source, "Sports", "#EXTM3U\nremembered\n");
    var playlistId = Guid.NewGuid();

    Assert.Null(fixture.Cache.TryLeaseLastGood(playlistId, "list.m3u"));
    Assert.True(fixture.Cache.RememberLastGood(playlistId, output));
    Assert.Null(fixture.Cache.TryLeaseLastGood(Guid.NewGuid(), "list.m3u"));

    await using var lease = fixture.Cache.TryLeaseLastGood(playlistId, "my\r\nlist.m3u");
    Assert.NotNull(lease);
    Assert.Equal("my__list.m3u", lease!.FileName);
    Assert.Equal("#EXTM3U\nremembered\n"u8.ToArray(), await File.ReadAllBytesAsync(lease.FilePath));

    var name = Path.GetFileName(lease.FilePath);
    Assert.StartsWith("lastgood-", name, StringComparison.Ordinal);
    Assert.DoesNotContain(playlistId.ToString("N"), name, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(playlistId.ToString("D"), name, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task LastGood_IsReplacedByNewerOutput()
  {
    using var fixture = new LastGoodFixture(maxDiskBytes: 300, maxSourceBytes: 100);
    var source = await fixture.StoreSourceAsync(new string('s', 100));
    var first = fixture.StoreOutput(source, "Sports", new string('1', 100));
    var playlistId = Guid.NewGuid();
    Assert.True(fixture.Cache.RememberLastGood(playlistId, first));
    var firstPath = fixture.LastGoodPath(playlistId);

    var second = fixture.StoreOutput(source, "News", new string('2', 100));
    Assert.True(fixture.Cache.RememberLastGood(playlistId, second));

    Assert.False(File.Exists(firstPath));
    Assert.Single(Directory.EnumerateFiles(fixture.Directory, "lastgood-*"));
    Assert.Equal(new string('2', 100), await File.ReadAllTextAsync(fixture.LastGoodPath(playlistId)));

    // Only the newer output and its copy remain tracked (200 of 300 bytes), so a 100-byte
    // reservation needs no eviction. A still-counted old copy would force the output out.
    using var reservation = fixture.Cache.ReserveSourceCapacity(null);
    using var outputLease = fixture.Cache.TryLeaseOutput(second.OutputKey);
    Assert.NotNull(outputLease);
  }

  [Fact]
  public async Task LastGood_CountsAgainstMaxDiskBytes()
  {
    // Source, output and copy fill the 300-byte budget. Without the copy counted, a 100-byte
    // reservation would fit without evicting the output.
    using var fixture = new LastGoodFixture(maxDiskBytes: 300, maxSourceBytes: 100);
    var source = await fixture.StoreSourceAsync(new string('s', 100));
    var output = fixture.StoreOutput(source, "Sports", new string('o', 100));
    var playlistId = Guid.NewGuid();
    Assert.True(fixture.Cache.RememberLastGood(playlistId, output));

    using var reservation = fixture.Cache.ReserveSourceCapacity(null);

    Assert.Null(fixture.Cache.TryLeaseOutput(output.OutputKey));
    using var lease = fixture.Cache.TryLeaseLastGood(playlistId);
    Assert.NotNull(lease);
  }

  [Fact]
  public async Task LastGood_RememberDoesNotCountTheCopyTwiceForConcurrentCleanup()
  {
    // Source, output and copy fill the budget exactly. If the copy's reservation were still held
    // after the copy was registered, a concurrent Cleanup would see 400 of 300 bytes and evict.
    using var fixture = new LastGoodFixture(maxDiskBytes: 300, maxSourceBytes: 100);
    var source = await fixture.StoreSourceAsync(new string('s', 100));
    var output = fixture.StoreOutput(source, "Sports", new string('o', 100));
    var playlistId = Guid.NewGuid();
    using var stop = new CancellationTokenSource();
    var cleaner = Task.Run(() =>
    {
      while (!stop.IsCancellationRequested) fixture.Cache.Cleanup();
    });

    try
    {
      for (var i = 0; i < 100; i++)
      {
        Assert.True(fixture.Cache.RememberLastGood(playlistId, output));
        Assert.True(File.Exists(source.FilePath), $"Source evicted in iteration {i}.");
        Assert.True(File.Exists(fixture.LastGoodPath(playlistId)), $"Copy evicted in iteration {i}.");
        fixture.Cache.ForgetLastGood(playlistId);
      }
    }
    finally
    {
      stop.Cancel();
      await cleaner;
    }

    using var outputLease = fixture.Cache.TryLeaseOutput(output.OutputKey);
    Assert.NotNull(outputLease);
  }

  [Fact]
  public async Task LastGood_ForgetDeletesTheFile()
  {
    using var fixture = new LastGoodFixture(maxDiskBytes: 4096);
    var source = await fixture.StoreSourceAsync(new string('s', 100));
    var output = fixture.StoreOutput(source, "Sports", "#EXTM3U\n");
    var playlistId = Guid.NewGuid();
    Assert.True(fixture.Cache.RememberLastGood(playlistId, output));
    var path = fixture.LastGoodPath(playlistId);

    fixture.Cache.ForgetLastGood(playlistId);

    Assert.False(File.Exists(path));
    Assert.Null(fixture.Cache.TryLeaseLastGood(playlistId));
    Assert.NotNull(fixture.Cache.TryGetOutput(source.ContentHash, new[] { "Sports" }));
  }

  private sealed class ManualTime : TimeProvider
  {
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
  }

  private sealed class LastGoodFixture : IDisposable
  {
    public LastGoodFixture(long maxDiskBytes, long maxSourceBytes = 1024)
    {
      Directory = Path.Combine(Path.GetTempPath(), $"strim-cache-test-{Guid.NewGuid():N}");
      Cache = new PlaylistFileCache(
        Options.Create(new PlaylistCacheOptions
        {
          Directory = Directory,
          MaxSourceBytes = maxSourceBytes,
          MaxDiskBytes = maxDiskBytes,
        }),
        NullLogger<PlaylistFileCache>.Instance,
        PlaylistFileCache.TryDelete,
        Time);
    }

    public string Directory { get; }
    public ManualTime Time { get; } = new();
    public PlaylistFileCache Cache { get; }

    public async Task<PlaylistSourceFile> StoreSourceAsync(string text)
    {
      var downloaded = await Cache.WriteRawTextAsync(text, CancellationToken.None);
      return Cache.StoreDownloaded("https://example.com/playlist.m3u", downloaded);
    }

    public PlaylistOutputFile StoreOutput(PlaylistSourceFile source, string disabledGroup, string content)
    {
      var temporaryPath = Cache.CreateOutputTemporaryPath();
      File.WriteAllText(temporaryPath, content);
      return Cache.StoreOutput(
        source,
        new[] { disabledGroup },
        temporaryPath,
        new PlaylistGenerationResult(1, 1, new FileInfo(temporaryPath).Length));
    }

    public string LastGoodPath(Guid playlistId)
    {
      using var lease = Cache.TryLeaseLastGood(playlistId);
      return lease!.FilePath;
    }

    public void Dispose()
    {
      if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }
  }
}
