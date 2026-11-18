using System.Runtime.CompilerServices;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class StreamingVariantFinderTests
{
  public static TheoryData<int, int> Generated => new() { { 1, 1 }, { 7, 250 }, { 42, 2_000 }, { 2026, 10_000 } };

  [Theory]
  [MemberData(nameof(Generated))]
  public async Task Find_EqualsVariantFinder_ForGeneratedPlaylists(int seed, int count)
  {
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed, count);
    var entries = await PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, long.MaxValue, CancellationToken.None);

    var result = await StreamingVariantFinder.FindAsync(
      token => PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, token), CancellationToken.None);

    AssertEqualsFind(entries, result);
    if (count >= 250) Assert.NotEmpty(result.Groups);
  }

  // The names of the cases in VariantFinderTests and VariantFinderAdversarialTests.
  public static TheoryData<string[]> NameCases
  {
    get
    {
      var data = new TheoryData<string[]>
      {
        new[] { "Sport 1 HD", "News", "Sport 1 FHD", "Sport 1 4K" },
        new[] { "Sport 1 (HD)", "Sport 1 [FHD]", "Sport 1 | HEVC", "  Sport   1 - UHD ", "Sport 1 HD (backup)" },
        new[] { "Sport (HD) 1", "Sport [FHD] 1", "Sport 1" },
        new[] { "Sport HD", "Sport -", "Sport" },
        new[] { "Raw Power", "Power" },
        new[] { "HDTV Classics", "TV Classics", "Shadow", "Shadow", "4Kids", "Kids", "Rawhide", "hide", "Backup Plan Movie", "Plan Movie" },
        new[] { "Sport 1 HD", "Sport 10 HD" },
        new[] { "News", "News" },
        new[] { "Sport 1 HD", "Sport 1", "Sport 1 4K", "Sport 1 SD" },
        new[] { "Sport 1 HD", "Sport 1", "Sport 0 HD", "Sport 2", "Sport 2 HD", "Sport 0" },
        new[] { "Sport -", "Sport |", "HD", "(HD)", "HD", "Sport1HDPlus", "Sport HDPlus 1", "Sport (HDPlus) 1" },
        new[] { "Sport ſD", "Sport", "Sport hd", "Sport h.265", "", "" },
      };
      foreach (var name in new[] { "Sport 1 sd", "Sport 1 Hd", "Sport 1 fHD", "Sport 1 4k", "Sport 1 uhd", "Sport 1 HeVc", "Sport 1 h265", "Sport 1 h.265", "Sport 1 BACKUP", "Sport 1 Raw" })
      {
        data.Add(new[] { "Sport 1", name });
      }
      foreach (var name in new[] { "HD Sport 1", "(HD) Sport 1", "[FHD] Sport 1", "Sport HD 1", "Sport (hd) 1", "Sport [4k] 1", "Sport UHD 1 raw", "Sport (H.265) 1 [backup]", "Sport HEVC FHD 1" })
      {
        data.Add(new[] { "Sport 1", name });
      }
      data.Add(new[] { "Sport", "Sport(HD)", "Sport[FHD]" });
      // The adversarial shapes: one short name followed by many long ones.
      static string Repeat(string part, int count) => string.Concat(Enumerable.Repeat(part, count));
      const int Tokens = 2_000;
      data.Add(new[] { "Sport 1 HD" }.Concat(Enumerable.Repeat("Sport 1" + Repeat(" HD", Tokens), 20)).ToArray());
      data.Add(new[] { " Sport 1 HD" }.Concat(Enumerable.Repeat(Repeat(" ", Tokens) + "Sport 1" + Repeat(" HD", Tokens), 20)).ToArray());
      data.Add(new[] { "Sport 1 HD x" }.Concat(Enumerable.Repeat("Sport 1" + Repeat(" HD", Tokens) + " x", 20)).ToArray());
      data.Add(new[] { "Sport 1 (HD)-" }.Concat(Enumerable.Repeat("Sport 1" + Repeat(" (HD)", Tokens / 2) + Repeat("-", Tokens), 20)).ToArray());
      return data;
    }
  }

  [Theory]
  [MemberData(nameof(NameCases))]
  public async Task Find_EqualsVariantFinder_ForTheVariantFinderCases(string[] names)
  {
    var entries = Entries(names);

    AssertEqualsFind(entries, await StreamingVariantFinder.FindAsync(new CountingSource(entries).Read, CancellationToken.None));
    AssertEqualsFind(entries, await StreamingVariantFinder.FindAsync(new CountingSource(entries).Read, CancellationToken.None, _ => 7));
  }

  [Theory]
  [MemberData(nameof(Generated))]
  public async Task Find_WithAFingerprintThatAlwaysCollides_EqualsVariantFinder(int seed, int count)
  {
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed, count);
    var entries = await PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, long.MaxValue, CancellationToken.None);
    var calls = 0;

    var result = await StreamingVariantFinder.FindAsync(
      token => PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, token),
      CancellationToken.None,
      _ =>
      {
        calls++;
        return 42;
      });

    Assert.Equal(count, calls);
    AssertEqualsFind(entries, result);
  }

  [Fact]
  public async Task Find_ReadsTheSourceAFixedNumberOfTimes()
  {
    Assert.Equal(2, StreamingVariantFinder.PassCount);
    var entries = Entries(Enumerable.Range(0, 500).Select(i => i % 3 == 0 ? $"Sport {i / 9} HD" : $"Sport {i / 9}").ToArray());
    var source = new CountingSource(entries);

    await StreamingVariantFinder.FindAsync(source.Read, CancellationToken.None);

    Assert.Equal(StreamingVariantFinder.PassCount, source.Passes);
    Assert.Equal(StreamingVariantFinder.PassCount * entries.Count, source.EntriesRead);
  }

  [Fact]
  public async Task Find_OnALargeList_HoldsLessThanTheEstimatedEntryBytes()
  {
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed: 300, entryCount: 300_000);
    long estimated = 0;
    await foreach (var entry in PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, CancellationToken.None))
    {
      estimated += PlaylistProcessor.EstimatedEntryBytes(entry);
    }

    var result = await StreamingVariantFinder.FindAsync(
      token => PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, token), CancellationToken.None);

    Assert.True(result.EntryCount > 100_000, $"Only {result.EntryCount} entries are in variant groups.");
    Assert.True(result.HeldBytes > 0);
    Assert.True(result.HeldBytes < estimated, $"Held {result.HeldBytes} bytes against {estimated} estimated.");
  }

  [Fact]
  public async Task Find_AddsTheHeldBytesToTheCounterWhileItReads()
  {
    var entries = Entries(Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? $"Sport {i} HD" : $"Sport {i - 1}").ToArray());
    var counter = new HeldBytesCounter();
    var seen = new List<long>();
    counter.Added = seen.Add;

    var result = await StreamingVariantFinder.FindAsync(new CountingSource(entries).Read, CancellationToken.None, counter: counter);

    Assert.Equal(result.HeldBytes, counter.Bytes);
    Assert.True(seen.Count > entries.Count, $"Only {seen.Count} additions.");
    Assert.Equal(seen.Order(), seen);
  }

  [Theory]
  [InlineData(3)]
  [InlineData(13)]
  public async Task Find_CancelledBetweenEntries_ThrowsAndStopsReading(int cancelAt)
  {
    // 10 entries per pass: 3 cancels in the first pass, 13 in the second.
    var entries = Entries(Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? $"Sport {i} HD" : $"Sport {i - 1}").ToArray());
    using var cancel = new CancellationTokenSource();
    var source = new CountingSource(entries, cancelAt, cancel);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StreamingVariantFinder.FindAsync(source.Read, cancel.Token));

    Assert.Equal(cancelAt + 1, source.EntriesRead);
  }

  private static void AssertEqualsFind(List<PlaylistEntry> entries, VariantIndexGroups result)
  {
    var expected = VariantFinder.Find(entries);
    Assert.Equal(
      expected.Select(g => $"{g.BaseName}: {string.Join(",", g.Entries.Select(v => v.Entry.Index))}"),
      result.Groups.Select(g => $"{g.BaseName}: {string.Join(",", g.Members)}"));
    Assert.Equal(expected.Count, result.GroupCount);
    Assert.Equal(expected.Sum(g => g.Entries.Count), result.EntryCount);
  }

  private static List<PlaylistEntry> Entries(string[] names) =>
    names.Select((name, i) => new PlaylistEntry(i, name, "A", null, $"http://example.invalid/live/{i}.ts")).ToList();

  /// <summary>Yields the entries on each read, counts passes and entries, and cancels once entry <c>cancelAt</c> is read.</summary>
  private sealed class CountingSource(List<PlaylistEntry> entries, int cancelAt = -1, CancellationTokenSource? cancel = null)
  {
    public int Passes { get; private set; }

    public int EntriesRead { get; private set; }

    public async IAsyncEnumerable<PlaylistEntry> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
      Passes++;
      foreach (var entry in entries)
      {
        await Task.Yield();
        if (EntriesRead++ == cancelAt) cancel!.Cancel();
        yield return entry;
      }
    }
  }

  private sealed class TempFile : IDisposable
  {
    public string Path { get; } = System.IO.Path.GetTempFileName();

    public void Dispose() => File.Delete(Path);
  }
}
