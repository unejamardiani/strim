using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistProcessorExtGrpTests
{
  private const string Header = "#EXTM3U\n# Created with Strim (https://strim.plis.dev)\n";

  [Fact]
  public async Task Analyze_UsesExtGrpWhenNoGroupTitle()
  {
    var analysis = await AnalyzeAsync("""
      #EXTM3U
      #EXTINF:-1 tvg-name="News One",News One
      #EXTGRP:News
      http://example.invalid/live/news.m3u8
      #EXTINF:-1 tvg-name="Plain",Plain
      http://example.invalid/live/plain.m3u8
      """);

    Assert.Equal(2, analysis.TotalChannels);
    Assert.Contains(analysis.Groups, g => g.GroupTitle == "News" && g.Count == 1);
    Assert.Contains(analysis.Groups, g => g.GroupTitle == "Ungrouped" && g.Count == 1);
  }

  [Fact]
  public async Task Analyze_ExtGrpPrefixIsCaseInsensitiveAndFirstLineCounts()
  {
    var analysis = await AnalyzeAsync("""
      #EXTM3U
      #EXTINF:-1,News One
      #extgrp:News
      #EXTGRP:Other
      http://example.invalid/live/news.m3u8
      """);

    var group = Assert.Single(analysis.Groups);
    Assert.Equal("News", group.GroupTitle);
  }

  [Fact]
  public async Task Generate_FiltersByExtGrpGroup()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1,News One
      #EXTGRP:News
      http://example.invalid/live/news.m3u8
      #EXTINF:-1 group-title="Sports",Sports One
      http://example.invalid/live/sports.m3u8
      """;

    var (disabled, disabledOutput) = await GenerateAsync(source, disabledGroups: ["News"]);
    Assert.Equal(2, disabled.TotalChannels);
    Assert.Equal(1, disabled.KeptChannels);
    Assert.DoesNotContain("news.m3u8", disabledOutput);
    Assert.DoesNotContain("#EXTGRP", disabledOutput);

    var (enabled, enabledOutput) = await GenerateAsync(source, disabledGroups: ["Sports"]);
    Assert.Equal(1, enabled.KeptChannels);
    Assert.Contains("http://example.invalid/live/news.m3u8", enabledOutput);
  }

  [Fact]
  public async Task Generate_WritesExtGrpLineUnchanged()
  {
    var (_, output) = await GenerateAsync(
      "#EXTM3U\n" +
      "#EXTINF:-1 tvg-name=\"News One\",News One\n" +
      "#EXTVLCOPT:http-user-agent=X\n" +
      "#EXTGRP: News\n" +
      "#KODIPROP:inputstream.adaptive.manifest_type=mpd\n" +
      "http://example.invalid/live/news.mpd\n");

    Assert.Equal(
      Header +
      "#EXTINF:-1 tvg-name=\"News One\" group-title=\"News\",News One\n" +
      "#EXTVLCOPT:http-user-agent=X\n" +
      "#EXTGRP: News\n" +
      "#KODIPROP:inputstream.adaptive.manifest_type=mpd\n" +
      "http://example.invalid/live/news.mpd\n",
      output);
  }

  [Fact]
  public async Task Analyze_GroupTitleWinsOverExtGrp()
  {
    var analysis = await AnalyzeAsync("""
      #EXTM3U
      #EXTINF:-1 group-title="A",One
      #EXTGRP:B
      http://example.invalid/live/one.m3u8
      """);

    var group = Assert.Single(analysis.Groups);
    Assert.Equal("A", group.GroupTitle);
  }

  [Fact]
  public async Task Generate_GroupTitleWinsOverExtGrp()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1 group-title="A",One
      #EXTGRP:B
      http://example.invalid/live/one.m3u8
      """;

    var (keptByB, output) = await GenerateAsync(source, disabledGroups: ["B"]);
    Assert.Equal(1, keptByB.KeptChannels);
    Assert.Equal(
      Header +
      "#EXTINF:-1 group-title=\"A\",One\n" +
      "#EXTGRP:B\n" +
      "http://example.invalid/live/one.m3u8\n",
      output);

    var (droppedByA, _) = await GenerateAsync(source, disabledGroups: ["A"]);
    Assert.Equal(0, droppedByA.KeptChannels);
  }

  [Fact]
  public async Task Analyze_EmptyExtGrpIsUngrouped()
  {
    var analysis = await AnalyzeAsync(
      "#EXTM3U\n" +
      "#EXTINF:-1,Empty\n" +
      "#EXTGRP:\n" +
      "http://example.invalid/live/empty.m3u8\n" +
      "#EXTINF:-1,Blank\n" +
      "#EXTGRP: \t \n" +
      "http://example.invalid/live/blank.m3u8\n");

    var group = Assert.Single(analysis.Groups);
    Assert.Equal("Ungrouped", group.GroupTitle);
    Assert.Equal(2, group.Count);
  }

  [Fact]
  public async Task Analyze_ExtGrpRespectsGroupTitleLengthLimit()
  {
    var longTitle = new string('x', 129);
    var source = $"#EXTM3U\n#EXTINF:-1,Long\n#EXTGRP:{longTitle}\nhttp://example.invalid/live/long.m3u8\n";

    await Assert.ThrowsAsync<PlaylistGroupTitleTooLongException>(
      () => AnalyzeAsync(source, maxGroupTitleLengthChars: 128));

    var many = string.Concat(Enumerable.Range(0, 3)
      .Select(i => $"#EXTINF:-1,Ch{i}\n#EXTGRP:Group {i}\nhttp://example.invalid/live/{i}.m3u8\n"));
    await Assert.ThrowsAsync<PlaylistGroupLimitExceededException>(
      () => AnalyzeAsync($"#EXTM3U\n{many}", maxGroupCount: 2));
  }

  [Fact]
  public async Task Generate_SplitsExtGrpGroupByContentType()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1,Cinema Live
      #EXTGRP:Cinema
      http://example.invalid/live/user/pass/1.ts
      #EXTINF:-1,Film One
      #EXTGRP:Cinema
      http://example.invalid/movie/user/pass/2.mp4
      """;

    var analysis = await AnalyzeAsync(source);
    var moviesKey = PlaylistProcessor.GroupKey("Cinema", PlaylistContentType.Movies);
    Assert.Contains("Cinema", analysis.SplitGroupTitles);
    Assert.Contains(analysis.Groups, g => g.Key == moviesKey && g.Count == 1);

    var (result, output) = await GenerateAsync(source, splitGroupTitles: analysis.SplitGroupTitles);
    Assert.Equal(2, result.KeptChannels);
    Assert.Contains("#EXTINF:-1 group-title=\"Cinema\",Cinema Live\n#EXTGRP:Cinema\n", output);
    Assert.Contains($"#EXTINF:-1 group-title=\"{moviesKey}\",Film One\n#EXTGRP:Cinema\n", output);

    var (moviesOff, moviesOffOutput) = await GenerateAsync(
      source, disabledGroups: [moviesKey], splitGroupTitles: analysis.SplitGroupTitles);
    Assert.Equal(1, moviesOff.KeptChannels);
    Assert.DoesNotContain("Film One", moviesOffOutput);
  }

  private static async Task<PlaylistAnalysisResult> AnalyzeAsync(
    string source,
    int maxGroupCount = 10_000,
    int maxGroupTitleLengthChars = 512)
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, source);
      return await PlaylistProcessor.AnalyzeFileAsync(
        sourcePath,
        CancellationToken.None,
        maxGroupCount: maxGroupCount,
        maxGroupTitleLengthChars: maxGroupTitleLengthChars);
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  private static async Task<(PlaylistGenerationResult Result, string Output)> GenerateAsync(
    string source,
    string[]? disabledGroups = null,
    IReadOnlySet<string>? splitGroupTitles = null)
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      await File.WriteAllTextAsync(sourcePath, source);
      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(disabledGroups ?? [], StringComparer.OrdinalIgnoreCase),
        CancellationToken.None,
        splitGroupTitles: splitGroupTitles);
      return (result, await File.ReadAllTextAsync(outputPath));
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }
}
