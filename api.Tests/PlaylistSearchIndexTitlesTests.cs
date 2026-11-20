using System.Text;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistSearchIndexTitlesTests
{
  private static readonly HashSet<string> NoneOff = new(StringComparer.OrdinalIgnoreCase);

  [Fact]
  public async Task Titles_CollapseEpisodesOfOneSeries()
  {
    var entries = new List<(string, string, string)>();
    for (var season = 1; season <= 2; season++)
      for (var episode = 1; episode <= 8; episode++)
        entries.Add(("Shows", $"Dark S{season:00}E{episode:00}", "series"));
    var index = await Analyze(Source([.. entries]));

    var title = Assert.Single(index.Titles(new PlaylistTitleQuery(), NoneOff, hideDisabled: true).Titles);

    Assert.Equal((16, 1, 2, 16), (title.Entries, title.SeasonFirst, title.SeasonLast, title.Episodes));
    Assert.Equal(("Dark S01E01", "Dark", PlaylistContentType.Series), (title.Name, title.Title, title.ContentType));
    Assert.Equal(MarkKey.From("Dark", MarkKind.Series)!.Key, title.MarkKey);
  }

  [Fact]
  public async Task Titles_CollapseVariantsOfOneFilmAcrossGroups()
  {
    var index = await Analyze(Source(("Films DE", "Heat (1995)", "movie"), ("Films EN", "Heat (1995) FHD", "movie")));

    var title = Assert.Single(index.Titles(new PlaylistTitleQuery(), NoneOff, hideDisabled: true).Titles);

    Assert.Equal((2, 2, 1995, "Heat"), (title.Entries, title.Groups, title.Year, title.Title));
    Assert.Null(title.Episodes);
    Assert.Null(title.SeasonFirst);
  }

  [Fact]
  public async Task Titles_KeepChannelsAndUntitledEntriesApart()
  {
    var index = await Analyze(Source(("News", "News 24", "live"), ("News", "News 24", "live"), ("Films", "[4K]", "movie")));

    var titles = index.Titles(new PlaylistTitleQuery(), NoneOff, hideDisabled: true).Titles;

    Assert.Equal(3, titles.Count);
    Assert.All(titles, t => Assert.Null(t.MarkKey));
    Assert.All(titles, t => Assert.Equal(1, t.Entries));
    Assert.Equal("News 24", titles[0].Title);
  }

  [Fact]
  public async Task Titles_WithGroupKey_ReadOnlyThatGroup()
  {
    var index = await Analyze(Source(
      ("Films DE", "Heat", "movie"), ("Films EN", "Heat", "movie"), ("Films EN", "Alien", "movie"), ("Films DE", "Heat 4K", "movie")));

    var titles = index.Titles(new PlaylistTitleQuery(GroupKey: "films de"), NoneOff, hideDisabled: true).Titles;

    var title = Assert.Single(titles);
    Assert.Equal((2, 1, "Films DE"), (title.Entries, title.Groups, title.GroupKey));
  }

  [Fact]
  public async Task Titles_WithQuery_MatchLikeSearch()
  {
    var index = await Analyze(Source(
      ("Films", "Amélie", "movie"), ("Films", "AMELIE Returns", "movie"), ("Films", "Heat", "movie"), ("Live", "Amelie TV", "live")));

    var titles = index.Titles(new PlaylistTitleQuery("amelie"), NoneOff, hideDisabled: true).Titles;
    var search = index.Search("amelie", NoneOff, 50).Matches;

    Assert.Equal(["Amélie", "AMELIE Returns", "Amelie TV"], titles.Select(t => t.Name));
    Assert.Equal(search.Select(m => m.Name), titles.Select(t => t.Name));
    Assert.Equal(4, index.Titles(new PlaylistTitleQuery(""), NoneOff, hideDisabled: true).Titles.Count);
  }

  [Fact]
  public async Task Titles_WithContentTypes_SkipOtherTypes()
  {
    var index = await Analyze(Source(("Films", "Heat", "movie"), ("Shows", "Dark S01E01", "series"), ("Live", "News 24", "live")));

    var types = new HashSet<PlaylistContentType> { PlaylistContentType.Movies, PlaylistContentType.Series };
    var titles = index.Titles(new PlaylistTitleQuery(ContentTypes: types), NoneOff, hideDisabled: true).Titles;

    Assert.Equal(["Heat", "Dark S01E01"], titles.Select(t => t.Name));
  }

  [Fact]
  public async Task Titles_WithKeys_ReturnOnlyThoseTitles()
  {
    var index = await Analyze(Source(
      ("Films", "Heat", "movie"), ("Shows", "Dark S01E01", "series"), ("Films", "Alien", "movie"), ("Live", "Heat", "live")));
    var keys = new HashSet<string> { MarkKey.From("Heat", MarkKind.Film)!.Key, MarkKey.From("Dark", MarkKind.Series)!.Key };

    var titles = index.Titles(new PlaylistTitleQuery(Keys: keys), NoneOff, hideDisabled: true).Titles;

    Assert.Equal(["Heat", "Dark S01E01"], titles.Select(t => t.Name));
    Assert.Equal([PlaylistContentType.Movies, PlaylistContentType.Series], titles.Select(t => t.ContentType));
  }

  [Fact]
  public async Task Titles_HideDisabled_CountsTitlesOnlyInDisabledGroups()
  {
    var index = await Analyze(Source(
      ("Films Off", "Heat", "movie"), ("Films Off", "Heat FHD", "movie"), ("Films Off", "Alien", "movie"),
      ("Live Off", "News 24", "live"), ("Films", "Alien", "movie")));
    var off = new HashSet<string>(["Films Off", "Live Off"], StringComparer.OrdinalIgnoreCase);

    var cleaned = index.Titles(new PlaylistTitleQuery(), off, hideDisabled: true);
    var title = Assert.Single(cleaned.Titles);
    Assert.Equal(("Alien", 1, true), (title.Name, title.Entries, title.Enabled));
    // Heat (two variants) once, the channel once; Alien is visible elsewhere.
    Assert.Equal(2, cleaned.HiddenCount);

    var full = index.Titles(new PlaylistTitleQuery(), off, hideDisabled: false);
    Assert.Equal(0, full.HiddenCount);
    Assert.Equal([("Heat", false), ("News 24", false), ("Alien", true)], full.Titles.Select(t => (t.Name, t.Enabled)));
  }

  [Fact]
  public async Task Titles_PreferAnEnabledGroupsEntry()
  {
    var index = await Analyze(Source(("Films Off", "Heat 4K", "movie"), ("Films", "Heat", "movie")));

    var title = Assert.Single(index.Titles(new PlaylistTitleQuery(), new HashSet<string> { "Films Off" }, hideDisabled: false).Titles);

    Assert.Equal(("Heat", "Films", true, 2, 2), (title.Name, title.GroupKey, title.Enabled, title.Entries, title.Groups));
  }

  [Fact]
  public async Task Titles_KeepSourceOrderOfTheFirstEntry()
  {
    var index = await Analyze(Source(
      ("Shows", "Lost S01E01", "series"), ("Films", "Heat", "movie"), ("Shows", "Dark S01E01", "series"),
      ("Shows", "Lost S01E02", "series"), ("Live", "News 24", "live"), ("Films", "Heat FHD", "movie")));

    var titles = index.Titles(new PlaylistTitleQuery(), NoneOff, hideDisabled: true).Titles;

    Assert.Equal(["Lost S01E01", "Heat", "Dark S01E01", "News 24"], titles.Select(t => t.Name));
  }

  [Fact]
  public async Task Titles_ScanTheIndexOnce()
  {
    var index = await Analyze(Source(("Films", "Heat", "movie"), ("Shows", "Dark S01E01", "series"), ("Live", "News 24", "live")));
    var before = index.PassCount;

    index.Titles(new PlaylistTitleQuery("e"), NoneOff, hideDisabled: true);

    Assert.Equal(before + 1, index.PassCount);
  }

  private static string Source(params (string Group, string Name, string Kind)[] entries)
  {
    var source = new StringBuilder("#EXTM3U\n");
    var i = 0;
    foreach (var (group, name, kind) in entries)
      source.Append($"#EXTINF:-1 group-title=\"{group}\",{name}\nhttps://example.com/{kind}/u/p/{++i}.{(kind == "live" ? "ts" : "mkv")}\n");
    return source.ToString();
  }

  private static async Task<PlaylistSearchIndex> Analyze(string source)
  {
    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, source);
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None);
      return Assert.IsType<PlaylistSearchIndex>(analysis.SearchIndex);
    }
    finally
    {
      File.Delete(path);
    }
  }
}
