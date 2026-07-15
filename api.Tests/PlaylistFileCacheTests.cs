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
}
