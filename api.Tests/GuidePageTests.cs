using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins how-to-use.html as the walk from the provider link to the Library, the Watchlist group and sharing (#594):
/// eight steps, control labels in bold exactly as the app shows them, and the facts the guide may state.
/// </summary>
public class GuidePageTests
{
  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string Guide() =>
    Regex.Match(Read("how-to-use.html"), @"<ol class=""guide"">(.*?)\n      </ol>", RegexOptions.Singleline).Groups[1].Value;

  private static string Text(string html) => Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ");

  /// <summary>The steps of the guide: the li elements one level below the ol (6 spaces of indent, then 8).</summary>
  private static List<string> Steps(string guide) =>
    Regex.Matches(guide, @"\n        <li>(.*?)\n        </li>", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();

  [Fact]
  public void Guide_HasEightSteps_EachWithAHeading()
  {
    var steps = Steps(Guide());

    Assert.Equal(8, steps.Count);
    foreach (var step in steps)
      Assert.Matches(@"<h2>[^<]+</h2>", step);
  }

  [Theory]
  [InlineData("Load groups")]
  [InlineData("Try a sample")]
  [InlineData("Everything")]
  [InlineData("Hide adult groups")]
  [InlineData("All on")]
  [InlineData("All off")]
  [InlineData("Download")]
  [InlineData("Save")]
  [InlineData("Copy")]
  [InlineData("Refresh every 6 hours")]
  [InlineData("Turn link off")]
  [InlineData("Replace link")]
  [InlineData("Delete playlist")]
  [InlineData("Library")]
  [InlineData("Home")]
  [InlineData("Search")]
  [InlineData("Groups")]
  [InlineData("Marks")]
  [InlineData("Seen")]
  [InlineData("Add to watchlist")]
  [InlineData("Include switched-off groups")]
  [InlineData("Watchlist group")]
  [InlineData("Share")]
  [InlineData("Invite someone")]
  [InlineData("Create invite link")]
  [InlineData("Remove access")]
  public void Guide_NamesTheControl_InBold_AsTheAppLabelsIt(string label)
  {
    Assert.Contains($"<strong>{label}</strong>", Guide());
    Assert.True(Read("main.js").Contains(label) || Read("library.js").Contains(label),
      $"Neither main.js nor library.js has the label \"{label}\".");
  }

  [Fact]
  public void Guide_NamesTheSeriesWatchlist_TheInviteLimits_AndTheTmdbCondition()
  {
    var text = Text(Guide());

    Assert.Contains("“Watchlist · Series”", text);
    Assert.Contains("It works once and for 7 days", text);
    Assert.Contains("where the server has a TMDB key", text);
  }

  [Fact]
  public void Page_NoLongerOffersToCreateANewLink()
  {
    Assert.DoesNotContain("create a new link", Read("how-to-use.html"), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Page_HasNoInlineStyleOrScript_AndLinksTheRepository()
  {
    var html = Read("how-to-use.html");

    Assert.DoesNotContain("style=", html);
    Assert.DoesNotContain("<script", html);
    Assert.DoesNotContain("<style", html);
    Assert.Contains("https://github.com/plis-labs/strim", html);
  }
}
