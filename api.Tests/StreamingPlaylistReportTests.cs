using System.Runtime.CompilerServices;
using System.Text.Json;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class StreamingPlaylistReportTests
{
  public static TheoryData<int, int, int> Generated => new()
  {
    { 1, 1, PlaylistReport.MaxListedSetsPerKind },
    { 7, 250, PlaylistReport.MaxListedSetsPerKind },
    { 42, 2_000, PlaylistReport.MaxListedSetsPerKind },
    { 2026, 10_000, PlaylistReport.MaxListedSetsPerKind },
    { 7, 250, 3 },
    { 42, 2_000, 3 },
    { 2026, 10_000, 3 },
  };

  [Theory]
  [MemberData(nameof(Generated))]
  public async Task BuildStreaming_EqualsBuild_ForGeneratedPlaylists(int seed, int count, int maxListed)
  {
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed, count);
    var entries = await PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, long.MaxValue, CancellationToken.None);
    var expected = PlaylistReport.Build(entries, maxListed);

    var actual = await PlaylistReport.BuildStreamingAsync(
      token => PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, token), long.MaxValue, maxListed, CancellationToken.None);

    Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    if (maxListed == 3 && count >= 250) Assert.True(actual.Truncated);
  }

  [Fact]
  public async Task BuildStreaming_KeepsNoAddress()
  {
    var entries = PlaylistEntryFixture.Read(GeneratedPlaylist.Text(42, 2_000));

    var report = await PlaylistReport.BuildStreamingAsync(new CountingSource(entries).Read, long.MaxValue, 500, CancellationToken.None);

    var json = JsonSerializer.Serialize(report);
    Assert.NotEmpty(report.DuplicateSets);
    Assert.DoesNotContain("example.invalid", json);
    Assert.DoesNotContain("/live/", json);
  }

  [Fact]
  public async Task BuildStreaming_ReadsAFixedNumberOfPassesAndCountsWithinTheBudget()
  {
    Assert.Equal(5, PlaylistReport.StreamingPassCount);
    var entries = PlaylistEntryFixture.Read(GeneratedPlaylist.Text(7, 2_000));
    var source = new CountingSource(entries);
    var counter = new HeldBytesCounter();

    await PlaylistReport.BuildStreamingAsync(source.Read, long.MaxValue, 500, CancellationToken.None, counter);

    Assert.Equal(PlaylistReport.StreamingPassCount, source.Passes);
    Assert.Equal(PlaylistReport.StreamingPassCount * entries.Count, source.EntriesRead);
    Assert.True(counter.Bytes > 0);

    // The exact sum it held fits; one byte less does not.
    var exact = new HeldBytesCounter();
    await PlaylistReport.BuildStreamingAsync(new CountingSource(entries).Read, counter.Bytes, 500, CancellationToken.None, exact);
    Assert.Equal(counter.Bytes, exact.Bytes);
    var error = await Assert.ThrowsAsync<PlaylistReportTooLargeException>(() =>
      PlaylistReport.BuildStreamingAsync(new CountingSource(entries).Read, counter.Bytes - 1, 500, CancellationToken.None));
    Assert.Equal("This playlist is too large for a duplicates and variants report.", error.Message);
  }

  [Fact]
  public async Task BuildStreaming_BelowWhatTheFindersHold_ThrowsTooLarge()
  {
    var entries = PlaylistEntryFixture.Read(GeneratedPlaylist.Text(42, 2_000));

    var error = await Assert.ThrowsAsync<PlaylistReportTooLargeException>(() =>
      PlaylistReport.BuildStreamingAsync(new CountingSource(entries).Read, 1_000, 500, CancellationToken.None));

    Assert.Equal("This playlist is too large for a duplicates and variants report.", error.Message);
  }

  [Fact]
  public async Task BuildStreaming_FarBelowTheDuplicateFinder_RefusesDuringItsFirstPass()
  {
    var entries = PlaylistEntryFixture.Read(GeneratedPlaylist.Text(42, 2_000));
    var source = new CountingSource(entries);
    var counter = new HeldBytesCounter();
    const long Budget = 10_000;

    var error = await Assert.ThrowsAsync<PlaylistReportTooLargeException>(() =>
      PlaylistReport.BuildStreamingAsync(source.Read, Budget, 500, CancellationToken.None, counter));

    Assert.Equal("This playlist is too large for a duplicates and variants report.", error.Message);
    Assert.Equal(1, source.Passes);
    Assert.True(source.EntriesRead < entries.Count, $"Read {source.EntriesRead} of {entries.Count} entries.");
    AssertRefusedWithinOneEntry(counter, Budget, entries);
  }

  [Fact]
  public async Task BuildStreaming_WhenOnlyTheVariantFinderPassesTheBudget_RefusesWhileItReads()
  {
    var entries = PlaylistEntryFixture.Read(GeneratedPlaylist.Text(42, 2_000));
    var duplicateBytes = new HeldBytesCounter();
    await StreamingDuplicateFinder.FindAsync(new CountingSource(entries).Read, CancellationToken.None, counter: duplicateBytes);
    var variants = await StreamingVariantFinder.FindAsync(new CountingSource(entries).Read, CancellationToken.None);
    var budget = duplicateBytes.Bytes + variants.HeldBytes / 2;
    var source = new CountingSource(entries);
    var counter = new HeldBytesCounter();

    await Assert.ThrowsAsync<PlaylistReportTooLargeException>(() =>
      PlaylistReport.BuildStreamingAsync(source.Read, budget, 500, CancellationToken.None, counter));

    // Both duplicate passes completed; the variant finder's two passes did not.
    var finderPasses = StreamingDuplicateFinder.PassCount + StreamingVariantFinder.PassCount;
    Assert.InRange(source.EntriesRead, StreamingDuplicateFinder.PassCount * entries.Count + 1, finderPasses * entries.Count - 1);
    AssertRefusedWithinOneEntry(counter, budget, entries);
  }

  private static void AssertRefusedWithinOneEntry(HeldBytesCounter counter, long budget, List<PlaylistEntry> entries)
  {
    var oneEntry = entries.Max(PlaylistProcessor.EstimatedEntryBytes);
    Assert.InRange(counter.Bytes, budget + 1, budget + oneEntry);
  }

  [Fact]
  public async Task BuildStreaming_ReportsALargeListUnderABudgetTheInMemoryPathRefuses()
  {
    const int Count = 300_000;
    using var file = new TempFile();
    GeneratedPlaylist.WriteFile(file.Path, seed: 300, entryCount: Count);
    long estimated = 0;
    await foreach (var entry in PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, CancellationToken.None))
    {
      estimated += PlaylistProcessor.EstimatedEntryBytes(entry);
    }
    var budget = estimated / 2;
    await Assert.ThrowsAsync<PlaylistReportTooLargeException>(() =>
      PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, budget, CancellationToken.None));

    var passes = 0;
    long entriesRead = 0;
    async IAsyncEnumerable<PlaylistEntry> Source([EnumeratorCancellation] CancellationToken token)
    {
      passes++;
      await foreach (var entry in PlaylistProcessor.StreamEntriesFromFileAsync(file.Path, token))
      {
        entriesRead++;
        yield return entry;
      }
    }
    var counter = new HeldBytesCounter();
    var report = await PlaylistReport.BuildStreamingAsync(Source, budget, PlaylistReport.MaxListedSetsPerKind, CancellationToken.None, counter);

    // The counts come from the in-memory report on the same list, not from the streaming finders the builder uses.
    var expected = PlaylistReport.Build(
      await PlaylistProcessor.ReadEntriesFromFileAsync(file.Path, long.MaxValue, CancellationToken.None));
    Assert.Equal(Count, report.TotalEntries);
    Assert.Equal(expected.DuplicateSetCount, report.DuplicateSetCount);
    Assert.Equal(expected.DuplicateEntryCount, report.DuplicateEntryCount);
    Assert.Equal(expected.VariantGroupCount, report.VariantGroupCount);
    Assert.Equal(expected.VariantEntryCount, report.VariantEntryCount);
    Assert.True(expected.DuplicateSetCount > 0 && expected.VariantGroupCount > 0);
    Assert.True(report.Truncated);
    Assert.Equal(PlaylistReport.MaxListedSetsPerKind, report.DuplicateSets.Count);
    Assert.Equal(PlaylistReport.StreamingPassCount, passes);
    Assert.Equal((long)PlaylistReport.StreamingPassCount * Count, entriesRead);
    Assert.InRange(counter.Bytes, 1, budget);
  }

  [Theory]
  [InlineData(3)]
  [InlineData(13)]
  [InlineData(23)]
  [InlineData(33)]
  [InlineData(43)]
  public async Task BuildStreaming_CancelledBetweenEntries_ThrowsAndStopsReading(int cancelAt)
  {
    // 10 entries per pass, so each case cancels in a different one of the five passes.
    var entries = Enumerable.Range(0, 10)
      .Select(i => new PlaylistEntry(i, i % 2 == 0 ? $"Sport {i} HD" : $"Sport {i - 1}", "A", $"id{i / 3}", $"http://example.invalid/live/{i}.ts"))
      .ToList();
    using var cancel = new CancellationTokenSource();
    var source = new CountingSource(entries, cancelAt, cancel);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      PlaylistReport.BuildStreamingAsync(source.Read, long.MaxValue, 500, cancel.Token));

    Assert.Equal(cancelAt + 1, source.EntriesRead);
  }

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
