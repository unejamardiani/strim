using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Api.Services;
using Xunit;

namespace Api.Tests;

[CollectionDefinition(nameof(SearchTiming), DisableParallelization = true)]
public class SearchTiming
{
}

// Runs alone so the timed search in Search_LargeFixture_StaysFastAndBounded does not compete with the rest of
// the suite for the processor.
[Collection(nameof(SearchTiming))]
public class PlaylistSearchIndexTests
{
  private const string Source = """
    #EXTM3U
    #EXTINF:-1 group-title="Germany",Das Erste
    https://example.com/live/u/p/1.ts
    #EXTINF:-1 group-title="Germany",Amélie
    https://example.com/movie/u/p/2.mkv
    #EXTINF:-1 group-title="Germany",ZDF
    https://example.com/live/u/p/3.ts
    #EXTINF:-1 group-title="Germany",Dark S01E01
    https://example.com/series/u/p/4.mkv
    #EXTINF:-1 group-title="Germany",Amelie Returns
    https://example.com/movie/u/p/5.mkv
    #EXTINF:-1 group-title="News",News 24
    https://example.com/live/u/p/6.ts
    """;

  [Fact]
  public async Task Index_GroupMatchesGeneratedOutput()
  {
    var (index, written) = await AnalyzeAndGenerate(Source, Set());

    Assert.Equal(6, written.Count);
    foreach (var (name, groupTitle) in written)
    {
      Assert.Equal(groupTitle, Find(index, name).OutputGroup);
    }
    Assert.Equal("Germany · Movies", Find(index, "Amélie").OutputGroup);
    Assert.Equal("Germany · Series", Find(index, "Dark S01E01").OutputGroup);
    Assert.Equal("Germany · Live", Find(index, "ZDF").GroupKey);
    Assert.Equal("Germany · Movies", Find(index, "Amélie").GroupKey);
    Assert.Equal("News", Find(index, "News 24").GroupKey);
  }

  [Fact]
  public async Task Index_PositionsPerOutputGroup()
  {
    var (index, _) = await AnalyzeAndGenerate(Source, Set());

    var names = new[] { "Das Erste", "ZDF", "Amélie", "Amelie Returns", "Dark S01E01", "News 24" };
    Assert.Equal(new[] { 1, 2, 1, 2, 1, 1 }, names.Select(name => Find(index, name).Position));

    // A live entry titled "Germany · Movies" lands in the same output group as the split movies of "Germany".
    const string collision = """
      #EXTM3U
      #EXTINF:-1 group-title="Germany",Das Erste
      https://example.com/live/u/p/1.ts
      #EXTINF:-1 group-title="Germany",Amélie
      https://example.com/movie/u/p/2.mkv
      #EXTINF:-1 group-title="Germany · Movies",Kino Live
      https://example.com/live/u/p/3.ts
      """;
    var (merged, written) = await AnalyzeAndGenerate(collision, Set());
    Assert.Equal(new[] { "Amélie", "Kino Live" }, written.Where(w => w.GroupTitle == "Germany · Movies").Select(w => w.Name));
    Assert.Equal(("Germany · Movies", 1), (Find(merged, "Amélie").OutputGroup, Find(merged, "Amélie").Position));
    Assert.Equal(("Germany · Movies", 2), (Find(merged, "Kino Live").OutputGroup, Find(merged, "Kino Live").Position));
    Assert.Equal(1, Find(merged, "Das Erste").Position);
  }

  [Fact]
  public async Task Index_GroupMatchesGeneratedOutput_SpacesAndExtGrp()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1 group-title=" News ",News 24
      https://example.com/live/u/p/1.ts
      #EXTINF:-1 group-title="",Sport 1
      #EXTGRP:Sport
      https://example.com/live/u/p/2.ts
      #EXTINF:-1,Sport 2
      #EXTGRP:Sport
      https://example.com/live/u/p/3.ts
      #EXTINF:-1 group-title=" Mixed ",Mixed Live
      https://example.com/live/u/p/4.ts
      #EXTINF:-1 group-title="",Mixed Film
      #EXTGRP:Mixed
      https://example.com/movie/u/p/5.mkv
      """;
    var (index, written) = await AnalyzeAndGenerate(source, Set());

    Assert.Equal(5, written.Count);
    foreach (var (name, groupTitle) in written)
    {
      Assert.Equal(groupTitle, Find(index, name).OutputGroup);
    }
    Assert.Equal(" News ", Find(index, "News 24").OutputGroup);
    Assert.Equal(("", 1), (Find(index, "Sport 1").OutputGroup, Find(index, "Sport 1").Position));
    Assert.Equal(("Sport", 1), (Find(index, "Sport 2").OutputGroup, Find(index, "Sport 2").Position));
    Assert.Equal(" Mixed ", Find(index, "Mixed Live").OutputGroup);
    Assert.Equal("Mixed · Movies", Find(index, "Mixed Film").OutputGroup);
    Assert.Equal("Sport", Find(index, "Sport 1").GroupKey);
  }

  [Fact]
  public async Task Search_IsAccentInsensitive()
  {
    var (index, _) = await AnalyzeAndGenerate(Source, Set());

    Assert.Equal(new[] { "Amélie", "Amelie Returns" }, index.Search("amelie", Set(), 10).Matches.Select(m => m.Name));
    Assert.Equal("Amelie Returns", Assert.Single(index.Search("ÄMÉLIE-returns", Set(), 10).Matches).Name);
    Assert.Empty(index.Search("erste\tamelie", Set(), 10).Matches);
  }

  [Fact]
  public async Task Search_LimitsAndReportsMore()
  {
    var (index, _) = await AnalyzeAndGenerate(Source, Set());

    var limited = index.Search("e", Set(), 2);
    Assert.Equal(new[] { "Das Erste", "Amélie" }, limited.Matches.Select(m => m.Name));
    Assert.True(limited.HasMore);
    var exact = index.Search("amelie", Set(), 2);
    Assert.Equal(2, exact.Matches.Count);
    Assert.False(exact.HasMore);
  }

  [Theory]
  [InlineData("Germany · Movies")]
  [InlineData("germany")]
  [InlineData("News")]
  public async Task Search_ReportsEnabledState(string disabledGroup)
  {
    var disabled = Set(disabledGroup);
    var (index, written) = await AnalyzeAndGenerate(Source, disabled);

    // The generated file keeps exactly the entries the search reports as enabled.
    var kept = written.Select(w => w.Name).ToHashSet();
    var all = index.Search("e", disabled, 100).Matches.Append(Find(index, "ZDF", disabled)).ToList();
    Assert.Equal(6, all.Count);
    Assert.Contains(all, m => !m.Enabled);
    Assert.All(all, m => Assert.Equal(kept.Contains(m.Name), m.Enabled));
  }

  [Fact]
  public async Task Search_HideDisabled_SkipsOffGroupBeforeLimit()
  {
    var index = await Analyze(OffAndOnGroups());

    var result = index.Search("news", Set("Off"), 50, hideDisabled: true);

    Assert.Equal(new[] { "News On 1", "News On 2", "News On 3" }, result.Matches.Select(m => m.Name));
    Assert.All(result.Matches, m => Assert.True(m.Enabled));
    Assert.False(result.HasMore);
    Assert.Equal(60, result.HiddenCount);
  }

  [Fact]
  public async Task Search_HideOff_KeepsTodaysResult()
  {
    var index = await Analyze(OffAndOnGroups());

    var shown = index.Search("news", Set("Off"), 50);
    var explicitOff = index.Search("news", Set("Off"), 50, hideDisabled: false);

    Assert.Equal(50, shown.Matches.Count);
    Assert.True(shown.HasMore);
    Assert.All(shown.Matches, m => Assert.False(m.Enabled));
    Assert.Equal(Enumerable.Range(1, 50).Select(i => $"News Off {i}"), shown.Matches.Select(m => m.Name));
    Assert.Equal(0, shown.HiddenCount);
    Assert.Equal(shown.Matches, explicitOff.Matches);
    Assert.Equal((shown.HasMore, 0), (explicitOff.HasMore, explicitOff.HiddenCount));
  }

  [Theory]
  [InlineData("off")]
  [InlineData("OFF")]
  public async Task Search_HideDisabled_MatchesGroupKeysCaseInsensitively(string disabledGroup)
  {
    var index = await Analyze(OffAndOnGroups());

    var result = index.Search("news", Set(disabledGroup), 50, hideDisabled: true);

    Assert.Equal(3, result.Matches.Count);
    Assert.Equal(60, result.HiddenCount);
  }

  [Fact]
  public async Task Search_HideDisabled_CountsOffMatchesPastTheLimit()
  {
    var index = await Analyze(OnThenOffGroups());

    var result = index.Search("news", Set("Off"), 50, hideDisabled: true);

    Assert.Equal(Enumerable.Range(1, 50).Select(i => $"News On {i}"), result.Matches.Select(m => m.Name));
    Assert.True(result.HasMore);
    Assert.Equal(60, result.HiddenCount);
  }

  [Fact]
  public async Task Search_WithoutContentTypes_IsUnchanged()
  {
    var index = await Analyze(MixedTypes());

    foreach (var hide in new[] { false, true })
    {
      var today = index.Search("star", Set("Films Off"), 50, hide);
      foreach (var types in new[] { null, new HashSet<PlaylistContentType>() })
      {
        var result = index.Search("star", Set("Films Off"), 50, hide, types);
        Assert.Equal(today.Matches, result.Matches);
        Assert.Equal((today.HasMore, today.HiddenCount), (result.HasMore, result.HiddenCount));
      }
    }
    Assert.True(index.Search("star", Set(), 50).HasMore);
  }

  [Fact]
  public async Task Search_ContentTypes_FiltersBeforeLimit()
  {
    var index = await Analyze(MixedTypes());

    var result = index.Search("star", Set(), 3, contentTypes: Types(PlaylistContentType.Movies));

    Assert.Equal(new[] { "Star Film Off 1", "Star Film On 1", "Star Film On 2" }, result.Matches.Select(m => m.Name));
    Assert.All(result.Matches, m => Assert.Equal(PlaylistContentType.Movies, m.ContentType));
    Assert.False(result.HasMore);
    Assert.True(index.Search("star", Set(), 4, contentTypes: Types(PlaylistContentType.Movies)) is { HasMore: false, Matches.Count: 3 });
  }

  [Fact]
  public async Task Search_ContentTypes_CombineAsOr()
  {
    var index = await Analyze(MixedTypes());

    var result = index.Search("star", Set(), 50, contentTypes: Types(PlaylistContentType.Movies, PlaylistContentType.Series));

    Assert.Equal(
      new[] { "Star Film Off 1", "Star Film On 1", "Star Film On 2", "Star Show 1", "Star Show 2" },
      result.Matches.Select(m => m.Name).Order(StringComparer.Ordinal));
    Assert.DoesNotContain(result.Matches, m => m.ContentType == PlaylistContentType.Live);
    Assert.False(result.HasMore);
  }

  [Fact]
  public async Task Search_ContentTypes_HiddenCountOnlyChosenTypes()
  {
    var index = await Analyze(MixedTypes());
    var disabled = Set("Films Off", "Live");

    var films = index.Search("star", disabled, 50, hideDisabled: true, contentTypes: Types(PlaylistContentType.Movies));
    var all = index.Search("star", disabled, 50, hideDisabled: true);

    Assert.Equal(new[] { "Star Film On 1", "Star Film On 2" }, films.Matches.Select(m => m.Name));
    Assert.Equal(1, films.HiddenCount);
    Assert.Equal(61, all.HiddenCount);
  }

  [Fact]
  public async Task Search_ContentTypesAndLanguages_Intersect()
  {
    var index = await Analyze("""
      #EXTM3U
      #EXTINF:-1 group-title="Films On",DE: Star Film One
      https://example.com/movie/u/p/1.mkv
      #EXTINF:-1 group-title="Films On",FR: Star Film Two
      https://example.com/movie/u/p/2.mkv
      #EXTINF:-1 group-title="Films Off",DE: Star Film Three
      https://example.com/movie/u/p/3.mkv
      #EXTINF:-1 group-title="Shows",DE: Star Show S01E01
      https://example.com/series/u/p/4.mkv
      #EXTINF:-1 group-title="Live",DE: Star Live
      https://example.com/live/u/p/5.ts
      #EXTINF:-1 group-title="Films On",Star Film Plain
      https://example.com/movie/u/p/6.mkv
      """);
    var films = Types(PlaylistContentType.Movies);

    // Type, language and the toggle together: only German films of groups that are on; the German film of the off
    // group is counted as hidden, the French film, the series and the channel are not counted at all.
    var kept = index.Search("star", Set("Films Off"), 50, hideDisabled: true, contentTypes: films, languages: ["de"]);
    Assert.Equal(new[] { "DE: Star Film One" }, kept.Matches.Select(m => m.Name));
    Assert.Equal((1, 1), (kept.Total, kept.HiddenCount));

    // With the switched-off groups included, the off film joins and is marked off.
    var withOff = index.Search("star", Set("Films Off"), 50, hideDisabled: false, contentTypes: films, languages: ["de"]);
    Assert.Equal(new[] { ("DE: Star Film One", true), ("DE: Star Film Three", false) }, withOff.Matches.Select(m => (m.Name, m.Enabled)));
    Assert.Equal(0, withOff.HiddenCount);

    // Each filter alone keeps more than the combination.
    Assert.Equal(4, index.Search("star", Set("Films Off"), 50, contentTypes: films).Matches.Count);
    Assert.Equal(4, index.Search("star", Set("Films Off"), 50, languages: ["de"]).Matches.Count);
  }

  private static readonly string DarkKey = MarkKey.From("Dark", MarkKind.Series)!.Key;
  private static readonly string LostKey = MarkKey.From("Lost", MarkKind.Series)!.Key;
  private static readonly string HeatKey = MarkKey.From("Heat", MarkKind.Series)!.Key;

  private static string SeriesSource(params (string Group, string Name, string Kind)[] entries)
  {
    var source = new StringBuilder("#EXTM3U\n");
    var i = 0;
    foreach (var (group, name, kind) in entries)
      source.Append($"#EXTINF:-1 group-title=\"{group}\",{name}\nhttps://example.com/{kind}/u/p/{++i}.mkv\n");
    return source.ToString();
  }

  [Fact]
  public async Task FindSeries_OnePass()
  {
    var index = await Analyze(SeriesSource(
      ("Shows", "Dark S01E01", "series"), ("Shows", "Lost S01E01", "series"),
      ("Shows", "Dark S01E02", "series"), ("Shows", "Heat S02E03", "series")));
    var before = index.PassCount;

    var result = index.FindSeries(DarkKey, new HashSet<string> { LostKey, HeatKey }, Set(), hideDisabled: true);

    Assert.Equal(before + 1, index.PassCount);
    Assert.Equal([1, 2], result.Seasons[1]);
    Assert.Equal(2, result.Episodes);
    Assert.Equal("Lost S01E01", result.Similar[LostKey].Name);
    Assert.Equal("Heat S02E03", result.Similar[HeatKey].Name);
    Assert.Equal("Shows", result.Similar[HeatKey].OutputGroup);
  }

  [Fact]
  public async Task FindSeries_DistinctAcrossGroupsAndVariants()
  {
    var index = await Analyze(SeriesSource(
      ("Shows DE", "DE: Dark S01E01", "series"), ("Shows EN", "Dark S01E01 FHD", "series"),
      ("Shows DE", "Dark 1x02", "series"), ("Shows EN", "Dark Staffel 1 Folge 2", "series"),
      ("Shows DE", "Dark S02E10", "series"), ("Shows DE", "Dark S02E01", "series"),
      ("Shows DE", "Dark S02", "series"), ("Shows EN", "Dark S02", "series"),
      ("Shows DE", "Dark", "series"), ("Shows DE", "Dark Special", "series"), ("Shows DE", "Dark (2017) S03E01", "series")));

    var result = index.FindSeries(DarkKey, new HashSet<string>(), Set(), hideDisabled: true);

    Assert.Equal([1, 2], result.Seasons.Keys.Order());
    Assert.Equal([1, 2], result.Seasons[1]);
    Assert.Equal([1, 10], result.Seasons[2]);
    Assert.Equal(4, result.Episodes);
    // "Dark S02" twice and "Dark"; "Dark Special" and the 2017 entry have keys of their own.
    Assert.Equal(2, result.Unnumbered);
  }

  [Fact]
  public async Task FindSeries_SkipsAdultAndFilms()
  {
    var index = await Analyze(SeriesSource(
      ("Shows", "Dark S01E01", "series"), ("Films", "Dark S01E05", "movie"), ("Live", "Dark S01E06", "live"),
      ("XXX Shows", "Dark S01E07", "series"), ("XXX Shows", "Lost S01E01", "series"),
      ("Films", "Heat S01E01", "movie")));

    var result = index.FindSeries(DarkKey, new HashSet<string> { LostKey, HeatKey }, Set(), hideDisabled: false);

    Assert.Equal([1], Assert.Single(result.Seasons).Value);
    Assert.Equal(0, result.Unnumbered);
    Assert.Empty(result.Similar);
  }

  [Fact]
  public async Task FindSeries_HideDisabled()
  {
    var index = await Analyze(SeriesSource(
      ("Shows Old", "Dark S03E01", "series"), ("Shows Old", "Lost S01E01", "series"),
      ("Shows Old", "Heat S01E01", "series"), ("Shows", "Dark S01E01", "series"), ("Shows", "Lost S02E01", "series")));
    var similarKeys = new HashSet<string> { LostKey, HeatKey };

    var cleaned = index.FindSeries(DarkKey, similarKeys, Set("Shows Old"), hideDisabled: true);
    Assert.Equal([1], cleaned.Seasons.Keys);
    Assert.Equal(("Lost S02E01", true), (cleaned.Similar[LostKey].Name, cleaned.Similar[LostKey].Enabled));
    Assert.False(cleaned.Similar.ContainsKey(HeatKey));

    // The full list: the switched-off season joins, an enabled entry still wins, and an off-only series is marked off.
    var full = index.FindSeries(DarkKey, similarKeys, Set("Shows Old"), hideDisabled: false);
    Assert.Equal([1, 3], full.Seasons.Keys.Order());
    Assert.Equal("Lost S02E01", full.Similar[LostKey].Name);
    Assert.Equal(("Heat S01E01", false), (full.Similar[HeatKey].Name, full.Similar[HeatKey].Enabled));
  }

  // 60 live channels, then films in an off and an on group, then series, all matching "star".
  private static string MixedTypes()
  {
    var source = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 60; i++)
      source.Append($"#EXTINF:-1 group-title=\"Live\",Star Live {i}\nhttps://example.com/live/u/p/{i}.ts\n");
    source.Append("#EXTINF:-1 group-title=\"Films Off\",Star Film Off 1\nhttps://example.com/movie/u/p/101.mkv\n");
    for (var i = 1; i <= 2; i++)
      source.Append($"#EXTINF:-1 group-title=\"Films On\",Star Film On {i}\nhttps://example.com/movie/u/p/{110 + i}.mkv\n");
    for (var i = 1; i <= 2; i++)
      source.Append($"#EXTINF:-1 group-title=\"Shows\",Star Show {i}\nhttps://example.com/series/u/p/{120 + i}.mkv\n");
    return source.ToString();
  }

  private static HashSet<PlaylistContentType> Types(params PlaylistContentType[] types) => new(types);

  private static string OnThenOffGroups()
  {
    var source = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 51; i++)
      source.Append($"#EXTINF:-1 group-title=\"On\",News On {i}\nhttps://example.com/live/u/p/{i}.ts\n");
    for (var i = 1; i <= 60; i++)
      source.Append($"#EXTINF:-1 group-title=\"Off\",News Off {i}\nhttps://example.com/live/u/p/{100 + i}.ts\n");
    return source.ToString();
  }

  private static string OffAndOnGroups()
  {
    var source = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 60; i++)
      source.Append($"#EXTINF:-1 group-title=\"Off\",News Off {i}\nhttps://example.com/live/u/p/{i}.ts\n");
    for (var i = 1; i <= 3; i++)
      source.Append($"#EXTINF:-1 group-title=\"On\",News On {i}\nhttps://example.com/live/u/p/{100 + i}.ts\n");
    return source.ToString();
  }

  private static async Task<PlaylistSearchIndex> Analyze(string source)
  {
    var path = await WriteTemp(source);
    try
    {
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None);
      return Assert.IsType<PlaylistSearchIndex>(analysis.SearchIndex);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public async Task Index_OverBudget_AnalysisStillSucceeds()
  {
    var path = await WriteTemp(Source);
    var full = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None);
    var bounded = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None, maxSearchIndexBytes: 200);
    File.Delete(path);

    Assert.True(full.SearchIndexAvailable);
    Assert.False(bounded.SearchIndexAvailable);
    Assert.Equal(full.TotalChannels, bounded.TotalChannels);
    Assert.Equal(full.Groups, bounded.Groups);
  }

  [Fact]
  public void TryAdd_CountsAllocatedChunksAndBlocks()
  {
    // One short entry still allocates a whole text chunk and a whole block of fields.
    var index = new PlaylistSearchIndex(1_000);

    Assert.False(index.TryAdd("#EXTINF:-1 group-title=\"News\",News 24", "News", PlaylistContentType.Live));
    Assert.True(index.EstimatedBytes >= AllocatedArrayBytes(new PlaylistSearchIndex(long.MaxValue), 1));
  }

  [Fact]
  public void TryAdd_LongNames_AccountAtLeastAllocatedStorage()
  {
    var line = $"#EXTINF:-1 group-title=\"Films\",{new string('a', 8_192)}";
    var index = new PlaylistSearchIndex(long.MaxValue);
    for (var i = 0; i < 2_000; i++) Assert.True(index.TryAdd(line, "Films", PlaylistContentType.Live));

    var allocated = ArrayBytes(index);
    Assert.True(allocated > 125L * 1024 * 1024, $"{allocated} bytes");
    Assert.True(index.EstimatedBytes >= allocated, $"{index.EstimatedBytes} < {allocated} bytes");

    // At the default budget, such a list no longer counts as searchable.
    var bounded = new PlaylistSearchIndex(new PlaylistCacheOptions().MaxSearchIndexBytes);
    Assert.False(Enumerable.Range(0, 2_000).All(_ => bounded.TryAdd(line, "Films", PlaylistContentType.Live)));
  }

  [Fact]
  public async Task Search_LargeFixture_StaysFastAndBounded()
  {
    const int count = 150_000;
    var kinds = new[] { "live", "movie", "series" };
    var source = new StringBuilder("#EXTM3U\n");
    for (var i = 0; i < count; i++)
    {
      // Every country title mixes content types, so all of them are split.
      source.Append($"#EXTINF:-1 tvg-id=\"c{i}.example\" group-title=\"Country {i % 40}\",Title Número {i} {kinds[i % 3]} HD\n")
        .Append($"https://example.com/{kinds[i % 3]}/u/p/{i}.ts\n");
    }

    var path = await WriteTemp(source.ToString());
    var analysis = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None);
    File.Delete(path);
    var index = Assert.IsType<PlaylistSearchIndex>(analysis.SearchIndex);
    Assert.Equal(count, index.Count);
    Assert.True(index.EstimatedBytes <= new PlaylistCacheOptions().MaxSearchIndexBytes, $"{index.EstimatedBytes} bytes");

    var disabled = Set("Country 7 · Movies");
    index.Search("numero 149999", disabled, 50); // warm-up
    // The fastest of three rounds: a slow scheduler tick on a shared CI runner must not fail the test,
    // while a search that is really slow is slow in every round.
    PlaylistSearchResult rare = null!, common = null!, none = null!;
    var fastest = long.MaxValue;
    for (var round = 0; round < 3; round++)
    {
      var stopwatch = Stopwatch.StartNew();
      rare = index.Search("numero 149999", disabled, 50);
      common = index.Search("numero", disabled, 50);
      none = index.Search("nothing like this", disabled, 50);
      stopwatch.Stop();
      fastest = Math.Min(fastest, stopwatch.ElapsedMilliseconds);
    }

    var last = Assert.Single(rare.Matches);
    Assert.Equal(("Title Número 149999 series HD", "Country 39 · Series", 1250), (last.Name, last.OutputGroup, last.Position));
    Assert.Equal(50, common.Matches.Count);
    Assert.True(common.HasMore);
    Assert.Empty(none.Matches);
    Assert.True(fastest < 500, $"{fastest} ms");
  }

  private static long AllocatedArrayBytes(PlaylistSearchIndex index, int entries)
  {
    for (var i = 0; i < entries; i++) index.TryAdd("#EXTINF:-1 group-title=\"News\",News 24", "News", PlaylistContentType.Live);
    return ArrayBytes(index);
  }

  /// <summary>The payload of every text chunk and field block the index holds, read from its private lists.</summary>
  private static long ArrayBytes(PlaylistSearchIndex index)
  {
    static T Get<T>(PlaylistSearchIndex index, string name) => (T)typeof(PlaylistSearchIndex)
      .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(index)!;
    return Get<List<char[]>>(index, "_chunks").Sum(c => (long)c.Length * sizeof(char))
      + Get<List<int[]>>(index, "_blocks").Sum(b => (long)b.Length * sizeof(int));
  }

  private static HashSet<string> Set(params string[] groups) => new(groups, StringComparer.OrdinalIgnoreCase);

  private static PlaylistSearchMatch Find(PlaylistSearchIndex index, string name, ISet<string>? disabled = null) =>
    Assert.Single(index.Search(name, disabled ?? Set(), 10).Matches, m => m.Name == name);

  /// <summary>Analyses the source, generates the file and returns the name and group-title of each written entry.</summary>
  private static async Task<(PlaylistSearchIndex Index, List<(string Name, string GroupTitle)> Written)> AnalyzeAndGenerate(
    string source, ISet<string> disabled)
  {
    var sourcePath = await WriteTemp(source);
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);
      await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath, outputPath, disabled, CancellationToken.None, splitGroupTitles: analysis.SplitGroupTitles);
      var written = (await File.ReadAllLinesAsync(outputPath))
        .Where(line => line.StartsWith("#EXTINF", StringComparison.Ordinal))
        .Select(line => (line[(line.LastIndexOf(',') + 1)..], Regex.Match(line, "group-title=\"([^\"]*)\"").Groups[1].Value))
        .ToList();
      return (Assert.IsType<PlaylistSearchIndex>(analysis.SearchIndex), written);
    }
    finally
    {
      File.Delete(sourcePath);
      File.Delete(outputPath);
    }
  }

  private static async Task<string> WriteTemp(string text)
  {
    var path = Path.GetTempFileName();
    await File.WriteAllTextAsync(path, text);
    return path;
  }
}
