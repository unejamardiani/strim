using System.Globalization;
using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the comparison page to what Strim does today and the site's leftovers to the current repository (#595):
/// duplicates are reported, not removed; the Library and the last list that worked are named; every repository
/// link points to plis-labs/strim; the sitemap dates are current.
/// </summary>
public class ComparisonPageTests
{
  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string Text(string html) => Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ");

  /// <summary>The body of the card whose heading is <paramref name="heading"/>.</summary>
  private static string Card(string html, string heading)
  {
    var match = Regex.Match(html, $@"<article class=""card"">\s*<h3>{Regex.Escape(heading)}</h3>(.*?)</article>", RegexOptions.Singleline);
    Assert.True(match.Success, $"card \"{heading}\" missing");
    return Text(match.Groups[1].Value);
  }

  [Fact]
  public void DoesNotCard_ReportsDuplicates_InsteadOfRemovingThem()
  {
    var card = Card(Read("comparison.html"), "Strim does not");

    Assert.DoesNotContain("Find or remove duplicate channels", card);
    Assert.Contains("Remove duplicates", card);
    Assert.Contains("only reports them", card);
  }

  [Fact]
  public void DoesCard_NamesTheLibrary_AndTheLastListThatWorked()
  {
    var card = Card(Read("comparison.html"), "Strim does");

    Assert.Contains("Library", card);
    Assert.Contains("last list that worked", card);
  }

  [Fact]
  public void Page_NoLongerClaimsStrimDoesOneThing()
  {
    Assert.DoesNotContain("one thing", Text(Read("comparison.html")), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Page_HasNoInlineStylesOrScripts()
  {
    var html = Read("comparison.html");

    Assert.DoesNotContain("style=", html);
    Assert.DoesNotContain("<script", html);
    Assert.DoesNotContain("<style", html);
  }

  [Fact]
  public void EveryRootHtmlFile_LinksTheRepositoryAtPlisLabs()
  {
    var files = Directory.GetFiles(StrimAppFactory.RepositoryRoot(), "*.html");
    Assert.NotEmpty(files);

    foreach (var file in files)
    {
      var html = File.ReadAllText(file);
      var name = Path.GetFileName(file);

      Assert.False(html.Contains("github.com/unejamardiani"), $"{name} still links github.com/unejamardiani");
      foreach (Match link in Regex.Matches(html, @"github\.com/[^/""\s]+/strim\b"))
        Assert.True(link.Value == "github.com/plis-labs/strim", $"{name} links {link.Value}");
    }
  }

  [Fact]
  public void NoMarkdownFile_InTheRootOrDocs_LinksTheOldRepository()
  {
    var root = StrimAppFactory.RepositoryRoot();
    var factoryDocs = Path.Combine(root, "docs", "factory") + Path.DirectorySeparatorChar;
    var files = Directory.GetFiles(root, "*.md")
      .Concat(Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
        .Where(file => !file.StartsWith(factoryDocs, StringComparison.Ordinal)))
      .ToList();
    Assert.Contains(files, file => Path.GetFileName(file) == "SECURITY.md");

    foreach (var file in files)
      Assert.False(File.ReadAllText(file).Contains("github.com/unejamardiani"), $"{Path.GetRelativePath(root, file)} still links github.com/unejamardiani");
  }

  [Fact]
  public void Sitemap_ListsTheFourSitePages_WithCurrentDates()
  {
    var xml = Read("sitemap.xml");

    var pages = Regex.Matches(xml, "<loc>([^<]+)</loc>").Select(m => m.Groups[1].Value);
    Assert.Equal(
      ["https://strim.plis.dev/", "https://strim.plis.dev/features.html", "https://strim.plis.dev/how-to-use.html", "https://strim.plis.dev/comparison.html"],
      pages);

    var dates = Regex.Matches(xml, "<lastmod>([^<]+)</lastmod>").Select(m => DateOnly.ParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
    Assert.Equal(4, dates.Count);
    Assert.All(dates, d => Assert.True(d >= new DateOnly(2026, 11, 27), $"lastmod {d:yyyy-MM-dd} is older than 2026-11-27"));
  }
}
