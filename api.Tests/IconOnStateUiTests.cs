using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the icons' on state (#494) at its source in theme.css and library.css, as the other UI tests do
/// (api.Tests has no JS engine): the current tab and a pressed mark fill the glyph's body and light its dot.</summary>
public class IconOnStateUiTests
{
  private const string BodyRule =
    "[aria-current=\"page\"] > .icon .b, [aria-pressed=\"true\"] > .icon .b { fill: currentColor; fill-opacity: .14; }";
  private const string DotRule =
    "[aria-current=\"page\"] > .icon .sig, [aria-pressed=\"true\"] > .icon .sig { fill: var(--accent); transform-box: fill-box; transform-origin: center; transform: scale(1.25); }";

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  [Fact]
  public void OnState_FillsTheBodyAndLightsTheDot()
  {
    var theme = Read("theme.css");
    var sig = theme.IndexOf(".icon .sig {", StringComparison.Ordinal);
    Assert.True(sig >= 0, "The .icon .sig rule is missing from theme.css.");
    var body = theme.IndexOf(BodyRule, StringComparison.Ordinal);
    var dot = theme.IndexOf(DotRule, StringComparison.Ordinal);
    Assert.True(body > sig, "The on-state body rule must exist after '.icon .sig'.");
    Assert.True(dot > sig, "The on-state dot rule must exist after '.icon .sig'.");
  }

  [Fact]
  public void TabGlyphs_HaveABodyToFill()
  {
    var js = Read("main.js");
    foreach (var name in new[] { "home", "search", "grid", "marks" })
    {
      var glyph = Regex.Match(js, $@"^  {name}: '([^']*)',$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
      Assert.True(glyph.Success, $"The '{name}' glyph is missing from ICONS.");
      Assert.Contains("class=\"b\"", glyph.Groups[1].Value);
    }
  }

  [Fact]
  public void CurrentTab_IsHeavierOnA16PercentPill()
  {
    var css = Read("library.css");
    var phone = Regex.Match(css, @"^\.lib-tabs a\[aria-current=""page""\] \{[^}]*\}", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    Assert.True(phone.Success, "The phone rule of the current tab is missing.");
    Assert.Contains("color-mix(in srgb, var(--dock-ink) 16%, transparent)", phone.Value);
    Assert.Contains("font-weight: 800;", phone.Value);
  }

  [Fact]
  public void OnDot_HasNoRingOrHole()
  {
    foreach (var file in new[] { "theme.css", "library.css" })
    {
      var css = Read(file);
      Assert.DoesNotContain("--icon-on-bg", css);
      Assert.DoesNotContain("paint-order", css);
    }
  }

  [Fact]
  public void OnDot_StaysTangerine()
  {
    // Forced colours (#497) use the system palette only, so their block is the one place the dot is not tangerine.
    var forced = new Regex(@"@media \(forced-colors: active\) \{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", RegexOptions.CultureInvariant);
    foreach (var file in new[] { "theme.css", "library.css" })
      Assert.Empty(CurrentDotFillsOtherThanAccent(forced.Replace(Read(file), "")));
  }

  [Fact]
  public void OnDot_CheckFindsOverridesInsideMediaQueries()
  {
    const string nested = "@media (min-width: 768px) {\n  .lib-tabs a[aria-current=\"page\"] .icon .sig { fill: var(--ink); }\n}\n";
    const string first = "@media (min-width: 768px) { .x { color: red; } .lib-tabs a[aria-current=\"page\"] .sig { fill: none; } }";
    const string plain = "/* a { } */ .a { color: red; }\n[aria-current=\"page\"] > .icon .sig { fill: currentColor; }";
    const string fine = "@media (min-width: 768px) { [aria-current=\"page\"] > .icon .sig { fill: var(--accent); } }";
    Assert.Equal(["var(--ink)"], CurrentDotFillsOtherThanAccent(nested));
    Assert.Equal(["none"], CurrentDotFillsOtherThanAccent(first));
    Assert.Equal(["currentColor"], CurrentDotFillsOtherThanAccent(plain));
    Assert.Empty(CurrentDotFillsOtherThanAccent(fine));
  }

  // Every fill other than var(--accent) set by a rule whose selector holds [aria-current="page"] and .sig, including
  // rules nested in @media or other at-rule blocks.
  private static List<string> CurrentDotFillsOtherThanAccent(string css)
  {
    var fill = new Regex(@"(?<![\w-])fill\s*:\s*([^;}]+)", RegexOptions.CultureInvariant);
    var found = new List<string>();
    foreach (var (selector, body) in Rules(css))
    {
      if (!selector.Contains("[aria-current=\"page\"]", StringComparison.Ordinal) || !selector.Contains(".sig", StringComparison.Ordinal))
        continue;
      foreach (Match f in fill.Matches(body))
        if (f.Groups[1].Value.Trim() != "var(--accent)")
          found.Add(f.Groups[1].Value.Trim());
    }
    return found;
  }

  // Style rules as (selector, declarations); an at-rule block ("@media … {") is entered, not taken as a rule.
  private static IEnumerable<(string Selector, string Body)> Rules(string css)
  {
    css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
    var start = 0;
    for (var i = 0; i < css.Length; i++)
    {
      if (css[i] == '}')
      {
        start = i + 1;
        continue;
      }
      if (css[i] != '{')
        continue;
      var prelude = css[start..i].Trim();
      if (prelude.StartsWith('@'))
      {
        start = i + 1;
        continue;
      }
      var close = css.IndexOf('}', i + 1);
      Assert.True(close > i, $"Unclosed rule '{prelude}'.");
      yield return (prelude, css[(i + 1)..close]);
      i = close;
      start = close + 1;
    }
  }

  [Fact]
  public void CurrentTab_IconIsNotRecoloured()
  {
    var css = Read("library.css");
    Assert.DoesNotContain(".lib-tabs a[aria-current=\"page\"] svg { color: var(--accent); }", css);
    Assert.DoesNotContain(".lib-tabs a[aria-current=\"page\"] svg { color: inherit; }", css);
  }

  [Fact]
  public void PressedBookmark_StaysSolid()
  {
    var css = Read("library.css");
    var solid = css.IndexOf(".lib-watch[aria-pressed=\"true\"] .b { fill-opacity: 1; }", StringComparison.Ordinal);
    Assert.True(solid >= 0, "The pressed bookmark's solid body rule is missing.");
    Assert.True(solid > css.IndexOf(".lib-watch[aria-pressed=\"true\"] .sig {", StringComparison.Ordinal));
  }
}
