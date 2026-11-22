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
    var rule = new Regex(@"([^{}]+)\{([^}]*)\}", RegexOptions.CultureInvariant);
    var fill = new Regex(@"(?<![\w-])fill\s*:\s*([^;}]+)", RegexOptions.CultureInvariant);
    foreach (var file in new[] { "theme.css", "library.css" })
    {
      foreach (Match m in rule.Matches(Read(file)))
      {
        var selector = m.Groups[1].Value;
        if (!selector.Contains("[aria-current=\"page\"]", StringComparison.Ordinal) || !selector.Contains(".sig", StringComparison.Ordinal))
          continue;
        foreach (Match f in fill.Matches(m.Groups[2].Value))
          Assert.Equal("var(--accent)", f.Groups[1].Value.Trim());
      }
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
