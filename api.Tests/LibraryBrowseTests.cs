using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Library browse view (#287): the analysed groups by type and then in playlist order, the shared
/// 'Include switched-off groups' toggle, and paging through a group with /playlist/group-entries.
/// The endpoint itself is covered by the group-entries tests of #215.
/// </summary>
public class LibraryBrowseTests
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

  private static string Browse() => Between(Read("library.js"), "// Browse (#287)", "function hit(");

  [Fact]
  public void Groups_ComeFromTheAnalysis_SortedByTypeThenPlaylistOrder_WithTheirCount()
  {
    var library = Read("library.js");
    var browse = Browse();
    Assert.Contains("const BROWSE_ORDER = ['Movies', 'Series', 'Live'];", browse);
    Assert.Contains("const BROWSE_HEADINGS = { Movies: 'Films', Series: 'Series', Live: 'Channels' };", browse);
    Assert.Contains("const sorted = [...shown].sort((a, b) => BROWSE_ORDER.indexOf(a.type) - BROWSE_ORDER.indexOf(b.type) || a.index - b.index);", browse);
    Assert.Contains("h('span', { class: 'search-hit-meta', text: plural(g.count, 'entry', 'entries') })", browse);
    Assert.Contains("h('h3', { class: 'field-label', text: BROWSE_HEADINGS[g.type] })", browse);

    // The groups are the ones the Library loaded once; the browse view sits next to the search.
    var load = Between(library, "async function load(", "const TYPES = [");
    Assert.Contains("const browse = browseSection(ctx, result);", load);
    Assert.Contains("searchSection(ctx, result, browse.refresh),\n    browse.el);", load);
    Assert.Single(Regex.Matches(library, Regex.Escape("ctx.loadGroups(")));

    // index and type are the playlist position and the content type main.js derives from the analysis.
    var apply = Between(Read("main.js"), "function applyAnalysis(ed, analysis, { sample })", "const perTitle = new Map();");
    Assert.Contains("return { key: g.name, title, type, count: g.count || 0, index,", apply);
  }

  [Fact]
  public void SharedToggle_ListsOnlySwitchedOnGroups_ShowsTheHiddenCount_AndMarksOffGroups()
  {
    var library = Read("library.js");
    var browse = Browse();
    // One toggle for search and browse.
    Assert.Single(Regex.Matches(library, Regex.Escape("'Include switched-off groups'")));
    var section = Between(library, "function searchSection(", "async function runSearch(");
    Assert.Contains("includeOff = !includeOff;", section);
    Assert.Contains("run();\n        onToggle();", section);

    Assert.Contains("const shown = list.groups.filter((g) => withOff || !list.disabled.has(g.key));", browse);
    Assert.Contains("return { sorted, hidden: list.groups.length - shown.length };", browse);
    Assert.Contains("const { sorted, hidden } = browseGroups(list, includeOff);", browse);
    Assert.Contains("if (hidden > 0) nodes.push(status(`${plural(hidden, 'switched-off group')} hidden`));", browse);
    Assert.Contains("const offBadge = (g) => (list.disabled.has(g.key) ? h('span', { class: 'search-badge off', text: 'Off' }) : null);", browse);

    var refresh = Between(browse, "const refresh = () => {", "};");
    Assert.Contains("renderGroups();", refresh);
    Assert.Contains("if (state.open && !includeOff && list.disabled.has(state.open.group.key)) close();", refresh);
  }

  [Fact]
  public void OpeningAGroup_PagesWithOffsetAndLimit_UntilTotal_AndAFailedPageKeepsTheEntries()
  {
    var browse = Browse();
    var load = Between(browse, "const loadPage = async (view) => {", "const refresh = () => {");
    Assert.Contains("page = await ctx.groupEntries({ cacheKey: list.cacheKey, group: view.group.key, offset: view.offset, limit: PAGE_SIZE }, ctx.controller.signal);", load);
    Assert.Contains("view.offset += names.length;", load);
    Assert.Contains("const done = names.length === 0 || view.offset >= page.total;", load);
    Assert.Contains("if (!done) view.foot.replaceChildren(h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadPage(view) }, 'More'));", load);
    // Names are text; new names are appended, so earlier pages stay.
    Assert.Contains("view.list.append(...names.map((name) => h('li', { class: 'library-hit' }, h('span', { class: 'search-hit-name', text: name }))));", load);
    Assert.DoesNotContain("innerHTML", Read("library.js"));

    // A failure replaces only the footer with the error and a retry; the list is not touched.
    var failure = Between(load, "} catch (err) {", "return;\n      }");
    Assert.Contains("view.foot.replaceChildren(h('div', { class: 'notice error', role: 'alert' },", failure);
    Assert.Contains("'Try again'", failure);
    Assert.DoesNotContain("view.list", failure);
    Assert.Contains("if (view !== state.open || ctx.controller.signal.aborted) return;", failure);

    // A page of a group no longer open is dropped; the page size is the server's limit.
    Assert.Contains("if (view !== state.open || !ctx.isCurrent()) return;", load);
    Assert.Contains("const PAGE_SIZE = 50;", browse);
    Assert.Contains("public const int MaxSearchResults = 50;", Read("api/Endpoints/EndpointSupport.cs"));

    // main.js hands in the read-only group-entries request with the parameters as the query string.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("groupEntries: (params, signal) => api(`/playlist/group-entries?${new URLSearchParams(params)}`, { signal }),", show);
    Assert.DoesNotContain("/playlist/group-entries", Read("library.js"));

    // 'All groups' goes back to the list.
    var open = Between(browse, "const open = (g) => {", "const loadPage =");
    Assert.Contains("h('button', { class: 'btn btn-sm', type: 'button', onclick: close }, icon('back'), 'All groups'),", open);
    var close = Between(browse, "const close = () => {", "const open = (g) => {");
    Assert.Contains("groupsEl.hidden = false;", close);
  }

  [Fact]
  public void BrowseView_HasNoSwitchBulkActionOrSave()
  {
    var browse = Browse();
    Assert.DoesNotMatch(@"\bswitch\b", browse);
    foreach (var forbidden in new[] { "checkbox", "class: 'toggle'", "library-toggle", "bulk", "Bulk", "Save", "Switch", "Turn off", "Turn on", "disabled.add", "disabled.delete", "POST" })
      Assert.DoesNotContain(forbidden, browse);
    // The only buttons open a group, page, retry or go back.
    var labels = Regex.Matches(browse, @"\}, '([^']+)'\)").Select(m => m.Groups[1].Value).ToHashSet();
    Assert.Equal(new HashSet<string> { "More", "Try again" }, labels);
  }
}
