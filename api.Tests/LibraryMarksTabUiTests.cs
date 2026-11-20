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
    // The page's keys, also on an overlapping or fully repeated page; row deduplication happens before, on its own.
    var dedupe = load.IndexOf("if (state.shown.has(m.key) || rows.has(m.key) || state.skipped.has(m.key)) continue;", StringComparison.Ordinal);
    var ask = load.IndexOf("const keys = items.map((m) => m.key);\n      if (keys.length) { state.pages.push(keys); lookup(keys, seq); }", StringComparison.Ordinal);
    // A fully repeated page sends its lookup, then moves on to the next page.
    Assert.True(dedupe >= 0 && ask > dedupe && load.IndexOf("if (!state.done && added.length === 0) {", StringComparison.Ordinal) > ask);
    var lookup = Between(tab, "const lookup = async", "view.marksListReady");
    Assert.Contains("found = await ctx.lookupTitles({ cacheKey: ctx.list.cacheKey, includeDisabled: includeOff, keys });", lookup);
    // Only rows in the tab are rebuilt (one whose title left goes back to "Not in this list"); a late failure for a
    // view or screen left since replaces nothing; "Include switched-off groups" asks again for every loaded page.
    Assert.Contains("if (!r || (!t && !r.t)) continue;", lookup);
    Assert.Contains("const lookup = async (keys, seq, off = includeOff) => {", tab);
    Assert.Contains("if (seq !== state.seq || off !== includeOff || !ctx.isCurrent()) return;", lookup);
    Assert.Contains("if (seq === state.seq && ctx.isCurrent() && !ctx.controller.signal.aborted) accessGone(ctx, err);", lookup);
    Assert.Contains("view.marksRelookup = () => state.pages.forEach((keys) => lookup(keys, state.seq));", tab);
    Assert.Contains("onToggle();\n      view.marksRelookup?.();", Read("library.js"));
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
    var row = Between(Tab(), "const markRow = (m, t, fresh) => {", "return li;");
    Assert.Contains("const live = !fresh || writes.has(m.key) ? marks.get(m.key) : null;\n      const now = live || m;", row);
    Assert.Contains("if (live) marks.set(m.key, live);", row);
    Assert.Contains("done = () => { resolve(); if (writes.get(lane) === mine) writes.delete(lane); };", Read("library.js"));
    Assert.Contains("const li = markRow(m, null, true);", Tab());
    Assert.Contains("const li = markRow(r.m, t, false);", Tab());
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
    var changed = Between(tab, "view.marksChanged = (key, mark, how) => {", "const start = () => {");
    Assert.Contains("r.li.hidden = !fits;\n      state.total += fits ? 1 : -1;", changed);
    // A row that leaves no longer counts for loadPage's page arithmetic; Undo counts it again.
    Assert.Contains("if (fits) { state.shown.add(key); state.skipped.delete(key); } else state.shown.delete(key);\n      paint();", changed);
    Assert.Contains("if (rows.get(m.key)?.li.hidden) state.skipped.add(m.key);\n        if (state.shown.has(m.key) || rows.has(m.key) || state.skipped.has(m.key)) continue;", tab);
    Assert.Contains("if (!option().fits(m)) { state.skipped.add(m.key); continue; }", tab);
    Assert.Contains("state.total = page.total - state.skipped.size;", tab);
    // A fitting mark without a row, or a similar film's mark cleared before it had a key, reloads the tab (lazily).
    Assert.Contains("if (!r) return option().fits(mark) ? view.marksStale() : undefined;", changed);
    Assert.Contains("return !ctx.isCurrent() || !list.isConnected ? undefined : view.panels.marks.hidden ? (state.stale = true) : start();", tab);
    Assert.Contains("return other || state.stale ? start() : keep();", tab);
    Assert.Contains("} else if (!real && key.startsWith('similar:')) view?.marksStale?.(name, next);", Read("library.js"));
    Assert.Contains("const hit = name && [...rows].find(([, r]) => r.t?.name === name && !r.li.hidden);\n      if (hit) return view.marksChanged(hit[0], mark);", tab);
    Assert.Contains("} else if (marks.get(key) === next) view?.marksChanged?.(key, next, 'saved');", Read("library.js"));
    Assert.Contains("if (!r.li.isConnected || how === 'saved') return;", changed);
    Assert.Contains("marks.get(key) === before ? 'failed' : undefined);", Read("library.js"));
    Assert.Contains("if (how === 'failed') r.back?.();\n        else { const p = r.li.previousElementSibling; r.back = () => (p?.isConnected ? p.after(r.li) : list.prepend(r.li)); list.prepend(r.li); }", changed);
    Assert.Contains("if (card.el?.matches(':modal')) card.el.addEventListener('close', () => { if (r.li.hidden) undo(); }, { once: true });", changed);
    var set = Between(Read("library.js"), "async function setMark(", "// Browse (#287)");
    Assert.Contains("await prior;\n      const saved = await ctx.setMark(", set);
    Assert.Contains("watchlist: next.watchlist });\n      done();", set);
    Assert.Contains("} catch (err) {\n      done();", set);
    // The toast only while the tab is visible; a hidden tab updates rows and count silently.
    Assert.Contains("if (fits || view.panels.marks.hidden) return;", changed);
    Assert.True(changed.IndexOf("view.panels.marks.hidden", StringComparison.Ordinal) < changed.IndexOf("ctx.toast(", StringComparison.Ordinal));
    // A mark changed in a card opened from a similar film reaches the tab through syncRows as well.
    Assert.Contains("view?.marksChanged?.(key, mark);", Between(Read("library.js"), "function syncRows(ctx, key, mark) {", "\n  }\n"));
    Assert.Contains("const text = state.view === 'watchlist' ? `${r.m.title} is off your watchlist` : `Mark on ${r.m.title} removed`;", changed);
    Assert.Contains("const undo = () => ctx.toast(text, { action: { label: 'Undo', onClick: () => {", changed);
    Assert.Contains("if (r.li.hidden) setMark(ctx, markName(r.m), r.m.kind, key, { seen: r.m.seen, rating: r.m.rating, watchlist: r.m.watchlist });", changed);
    Assert.Contains("view?.marksChanged?.(key, marks.get(key), marks.get(key) === before ? 'failed' : undefined);", Between(Read("library.js"), "async function setMark(", "// Browse (#287)"));
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
