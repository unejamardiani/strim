using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins features.html as the complete list of what Strim does (#593): its sections in the order a person meets
/// them, the TMDB condition on posters and cards, and control labels that must match the app's own wording.
/// </summary>
public class FeaturesPageTests
{
  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static readonly string[] SectionOrder =
    ["load-title", "pick-title", "link-title", "facts-title", "library-title", "sharing-title", "account-title", "players-title"];

  /// <summary>Every content section with its aria-labelledby id, in page order (the closing band is left out).</summary>
  private static List<(string Id, string Html)> Sections(string html) =>
    Regex.Matches(html, @"<section\b[^>]*aria-labelledby=""([^""]+)""[^>]*>(.*?)</section>", RegexOptions.Singleline)
      .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
      .Where(s => s.Item1 != "cta-title")
      .ToList();

  private static string Text(string html) => Regex.Replace(html, "<[^>]+>", " ");

  [Fact]
  public void Sections_AppearInOrder_EachLabelledByItsHeading()
  {
    var sections = Sections(Read("features.html"));

    Assert.Equal(SectionOrder, sections.Select(s => s.Id));
    foreach (var (id, html) in sections)
      Assert.Matches($@"<h2 id=""{id}"">", html);
  }

  [Fact]
  public void Library_NamesTmdbAndTheServerKey_AndNoOtherSectionMentionsPostersOrCardsWithoutTmdb()
  {
    var sections = Sections(Read("features.html"));

    var library = Text(sections.Single(s => s.Id == "library-title").Html);
    Assert.Contains("where the server has a TMDB key", library);
    Assert.Contains("cleaned title and its year", library);

    foreach (var (id, html) in sections.Where(s => s.Id != "library-title"))
    {
      var text = Text(html);
      var mentions = Regex.IsMatch(text, @"\bposters?\b|\bcards?\b", RegexOptions.IgnoreCase);
      Assert.False(mentions && !text.Contains("TMDB"), $"Section {id} mentions posters or cards without TMDB.");
    }
  }

  [Fact]
  public void Page_NamesThePlayerLinkFallback()
  {
    Assert.Contains("the last list that worked", Read("features.html"));
  }

  [Theory]
  [InlineData("Refresh every 6 hours", "Refresh every 6 hours")]
  [InlineData("Watchlist group", "Watchlist group")]
  [InlineData("Hide adult groups", "Hide adult groups")]
  [InlineData("Duplicates &amp; variants", "Duplicates & variants")]
  [InlineData("Include switched-off groups", "Include switched-off groups")]
  public void Page_NamesTheControl_AsTheAppLabelsIt(string inPage, string inApp)
  {
    Assert.Contains(inPage, Read("features.html"));
    Assert.True(Read("main.js").Contains(inApp) || Read("library.js").Contains(inApp),
      $"Neither main.js nor library.js has the label \"{inApp}\".");
  }

  [Fact]
  public void Page_HasNoInlineStyleOrScript_AndLinksTheRepository()
  {
    var html = Read("features.html");

    Assert.DoesNotContain("style=", html);
    Assert.DoesNotContain("<script", html);
    Assert.DoesNotContain("<style", html);
    Assert.Contains("https://github.com/plis-labs/strim", html);
  }
}
