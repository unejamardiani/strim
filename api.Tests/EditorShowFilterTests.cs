using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the labelled group show filter (#269): a visible label names the All / On / Off control, and the
/// choice is remembered per saved playlist in localStorage, read through a whitelist and behind try/catch.
/// </summary>
public class EditorShowFilterTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"'{start}' not found.");
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    Assert.True(to > from, $"'{end}' not found after '{start}'.");
    return source[from..to];
  }

  [Fact]
  public void ShowControl_HasVisibleLabel_ReferencedAsItsAccessibleName()
  {
    var js = Read("main.js");
    Assert.Contains("h('span', { class: 'show-label', id: 'group-show-label' },", js);
    Assert.Contains("'Show', h('span', { class: 'show-hint', text: ' groups' })", js);
    Assert.Contains("picker.showButtons.setAttribute('aria-labelledby', 'group-show-label');", js);
    // The label sits in the same toolbar row as the control, in front of it; all three states stay.
    var row = Between(js, "const filters = h('div', { class: 'toolbar-row show-row' });", "rows.push(filters);");
    Assert.True(row.IndexOf("show-label", StringComparison.Ordinal) < row.IndexOf("picker.showButtons = segmented(", StringComparison.Ordinal));
    Assert.Contains("[{ value: 'all', label: 'All' }, { value: 'kept', label: 'On' }, { value: 'hidden', label: 'Off' }]", row);
  }

  [Fact]
  public void ShowChoice_IsReadAndWrittenPerPlaylist_Whitelisted_AndGuarded()
  {
    var js = Read("main.js");
    Assert.Contains("const GROUP_SHOW_VALUES = ['all', 'kept', 'hidden'];", js);

    var read = Between(js, "function readGroupShow(id)", "function writeGroupShow");
    Assert.Contains("if (!id) return 'all';", read);
    Assert.Matches(@"try \{\s*const value = localStorage\.getItem\(`strim\.groupShow\.\$\{id\}`\);", read);
    Assert.Contains("return GROUP_SHOW_VALUES.includes(value) ? value : 'all';", read);
    Assert.Matches(@"\} catch \{\s*return 'all';", read);

    var write = Between(js, "function writeGroupShow(id, value)", "function newEditor");
    Assert.Contains("if (!id || !GROUP_SHOW_VALUES.includes(value)) return;", write);
    Assert.Matches(@"try \{\s*localStorage\.setItem\(`strim\.groupShow\.\$\{id\}`, value\);\s*\} catch \{", write);

    // Only these two functions touch the per-playlist key.
    Assert.Equal(2, Regex.Matches(js, Regex.Escape("strim.groupShow.")).Count);

    // A saved playlist starts from the stored value; a new one (no id) starts on All.
    Assert.Contains("filter: { query: '', type: 'all', show: readGroupShow(playlist?.id), sort: 'provider' },", js);

    // Pressing a show button stores the choice, but never for the sample.
    var row = Between(js, "const filters = h('div', { class: 'toolbar-row show-row' });", "rows.push(filters);");
    Assert.Contains("writeGroupShow(ed.isSample ? null : ed.id, value);", row);
    Assert.Single(Regex.Matches(js, @"writeGroupShow\(ed"));
  }

  [Fact]
  public void JumpToGroup_ResetsTheView_WithoutWritingTheStoredChoice()
  {
    var js = Read("main.js");
    var jump = Between(js, "function jumpToGroup(ed, key)", "\n}\n");
    Assert.Contains("ed.filter.show = 'all';", jump);
    Assert.DoesNotContain("writeGroupShow", jump);
    Assert.DoesNotContain("localStorage", jump);
  }

  [Fact]
  public void EverythingSearch_FollowsTheShowFilter_AndReportsHiddenMatches()
  {
    var js = Read("main.js");
    var run = Between(js, "async function runSearch(ed)", "function renderSearchResults(ed)");
    // On hides switched-off groups in the search too; All and Off list every match.
    Assert.Contains("const hideDisabled = ed.filter.show === 'kept';", run);
    Assert.Contains("body: { cacheKey: ed.cacheKey, query, disabledGroups: [...ed.disabled], hideDisabled },", run);
    // A cached result only counts when it was fetched with the same flag.
    Assert.Contains("ed.search.query === query && ed.search.hideDisabled === hideDisabled", run);
    Assert.Contains("hiddenCount: hideDisabled && Number.isInteger(result.hiddenCount) ? result.hiddenCount : 0,", run);

    var render = Between(js, "function renderSearchResults(ed)", "function searchHit(ed, m)");
    Assert.Contains("const hidden = s.status === 'done' ? s.hiddenCount || 0 : 0;", render);
    Assert.Contains("${fmt(hidden)} in switched-off groups not shown", render);
    Assert.Contains("All ${plural(hidden, 'match', 'matches')} are in switched-off groups.", render);
    Assert.Contains("'Nothing matches your search.'", render);

    // Pressing a show button in Everything mode searches again.
    var row = Between(js, "const filters = h('div', { class: 'toolbar-row show-row' });", "rows.push(filters);");
    Assert.Contains("if (ed.searchMode === 'everything') runSearch(ed);", row);
  }

  [Fact]
  public void AppCss_StylesTheLabel_WithTokensOnly_AndKeepsTheRowOnOneLine()
  {
    var css = Read("app.css");
    var label = Regex.Match(css, @"\.show-label\s*\{[^}]*\}").Value;
    Assert.NotEmpty(label);
    Assert.Contains("color: var(--ink-3);", label);
    Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(", label);
    Assert.Matches(@"\.toolbar-row\.show-row\s*\{\s*flex-wrap:\s*nowrap;", css);
    Assert.Matches(@"@media \(max-width: 440px\)\s*\{\s*\.show-hint\s*\{\s*display:\s*none;", css);
  }

  [Fact]
  public void Editor_HasNoInlineStyles()
  {
    var html = Read("app.html");
    Assert.DoesNotMatch(@"<style[\s>]|\sstyle\s*=", html);
    var js = Read("main.js");
    // No style attribute (the virtual list's CSSOM height is not an inline style attribute).
    Assert.DoesNotMatch(@"['""]?\bstyle['""]?\s*:\s*['""`]|setAttribute\(\s*['""]style['""]|\bstyle=|<style", js);
  }
}
