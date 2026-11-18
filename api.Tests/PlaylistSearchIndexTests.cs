using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Api.Services;
using Xunit;

namespace Api.Tests;

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
    var stopwatch = Stopwatch.StartNew();
    var rare = index.Search("numero 149999", disabled, 50);
    var common = index.Search("numero", disabled, 50);
    var none = index.Search("nothing like this", disabled, 50);
    stopwatch.Stop();

    var last = Assert.Single(rare.Matches);
    Assert.Equal(("Title Número 149999 series HD", "Country 39 · Series", 1250), (last.Name, last.OutputGroup, last.Position));
    Assert.Equal(50, common.Matches.Count);
    Assert.True(common.HasMore);
    Assert.Empty(none.Matches);
    Assert.True(stopwatch.ElapsedMilliseconds < 500, $"{stopwatch.ElapsedMilliseconds} ms");
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
