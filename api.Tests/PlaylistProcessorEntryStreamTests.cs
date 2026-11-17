using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistProcessorEntryStreamTests
{
  public static TheoryData<int, int> Generated => new() { { 1, 1 }, { 7, 250 }, { 42, 2_000 }, { 2026, 10_000 } };

  [Theory]
  [MemberData(nameof(Generated))]
  public async Task Stream_EqualsReadEntries_ForGeneratedPlaylists(int seed, int count)
  {
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed, count);

    var expected = await PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, long.MaxValue, CancellationToken.None);
    var streamed = await CollectAsync(file.Path);

    Assert.Equal(count, expected.Count);
    Assert.Equal(expected, streamed);
    Assert.Equal(Enumerable.Range(0, count), streamed.Select(e => e.Index));
  }

  public static TheoryData<string> Fixtures => new()
  {
    // Option lines from PlaylistProcessorEntryOptionLineTests.
    "#EXTM3U\n#EXTINF:-1 group-title=\"News\",News One\n#EXTVLCOPT:http-user-agent=X\nhttp://example.invalid/live/news.m3u8\n",
    "#EXTM3U\n#EXTINF:-1 group-title=\"News\",News One\n#KODIPROP:inputstream.adaptive.license_type=com.widevine.alpha\nhttp://example.invalid/live/news.mpd\n",
    "#EXTM3U\n#EXTINF:-1 group-title=\"News\",News One\n#EXTVLCOPT:http-user-agent=X\n#KODIPROP:inputstream.adaptive.manifest_type=mpd\n\n" +
      "#EXTVLCOPT:http-referrer=http://example.invalid/\nhttp://example.invalid/live/news.mpd\n" +
      "#EXTINF:-1 tvg-id=\"two\" group-title=\"Sport\",Sport Two\nhttp://example.invalid/live/sport.m3u8\n",
    // #EXTGRP from PlaylistProcessorExtGrpTests.
    "#EXTM3U\n#EXTINF:-1 tvg-name=\"News One\",News One\n#EXTGRP:News\nhttp://example.invalid/live/news.m3u8\n" +
      "#EXTINF:-1 tvg-name=\"Plain\",Plain\nhttp://example.invalid/live/plain.m3u8\n",
    "#EXTM3U\n#EXTINF:-1,News One\n#extgrp:News\n#EXTGRP:Other\nhttp://example.invalid/live/news.m3u8\n",
    "#EXTM3U\n#EXTINF:-1 tvg-name=\"News One\",News One\n#EXTVLCOPT:http-user-agent=X\n#EXTGRP: News\n" +
      "#KODIPROP:inputstream.adaptive.manifest_type=mpd\nhttp://example.invalid/live/news.mpd\n",
    // An entry without an address, followed by the next one.
    "#EXTM3U\n#EXTINF:-1 group-title=\"A\",First\n#EXTINF:-1 group-title=\"B\",Second\nhttp://example.invalid/live/b.ts\n",
  };

  [Theory]
  [MemberData(nameof(Fixtures))]
  public async Task Stream_EqualsReadEntries_ForOptionLineAndExtGrpFixtures(string playlist)
  {
    using var file = new TempFile();
    await File.WriteAllTextAsync(file.Path, playlist);

    var expected = await PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, long.MaxValue, CancellationToken.None);

    Assert.NotEmpty(expected);
    Assert.Equal(expected, await CollectAsync(file.Path));
  }

  [Fact]
  public async Task Stream_ReadTwice_GivesIdenticalSequences()
  {
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed: 5, entryCount: 3_000);

    Assert.Equal(await CollectAsync(file.Path), await CollectAsync(file.Path));
  }

  [Fact]
  public async Task Stream_LineOverLimit_FailsLikeReadEntries()
  {
    using var file = new TempFile();
    await File.WriteAllTextAsync(file.Path,
      "#EXTM3U\n#EXTINF:-1,Short\nhttp://example.invalid/a.ts\n#EXTINF:-1," + new string('x', 200) + "\nhttp://example.invalid/b.ts\n");

    await Assert.ThrowsAsync<PlaylistLineTooLongException>(() =>
      PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, long.MaxValue, CancellationToken.None, maxLineLengthChars: 64));
    await Assert.ThrowsAsync<PlaylistLineTooLongException>(() => CollectAsync(file.Path, maxLineLengthChars: 64));
  }

  [Theory]
  [InlineData(1)]
  [InlineData(17)]
  public async Task Stream_CancelledAfterNthEntry_StopsWithoutFurtherEntry(int n)
  {
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed: 3, entryCount: 100);
    using var cts = new CancellationTokenSource();
    var yielded = new List<PlaylistEntry>();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
    {
      await foreach (var entry in PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, cts.Token))
      {
        yielded.Add(entry);
        if (yielded.Count == n) cts.Cancel();
      }
    });

    Assert.Equal(n, yielded.Count);
    Assert.Equal(n - 1, yielded[^1].Index);
  }

  [Fact]
  public async Task Stream_HasNoMemoryBudgetAndKeepsNoEntries()
  {
    const int count = 300_000;
    const long budget = 1024 * 1024;
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed: 11, entryCount: count);

    await Assert.ThrowsAsync<PlaylistReportTooLargeException>(() =>
      PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, budget, CancellationToken.None));

    var streamed = 0;
    WeakReference? first = null;
    var firstCollected = false;
    await foreach (var entry in PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, CancellationToken.None))
    {
      Assert.Equal(streamed, entry.Index);
      if (streamed == 0) first = new WeakReference(entry);
      if (streamed == count / 2)
      {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        firstCollected = !first!.IsAlive;
      }
      streamed++;
    }

    Assert.Equal(count, streamed);
    // Nothing behind the stream still holds the first entry halfway through, so no list is built.
    Assert.True(firstCollected);
  }

  [Fact]
  public void Generator_SameSeedAndCount_GivesByteIdenticalOutput()
  {
    using var a = new TempFile();
    using var b = new TempFile();
    GeneratedPlaylist.WriteFile(a.Path, seed: 9, entryCount: 5_000);
    GeneratedPlaylist.WriteFile(b.Path, seed: 9, entryCount: 5_000);

    Assert.Equal(File.ReadAllBytes(a.Path), File.ReadAllBytes(b.Path));
    Assert.NotEqual(GeneratedPlaylist.Text(9, 5_000), GeneratedPlaylist.Text(10, 5_000));
  }

  [Fact]
  public async Task Generator_CoversDuplicatesVariantsOptionLinesAndFakeAddresses()
  {
    var text = GeneratedPlaylist.Text(seed: 1, entryCount: 4_000);
    Assert.StartsWith("#EXTM3U url-tvg=\"http://example.invalid/", text);
    foreach (var marker in new[] { "#EXTVLCOPT:", "#KODIPROP:", "#EXTGRP:" })
    {
      Assert.Contains(marker, text);
    }
    foreach (var variant in new[] { " SD", " HD", " FHD", " 4K", " HEVC", " H.265", " [HD]", " (FHD)", " Backup", " RAW" })
    {
      Assert.Contains(variant + "\n", text);
    }
    Assert.All(
      text.Split('\n').Where(line => line.Contains("://")),
      line => Assert.Contains("://example.invalid/", line));

    using var file = new TempFile();
    await File.WriteAllTextAsync(file.Path, text);
    var entries = await CollectAsync(file.Path);
    Assert.Contains(entries, e => e.TvgId is null);
    Assert.Contains(entries.Where(e => e.TvgId is not null).GroupBy(e => e.TvgId), g => g.Count() > 1);
    Assert.Contains(entries.GroupBy(e => e.Address), g => g.Count() > 1);
  }

  private static async Task<List<PlaylistEntry>> CollectAsync(string path, int maxLineLengthChars = 64 * 1024)
  {
    var entries = new List<PlaylistEntry>();
    await foreach (var entry in PlaylistProcessor.StreamEntriesFromFileAsync(path, CancellationToken.None, maxLineLengthChars))
    {
      entries.Add(entry);
    }
    return entries;
  }

  private sealed class TempFile : IDisposable
  {
    public string Path { get; } = System.IO.Path.GetTempFileName();

    public void Dispose() => File.Delete(Path);
  }
}
