using System.Runtime.CompilerServices;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class StreamingDuplicateFinderTests
{
  /// <summary>An in-memory source that counts how often it is read and how many entries it yields.</summary>
  private sealed class CountingSource(IReadOnlyList<PlaylistEntry> entries)
  {
    public int Passes { get; private set; }
    public long EntriesRead { get; private set; }
    public Action<long>? OnEntry { get; init; }

    public async IAsyncEnumerable<PlaylistEntry> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
      Passes++;
      await Task.Yield();
      foreach (var entry in entries)
      {
        EntriesRead++;
        yield return entry;
        OnEntry?.Invoke(EntriesRead);
      }
    }
  }

  private static void AssertSameAsOracle(IReadOnlyList<PlaylistEntry> entries, DuplicateIndexSets actual)
  {
    var expected = DuplicateFinder.Find(entries);
    Assert.Equal(
      expected.Select(set => set.Entries.Select(e => e.Index).ToArray()).ToArray(),
      actual.Sets.Select(set => set.ToArray()).ToArray());
    Assert.Equal(expected.Count, actual.SetCount);
    Assert.Equal(expected.Sum(set => set.Entries.Count), actual.EntryCount);
  }

  private static List<PlaylistEntry> Generated(int seed, int count) =>
    PlaylistEntryFixture.Read(GeneratedPlaylist.Text(seed, count));

  /// <summary>
  /// A non-negative id names a <see cref="DuplicateFinderTests"/> case, a negative one the seed of a generated
  /// playlist. Each runs with the real fingerprint and with one that collides for every key.
  /// </summary>
  public static IEnumerable<object[]> Cases() =>
    Enumerable.Range(0, DuplicateFinderTests.AllCases.Length).Concat(new[] { -1, -7, -42, -2026 })
      .SelectMany(id => new[] { new object[] { id, false }, new object[] { id, true } });

  [Theory]
  [MemberData(nameof(Cases))]
  public async Task Find_EqualsDuplicateFinder_EvenWhenEveryFingerprintCollides(int id, bool collide)
  {
    var entries = id >= 0 ? PlaylistEntryFixture.Read(DuplicateFinderTests.AllCases[id]) : Generated(-id, 2_000);

    var result = await StreamingDuplicateFinder.FindAsync(
      new CountingSource(entries).Read, CancellationToken.None, fingerprint: collide ? _ => 7UL : null);

    AssertSameAsOracle(entries, result);
  }

  [Fact]
  public async Task Find_JoinsTransitively_TvgIdThenAddress()
  {
    var entries = PlaylistEntryFixture.Read("""
      #EXTM3U
      #EXTINF:-1 tvg-id="a.de" group-title="A",A
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="a.de" group-title="B",B
      http://example.invalid/live/2.ts
      #EXTINF:-1 group-title="X",Other
      http://example.invalid/live/3.ts
      #EXTINF:-1 group-title="C",C
      http://example.invalid/live/2.ts
      """);

    var result = await StreamingDuplicateFinder.FindAsync(new CountingSource(entries).Read, CancellationToken.None);

    var set = Assert.Single(result.Sets);
    Assert.Equal(new[] { 0, 1, 3 }, set);
    Assert.Equal(3, result.EntryCount);
  }

  [Theory]
  [InlineData(10)]
  [InlineData(5_000)]
  public async Task Find_ReadsTheSourceAFixedNumberOfTimes(int count)
  {
    var entries = Generated(3, count);
    var source = new CountingSource(entries);

    await StreamingDuplicateFinder.FindAsync(source.Read, CancellationToken.None);

    Assert.Equal(2, StreamingDuplicateFinder.PassCount);
    Assert.Equal(StreamingDuplicateFinder.PassCount, source.Passes);
    Assert.Equal((long)StreamingDuplicateFinder.PassCount * entries.Count, source.EntriesRead);
  }

  [Fact]
  public async Task Cancellation_BetweenEntries_StopsReading()
  {
    var entries = Generated(9, 1_000);
    using var cancellation = new CancellationTokenSource();
    var source = new CountingSource(entries) { OnEntry = read => { if (read == 100) cancellation.Cancel(); } };

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
      () => StreamingDuplicateFinder.FindAsync(source.Read, cancellation.Token));

    Assert.True(source.EntriesRead < entries.Count, $"{source.EntriesRead} entries read");
  }
}
