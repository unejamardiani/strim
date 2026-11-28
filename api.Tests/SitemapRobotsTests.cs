using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Ties the public pages to <c>sitemap.xml</c>, <c>robots.txt</c> and the image's copy step (#625), so a page cannot
/// be added, renamed or dropped without all three following.
/// </summary>
public class SitemapRobotsTests
{
  private const string Origin = "https://strim.plis.dev";
  private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";
  private static readonly string[] NonPublicPages = ["app.html", "404.html", "500.html", "50x.html"];

  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  /// <summary>The <c>.html</c> files in the repository root that search engines may index.</summary>
  private static List<string> PublicPages() =>
    Directory.GetFiles(StrimAppFactory.RepositoryRoot(), "*.html")
      .Select(Path.GetFileName)
      .OfType<string>()
      .Where(f => !NonPublicPages.Contains(f))
      .Order(StringComparer.Ordinal)
      .ToList();

  private static string UrlOf(string page) => page == "index.html" ? $"{Origin}/" : $"{Origin}/{page}";

  private static XElement Urlset() => XDocument.Parse(Read("sitemap.xml")).Root!;

  private static List<string> Locs() =>
    Urlset().Elements(SitemapNs + "url").Select(u => u.Element(SitemapNs + "loc")?.Value.Trim() ?? "").ToList();

  [Fact]
  public void Sitemap_IsWellFormed_InTheSitemapNamespace()
  {
    var root = Urlset();

    Assert.True(root.Name == SitemapNs + "urlset", $"sitemap root is {root.Name}, expected {{{SitemapNs}}}urlset");
  }

  [Fact]
  public void Sitemap_ListsEveryPublicPage_AndNothingElse()
  {
    var locs = Locs();
    var expected = PublicPages().Select(UrlOf).ToList();

    var duplicates = locs.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    Assert.True(duplicates.Count == 0, $"sitemap lists more than once: {string.Join(", ", duplicates)}");
    var missing = expected.Except(locs).ToList();
    Assert.True(missing.Count == 0, $"sitemap misses: {string.Join(", ", missing)}");
    var extra = locs.Except(expected).ToList();
    Assert.True(extra.Count == 0, $"sitemap lists pages that are not public pages: {string.Join(", ", extra)}");
  }

  [Fact]
  public void EveryPublicPage_HasACanonicalUrl_EqualToItsSitemapLoc()
  {
    foreach (var page in PublicPages())
    {
      var match = Regex.Match(Read(page), @"<link rel=""canonical"" href=""([^""]+)""");
      Assert.True(match.Success, $"{page} has no <link rel=\"canonical\">");
      Assert.True(match.Groups[1].Value == UrlOf(page),
        $"{page}: canonical {match.Groups[1].Value} differs from its sitemap loc {UrlOf(page)}");
    }
  }

  [Fact]
  public void EveryLastmod_IsAValidDate_NotInTheFuture()
  {
    var today = DateOnly.FromDateTime(DateTime.UtcNow);

    foreach (var url in Urlset().Elements(SitemapNs + "url"))
    {
      var loc = url.Element(SitemapNs + "loc")?.Value.Trim();
      var lastmod = url.Element(SitemapNs + "lastmod")?.Value.Trim();
      Assert.True(lastmod is not null, $"{loc} has no <lastmod>");
      Assert.True(DateOnly.TryParseExact(lastmod, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date),
        $"{loc}: lastmod \"{lastmod}\" is not a yyyy-MM-dd date");
      Assert.True(date <= today, $"{loc}: lastmod {lastmod} is in the future");
    }
  }

  [Fact]
  public void Robots_LetsCrawlersIndexThePages_ButNotTheAppOrTheApi()
  {
    var lines = Read("robots.txt").Split('\n').Select(l => l.Trim()).ToList();

    Assert.Contains("User-agent: *", lines);
    Assert.Contains("Disallow: /app", lines);
    Assert.Contains("Disallow: /api/", lines);
    Assert.DoesNotContain("Disallow: /", lines);
    Assert.Contains($"Sitemap: {Origin}/sitemap.xml", lines);
  }

  [Fact]
  public void DockerfileCopyStep_ShipsEveryPage_AndTheCrawlerFiles()
  {
    var copied = Read("Dockerfile").Split('\n')
      .Select(l => l.Trim())
      .Where(l => l.StartsWith("cp ", StringComparison.Ordinal) && l.Contains("api/wwwroot/"))
      .SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
      .ToHashSet();
    var required = PublicPages().Concat(NonPublicPages).Append("robots.txt").Append("sitemap.xml");

    var missing = required.Where(f => !copied.Contains(f)).ToList();
    Assert.True(missing.Count == 0, $"the Dockerfile copy step does not ship: {string.Join(", ", missing)}");
  }
}
