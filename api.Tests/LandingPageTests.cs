using System.Globalization;
using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the landing page to what Strim does today (#596): a hero in two sentences, four stops in the order Strim is
/// used (Clean, Link, Library, Together), the band about the link and five questions; the old group-editor sections
/// are gone, and site.css styles the new parts before its forced-colours block. Stops 2 and 4 hold their stills
/// (#601): pictures drawn with the app's own text and icons. Stop 3 holds a film card (#602) with drawn posters, never
/// a poster image. The hero shows a television with the Watchlist group and a phone with the Library (#603); the group
/// picker moved to stop 1.
/// </summary>
public class LandingPageTests
{
  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string Text(string html) => Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ");

  private static string Block(string html, string pattern)
  {
    var match = Regex.Match(html, pattern, RegexOptions.Singleline);
    Assert.True(match.Success, $"no match for {pattern}");
    return match.Groups[1].Value;
  }

  /// <summary>The markup of each <c>.stop</c>, in page order.</summary>
  private static string[] Stops() =>
    Block(Read("index.html"), @"<ol class=""stops"">(.*?)</ol>").Split(@"<li class=""stop"">")[1..];

  private static string Stop(string eyebrow) =>
    Stops().Single(stop => stop.Contains($@"<span class=""eyebrow"">{eyebrow}</span>"));

  [Fact]
  public void Hero_SaysBothSentences_EachInItsOwnElement()
  {
    var h1 = Block(Read("index.html"), @"<h1>(.*?)</h1>");

    Assert.Contains("<span>Pick it on the phone.</span>", h1);
    Assert.Contains("<em>Find it on the TV.</em>", h1);
  }

  [Fact]
  public void Stops_AreCleanLinkLibraryTogether_EachWithOneHeadingAndThreeTicks()
  {
    var stops = Stops();

    Assert.Equal(4, stops.Length);
    Assert.Equal(
      new[] { "Clean", "Link", "Library", "Together" },
      stops.Select(stop => Block(stop, @"<span class=""eyebrow"">(.*?)</span>")));
    foreach (var stop in stops)
    {
      Assert.Single(Regex.Matches(stop, "<h2[ >]"));
      Assert.Equal(3, Regex.Matches(Block(stop, @"<ul class=""ticks"">(.*?)</ul>"), "<li>").Count);
    }
  }

  [Fact]
  public void LibraryStop_NamesTmdbWithTheServerKey_TogetherStop_NamesTheInviteLimits()
  {
    var library = Text(Stop("Library"));
    Assert.Contains("TMDB", library);
    Assert.Contains("where the server has a TMDB key", library);

    Assert.Contains("It works once and for seven days.", Text(Stop("Together")));
  }

  [Fact]
  public void Band_HoldsThreeFacts_Faq_HoldsFiveQuestions()
  {
    var html = Read("index.html");

    var band = Block(html, @"<div class=""band-grid"">(.*?)</div>\s*</section>");
    Assert.Equal(3, Regex.Matches(band, @"<div><b>").Count);

    var faq = Block(html, @"<div class=""faq"">(.*?)</div>\s*</section>");
    Assert.Equal(5, Regex.Matches(faq, @"<details class=""card"">").Count);
  }

  [Fact]
  public void Page_NoLongerHoldsTheOldSections()
  {
    var html = Read("index.html");

    Assert.DoesNotContain("How it works", html);
    Assert.DoesNotContain("One job", html);
    Assert.DoesNotContain(@"class=""steps""", html);
  }

  [Fact]
  public void Hero_LinksTheAppAndFeatures_MetaDescription_NamesTheLibrary()
  {
    var html = Read("index.html");
    var hero = Block(html, @"<section class=""hero hero-tv"">(.*?)<figure");

    Assert.Contains(@"href=""/app""", hero);
    Assert.Contains(@"href=""features.html""", hero);
    Assert.Contains("library", Block(html, @"<meta name=""description"" content=""([^""]*)"""));
  }

  [Fact]
  public void Page_HasNoInlineStylesOrScripts()
  {
    var html = Read("index.html");

    Assert.DoesNotContain("style=", html);
    Assert.DoesNotContain("<script", html);
    Assert.DoesNotContain("<style", html);
  }

  [Fact]
  public void SiteCss_StylesTheNewParts_BeforeTheForcedColoursBlock_WhichBordersTheBand()
  {
    var css = Read("site.css");
    var forced = css.LastIndexOf("@media (forced-colors: active)", StringComparison.Ordinal);
    Assert.True(forced >= 0, "site.css has no forced-colors block.");

    foreach (var rule in new[] { ".stops {", ".stop {", ".ticks {", ".band {" })
    {
      var at = css.IndexOf("\n" + rule, StringComparison.Ordinal);
      Assert.True(at >= 0 && at < forced, $"site.css must hold {rule} before the forced-colors block.");
    }
    Assert.Contains(".band { border: 1px solid CanvasText; }", css[forced..]);
  }

  [Theory]
  [InlineData("Link")]
  [InlineData("Library")]
  [InlineData("Together")]
  public void LinkLibraryAndTogetherStops_HoldOneStill_APictureWithoutLinksButtonsOrImages(string eyebrow)
  {
    var stop = Stop(eyebrow);

    var figure = Assert.Single(Regex.Matches(stop, "<figure[^>]*>"));
    Assert.Contains(@"role=""img""", figure.Value);
    Assert.Matches(@"aria-label=""[^""]+""", figure.Value);

    var still = Block(stop, "<figure[^>]*>(.*?)</figure>");
    Assert.DoesNotContain("<a", still);
    Assert.DoesNotContain("<button", still);
    Assert.DoesNotContain("<img", still);
  }

  [Theory]
  [InlineData("Similar films")]
  [InlineData("On your watchlist")]
  public void FilmCardStill_UsesTheLibrarysOwnText(string text)
  {
    var still = Block(Stop("Library"), "<figure[^>]*>(.*?)</figure>");

    Assert.Contains(text, still);
    Assert.Contains(text, Read("library.js"));
  }

  [Fact]
  public void Page_ShowsNoPosterImage()
  {
    var html = Read("index.html");

    Assert.DoesNotContain("image.tmdb.org", html);
    Assert.DoesNotContain("<img", html);
  }

  [Fact]
  public void SiteCss_DrawsThePosters_BeforeTheForcedColoursBlock_WhichBordersThem()
  {
    var css = Read("site.css");
    var forced = css.LastIndexOf("@media (forced-colors: active)", StringComparison.Ordinal);
    Assert.True(forced >= 0, "site.css has no forced-colors block.");
    var before = css[..forced];

    Assert.Contains("aspect-ratio: 2 / 3;", Block(before, @"\n\.art \{([^}]*)\}"));
    foreach (var rule in new[] { ".p1 {", ".p2 {", ".p3 {", ".p4 {", ".p5 {", ".p6 {" })
      Assert.Contains("\n" + rule, before);
    var tokens = Block(before, @"\n\.site \{([^}]*--po-[^}]*)\}");
    foreach (var token in new[] { "--po-teal:", "--po-sun:", "--po-sand:", "--po-ink:", "--po-cream:", "--po-sea-2:" })
      Assert.Contains(token, tokens);
    Assert.Contains(".art, .capsule, .watch { border: 1px solid CanvasText; }", css[forced..]);
  }

  [Theory]
  [InlineData("Refresh every 6 hours")]
  [InlineData("Keeps the provider list cached for players")]
  [InlineData("Watchlist group")]
  [InlineData("Players show changes when they reload the list")]
  [InlineData("What they get")]
  [InlineData("Browse and search the groups you switched on")]
  [InlineData("Open film and series cards")]
  [InlineData("Keep their own seen and thumbs marks")]
  [InlineData("No player link, no source, no changes to the list")]
  [InlineData("Invite someone")]
  public void Stills_UseTheAppsOwnText(string text)
  {
    Assert.Contains(text, Read("index.html"));
    Assert.Contains(text, Read("main.js"));
  }

  [Theory]
  [InlineData("clock")]
  [InlineData("bookmark")]
  [InlineData("check")]
  [InlineData("star")]
  [InlineData("up")]
  [InlineData("down")]
  public void Stills_DrawTheAppsIcons(string icon)
  {
    var icons = Block(Read("main.js"), @"const ICONS = \{(.*?)\n\};");
    var markup = Block(icons, $@"\n\s*{icon}: '([^']*)'");
    var path = Block(markup, @"(?<![\w-])d=""([^""]+)""");

    Assert.Contains($@"d=""{path}""", Read("index.html"));
  }

  [Fact]
  public void SiteCss_StylesTheStills_BeforeTheForcedColoursBlock_WhichBordersTheStill()
  {
    var css = Read("site.css");
    var forced = css.LastIndexOf("@media (forced-colors: active)", StringComparison.Ordinal);
    Assert.True(forced >= 0, "site.css has no forced-colors block.");

    foreach (var rule in new[] { ".still {", ".ic {" })
    {
      var at = css.IndexOf("\n" + rule, StringComparison.Ordinal);
      Assert.True(at >= 0 && at < forced, $"site.css must hold {rule} before the forced-colors block.");
    }
    Assert.Contains(".still { border: 1px solid CanvasText; }", css[forced..]);
  }

  private static string Hero() => Block(Read("index.html"), @"<section class=""hero hero-tv"">(.*?)</section>");

  private static string Duo()
  {
    var hero = Hero();
    Assert.Single(Regex.Matches(hero, @"<div class=""duo"">"));
    return Block(hero, @"<div class=""duo"">(.*)</div>\s*$");
  }

  private static string Phone() => Block(Duo(), @"<div class=""phone"">(.*?)</figure>");

  [Fact]
  public void Hero_HoldsATelevisionAndAPhone_BothPicturesWithoutLinksButtonsOrImages()
  {
    var duo = Duo();

    var tv = Assert.Single(Regex.Matches(duo, @"<figure[^>]*class=""tvset""[^>]*>"));
    Assert.Contains(@"role=""img""", tv.Value);
    Assert.Matches(@"aria-label=""[^""]+""", tv.Value);

    Assert.Single(Regex.Matches(duo, @"<div class=""phone"">"));
    var still = Assert.Single(Regex.Matches(Block(duo, @"<div class=""phone"">(.*)"), @"<figure[^>]*>"));
    Assert.Contains(@"class=""still", still.Value);
    Assert.Contains(@"role=""img""", still.Value);
    Assert.Matches(@"aria-label=""[^""]+""", still.Value);

    Assert.DoesNotContain("<a", duo);
    Assert.DoesNotContain("<button", duo);
    Assert.DoesNotContain("<img", duo);
  }

  [Fact]
  public void Television_ListsWatchlistAndWatchlistSeries_AsItsFirstGroups()
  {
    var groups = Block(Duo(), @"<div class=""tv-col"">(.*?)</div>");

    Assert.Equal(
      new[] { "Watchlist", "Watchlist · Series" },
      Regex.Matches(groups, "<b>(.*?)</b>").Take(2).Select(match => match.Groups[1].Value));
  }

  [Fact]
  public void GroupPicker_StandsInTheCleanStop_EveryStopHoldsOneFigure()
  {
    Assert.Single(Regex.Matches(Read("index.html"), @"class=""demo"""));
    Assert.Contains(@"<figure class=""demo""", Stops()[0]);
    Assert.Contains(@"<span class=""eyebrow"">Clean</span>", Stops()[0]);
    foreach (var stop in Stops())
      Assert.Single(Regex.Matches(stop, "<figure[ >]"));
  }

  private static string Picker() => Block(Read("index.html"), @"<figure class=""demo""[^>]*>(.*?)</figure>");

  /// <summary>Each row of the group picker: its name, its count and whether it is on (#618).</summary>
  private static (string Name, int Count, bool On)[] PickerRows() =>
    Regex.Matches(Picker(), @"<li class=""demo-row( off)?"">(.*?)</li>")
      .Select(row =>
      {
        var markup = row.Groups[2].Value;
        var on = row.Groups[1].Value == "";
        Assert.Equal(on, markup.Contains(@"<span class=""switch on"">"));
        return (
          Block(markup, @"<span class=""name"">(.*?)</span>"),
          int.Parse(Block(markup, @"<span class=""count"">(.*?)</span>"), NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
          on);
      })
      .ToArray();

  [Fact]
  public void GroupPicker_ShowsMovies4KSwitchedOn()
  {
    var movies = PickerRows().Single(row => row.Name == "Movies · 4K");

    Assert.True(movies.On);
    Assert.Matches(
      @"<li class=""demo-row""><span class=""dot movies""></span><span class=""name"">Movies · 4K</span>.*?<span class=""switch on""></span></li>",
      Picker());
  }

  [Fact]
  public void GroupPickerDock_AddsUpTheRowsThatAreOn()
  {
    var rows = PickerRows();
    var on = rows.Where(row => row.On).ToArray();
    var dock = Block(Picker(), @"<div class=""demo-dock""[^>]*>(.*?)</div>");

    Assert.Equal(6, rows.Length);
    Assert.Contains(
      $@"<span class=""value"">{on.Sum(row => row.Count).ToString("N0", CultureInfo.InvariantCulture)} <small>channels</small></span>",
      dock);
    Assert.Contains($@"<span class=""sub"">{on.Length} of {rows.Length} groups on</span>", dock);
  }

  [Theory]
  [InlineData("Search this list")]
  [InlineData("Your watchlist")]
  [InlineData("Home")]
  [InlineData("Search")]
  [InlineData("Groups")]
  [InlineData("Marks")]
  public void PhoneStill_UsesTheLibrarysOwnText(string text)
  {
    Assert.Contains(text, Phone());
    Assert.Contains(text, Read("library.js"));
  }

  [Theory]
  [InlineData("search")]
  [InlineData("bookmark")]
  [InlineData("home")]
  [InlineData("grid")]
  [InlineData("marks")]
  public void Hero_DrawsTheAppsIcons(string icon)
  {
    // The whole markup of the icon, not only its first d value: grid is drawn with rects and has no d.
    var icons = Block(Read("main.js"), @"const ICONS = \{(.*?)\n\};");
    var markup = Block(icons, $@"\n\s*{icon}: '([^']*)'");

    Assert.Contains(markup, Block(Read("index.html"), @"<div class=""duo"">(.*?)</section>"));
  }

  [Fact]
  public void SiteCss_StylesTheHeroFigure_BeforeTheForcedColoursBlock_WhichBordersIt()
  {
    var css = Read("site.css");
    var forced = css.LastIndexOf("@media (forced-colors: active)", StringComparison.Ordinal);
    Assert.True(forced >= 0, "site.css has no forced-colors block.");

    foreach (var rule in new[] { ".duo {", ".tvset .tv-screen {", ".phone {", ".tabbar {" })
    {
      var at = css.IndexOf("\n" + rule, StringComparison.Ordinal);
      Assert.True(at >= 0 && at < forced, $"site.css must hold {rule} before the forced-colors block.");
    }
    Assert.Contains(".tv-body, .phone, .tabbar { border: 1px solid CanvasText; }", css[forced..]);
    Assert.Contains(
      ".tv-col span.sel { forced-color-adjust: none; background: Highlight; color: HighlightText; }", css[forced..]);
    Assert.DoesNotContain(".stop:not(:has(figure))", css);
  }
}
