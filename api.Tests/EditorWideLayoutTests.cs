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
    // #228: the group column takes the larger share, the pane keeps a usable minimum.
    Assert.Contains("`minmax(0, 1fr) minmax(300px, 0.4fr)`", design);
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
    Assert.Matches(@"\.editor-split\s*\{[^}]*display:\s*grid;[^}]*grid-template-columns:\s*minmax\(0, 1fr\) minmax\(300px, 0\.4fr\);", body);
    Assert.Matches(@"\.entry-pane\s*\{[^}]*display:\s*block;", body);
  }

  /// <summary>The desktop group row (#228): extra columns from 1100px only, fixed tracks, one line.</summary>
  [Fact]
  public void AppCss_DesktopRow_HasFixedColumns_FromBreakpointOnly()
  {
    var css = Read("app.css");
    var outsideMedia = Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "");
    Assert.Matches(@"\.group-samples,\s*\.group-adult,\s*\.group-origin\s*\{\s*display:\s*none;", outsideMedia);
    Assert.Matches(@"\.group-row\s*\{[^}]*display:\s*flex;", outsideMedia);
    Assert.Matches(@"\.group-row\s*\{[^}]*height:\s*var\(--row-h\);", outsideMedia);
    var rowMedia = Regex.Matches(css, @"@media \(min-width: 1100px\)\s*\{((?:[^{}]*\{[^{}]*\})*)[^{}]*\}")
      .Select(m => m.Groups[1].Value)
      .Single(b => b.Contains(".group-samples"));
    Assert.Matches(@"\.group-row\s*\{\s*display:\s*grid;\s*grid-template-columns:\s*10px minmax\(340px, 1fr\) 64px minmax\(0, 1fr\) 36px minmax\(0, 0\.6fr\) 56px 46px;\s*\}", rowMedia);
    var order = new[] { ".dot", ".group-name", ".tag", ".group-samples", ".group-adult", ".group-origin", ".group-count", ".toggle" };
    for (var i = 0; i < order.Length; i++)
      Assert.Matches($@"\.group-row > {Regex.Escape(order[i])} \{{ grid-column: {i + 1};", rowMedia);
    Assert.DoesNotContain("height", Regex.Match(rowMedia, @"\.group-row\s*\{[^}]*\}").Value);
    Assert.Matches(@"\.group-samples,\s*\.group-origin\s*\{[^}]*text-overflow:\s*ellipsis;\s*white-space:\s*nowrap;", rowMedia);
    Assert.Contains("Desktop group row (from 1100px)", Read("DESIGN.md"));
  }

  [Fact]
  public void Editor_PooledRows_PaintSamplesAdultAndOrigin_WithoutPlaceholders()
  {
    var js = Read("main.js");
    Assert.Contains("const samples = Array.isArray(g.sampleTitles) ? g.sampleTitles.filter(Boolean).join(', ') : '';", js);
    Assert.Contains("h('label', { class: 'group-row', role: 'listitem' }, dot, name, badge, samples, adult, origin, count, check);", js);
    var paint = js[js.IndexOf("function paintRows", StringComparison.Ordinal)..js.IndexOf("function onRowToggle", StringComparison.Ordinal)];
    Assert.Contains("samples.textContent = g.samples;", paint);
    Assert.Contains("adult.textContent = g.adult ? '18+' : '';", paint);
    Assert.Contains("origin.textContent = g.split ? `from ${g.title}` : '';", paint);
    // No per-row request: painting stays local.
    Assert.DoesNotContain("api(", paint);
    Assert.Contains("picker.rowHeight = Number.isFinite(value) && value > 0 ? value : 48;", js);
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
    Assert.Contains("const limit = prev ? pane.start - offset : MAX_SEARCH_RESULTS;", pane);
    Assert.Contains("limit: String(limit)", pane);
    Assert.Contains("if (pane.ed !== ed || pane.controller || pane.failed) return;", pane);
    Assert.Contains("if (prev ? pane.start === 0 : pane.done) return;", pane);
    Assert.Contains("if (pane.controller !== controller) return;", pane);
    Assert.Contains("pane.controller?.abort();", pane);
    Assert.Contains("const items = (page.names || []).map((name) => h('li', { text: name }));", pane);
    // Below the breakpoint the hidden scroller has no size; paging must stop instead of fetching every page.
    Assert.Contains("if (!WIDE_EDITOR.matches) return;", pane);
    Assert.Contains("return WIDE_EDITOR.matches && pane.scroller?.clientHeight > 0;", pane);
    Assert.Contains("if (!s || !paneVisible()) return;", pane);
    var onWidth = pane[pane.IndexOf("WIDE_EDITOR.addEventListener('change',", StringComparison.Ordinal)..];
    // Narrowing aborts only a pending page; an error and its "Try again" button stay.
    Assert.Contains("if (!c) return;", onWidth);
    Assert.Contains("c.abort();", onWidth);
  }

  [Fact]
  public void EntryPane_KeepsAtMostThreePagesRendered_AndRefetchesEvictedPages()
  {
    var pane = PaneJs();
    Assert.Contains("const ENTRY_PANE_PAGES = 3;", pane);
    // Scrolling down drops the top page; scrolling up fetches it again and drops the bottom page.
    Assert.Equal(2, Regex.Matches(pane, @"if \(pane\.pages\.length > ENTRY_PANE_PAGES\)").Count);
    Assert.Contains("dropEntries(n, true);", pane);
    Assert.Contains("dropEntries(n, false);", pane);
    Assert.Contains("else if (s.scrollTop < 240 && pane.start > 0) loadEntryPage(ed, 'prev');", pane);
    Assert.Contains("const offset = prev ? Math.max(0, pane.start - MAX_SEARCH_RESULTS) : pane.end;", pane);
    // The browser's own scroll anchoring would double the manual scroll correction.
    Assert.Matches(@"\.entry-scroll\s*\{[^}]*overflow-anchor:\s*none;", Read("app.css"));
  }

  [Fact]
  public void EntryPane_EnterOnTheSwitchTogglesIt_WithoutOpeningThePane()
  {
    var pane = PaneJs();
    Assert.Contains("picker.list.addEventListener('keydown', onSwitchEnter);", Read("main.js"));
    var handler = pane[pane.IndexOf("function onSwitchEnter", StringComparison.Ordinal)..pane.IndexOf("function resetPane", StringComparison.Ordinal)];
    Assert.Contains("if (e.key !== 'Enter' || e.repeat || !e.target.classList?.contains('toggle')) return;", handler);
    Assert.Contains("e.target.click();", handler);
    Assert.DoesNotContain("openPane", handler);
  }

  [Fact]
  public void EntryPane_InsertsTitlesAsText_AndShowsLoadingEmptyAndErrorStates()
  {
    var pane = PaneJs();
    Assert.DoesNotContain("innerHTML", pane);
    Assert.DoesNotContain("insertAdjacentHTML", pane);
    Assert.Contains("h('h2', { text: g.title })", pane);
    Assert.Contains("'Loading entries…'", pane);
    // The empty state follows the server's total (names: [], total: 0), not the group's count in the list.
    Assert.Contains("pane.status.replaceChildren(page.total === 0 ? h('p', { class: 'search-status', text: 'No entries in this group.' }) : '');", pane);
    Assert.Contains("pane.done = items.length === 0 || pane.end >= page.total;", pane);
    Assert.Contains("pane.count.textContent = paneCount(page.total, pane.group);", pane);
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
