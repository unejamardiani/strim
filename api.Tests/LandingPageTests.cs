using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the landing page to what Strim does today (#596): a hero in two sentences, four stops in the order Strim is
/// used (Clean, Link, Library, Together), the band about the link and five questions; the old group-editor sections
/// are gone, and site.css styles the new parts before its forced-colours block.
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
}
