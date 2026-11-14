using Api.Services;
using Xunit;

namespace Api.Tests;

public class DuplicateFinderTests
{
  private static IReadOnlyList<DuplicateSet> Find(string playlist) =>
    DuplicateFinder.Find(PlaylistProcessor.ReadEntries(playlist));

  [Fact]
  public void SameTvgId_FormsOneSet()
  {
    var sets = Find("""
      #EXTM3U
      #EXTINF:-1 tvg-id="a.de" group-title="DE",Alpha
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="b.de" group-title="DE",Beta
      http://example.invalid/live/2.ts
      #EXTINF:-1 tvg-id="a.de" group-title="DE HD",Alpha HD
      http://example.invalid/live/3.ts
      """);

    var set = Assert.Single(sets);
    Assert.Equal(new[] { 0, 2 }, set.Entries.Select(e => e.Index));
    Assert.Equal(new[] { "Alpha", "Alpha HD" }, set.Entries.Select(e => e.Name));
    Assert.Equal(new[] { "DE", "DE HD" }, set.Groups);
  }

  [Fact]
  public void SameAddress_FormsOneSet()
  {
    var sets = Find("""
      #EXTM3U
      #EXTINF:-1 group-title="News",News One
      http://example.invalid/live/7.ts
      #EXTINF:-1 group-title="News",Other
      http://example.invalid/live/8.ts
      #EXTINF:-1 group-title="News",News 1
      http://example.invalid/live/7.ts
      """);

    var set = Assert.Single(sets);
    Assert.Equal(new[] { "News One", "News 1" }, set.Entries.Select(e => e.Name));
    Assert.Equal(new[] { "News" }, set.Groups);
  }

  [Fact]
  public void EmptyOrMissingTvgId_IsNotADuplicate()
  {
    var sets = Find("""
      #EXTM3U
      #EXTINF:-1 tvg-id="" group-title="A",One
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="" group-title="A",Two
      http://example.invalid/live/2.ts
      #EXTINF:-1 tvg-id="  " group-title="A",Three
      http://example.invalid/live/3.ts
      #EXTINF:-1 group-title="A",Four
      http://example.invalid/live/4.ts
      #EXTINF:-1 group-title="A",Five
      http://example.invalid/live/5.ts
      """);

    Assert.Empty(sets);
  }

  /// <summary>
  /// Rule (until the owner decides otherwise): whitespace around a tvg-id is trimmed, the comparison
  /// is case-sensitive. "a.de" and "a.de " match, "A.de" does not.
  /// </summary>
  [Fact]
  public void TvgIdComparison_IsExact()
  {
    var sets = Find("""
      #EXTM3U
      #EXTINF:-1 tvg-id="a.de" group-title="A",One
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="a.de " group-title="A",Two
      http://example.invalid/live/2.ts
      #EXTINF:-1 tvg-id="A.de" group-title="A",Three
      http://example.invalid/live/3.ts
      """);

    var set = Assert.Single(sets);
    Assert.Equal(new[] { "One", "Two" }, set.Entries.Select(e => e.Name));
  }

  [Fact]
  public void NearMissAddresses_AreNotDuplicates()
  {
    var sets = Find("""
      #EXTM3U
      #EXTINF:-1 group-title="A",One
      http://example.invalid/live/user/pass/1001.ts
      #EXTINF:-1 group-title="A",Two
      http://example.invalid/live/user/pass/10010.ts
      #EXTINF:-1 group-title="A",Three
      http://example.invalid/play?id=5&q=hd
      #EXTINF:-1 group-title="A",Four
      http://example.invalid/play?id=5&q=sd
      """);

    Assert.Empty(sets);
  }

  [Fact]
  public void AddressAfterOptionLines_IsUsed()
  {
    var sets = Find("""
      #EXTM3U
      #EXTINF:-1 group-title="A",One
      #EXTVLCOPT:http-user-agent=X
      #EXTVLCOPT:http-referrer=http://example.invalid/
      http://example.invalid/live/9.ts
      #EXTINF:-1 group-title="B",Two
      http://example.invalid/live/9.ts
      """);

    var set = Assert.Single(sets);
    Assert.Equal(new[] { "One", "Two" }, set.Entries.Select(e => e.Name));
    Assert.Equal("http://example.invalid/live/9.ts", set.Entries[0].Address);
    Assert.Equal(new[] { "A", "B" }, set.Groups);
  }

  [Fact]
  public void EntryInBothRules_AppearsInOneSetOnly()
  {
    var sets = Find("""
      #EXTM3U
      #EXTINF:-1 tvg-id="a.de" group-title="A",One
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="a.de" group-title="B",Two
      http://example.invalid/live/1.ts
      #EXTINF:-1 group-title="C",Three
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="a.de" group-title="D",Four
      http://example.invalid/live/4.ts
      """);

    var set = Assert.Single(sets);
    Assert.Equal(new[] { 0, 1, 2, 3 }, set.Entries.Select(e => e.Index));
    Assert.Equal(new[] { "A", "B", "C", "D" }, set.Groups);
  }

  [Fact]
  public void UniqueEntries_YieldEmptyResult()
  {
    var entries = PlaylistProcessor.ReadEntries("""
      #EXTM3U
      #EXTINF:-1 tvg-id="a.de" group-title="A",One
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="b.de" group-title="A",Two
      http://example.invalid/live/2.ts
      """);
    var before = entries.ToList();

    Assert.Empty(DuplicateFinder.Find(entries));
    Assert.Equal(before, entries);
  }

  [Fact]
  public void Find_DoesNotModifyInput()
  {
    var entries = PlaylistProcessor.ReadEntries("""
      #EXTM3U
      #EXTINF:-1 tvg-id="a.de " group-title="A",One
      http://example.invalid/live/1.ts
      #EXTINF:-1 tvg-id="a.de" group-title="B",Two
      http://example.invalid/live/1.ts
      """);
    var before = entries.ToList();

    Assert.Single(DuplicateFinder.Find(entries));
    Assert.Equal(before, entries);
    Assert.Equal("a.de ", entries[0].TvgId);
  }
}
