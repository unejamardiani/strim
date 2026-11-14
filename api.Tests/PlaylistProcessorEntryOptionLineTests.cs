using Api.Endpoints;
using Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Tests;

public class PlaylistProcessorEntryOptionLineTests
{
  private const string Header = "#EXTM3U\n# Created with Strim (https://strim.plis.dev)\n";

  [Fact]
  public async Task Generate_KeepsExtVlcOptLine()
  {
    var (result, output) = await GenerateAsync("""
      #EXTM3U
      #EXTINF:-1 group-title="News",News One
      #EXTVLCOPT:http-user-agent=X
      http://example.invalid/live/news.m3u8
      """);

    Assert.Equal(1, result.KeptChannels);
    Assert.Equal(
      Header +
      "#EXTINF:-1 group-title=\"News\",News One\n" +
      "#EXTVLCOPT:http-user-agent=X\n" +
      "http://example.invalid/live/news.m3u8\n",
      output);
  }

  [Fact]
  public async Task Generate_KeepsKodiPropLine()
  {
    var (result, output) = await GenerateAsync("""
      #EXTM3U
      #EXTINF:-1 group-title="News",News One
      #KODIPROP:inputstream.adaptive.license_type=com.widevine.alpha
      http://example.invalid/live/news.mpd
      """);

    Assert.Equal(1, result.KeptChannels);
    Assert.Equal(
      Header +
      "#EXTINF:-1 group-title=\"News\",News One\n" +
      "#KODIPROP:inputstream.adaptive.license_type=com.widevine.alpha\n" +
      "http://example.invalid/live/news.mpd\n",
      output);
  }

  [Fact]
  public async Task Generate_KeepsMultipleOptionLinesInOrder()
  {
    var (_, output) = await GenerateAsync("""
      #EXTM3U
      #EXTINF:-1 group-title="News",News One
      #EXTVLCOPT:http-user-agent=X
      #KODIPROP:inputstream.adaptive.manifest_type=mpd

      #EXTVLCOPT:http-referrer=http://example.invalid/
      http://example.invalid/live/news.mpd
      """);

    Assert.Equal(
      Header +
      "#EXTINF:-1 group-title=\"News\",News One\n" +
      "#EXTVLCOPT:http-user-agent=X\n" +
      "#KODIPROP:inputstream.adaptive.manifest_type=mpd\n" +
      "#EXTVLCOPT:http-referrer=http://example.invalid/\n" +
      "http://example.invalid/live/news.mpd\n",
      output);
  }

  [Fact]
  public async Task Generate_EntryAfterOptionLinesIsIntact()
  {
    var (result, output) = await GenerateAsync(
      """
      #EXTM3U
      #EXTINF:-1 group-title="News",News One
      #EXTVLCOPT:http-user-agent=X
      #KODIPROP:inputstream.adaptive.license_type=clearkey
      http://example.invalid/live/news.m3u8
      #EXTINF:-1 group-title="Sports",Sports One
      http://example.invalid/live/sports.m3u8
      #EXTINF:-1 group-title="Kids",Kids One
      http://example.invalid/live/kids.m3u8
      """,
      "Kids");

    Assert.Equal(3, result.TotalChannels);
    Assert.Equal(2, result.KeptChannels);
    Assert.EndsWith(
      "http://example.invalid/live/news.m3u8\n" +
      "#EXTINF:-1 group-title=\"Sports\",Sports One\n" +
      "http://example.invalid/live/sports.m3u8\n",
      output);
  }

  [Fact]
  public async Task Generate_DisabledEntryDropsItsOptionLines()
  {
    var (result, output) = await GenerateAsync(
      """
      #EXTM3U
      #EXTINF:-1 group-title="Sports",Sports One
      #EXTVLCOPT:http-user-agent=SportsAgent
      #KODIPROP:inputstream.adaptive.license_key=sports-key
      http://example.invalid/live/sports.m3u8
      #EXTINF:-1 group-title="News",News One
      http://example.invalid/live/news.m3u8
      """,
      "Sports");

    Assert.Equal(2, result.TotalChannels);
    Assert.Equal(1, result.KeptChannels);
    Assert.Equal(
      Header +
      "#EXTINF:-1 group-title=\"News\",News One\n" +
      "http://example.invalid/live/news.m3u8\n",
      output);
  }

  [Fact]
  public async Task Analyze_DetectsContentTypeFromAddressAfterOptionLines()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, """
        #EXTM3U
        #EXTINF:-1 group-title="Cinema",Film One
        #EXTVLCOPT:http-user-agent=X
        http://example.invalid/movie/user/pass/1.mp4
        #EXTINF:-1 group-title="Cinema",Film Two
        http://example.invalid/live/user/pass/2.ts
        """);

      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);

      Assert.Equal(2, analysis.TotalChannels);
      Assert.Contains(analysis.Groups, g => g.GroupTitle == "Cinema" && g.ContentType == PlaylistContentType.Movies && g.Count == 1);
      Assert.Contains(analysis.Groups, g => g.GroupTitle == "Cinema" && g.ContentType == PlaylistContentType.Live && g.Count == 1);
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task Generate_ExtinfWithoutAddressDoesNotSwallowNextEntry()
  {
    var (result, output) = await GenerateAsync("""
      #EXTM3U
      #EXTINF:-1 group-title="News",Broken
      #EXTVLCOPT:http-user-agent=X
      #EXTINF:-1 group-title="Sports",Sports One
      http://example.invalid/live/sports.m3u8
      """);

    Assert.Equal(2, result.TotalChannels);
    Assert.Equal(2, result.KeptChannels);
    Assert.Equal(
      Header +
      "#EXTINF:-1 group-title=\"News\",Broken\n" +
      "#EXTVLCOPT:http-user-agent=X\n" +
      "\n" +
      "#EXTINF:-1 group-title=\"Sports\",Sports One\n" +
      "http://example.invalid/live/sports.m3u8\n",
      output);
  }

  [Fact]
  public async Task Generate_TooManyOptionLinesFails()
  {
    var options = string.Concat(Enumerable.Range(0, PlaylistProcessor.MaxOptionLinesPerEntry + 1)
      .Select(i => $"#EXTVLCOPT:option-{i}=1\n"));
    var source = $"#EXTM3U\n#EXTINF:-1 group-title=\"News\",News One\n{options}http://example.invalid/live/news.m3u8\n";

    var exception = await Assert.ThrowsAsync<PlaylistEntryOptionLinesLimitExceededException>(() => GenerateAsync(source));
    Assert.Contains(PlaylistProcessor.MaxOptionLinesPerEntry.ToString(), exception.Message);

    var mapped = EndpointHelpers.PlaylistFailure(exception, NullLogger.Instance, "generate");
    Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(mapped).StatusCode);

    // Exactly at the cap is still accepted.
    var atCap = string.Concat(Enumerable.Range(0, PlaylistProcessor.MaxOptionLinesPerEntry)
      .Select(i => $"#EXTVLCOPT:option-{i}=1\n"));
    var (result, _) = await GenerateAsync(
      $"#EXTM3U\n#EXTINF:-1 group-title=\"News\",News One\n{atCap}http://example.invalid/live/news.m3u8\n");
    Assert.Equal(1, result.KeptChannels);
  }

  [Fact]
  public async Task Generate_PlaylistWithoutOptionLinesIsUnchanged()
  {
    var (_, output) = await GenerateAsync("""
      #EXTM3U
      #EXTINF:-1 group-title="News",News One
      http://example.invalid/live/news.m3u8

      #EXTINF:-1 tvg-name="Plain",Plain
      http://example.invalid/live/plain.m3u8
      """);

    Assert.Equal(
      Header +
      "#EXTINF:-1 group-title=\"News\",News One\n" +
      "http://example.invalid/live/news.m3u8\n" +
      "#EXTINF:-1 tvg-name=\"Plain\" group-title=\"Ungrouped\",Plain\n" +
      "http://example.invalid/live/plain.m3u8\n",
      output);
  }

  private static async Task<(PlaylistGenerationResult Result, string Output)> GenerateAsync(
    string source,
    params string[] disabledGroups)
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      await File.WriteAllTextAsync(sourcePath, source);
      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(disabledGroups, StringComparer.OrdinalIgnoreCase),
        CancellationToken.None);
      return (result, await File.ReadAllTextAsync(outputPath));
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }
}
