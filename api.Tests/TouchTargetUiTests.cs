using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the invisible 44px phone touch targets of the small controls (#490): drawn sizes stay, ::after adds the target.</summary>
public class TouchTargetUiTests
{
  private const string PhoneQuery = "@media (max-width: 767px) {";
  private const string NarrowPhoneQuery = "@media (max-width: 379px) {";
  private const string ThreeRowQuery = "@media (max-width: 229px) {";

  private static readonly (string Selector, string Inset)[] EditorTargets =
  [
    (".link-box .btn", "-3px 0"),
    (".share-link .link-box .btn", "-5px 0"),
    (".pills button", "-4px 0"),
    (".bulk .btn", "-6px 0"),
    (".account", "-3px 0"),
    (".playlist-title a", "-7px min(0px, calc(100% - 44px)) -7px 0"),
    (".site-links > a", "-8px -5px -5px"),
    (".sharing-line", "-12px -8px"),
    (".adult-switch", "-11px 0 -2px"),
  ];

  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  /// <summary>The bodies of every phone-only media block (nesting one level of rules deep).</summary>
  private static List<string> PhoneBlocks(string css, string query = PhoneQuery)
  {
    var blocks = new List<string>();
    var at = 0;
    while ((at = css.IndexOf(query, at, StringComparison.Ordinal)) >= 0)
    {
      var start = at + query.Length;
      var depth = 1;
      var i = start;
      for (; i < css.Length && depth > 0; i++)
      {
        if (css[i] == '{') depth++;
        else if (css[i] == '}') depth--;
      }
      blocks.Add(css[start..(i - 1)]);
      at = i;
    }
    return blocks;
  }

  private static string AfterRule(string selector, string inset) =>
    $"{selector}::after {{ content: \"\"; position: absolute; inset: {inset}; }}";

  private static bool HasPositionRelative(string block, string selector)
  {
    foreach (Match m in Regex.Matches(block, @"([^{}]+)\{([^}]*)\}", RegexOptions.CultureInvariant))
    {
      var selectors = m.Groups[1].Value.Split(',').Select(s => s.Trim());
      if (selectors.Contains(selector) && m.Groups[2].Value.Contains("position: relative;"))
        return true;
    }
    return false;
  }

  [Fact]
  public void SectionLinks_Get44pxOnPhones()
  {
    var blocks = PhoneBlocks(Read("library.css"));
    Assert.Contains(blocks, b =>
      b.Contains(".lib-sec-head a, .lib-sec-head button { position: relative; }") &&
      b.Contains(".lib-sec-head a::after, .lib-sec-head button::after { content: \"\"; position: absolute; inset: -20px -14px -8px; }"));
  }

  [Fact]
  public void SmallButtons_Get44pxOnPhones()
  {
    var blocks = PhoneBlocks(Read("theme.css"));
    Assert.Contains(blocks, b =>
      b.Contains(".btn-sm { position: relative; }") && b.Contains(AfterRule(".btn-sm", "-3px 0")));
  }

  [Fact]
  public void EditorAndCardControls_Get44pxOnPhones()
  {
    var blocks = PhoneBlocks(Read("app.css"));
    var block = blocks.SingleOrDefault(b => b.Contains(AfterRule(".pills button", "-4px 0")));
    Assert.NotNull(block);
    foreach (var (selector, inset) in EditorTargets)
    {
      Assert.Contains(AfterRule(selector, inset), block);
      Assert.True(HasPositionRelative(block, selector), $"'{selector}' has no position: relative in the phone block.");
    }
  }

  [Fact]
  public void FooterTargets_SkipTheTmdbLogo_AndKeepWrappedRowsApart()
  {
    var css = Read("app.css");
    var block = PhoneBlocks(css).Single(b => b.Contains(AfterRule(".pills button", "-4px 0")));
    Assert.DoesNotContain(".site-links a::after", block);
    Assert.Contains("<a href=\"https://github.com/unejamardiani/strim\" rel=\"noopener\">Source</a>\n      <p id=\"tmdb-attribution\"",
      Read("app.html"));
    // The footer must not move: no gap, margin or padding change on the footer row.
    Assert.DoesNotContain(".site-links {", block);
    Assert.DoesNotContain("row-gap", block);
    // 31.5px links: 8px up and 5px down give 44.5px, short of the TMDB attribution 12px below; 5px sideways give the
    // 35.6px "Home" 45.6px and leave 8px of the 18px column gap free.
    // On narrow phones Source, and below about 305px Compare too, wrap 4px under the first row: Home, Features and
    // How to use reach 44.5px upwards, Compare and Source (the 4th and 5th link) downwards, and both stop in the
    // middle of the gap, so the rows' targets never overlap whichever of the two has wrapped.
    var narrow = Assert.Single(PhoneBlocks(css, NarrowPhoneQuery));
    Assert.Equal(
      AfterRule(".site-links > a", "-11px -5px -2px") + "\n  " + AfterRule(".site-links > a:nth-of-type(n+4)", "-2px -5px -11px"),
      narrow.Trim());
    var links = Regex.Matches(Read("app.html"), "\n      <a href=\"[^\"]+\"[^>]*>([^<]+)</a>")
      .Select(m => m.Groups[1].Value).ToArray();
    Assert.Equal(new[] { "Home", "Features", "How to use", "Compare", "Source" }, links);
    Assert.True(css.IndexOf(NarrowPhoneQuery, StringComparison.Ordinal) >
      css.IndexOf(AfterRule(".site-links > a", "-8px -5px -5px"), StringComparison.Ordinal),
      "The narrow-phone footer rule must come after the phone rule to override it.");
    // Below about 230px three rows: Home and Features, How to use and Compare, Source. The middle row has a row above
    // and below, so How to use and Compare stop 2px into both gaps instead of reaching 11px into a neighbour's row.
    var threeRows = Assert.Single(PhoneBlocks(css, ThreeRowQuery));
    Assert.Equal(
      ".site-links > a:nth-of-type(3)::after, " + AfterRule(".site-links > a:nth-of-type(4)", "-2px -5px"),
      threeRows.Trim());
    Assert.True(css.IndexOf(ThreeRowQuery, StringComparison.Ordinal) > css.IndexOf(NarrowPhoneQuery, StringComparison.Ordinal),
      "The three-row footer rule must come after the narrow-phone rule to override it.");
  }

  [Fact]
  public void Targets_AreNotDrawn()
  {
    var targets = EditorTargets.Select(t => t.Selector)
      .Concat([".btn-sm", ".lib-sec-head a", ".lib-sec-head button", ".site-links > a:nth-of-type(n+4)",
        ".site-links > a:nth-of-type(3)", ".site-links > a:nth-of-type(4)"])
      .Select(s => s + "::after")
      .ToHashSet();
    var found = new HashSet<string>();
    foreach (var file in new[] { "library.css", "theme.css", "app.css" })
    {
      // Comments would otherwise become part of the selector text and hide a painted rule.
      var css = Regex.Replace(Read(file), @"/\*.*?\*/", "", RegexOptions.Singleline | RegexOptions.CultureInvariant);
      foreach (var block in PhoneBlocks(css).Concat(PhoneBlocks(css, NarrowPhoneQuery)).Concat(PhoneBlocks(css, ThreeRowQuery)))
        foreach (Match m in Regex.Matches(block, @"([^{}]+)\{([^}]*)\}", RegexOptions.CultureInvariant))
        {
          var hit = m.Groups[1].Value.Split(',').Select(s => s.Trim()).Where(targets.Contains).ToList();
          if (hit.Count == 0) continue;
          found.UnionWith(hit);
          var body = m.Groups[2].Value;
          Assert.DoesNotContain("background", body);
          Assert.DoesNotContain("border", body);
          Assert.DoesNotContain("box-shadow", body);
          Assert.DoesNotContain("outline", body);
        }
    }
    Assert.Equal(targets.OrderBy(t => t, StringComparer.Ordinal), found.OrderBy(t => t, StringComparer.Ordinal));
  }
}
