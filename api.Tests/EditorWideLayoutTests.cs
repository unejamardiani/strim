using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the wide editor rule (#216): DESIGN.md states it, app.css implements it with a media query
/// alone, and below the breakpoint the wrappers produce no box so the 760px column is unchanged.
/// </summary>
public class EditorWideLayoutTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  [Fact]
  public void DesignMd_StatesBreakpoint_MaxWidth_Split_AndNarrowFallback()
  {
    var design = Read("DESIGN.md");
    Assert.Contains("Wide editor (from 1100px", design);
    Assert.Contains("1440px maximum", design);
    Assert.Contains("up\nto 560px", design.Replace("\r\n", "\n"));
    Assert.Contains("entry pane", design);
    Assert.Contains("the 760px page applies unchanged", design);
  }

  [Fact]
  public void AppCss_BelowBreakpoint_WrappersHaveNoBox_AndPaneIsHidden()
  {
    var css = Read("app.css");
    var outsideMedia = Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "");
    Assert.Matches(@"\.editor-split,\s*\.editor-groups\s*\{\s*display:\s*contents;", outsideMedia);
    Assert.Matches(@"\.entry-pane\s*\{\s*display:\s*none;", outsideMedia);
  }

  [Fact]
  public void AppCss_FromBreakpoint_PlacesGroupsAndPaneSideBySide()
  {
    var css = Read("app.css");
    var media = Regex.Match(css, @"@media \(min-width: 1100px\)\s*\{((?:[^{}]*\{[^{}]*\})*)[^{}]*\}");
    Assert.True(media.Success, "app.css has no @media (min-width: 1100px) block.");
    var body = media.Groups[1].Value;
    Assert.Matches(@"\.page:has\(> \.editor-split\)\s*\{\s*max-width:\s*1440px;", body);
    Assert.Matches(@"\.editor-split\s*\{[^}]*display:\s*grid;[^}]*grid-template-columns:\s*minmax\(0, 560px\) minmax\(0, 1fr\);", body);
    Assert.Matches(@"\.entry-pane\s*\{[^}]*display:\s*block;", body);
  }

  [Fact]
  public void Editor_WrapsPickerInSplit_WithEmptyPane_AndNoInlineStyle()
  {
    var js = Read("main.js");
    Assert.Contains("h('div', { class: 'editor-split' },", js);
    Assert.Contains("h('div', { class: 'editor-groups' }, ...pickerNodes(ed))", js);
    Assert.Contains("h('aside', { class: 'entry-pane', 'aria-hidden': 'true' })", js);
    Assert.DoesNotContain("style=", Read("app.html"));
  }
}
