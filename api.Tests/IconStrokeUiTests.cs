using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the line-icon rule (#481) at its source in main.js, theme.css, app.css and library.css, as the other
/// UI tests do (api.Tests has no JS engine): every icon <c>icon()</c> builds carries <c>class="icon"</c>, which draws
/// a currentColor stroke and yields to every holder rule.</summary>
public class IconStrokeUiTests
{
  private const string IconRule =
    ".icon { fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }";

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  [Fact]
  public void Icon_CarriesTheIconClass()
  {
    Assert.Contains(
      "tpl.innerHTML = `<svg class=\"icon\" viewBox=\"0 0 24 24\" aria-hidden=\"true\">${ICONS[name]}</svg>`;",
      Read("main.js"));
  }

  [Fact]
  public void IconClass_DrawsAStrokeAndYieldsToEveryHolderRule()
  {
    var theme = Read("theme.css");
    var rule = theme.IndexOf(IconRule, StringComparison.Ordinal);
    Assert.True(rule >= 0, "The .icon rule is missing from theme.css.");
    var holder = theme.IndexOf(".btn svg,", StringComparison.Ordinal);
    Assert.True(holder > rule, "The .icon rule must come before '.btn svg,'.");

    var importantIconRule = new Regex(@"\.icon(?![\w-])[^{}]*\{[^}]*!important", RegexOptions.CultureInvariant);
    foreach (var file in new[] { "theme.css", "app.css", "library.css" })
    {
      var css = Read(file);
      Assert.DoesNotContain("svg.icon", css);
      Assert.False(importantIconRule.IsMatch(css), $"{file} has an .icon rule with !important.");
    }
  }

  [Fact]
  public void IntendedFills_Stay()
  {
    var library = Read("library.css");
    Assert.Contains(".lib-art-watch svg { width: 12px; height: 12px; fill: currentColor; }", library);
    Assert.Contains(".lib-watch[aria-pressed=\"true\"] svg { fill: currentColor; }", library);
    var rating = Regex.Match(library, @"\.film-rating svg \{[^}]*\}", RegexOptions.CultureInvariant);
    Assert.True(rating.Success, "The .film-rating svg rule is missing.");
    Assert.Contains("fill: var(--movies)", rating.Value);
  }
}
