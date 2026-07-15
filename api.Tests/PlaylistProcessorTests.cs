using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistProcessorTests
{
  [Fact]
  public async Task FileProcessing_AnalyzesAndFiltersWithoutMaterializingThePlaylistResult()
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    const string source = """
      #EXTM3U
      #EXTINF:-1 tvg-name="News One" group-title="News",News One
      https://example.com/news.m3u8

      #EXTINF:-1 tvg-name="Sports One" group-title="Sports",Sports One
      https://example.com/sports.m3u8
      #EXTINF:-1 tvg-name="No Group",No Group
      https://example.com/other.m3u8
      """;

    try
    {
      await File.WriteAllTextAsync(sourcePath, source);

      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);
      Assert.Equal(3, analysis.TotalChannels);
      Assert.Equal(1, analysis.Groups["News"]);
      Assert.Equal(1, analysis.Groups["Sports"]);
      Assert.Equal(1, analysis.Groups["Ungrouped"]);

      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(new[] { "sports" }, StringComparer.OrdinalIgnoreCase),
        CancellationToken.None);
      var output = await File.ReadAllTextAsync(outputPath);

      Assert.Equal(3, result.TotalChannels);
      Assert.Equal(2, result.KeptChannels);
      Assert.Contains("News One", output);
      Assert.DoesNotContain("Sports One", output);
      Assert.Contains("group-title=\"Ungrouped\"", output);
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public void InMemoryCompatibilityPath_PreservesCaseInsensitiveDisabledGroups()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1 group-title="Kids",Kids One
      https://example.com/kids.m3u8
      #EXTINF:-1 group-title="News",News One
      https://example.com/news.m3u8
      """;

    var filtered = PlaylistProcessor.GenerateFiltered(
      source,
      new HashSet<string>(new[] { "KIDS" }, StringComparer.OrdinalIgnoreCase));

    Assert.Equal(2, filtered.TotalChannels);
    Assert.Equal(1, filtered.KeptChannels);
    Assert.DoesNotContain("Kids One", filtered.Text);
    Assert.Contains("News One", filtered.Text);
  }

  [Fact]
  public async Task FileAnalysis_RejectsAnOversizedLineBeforeItCanBecomeAPlaylistResult()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, $"#EXTM3U\n#EXTINF:-1 group-title=\"{new string('a', 128)}\",Test\nhttps://example.com/test.m3u8\n");

      await Assert.ThrowsAsync<PlaylistLineTooLongException>(() =>
        PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None, maxLineLengthChars: 64));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task FileAnalysis_RejectsExcessiveDistinctGroupMetadata()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, """
        #EXTM3U
        #EXTINF:-1 group-title="News",News
        https://example.com/news.m3u8
        #EXTINF:-1 group-title="Sports",Sports
        https://example.com/sports.m3u8
        """);

      await Assert.ThrowsAsync<PlaylistGroupLimitExceededException>(() =>
        PlaylistProcessor.AnalyzeFileAsync(
          sourcePath,
          CancellationToken.None,
          maxLineLengthChars: 1024,
          maxGroupCount: 1,
          maxGroupTitleLengthChars: 128,
          maxGroupMetadataBytes: 1024));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }
}
