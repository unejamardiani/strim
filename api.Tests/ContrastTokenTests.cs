using System.Globalization;
using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Computes WCAG 2.2 contrast of the Signal Paper text tokens from theme.css (#488), so a later palette change
/// fails only when it breaks AA, not when a hex value moves.</summary>
public class ContrastTokenTests
{
  private const string FieldGrey = "#857a6d";
  private static readonly string[] Surfaces = ["--bg", "--surface", "--surface-2", "--surface-3"];

  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static Dictionary<string, string> Tokens(string block)
  {
    var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (Match m in Regex.Matches(block, @"(--[\w-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;", RegexOptions.CultureInvariant))
      tokens[m.Groups[1].Value] = m.Groups[2].Value;
    return tokens;
  }

  private static Dictionary<string, string> Light()
  {
    var theme = Read("theme.css");
    var start = theme.IndexOf(":root {", StringComparison.Ordinal);
    Assert.True(start >= 0, "theme.css has no :root block.");
    return Tokens(theme[start..theme.IndexOf('}', start)]);
  }

  private static Dictionary<string, string> Dark()
  {
    var theme = Read("theme.css");
    var media = theme.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal);
    Assert.True(media >= 0, "theme.css has no dark block.");
    var start = theme.IndexOf(":root {", media, StringComparison.Ordinal);
    return Tokens(theme[start..theme.IndexOf('}', start)]);
  }

  /// <summary>Every declaration (token or <c>color-scheme</c>) of the block that opens with <paramref name="head"/>.</summary>
  private static Dictionary<string, string> Declarations(string css, int head)
  {
    var start = css.IndexOf('{', head) + 1;
    var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
    var block = Regex.Replace(css[start..css.IndexOf('}', start)], @"/\*.*?\*/", "", RegexOptions.Singleline);
    foreach (Match m in Regex.Matches(block, @"([\w-]+)\s*:\s*([^;]+);", RegexOptions.CultureInvariant))
      declarations[m.Groups[1].Value] = m.Groups[2].Value.Trim();
    return declarations;
  }

  /// <summary>The forced set (#580) that theme-boot.js switches on with <c>data-theme</c>.</summary>
  private static Dictionary<string, string> ForcedDeclarations(string theme)
  {
    var css = Read("theme.css");
    var head = css.IndexOf($":root[data-theme=\"{theme}\"] {{", StringComparison.Ordinal);
    Assert.True(head >= 0, $"theme.css has no forced {theme} block.");
    return Declarations(css, head);
  }

  private static Dictionary<string, string> ForcedTokens(string theme) =>
    ForcedDeclarations(theme).Where(d => Regex.IsMatch(d.Value, "^#[0-9a-fA-F]{6}$")).ToDictionary(d => d.Key, d => d.Value);

  private static IEnumerable<(string Name, Dictionary<string, string> Tokens)> LightThemes() =>
    [("light", Light()), ("forced light", ForcedTokens("light"))];

  private static IEnumerable<(string Name, Dictionary<string, string> Tokens)> DarkThemes() =>
    [("dark", Dark()), ("forced dark", ForcedTokens("dark"))];

  private static IEnumerable<(string Name, Dictionary<string, string> Tokens)> Themes() => LightThemes().Concat(DarkThemes());

  [Fact]
  public void ForcedSets_EqualTheSystemSets()
  {
    var css = Read("theme.css");
    var dark = Declarations(css, css.IndexOf(":root {", css.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal), StringComparison.Ordinal));
    Assert.Equal(dark, ForcedDeclarations("dark"));
    Assert.Equal("dark", dark["color-scheme"]);

    // The forced light set holds exactly what the dark media query replaces, with the base light values.
    var light = ForcedDeclarations("light");
    Assert.Equal(dark.Keys.Order(), light.Keys.Order());
    var baseLight = Declarations(css, css.IndexOf(":root {", StringComparison.Ordinal));
    foreach (var (name, value) in light)
      Assert.True(baseLight.TryGetValue(name, out var expected) && expected == value, $"forced light {name} is {value}, the light set has {expected}.");
  }

  private static double Luminance(string hex)
  {
    double Channel(int i)
    {
      var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
      return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
    return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
  }

  private static double Contrast(string a, string b)
  {
    var (la, lb) = (Luminance(a), Luminance(b));
    return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
  }

  private static void AssertAtLeast(double min, string theme, Dictionary<string, string> t, string fg, string bg)
  {
    var ratio = Contrast(t[fg], t[bg]);
    Assert.True(ratio >= min, $"{theme} {fg} on {bg} is {ratio:F2}:1, below {min}:1.");
  }

  [Fact]
  public void InkLevels_ReachAaOnEverySurface()
  {
    foreach (var (name, t) in Themes())
      foreach (var ink in new[] { "--ink", "--ink-2", "--ink-3" })
        foreach (var surface in Surfaces)
          AssertAtLeast(4.5, name, t, ink, surface);
  }

  [Fact]
  public void AccentInk_ReachesAaOnPaperAndOnItsSoftFill()
  {
    foreach (var (name, t) in LightThemes())
      foreach (var bg in new[] { "--bg", "--surface", "--surface-2", "--accent-soft" })
        AssertAtLeast(4.5, name, t, "--accent-ink", bg);
    foreach (var (name, t) in DarkThemes())
      foreach (var bg in new[] { "--bg", "--surface", "--surface-2" })
        AssertAtLeast(4.5, name, t, "--accent-ink", bg);
  }

  [Fact]
  public void FocusRing_Has3To1OnEverySurface()
  {
    foreach (var (name, t) in Themes())
      foreach (var surface in Surfaces.Append("--dock-bg"))
        AssertAtLeast(3, name, t, "--focus", surface);
  }

  [Fact]
  public void Edge_Has3To1OnEverySurface()
  {
    foreach (var (name, t) in Themes())
    {
      Assert.True(t.ContainsKey("--edge"), $"theme.css {name} block has no --edge token.");
      foreach (var surface in Surfaces)
        AssertAtLeast(3, name, t, "--edge", surface);
    }
  }

  [Fact]
  public void Accent_CarriesItsLabel()
  {
    foreach (var (name, t) in Themes())
      foreach (var fill in new[] { "--accent", "--accent-hover" })
        AssertAtLeast(4.5, name, t, "--on-accent", fill);
  }

  [Fact]
  public void Accent_ReadsAsLargeTextOnPaper()
  {
    foreach (var (name, t) in Themes())
      foreach (var bg in new[] { "--bg", "--surface" })
        AssertAtLeast(3, name, t, "--accent", bg);
  }

  [Fact]
  public void LiveMarker_FollowsTheAccent()
  {
    foreach (var (_, light) in LightThemes())
      Assert.Equal(light["--accent"], light["--live"]);
  }

  [Fact]
  public void TypeMarkers_Have3To1()
  {
    foreach (var (name, t) in Themes())
    {
      foreach (var marker in new[] { "--live", "--movies", "--series" })
        AssertAtLeast(3, name, t, marker, "--surface");
      foreach (var bg in new[] { "--bg", "--surface-2" })
        AssertAtLeast(3, name, t, "--movies", bg);
    }
    // In dark, --movies-soft is an rgba tint and is skipped.
    foreach (var (name, light) in LightThemes())
      AssertAtLeast(3, name, light, "--movies", "--movies-soft");
  }

  [Fact]
  public void InkLevels_StayApart()
  {
    foreach (var (name, t) in Themes())
    {
      var ink2 = Contrast(t["--ink-2"], t["--bg"]);
      var ink3 = Contrast(t["--ink-3"], t["--bg"]);
      Assert.True(ink2 >= 1.25 * ink3, $"{name} --ink-2 ({ink2:F2}) is less than 1.25 times --ink-3 ({ink3:F2}) on --bg.");
    }
  }

  [Fact]
  public void FieldGlyphs_UseTheSharedEdgeGrey()
  {
    var app = Read("app.css");
    Assert.True(Regex.Matches(app, Regex.Escape("%23857a6d")).Count >= 2, "The field glyphs must use the shared edge grey.");
    foreach (var selector in new[] { @"\.input-search", @"select\.input" })
    {
      var rule = Regex.Match(app, selector + @" \{[^}]*\}", RegexOptions.CultureInvariant);
      Assert.True(rule.Success, $"app.css has no {selector} rule.");
      Assert.Contains("stroke='%23857a6d'", rule.Value);
    }
    Assert.DoesNotContain("%239a9084", app);
    foreach (var (name, t) in Themes())
      foreach (var bg in new[] { "--bg", "--surface", "--surface-2" })
      {
        var ratio = Contrast(FieldGrey, t[bg]);
        Assert.True(ratio >= 3, $"Field glyph grey on {name} {bg} is {ratio:F2}:1, below 3:1.");
      }
  }

  [Fact]
  public void PillAndChipCounts_AreNotDimmed()
  {
    var app = Read("app.css");
    var rule = Regex.Match(app, @"\.pills \.count \{[^}]*\}", RegexOptions.CultureInvariant);
    Assert.True(rule.Success, "app.css has no .pills .count rule.");
    Assert.Contains("color: var(--ink-2);", rule.Value);
    Assert.Contains(".lib-chip .n { font-weight: 500; font-variant-numeric: tabular-nums; }", Read("library.css"));
  }
}
