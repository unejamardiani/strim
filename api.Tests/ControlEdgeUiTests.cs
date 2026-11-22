using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the --edge boundary of fields, switches and the selected pill segment (#491): a control that is told
/// apart only by its fill needs an edge with 3:1 on its surroundings.</summary>
public class ControlEdgeUiTests
{
  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  /// <summary>The bodies of every rule whose selector list equals <paramref name="selector"/>, whitespace collapsed.</summary>
  private static string Rule(string css, string selector)
  {
    css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    var bodies = Regex.Matches(css,@"([^{}]+)\{([^{}]*)\}", RegexOptions.CultureInvariant)
      .Where(m => Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ") == selector)
      .Select(m => m.Groups[2].Value)
      .ToList();
    Assert.True(bodies.Count > 0, $"No rule for '{selector}'.");
    return string.Join("\n", bodies);
  }

  [Fact]
  public void Fields_HaveAnEdge()
  {
    var app = Read("app.css");
    Assert.Contains("border: 1.5px solid var(--edge);", Rule(app, ".input"));
    Assert.Contains("border-color: transparent;", Rule(app, ".input-name"));
    Assert.Contains("border-color: var(--edge);", Rule(app, ".input-name:hover"));
    Assert.Contains("border-color: var(--focus);", Rule(app, ".input:focus"));
  }

  [Theory]
  [InlineData("app.css", ".toggle, .switch", ".toggle::after, .switch::after")]
  [InlineData("site.css", ".switch", ".switch::after")]
  public void SwitchOff_HasRingAndKnob(string file, string track, string knob)
  {
    var css = Read(file);
    Assert.Contains("box-shadow: inset 0 0 0 1.5px var(--edge);", Rule(css, track));
    var knobRule = Rule(css, knob);
    Assert.Contains("background: var(--edge);", knobRule);
    Assert.Contains("box-shadow: none;", knobRule);
  }

  [Theory]
  [InlineData("app.css", ".toggle:checked, .switch.on", ".toggle:checked::after, .switch.on::after")]
  [InlineData("site.css", ".switch.on", ".switch.on::after")]
  public void SwitchOn_KnobIsOnAccent(string file, string track, string knob)
  {
    var css = Read(file);
    Assert.Contains("box-shadow: none;", Rule(css, track));
    var knobRule = Rule(css, knob);
    Assert.Contains("background: var(--on-accent);", knobRule);
    Assert.DoesNotContain("background: #fffcf7;", knobRule);
  }

  [Fact]
  public void SelectedSegment_HasAnEdge()
  {
    var rule = Rule(Read("app.css"), ".pills button[aria-pressed=\"true\"]");
    Assert.Contains(
      "box-shadow: inset 0 0 0 1.5px var(--edge), 0 1px 2px rgba(29, 26, 22, 0.12), 0 4px 12px -6px rgba(29, 26, 22, 0.25);",
      rule);
  }
}
