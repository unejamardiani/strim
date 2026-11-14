using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistProcessorLegacyEntryTests
{
  private static HashSet<string> Disabled(params string[] groups) =>
    new(groups, StringComparer.OrdinalIgnoreCase);

  [Fact]
  public void Legacy_GenerateKeepsOptionLines()
  {
    var result = PlaylistProcessor.GenerateFiltered(
      "#EXTM3U\n" +
      "#EXTINF:-1 group-title=\"News\",News One\n" +
      "#EXTVLCOPT:http-user-agent=X\n" +
      "#EXTGRP:Ignored\n" +
      "#KODIPROP:inputstream.adaptive.manifest_type=mpd\n" +
      "http://example.invalid/live/news.mpd\n" +
      "#EXTINF:-1 group-title=\"Kids\",Kids One\n" +
      "#EXTVLCOPT:http-referrer=http://example.invalid/\n" +
      "http://example.invalid/live/kids.m3u8\n" +
      "#EXTINF:-1 group-title=\"Sports\",Sports One\n" +
      "http://example.invalid/live/sports.m3u8\n",
      Disabled("Kids"));

    Assert.Equal(3, result.TotalChannels);
    Assert.Equal(2, result.KeptChannels);
    Assert.Equal(
      "#EXTM3U\n" +
      "# Created with Strim (https://strim.plis.dev)\n" +
      "#EXTINF:-1 group-title=\"News\",News One\n" +
      "#EXTVLCOPT:http-user-agent=X\n" +
      "#EXTGRP:Ignored\n" +
      "#KODIPROP:inputstream.adaptive.manifest_type=mpd\n" +
      "http://example.invalid/live/news.mpd\n" +
      "#EXTINF:-1 group-title=\"Sports\",Sports One\n" +
      "http://example.invalid/live/sports.m3u8",
      result.Text.ReplaceLineEndings("\n"));
  }

  [Fact]
  public void Legacy_CountGroupsUsesExtGrp()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1,News One
      #EXTVLCOPT:http-user-agent=X
      #EXTGRP:News
      http://example.invalid/live/news.m3u8
      #EXTINF:-1,Plain
      http://example.invalid/live/plain.m3u8
      """;

    var (groups, total) = PlaylistProcessor.CountGroups(source);
    Assert.Equal(2, total);
    Assert.Equal(1, groups["News"]);
    Assert.Equal(1, groups["Ungrouped"]);
    Assert.Equal(2, groups.Count);

    var filtered = PlaylistProcessor.GenerateFiltered(source, Disabled("News"));
    Assert.Equal(1, filtered.KeptChannels);
    Assert.DoesNotContain("news.m3u8", filtered.Text);

    var kept = PlaylistProcessor.GenerateFiltered(source, Disabled("Ungrouped"));
    Assert.Contains("#EXTINF:-1 group-title=\"News\",News One", kept.Text);
    Assert.Contains("#EXTGRP:News", kept.Text);
    Assert.Contains("http://example.invalid/live/news.m3u8", kept.Text);
  }

  [Fact]
  public void Legacy_GroupTitleWinsOverExtGrp()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1 group-title="Sports",Sports One
      #EXTGRP:News
      http://example.invalid/live/sports.m3u8
      """;

    var (groups, _) = PlaylistProcessor.CountGroups(source);
    var group = Assert.Single(groups);
    Assert.Equal("Sports", group.Key);

    Assert.Equal(1, PlaylistProcessor.GenerateFiltered(source, Disabled("News")).KeptChannels);
    Assert.Equal(0, PlaylistProcessor.GenerateFiltered(source, Disabled("Sports")).KeptChannels);
  }

  [Fact]
  public async Task Legacy_CountGroupsMatchesAnalyzeFile()
  {
    const string source = """
      #EXTM3U url-tvg="http://example.invalid/epg.xml"
      #EXTINF:-1 group-title="News",News One
      #EXTVLCOPT:http-user-agent=X
      #KODIPROP:inputstream.adaptive.manifest_type=mpd
      http://example.invalid/live/news.mpd
      #EXTINF:-1,Missing Address
      #EXTINF:-1,Grouped Later
      #EXTGRP:Kids
      http://example.invalid/live/kids.m3u8
      #EXTINF:-1 group-title="Germany",Film
      http://example.invalid/movie/u/p/1.mp4
      #EXTINF:-1 group-title="Germany",Channel
      #EXTGRP:Other
      http://example.invalid/live/de.m3u8
      #EXTINF:-1,Plain
      http://example.invalid/live/plain.m3u8
      """;

    var (groups, total) = PlaylistProcessor.CountGroups(source);

    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, source);
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None);

      var expected = analysis.Groups
        .GroupBy(g => g.GroupTitle, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.OrdinalIgnoreCase);

      Assert.Equal(analysis.TotalChannels, total);
      Assert.Equal(6, total);
      Assert.Equal(expected.OrderBy(kv => kv.Key), groups.OrderBy(kv => kv.Key));
      Assert.Equal(2, groups["Ungrouped"]);
      Assert.Equal(2, groups["Germany"]);
      Assert.Equal(1, groups["Kids"]);
    }
    finally
    {
      File.Delete(path);
    }
  }
}
