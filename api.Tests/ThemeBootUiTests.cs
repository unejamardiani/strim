using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the early theme start (#580) at its source in theme-boot.js, app.html and the Dockerfile, as the
/// other UI tests do (api.Tests has no JS engine): the stored choice is applied before the stylesheets, only in the
/// editor, and the image ships the script.</summary>
public class ThemeBootUiTests
{
  private const string BootTag = "<script src=\"theme-boot.js\"></script>";

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  [Fact]
  public void AppHtml_LoadsTheBootScriptBeforeTheStylesheets()
  {
    var html = Read("app.html");
    var boot = html.IndexOf(BootTag, StringComparison.Ordinal);
    Assert.True(boot >= 0, "app.html must load theme-boot.js as a plain script without defer or type=module.");
    Assert.True(boot > html.IndexOf("Content-Security-Policy", StringComparison.Ordinal), "theme-boot.js must load after the CSP.");
    Assert.True(boot < html.IndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal), "theme-boot.js must load before the stylesheets.");
    Assert.True(boot < html.IndexOf("</head>", StringComparison.Ordinal));
  }

  [Theory]
  [InlineData("index.html")]
  [InlineData("features.html")]
  [InlineData("how-to-use.html")]
  [InlineData("comparison.html")]
  [InlineData("404.html")]
  [InlineData("500.html")]
  [InlineData("50x.html")]
  public void SitePages_FollowTheSystemOnly(string page) => Assert.DoesNotContain("theme-boot.js", Read(page));

  [Fact]
  public void BootScript_AppliesOnlyLightOrDark_AndGuardsStorage()
  {
    var js = Read("theme-boot.js");
    Assert.Contains("const key = \"strim.theme\";", js);
    Assert.Contains("const forced = (theme) => theme === \"light\" || theme === \"dark\";", js);
    Assert.Contains("if (forced(theme)) root.dataset.theme = theme;\n    else delete root.dataset.theme;", js);
    Assert.Matches(new Regex(@"try \{\n    show\(localStorage\.getItem\(key\)\);\n  \} catch"), js);
    Assert.Matches(new Regex(@"try \{\n        if \(forced\(theme\)\) localStorage\.setItem\(key, theme\);\n        else localStorage\.removeItem\(key\);\n      \} catch"), js);
    // One global, and no module syntax: a classic script runs while <head> is parsed.
    Assert.Single(Regex.Matches(js, @"window\.\w+"));
    Assert.Contains("window.strimTheme = {", js);
    Assert.DoesNotMatch(new Regex(@"^\s*(import|export)\b", RegexOptions.Multiline), js);
  }

  [Fact]
  public void Image_ShipsAndVersionsTheBootScript()
  {
    var dockerfile = Read("Dockerfile");
    Assert.Matches(new Regex(@"cp [^\n]*\*\.js[^\n]* api/wwwroot/"), dockerfile);
    Assert.Contains("for f in *.css *.js; do", dockerfile);
    Assert.True(File.Exists(Path.Combine(StrimAppFactory.RepositoryRoot(), "theme-boot.js")));
  }
}
