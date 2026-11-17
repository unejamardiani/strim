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
    var flat = design.Replace("\r\n", "\n").Replace("\n", " ");
    Assert.Contains("the site pages keep their 1080px main column and the guides their 700px column", flat);
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
    Assert.Contains("h('aside', { class: 'entry-pane', 'aria-label': 'Group entries', 'aria-hidden': 'true' })", js);
    Assert.DoesNotContain("style=", Read("app.html"));
  }

  /// <summary>The entry pane (#217) and the part of main.js it lives in.</summary>
  private static string PaneJs()
  {
    var js = Read("main.js");
    var start = js.IndexOf("// ---------- Entry pane (wide editor, #217)", StringComparison.Ordinal);
    var end = js.IndexOf("const MAX_SEARCH_RESULTS", StringComparison.Ordinal);
    Assert.True(start >= 0 && end > start, "main.js has no entry pane section before the search.");
    return js[start..end];
  }

  [Fact]
  public void EntryPane_OpensOnlyFromWideRowClicks_AndLeavesTheSwitchAlone()
  {
    var pane = PaneJs();
    Assert.Contains("const WIDE_EDITOR = window.matchMedia('(min-width: 1100px)');", pane);
    // The row is a <label>: outside the switch a wide click selects instead of toggling.
    Assert.Contains("if (!WIDE_EDITOR.matches || e.target.closest('.toggle')) return;", pane);
    Assert.Contains("e.preventDefault();", pane);
    var js = Read("main.js");
    Assert.Contains("picker.list.addEventListener('click', onRowSelect);", js);
    Assert.Contains("picker.list.addEventListener('change', onRowToggle);", js);
    Assert.DoesNotContain("openPane", js[js.IndexOf("function onRowToggle", StringComparison.Ordinal)..js.IndexOf("function bulkSet", StringComparison.Ordinal)]);
  }

  [Fact]
  public void EntryPane_PagesTheExistingEndpoint_OneRequestAtATime_AndDropsLateResponses()
  {
    var pane = PaneJs();
    // Only the existing read-only endpoint from #215; no new API.
    Assert.Single(Regex.Matches(pane, @"api\(`/playlist/"));
    Assert.Contains("api(`/playlist/group-entries?${query}`, { signal: controller.signal })", pane);
    Assert.Contains("limit: String(MAX_SEARCH_RESULTS)", pane);
    Assert.Contains("if (pane.ed !== ed || pane.controller || pane.done || pane.failed) return;", pane);
    Assert.Contains("if (pane.controller !== controller) return;", pane);
    Assert.Contains("pane.controller?.abort();", pane);
    Assert.Contains("pane.list.append(...names.map((name) => h('li', { text: name })));", pane);
  }

  [Fact]
  public void EntryPane_InsertsTitlesAsText_AndShowsLoadingEmptyAndErrorStates()
  {
    var pane = PaneJs();
    Assert.DoesNotContain("innerHTML", pane);
    Assert.DoesNotContain("insertAdjacentHTML", pane);
    Assert.Contains("h('h2', { text: g.title })", pane);
    Assert.Contains("'Loading entries…'", pane);
    Assert.Contains("'No entries in this group.'", pane);
    Assert.Contains("h('div', { class: 'notice error', role: 'alert' }", pane);
    Assert.Contains("'aria-label': 'Close entries', onclick: () => closePane(ed)", pane);
  }

  [Fact]
  public void AppCss_OpenPane_ScrollsItsListInsideTheBreakpointOnly()
  {
    var css = Read("app.css");
    var outsideMedia = Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "");
    Assert.DoesNotContain(".entry-pane.open", outsideMedia);
    Assert.DoesNotContain(".entry-scroll", outsideMedia);
    Assert.Matches(@"\.entry-pane\.open\s*\{[^}]*display:\s*flex;", css);
    Assert.Matches(@"\.entry-scroll\s*\{[^}]*overflow-y:\s*auto;", css);
  }

  /// <summary>The h() helper must keep writing `text` through textContent, which keeps titles literal.</summary>
  [Fact]
  public void Helper_WritesTextPropertyAsTextContent()
  {
    Assert.Contains("else if (key === 'text') el.textContent = value;", Read("main.js"));
  }
}
