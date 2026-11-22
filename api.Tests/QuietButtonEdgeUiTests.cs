using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the 1px --edge line of quiet buttons, the Library's icon buttons, chips and the account pill (#492):
/// their fill alone is 1.1 to 1.2:1 against the page, so the line is what shows the button's shape.</summary>
public class QuietButtonEdgeUiTests
{
  private const string Edge = "box-shadow: inset 0 0 0 1px var(--edge);";

  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  /// <summary>The bodies of every rule whose selector list equals <paramref name="selector"/>, whitespace collapsed.</summary>
  private static string Rule(string css, string selector)
  {
    css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    var bodies = Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}", RegexOptions.CultureInvariant)
      .Where(m => Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ") == selector)
      .Select(m => m.Groups[2].Value)
      .ToList();
    Assert.True(bodies.Count > 0, $"No rule for '{selector}'.");
    return string.Join("\n", bodies);
  }

  [Fact]
  public void QuietButtons_HaveAnEdge()
  {
    var theme = Read("theme.css");
    Assert.Contains(Edge, Rule(theme, ".btn"));
    Assert.Contains("box-shadow: none;", Rule(theme, ".btn-primary, .btn-ghost"));
  }

  [Fact]
  public void LibraryButtonsAndChips_HaveAnEdge()
  {
    var css = Read("library.css");
    Assert.Contains(Edge, Rule(css, ".lib-ib"));
    Assert.Contains("box-shadow: none;", Rule(css, ".lib-ib.is-quiet"));
    Assert.Contains(Edge, Rule(css, ".lib-chip"));
    Assert.Contains("box-shadow: none;", Rule(css, ".lib-chip[aria-pressed=\"true\"]"));
  }

  [Fact]
  public void AccountPillAndSaved()
  {
    var app = Read("app.css");
    Assert.Contains("border: 1px solid var(--edge);", Rule(app, ".account"));
    // The hover line must be darker than the resting --edge; --line-strong would be lighter.
    Assert.Contains("border-color: var(--ink-2);", Rule(app, ".account:hover"));
    Assert.Contains("box-shadow: none;", Rule(app, ".dock .btn.is-saved"));
  }
}
