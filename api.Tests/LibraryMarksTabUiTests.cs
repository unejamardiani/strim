using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Marks tab (#424) at its source, as the other UI tests do (no JS engine): five views, the view in
/// the address, one titles lookup per page, rows that open their card or say "Not in this list", the shared mark
/// controls, rows that leave with Undo, and the empty states.</summary>
public class LibraryMarksTabUiTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"'{start}' not found.");
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    Assert.True(to > from, $"'{end}' not found after '{start}'.");
    return source[from..to];
  }

  private static string Tab() => LibraryMyMarksTests.MyMarks();

  [Fact]
  public void Views_AreFiveChipsWithTheirQueries()
  {
    var tab = Tab();
    var labels = new[] { "'Watchlist'", "'All'", "'Seen'", "'Liked'", "'Disliked'" }.Select(l => tab.IndexOf($"label: {l}", StringComparison.Ordinal)).ToArray();
    Assert.All(labels, i => Assert.True(i >= 0));
    Assert.Equal(labels.OrderBy(i => i), labels);
    Assert.Contains("{ value: 'watchlist', label: 'Watchlist', params: { watchlist: true, sort: 'watchlisted' },", tab);
    Assert.Contains("{ value: 'all', label: 'All', params: {},", tab);
    Assert.Contains("{ value: 'seen', label: 'Seen', params: { seen: true },", tab);
    Assert.Contains("{ value: 'liked', label: 'Liked', params: { rating: 1 },", tab);
    Assert.Contains("{ value: 'disliked', label: 'Disliked', params: { rating: -1 },", tab);
    Assert.Contains("view: MARK_VIEWS.some((v) => v.value === asked) ? asked : 'all'", tab);
    Assert.Contains("class: 'lib-chip', type: 'button', 'aria-pressed': String(v.value === state.view), dataset: { value: v.value },", tab);
    Assert.Contains("}, v.label, h('span', { class: 'n' })));", tab);
    Assert.Contains("b.lastElementChild.textContent = ready && b.dataset.value === state.view ? fmt(state.total) : '';", tab);
    Assert.Contains("h('div', { class: 'lib-sticky' },\n        h('div', { class: 'lib-ghead' }, h('h2', { text: 'My marks' }), line),\n        h('div', { class: 'lib-chips', role: 'group', 'aria-label': 'Show marks' }, chips)),", tab);
    Assert.Contains("`${plural(state.total, 'title')} · newest first`", tab);
    Assert.Contains("`${plural(state.total, 'mark')} · newest change first`", tab);
    Assert.DoesNotContain("also titles no longer in a playlist", Read("library.js"));
  }

  [Fact]
  public void View_IsKeptInTheAddress()
  {
    var tab = Tab();
    Assert.Contains("const hash = `${view.base}/marks?view=${state.view}`;", tab);
    Assert.Contains("if (!view.panels.marks.hidden && location.hash !== hash) history.replaceState(history.state, '', hash);", tab);
    Assert.Contains("new URLSearchParams(ctx.query || '').get('view')", tab);
    Assert.Contains("if (tab === 'marks') view.marksView?.(query);", Between(Read("library.js"), "function show(sub, query)", "function inPanels("));
  }

  [Fact]
  public void Rows_AskLookupOncePerPage()
  {
    var tab = Tab();
    var load = Between(tab, "const loadPage = async () => {", "const lookup = async");
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(load, @"lookup\("));
    Assert.Contains("if (added.length) lookup(added, seq);", load);
    Assert.Contains("found = await ctx.lookupTitles({ cacheKey: ctx.list.cacheKey, includeDisabled: includeOff, keys });", tab);
    // A page loaded before the list waits for it; the list's load hands the pages on.
    Assert.Contains("waiting.push([keys, seq]);", tab);
    Assert.Contains("ctx.list = result;\n    view.marksListReady?.();", Read("library.js"));

    var main = Read("main.js");
    Assert.Contains("lookupTitles: (body) => api(`/playlists/${playlist.id}/library/titles/lookup`, { method: 'POST', body }),",
      Between(main, "function showLibrary(playlist)", "function showSharedLibrary("));
    Assert.Contains("lookupTitles: (body) => api(`${base}/titles/lookup`, { method: 'POST', body: scoped(body) }),",
      Between(main, "function showSharedLibrary(", "function linkCard("));
  }

  [Fact]
  public void Row_InList_OpensTheCard_OtherwiseSaysNotInThisList()
  {
    var row = Between(Tab(), "const markRow = (m, t) => {", "return li;");
    Assert.Contains("const card = t ? { ...entry, name: t.name, enabled: t.enabled } : { ...entry, markKey: null };", row);
    Assert.Contains("const meta = !t ? 'Not in this list' : type === 'Series' ? [h('b', { text: seasonsLine(ctx, t) }), ' · ', t.group] : t.group;", row);
    Assert.Contains("h('li', { class: opens(ctx, card) ? 'lib-row' : 'lib-row is-static', dataset: { type, key: m.key } },", row);
    Assert.Contains("filmName(ctx, card, t?.group, ctx.list?.cacheKey)", row);
    Assert.Contains("h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, icon('chevron'))", row);
    Assert.DoesNotContain("Not in your list", Read("library.js"));
    Assert.Contains(".lib-row.is-static .lib-chev { visibility: hidden; }", Read("library.css"));
    Assert.Contains("@media (min-width: 1100px) { .library-mylist { max-width: 920px; } }", Read("library.css"));
  }

  [Fact]
  public void Row_UsesTheSharedMarkControls_NoRemoveButton()
  {
    var tab = Tab();
    Assert.Contains("const markName = (m) => (m.year ? `${m.title} (${m.year})` : m.title);", tab);
    Assert.Contains("const entry = { name: markName(m), title: m.title, year: m.year, contentType: type, markKey: m.key,", tab);
    Assert.Contains("rowMarks(ctx, entry),", tab);
    Assert.DoesNotContain("'Remove'", tab);
  }

  [Fact]
  public void EmptyMarkOrWrongView_LeavesWithUndoToast()
  {
    var tab = Tab();
    Assert.Contains("fits: (m) => m.watchlist === true,", tab);
    Assert.Contains("fits: (m) => m.seen || m.rating !== 0 || m.watchlist === true,", tab);
    var changed = Between(tab, "view.marksChanged = (key, mark) => {", "const start = () => {");
    Assert.Contains("r.li.hidden = !fits;\n      state.total += fits ? 1 : -1;\n      paint();", changed);
    Assert.Contains("const text = state.view === 'watchlist' ? `${r.m.title} is off your watchlist` : `Mark on ${r.m.title} removed`;", changed);
    Assert.Contains("ctx.toast(text, { action: { label: 'Undo', onClick: () => {", changed);
    Assert.Contains("if (r.li.hidden) setMark(ctx, markName(r.m), r.m.kind, key, { seen: r.m.seen, rating: r.m.rating, watchlist: r.m.watchlist });", changed);
    // Every change of a mark, a failed one put back too, reaches the tab.
    Assert.Contains("view?.marksChanged?.(key, marks.get(key));", Between(Read("library.js"), "async function setMark(", "// Browse (#287)"));
  }

  [Fact]
  public void EmptyStates_HaveTheThreeTexts()
  {
    var tab = Tab();
    Assert.Contains("empty: 'Nothing on your watchlist yet. Add a film or series with the bookmark in its card.' },", tab);
    Assert.Contains("empty: 'No marks yet. Mark a film or series as seen, or give it a thumb, in the search or a group.' },", tab);
    Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(tab, "empty: 'No marks of this kind.'").Count);
    Assert.Contains("if (state.done) foot.replaceChildren(...(state.total > 0 ? [] : [status(option().empty)]));", tab);
  }
}
