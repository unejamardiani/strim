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

  private sealed record SitemapEntry(int Index, List<string> Locs, List<string> Lastmods)
  {
    public string Name => Locs.Count == 0 ? $"<url> #{Index}" : string.Join(" + ", Locs);
  }

  /// <summary>Every <c>&lt;url&gt;</c> with all its <c>&lt;loc&gt;</c> and <c>&lt;lastmod&gt;</c> values, so a second one cannot hide.</summary>
  private static List<SitemapEntry> SitemapEntries() =>
    Urlset().Elements(SitemapNs + "url")
      .Select((u, i) => new SitemapEntry(i + 1,
        u.Elements(SitemapNs + "loc").Select(e => e.Value.Trim()).ToList(),
        u.Elements(SitemapNs + "lastmod").Select(e => e.Value.Trim()).ToList()))
      .ToList();

  [Fact]
  public void Sitemap_IsWellFormed_InTheSitemapNamespace()
  {
    var root = Urlset();

    Assert.True(root.Name == SitemapNs + "urlset", $"sitemap root is {root.Name}, expected {{{SitemapNs}}}urlset");
  }

  [Fact]
  public void Sitemap_ListsEveryPublicPage_AndNothingElse()
  {
    var entries = SitemapEntries();
    foreach (var entry in entries)
      Assert.True(entry.Locs.Count == 1, $"sitemap {entry.Name}: {entry.Locs.Count} <loc> elements, expected exactly one");

    var locs = entries.SelectMany(e => e.Locs).ToList();
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
      var canonicals = CanonicalHrefs(Read(page));
      Assert.True(canonicals.Count == 1,
        $"{page} has {canonicals.Count} active <link rel=\"canonical\">, expected exactly one");
      Assert.True(canonicals[0] == UrlOf(page),
        $"{page}: canonical {canonicals[0]} differs from its sitemap loc {UrlOf(page)}");
    }
  }

  /// <summary>
  /// The <c>href</c> of every <c>&lt;link&gt;</c> outside HTML comments whose <c>rel</c> names <c>canonical</c>, in
  /// any attribute order.
  /// </summary>
  private static List<string> CanonicalHrefs(string html)
  {
    var active = Regex.Replace(html, "<!--.*?-->", "", RegexOptions.Singleline);
    var hrefs = new List<string>();
    foreach (Match link in Regex.Matches(active, @"<link\b([^>]*)>", RegexOptions.IgnoreCase))
    {
      var attributes = new Dictionary<string, string>();
      foreach (Match a in Regex.Matches(link.Groups[1].Value, @"([\w-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')"))
        attributes.TryAdd(a.Groups[1].Value.ToLowerInvariant(), a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value);

      var isCanonical = attributes.TryGetValue("rel", out var rel)
        && rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("canonical", StringComparer.OrdinalIgnoreCase);
      if (isCanonical)
        hrefs.Add(attributes.GetValueOrDefault("href", "").Trim());
    }
    return hrefs;
  }

  [Fact]
  public void EveryLastmod_IsAValidDate_NotInTheFuture()
  {
    var today = DateOnly.FromDateTime(DateTime.UtcNow);

    foreach (var entry in SitemapEntries())
    {
      Assert.True(entry.Lastmods.Count == 1,
        $"{entry.Name}: {entry.Lastmods.Count} <lastmod> elements, expected exactly one");
      foreach (var lastmod in entry.Lastmods)
      {
        Assert.True(DateOnly.TryParseExact(lastmod, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date),
          $"{entry.Name}: lastmod \"{lastmod}\" is not a yyyy-MM-dd date");
        Assert.True(date <= today, $"{entry.Name}: lastmod {lastmod} is in the future");
      }
    }
  }

  [Fact]
  public void Robots_LetsCrawlersIndexThePages_ButNotTheAppOrTheApi()
  {
    var directives = RobotsDirectives(Read("robots.txt"));
    var wildcardRules = WildcardGroupRules(directives);

    Assert.True(wildcardRules is not null, "robots.txt has no \"User-agent: *\" group");
    Assert.Contains(("disallow", "/app"), wildcardRules);
    Assert.Contains(("disallow", "/api/"), wildcardRules);
    Assert.DoesNotContain(("disallow", "/"), wildcardRules);
    Assert.Contains(("sitemap", $"{Origin}/sitemap.xml"), directives);
  }

  /// <summary>The robots.txt directives as (lower-case name, value), with comments and blank lines removed.</summary>
  private static List<(string Name, string Value)> RobotsDirectives(string robots) =>
    robots.Split('\n')
      .Select(l => l.Split('#')[0].Trim())
      .Where(l => l.Contains(':'))
      .Select(l => (l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l[(l.IndexOf(':') + 1)..].Trim()))
      .ToList();

  /// <summary>
  /// The allow and disallow rules of the group that applies to every crawler. Consecutive <c>User-agent</c> lines open
  /// a group; its rules run until the next <c>User-agent</c> line that follows a rule. Null when no group names <c>*</c>.
  /// </summary>
  private static List<(string Name, string Value)>? WildcardGroupRules(List<(string Name, string Value)> directives)
  {
    List<(string Name, string Value)>? wildcard = null;
    var rules = new List<(string Name, string Value)>();
    var inRules = false;
    foreach (var directive in directives)
    {
      if (directive.Name == "user-agent")
      {
        if (inRules)
        {
          rules = [];
          inRules = false;
        }
        if (directive.Value == "*")
          wildcard = rules;
      }
      else if (directive.Name is "allow" or "disallow")
      {
        inRules = true;
        rules.Add(directive);
      }
    }
    return wildcard;
  }

  [Fact]
  public void DockerfileCopyStep_ShipsEveryPage_AndTheCrawlerFiles()
  {
    var copied = WwwrootCopySources(Read("Dockerfile"));
    var pages = PublicPages().Concat(NonPublicPages).ToHashSet();

    var missing = pages.Append("robots.txt").Append("sitemap.xml").Where(f => !copied.Contains(f)).ToList();
    Assert.True(missing.Count == 0, $"the Dockerfile copy step does not ship to api/wwwroot/: {string.Join(", ", missing)}");
    var stale = copied.Where(f => f.EndsWith(".html", StringComparison.Ordinal) && !pages.Contains(f)).ToList();
    Assert.True(stale.Count == 0, $"the Dockerfile copy step names pages that do not exist: {string.Join(", ", stale)}");
  }

  /// <summary>The source arguments of every <c>cp</c> command whose destination is exactly <c>api/wwwroot/</c>.</summary>
  private static HashSet<string> WwwrootCopySources(string dockerfile) =>
    dockerfile.Split('\n')
      .Select(l => l.Trim().TrimEnd('\\').Trim())
      .Select(l => l.EndsWith("&&", StringComparison.Ordinal) ? l[..^2].Trim() : l)
      .Where(l => l.StartsWith("cp ", StringComparison.Ordinal))
      .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
      .Where(args => args[^1] == "api/wwwroot/")
      .SelectMany(args => args[1..^1])
      .ToHashSet();
}
