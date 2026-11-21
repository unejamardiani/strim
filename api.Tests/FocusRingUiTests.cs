using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the keyboard focus rules to the --focus token (#489), so the ring keeps 3:1 on every surface.</summary>
public class FocusRingUiTests
{
  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string Rule(string source, string selector)
  {
    var m = Regex.Match(source, @"(?m)^" + Regex.Escape(selector) + @" \{[^}]*\}", RegexOptions.CultureInvariant);
    Assert.True(m.Success, $"No '{selector}' rule found.");
    return m.Value;
  }

  [Fact]
  public void FocusRules_UseTheFocusToken()
  {
    Assert.Contains("outline: 2px solid var(--focus);", Rule(Read("theme.css"), ":focus-visible"));
    var app = Read("app.css");
    Assert.Contains("outline: 2px solid var(--focus);", Rule(app, ".toast-action:focus-visible"));
    Assert.Contains("border-color: var(--focus);", Rule(app, ".input:focus"));
    Assert.Contains(
      ".lib-tile:focus-visible .lib-art { outline: 2px solid var(--focus); outline-offset: 2px; }",
      Read("library.css"));
  }

  [Fact]
  public void NoFocusRule_NamesTheAccent()
  {
    foreach (var file in new[] { "theme.css", "app.css", "library.css", "site.css" })
      Assert.DoesNotContain("outline: 2px solid var(--accent)", Read(file));
  }
}
