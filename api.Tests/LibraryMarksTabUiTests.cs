using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Marks tab's views, head and address (#446) in library.js and library.css at their source, as the other
/// Library UI tests do (api.Tests has no JS engine). That the list leaves out match-only records is covered by
/// <see cref="LibraryFilmMatchTests"/>.
/// </summary>
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

  private static string MyMarks() => Between(Read("library.js"), "// My marks (#299)", "// A remove or an undo in My marks");

  [Fact]
  public void Views_AreFiveChipsWithTheirQueries()
  {
    var section = MyMarks();
    var views = Between(section, "const MARK_VIEWS = [", "];");
    string[] lines =
    [
      "{ value: 'watchlist', label: 'Watchlist', params: { watchlist: true, sort: 'watchlisted' } },",
      "{ value: 'all', label: 'All', params: {} },",
      "{ value: 'seen', label: 'Seen', params: { seen: true } },",
      "{ value: 'liked', label: 'Liked', params: { rating: 1 } },",
      "{ value: 'disliked', label: 'Disliked', params: { rating: -1 } },",
    ];
    var at = -1;
    foreach (var line in lines)
    {
      var next = views.IndexOf(line, StringComparison.Ordinal);
      Assert.True(next > at, $"'{line}' missing or out of order.");
      at = next;
    }
    Assert.Contains("const MARKS_PAGE = 50;", section);

    // Single choice in the chip row, "All" when no address names a view.
    Assert.Contains("const chips = h('div', { class: 'lib-chips', role: 'group', 'aria-label': 'Show marks' });", section);
    Assert.Contains("class: 'lib-chip', type: 'button', 'aria-pressed': String(option.value === view.filter)", section);
    Assert.Contains("for (const b of chips.children) b.setAttribute('aria-pressed', String(b.dataset.value === name));", section);
    Assert.Contains("const view = { filter: asked || 'all',", section);

    // The head: title and the count of the view, empty until the first page answered; the count in the active chip.
    Assert.Contains("h('div', { class: 'lib-sticky' },\n        h('div', { class: 'lib-ghead' }, h('div', { class: 'lib-ghead-text' }, h('h2', { text: 'My marks' }), count)),\n        chips),", section);
    Assert.Contains("count.textContent = !view.loaded ? ''", section);
    Assert.Contains("view.filter === 'watchlist' ? `${plural(view.total, 'title')} · newest first` : `${plural(view.total, 'mark')} · newest change first`;", section);
    Assert.Contains("if (view.loaded && b.dataset.value === view.filter) b.append(h('span', { class: 'n', text: fmt(view.total) }));", section);
    Assert.Contains("view.loaded = false;", Between(section, "const start = () => {", "start();"));

    // The replaced parts are gone.
    var library = Read("library.js");
    Assert.DoesNotContain("Every film and series you marked", library);
    Assert.DoesNotContain("MARK_FILTERS", library);
    Assert.DoesNotContain("'pills'", section);
    Assert.DoesNotContain("shown`", section);

    var css = Read("library.css");
    Assert.Contains("@media (min-width: 1100px) { .library-mylist { max-width: 920px; } }", css);
    Assert.Contains(".library-mylist > .lib-sticky + .lib-list { margin-top: -6px; }", css);
  }

  [Fact]
  public void View_IsKeptInTheAddress()
  {
    var library = Read("library.js");
    var keep = Between(library, "function keepMarksAddress(name) {", "\n  }");
    Assert.Contains("const hash = `${view.base}/marks?view=${name}`;", keep);
    Assert.Contains("view.nav.querySelector('[data-tab=\"marks\"]').href = hash;", keep);
    // Through the router's replaceAddress, so its hashchange guard knows the address and a link back to the one
    // replaced (Watchlist, then All, then the Watchlist link again) still switches the view.
    Assert.Contains("if (!view.panels.marks.hidden && location.hash !== hash) view.ctx.replaceAddress(hash);", keep);
    Assert.DoesNotContain("history.replaceState", keep);
    Assert.Contains("history.replaceState(history.state, '', hash);\n      currentHash = hash;", Read("main.js"));
    Assert.DoesNotContain("pushState", keep);

    // Read from the address when the tab is built, and whenever show() shows the Marks tab.
    var section = MyMarks();
    Assert.Contains("const asked = String(ctx.sub || '').startsWith('marks') ? markViewOf(ctx.query) : null;", section);
    Assert.Contains("return MARK_VIEWS.some((v) => v.value === name) ? name : null;", section);
    Assert.Contains("if (name && name !== view.filter) choose(name);\n      else keepMarksAddress(view.filter);", section);
    Assert.Contains("keepMarksAddress(name);\n      start();", Between(section, "const choose = (name) => {", "};"));

    var show = Between(library, "function show(sub, query) {", "function groupKeyOf(");
    Assert.Contains("if (tab === 'marks') view.ctx.marksView?.(query);", show);
    // After the panels' hidden flags, so the address is written for the Marks panel only once it is shown.
    Assert.True(show.IndexOf("panel.hidden = key !== tab;", StringComparison.Ordinal) < show.IndexOf("view.ctx.marksView", StringComparison.Ordinal));
    var mount = Between(library, "function mount(ctx)", "function show(sub, query)");
    Assert.True(mount.IndexOf("view = { ctx,", StringComparison.Ordinal) < mount.IndexOf("myMarks(ctx)", StringComparison.Ordinal));
  }

  [Fact]
  public void Page_OverlappingARemoveOrUndo_IsAskedAgain()
  {
    var section = MyMarks();
    // A page that answers while a Remove or Undo started meanwhile or is still pending may carry a total from before or
    // after the server's change, in either response order: it is dropped and asked again once no edit is pending.
    var load = Between(section, "const loadPage = async () => {", "const build = (key) => {");
    Assert.Contains("const edits = view.edits;", load);
    Assert.Contains("if (edits !== view.edits || view.pending) {\n        if (view.pending) view.waiting = seq;\n        else { view.loading = false; loadPage(); }\n        return;\n      }", load);
    Assert.True(load.IndexOf("if (edits !== view.edits", StringComparison.Ordinal) < load.IndexOf("view.total = page.total;", StringComparison.Ordinal));
    Assert.True(load.IndexOf("const edits = view.edits;", StringComparison.Ordinal) < load.IndexOf("await ctx.listMarks", StringComparison.Ordinal));
    // A page that would start while an edit is pending (Remove on the last row, the sentinel firing before DELETE
    // answers) is not requested with the old row count: it waits, still loading, and is asked for after the edit.
    const string defer = "if (view.pending) {\n        view.waiting = seq;\n        return;\n      }";
    Assert.Contains(defer, load);
    Assert.True(load.IndexOf("view.loading = true;", StringComparison.Ordinal) < load.IndexOf(defer, StringComparison.Ordinal));
    Assert.True(load.IndexOf(defer, StringComparison.Ordinal) < load.IndexOf("requested = Math.max(", StringComparison.Ordinal));
    // The edit counts from its start (before the request) until its bookkeeping is done, then a waiting page is asked again.
    var edit = Between(section, "const edit = (work) =>", "const remove = edit(");
    Assert.True(edit.IndexOf("view.edits += 1;\n      view.pending += 1;", StringComparison.Ordinal) < edit.IndexOf("await work(...args);", StringComparison.Ordinal));
    Assert.Contains("} finally {\n        view.pending -= 1;\n        if (!view.pending && view.waiting) {", edit);
    Assert.Contains("if (again) { view.loading = false; loadPage(); }", edit);
    Assert.Contains("const restore = edit(async (m, li) => {", section);
    Assert.DoesNotContain("view.edits += 1;", Between(section, "const remove = edit(", "const start = () => {"));
    Assert.Contains("view.waiting = 0;", Between(section, "const start = () => {", "keepMarksAddress(view.filter);\n    start();"));
  }

  private static string Lookup() => Between(MyMarks(), "const lookup = async (keys, seq) => {", "ctx.lookupMarks = () => {");

  [Fact]
  public void Rows_AskLookupOncePerPage()
  {
    var section = MyMarks();
    var load = Between(section, "const loadPage = async () => {", "const build = (key) => {");
    // One call site, with the keys of the whole page (items), after the loop that skips repeated rows and before the
    // move to the next page for a page that added nothing: a fully repeated page is looked up too.
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(load, @"\blookup\("));
    // The other is the re-ask of ctx.lookupMarks.
    Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(section, @"\blookup\(").Count);
    Assert.Contains("const keys = items.map((m) => m.key);\n      view.pages.push(keys);\n      lookup(keys, seq);", load);
    var call = load.IndexOf("lookup(keys, seq);", StringComparison.Ordinal);
    Assert.True(load.IndexOf("if (view.shown.has(m.key)) continue;", StringComparison.Ordinal) < call);
    Assert.True(call < load.IndexOf("if (!view.done && added === 0) {", StringComparison.Ordinal));

    var main = Read("main.js");
    Assert.Contains("lookupTitles: (body) => api(`/playlists/${playlist.id}/library/titles/lookup`, { method: 'POST', body }),", Between(main, "function showLibrary(playlist)", "function showSharedLibrary("));
    Assert.Contains("lookupTitles: (body) => api(`${base}/titles/lookup`, { method: 'POST', body: scoped(body) }),", Between(main, "function showSharedLibrary(", "function linkCard("));

    // A page loaded before the list waits for it; the switch asks again for every page loaded so far.
    var lookup = Lookup();
    Assert.Contains("if (!ctx.list || !keys.length) return;", lookup);
    Assert.Contains("answer = await ctx.lookupTitles({ cacheKey: ctx.list.cacheKey, includeDisabled: includeOff, keys });", lookup);
    var library = Read("library.js");
    Assert.Contains("ctx.list = result;\n    // Marks pages loaded before the list get their lookup now (#455).\n    ctx.lookupMarks?.();", Between(library, "async function load(", "const TYPES = ["));
    Assert.Contains("run();\n      onToggle();\n      ctx.lookupMarks?.();", Between(library, "ctx.onIncludeOff = () => {", "};"));
    Assert.Contains("ctx.lookupMarks = () => {\n      view.asks += 1;\n      for (const keys of view.pages) lookup(keys, view.seq);\n    };", section);
    Assert.Contains("view.pages = [];\n      rows.clear();", Between(section, "const start = () => {", "keepMarksAddress(view.filter);\n    start();"));

    // An answer for an earlier setting of the switch, a view or a Library left since changes nothing.
    Assert.Contains("const ask = view.asks;", lookup);
    Assert.Contains("if (seq !== view.seq || ask !== view.asks || !ctx.isCurrent() || ctx.controller.signal.aborted) return;\n      for (const key of keys) {", lookup);
  }

  [Fact]
  public void LookupFailure_LeavesTheNextScreenAlone()
  {
    var lookup = Lookup();
    // The catch holds only its guard: cancellation and the route are checked before accessGone, and a failed current
    // lookup falls through with no answer, so every row of its keys (also one matched before) reads "Not in this list".
    Assert.Contains("let answer = null;", lookup);
    var catchBody = Between(lookup, "} catch (err) {", "\n      }");
    Assert.Equal("} catch (err) {\n        if (seq !== view.seq || ask !== view.asks || !ctx.isCurrent() || ctx.controller.signal.aborted || accessGone(ctx, err)) return;", catchBody);
    Assert.Contains("const title = answer?.titles?.[key] || null;", lookup);
    Assert.Contains("if (!state || JSON.stringify(title) === JSON.stringify(state.title)) continue;", lookup);
  }

  [Fact]
  public void LookupSuccess_TurnsAnAbsentRowIntoACardLink()
  {
    var lookup = Lookup();
    Assert.Contains("state.title = title;\n        build(key);\n      }", lookup);
    // The row is built from that title: it opens the card only with an answer.
    var build = Between(MyMarks(), "const build = (key) => {", "return li;");
    Assert.Contains("const { mark: m, title: t } = state;", build);
    Assert.Contains("markKey: t ? key : null", build);
  }

  [Fact]
  public void Row_InList_OpensTheCard_OtherwiseSaysNotInThisList()
  {
    var build = Between(MyMarks(), "const build = (key) => {", "return li;");
    // Film: the group; series: bold seasons and episodes · group; otherwise one wording; then the mark's words.
    Assert.Contains("const where = !t ? ['Not in this list'] : type === 'Series' ? [h('b', { text: seasonsLine(ctx, t) }), ' · ', t.group] : [t.group];", build);
    Assert.Contains("h('span', { class: 'lib-meta' }, [...where, ...words].flatMap((p, i) => (i ? [' · ', p] : [p])))", build);
    Assert.DoesNotContain("'Film'", build);
    // The card with the lookup's name, group and enabled, the list's cacheKey and the mark's key.
    Assert.Contains("const e = { name: t ? t.name : m.title, title: m.title, year: m.year, contentType: type, markKey: t ? key : null, enabled: t?.enabled };", build);
    Assert.Contains("filmName(ctx, e, t?.group, ctx.list?.cacheKey)", build);
    Assert.Contains("li.className = opens(ctx, e) ? 'lib-row' : 'lib-row is-static';", build);
    Assert.Contains("Object.assign(li.dataset, { type, key });", build);
    // Remove unchanged, before the chevron that every row renders.
    const string remove = "h('button', { class: 'btn btn-sm', type: 'button', 'aria-label': `Remove the mark on ${m.title}`, onclick: () => remove(m, li) }, 'Remove'),";
    const string chev = "h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, icon('chevron')));";
    Assert.Contains(remove, build);
    Assert.True(build.IndexOf(remove, StringComparison.Ordinal) < build.IndexOf(chev, StringComparison.Ordinal));
    Assert.Contains("h('div', { class: 'lib-list' }, list, foot));", MyMarks());
    Assert.DoesNotContain("Not in your list", Read("library.js"));

    var css = Read("library.css");
    Assert.Contains(".lib-row.is-static .lib-chev { visibility: hidden; }", css);
    Assert.Contains(".library-mylist .lib-row > .btn { position: relative; z-index: 1; flex-shrink: 0; min-height: var(--control-h); }", css);
    foreach (var gone in new[] { ".library-hit", ".library-mylist > .card", ".library-mylist .search-hit-name" })
      Assert.DoesNotContain(gone, css);
  }

  [Fact]
  public void LookupRebuild_KeepsTheRowElement_ForRemoveAndUndo()
  {
    var build = Between(MyMarks(), "const build = (key) => {", "return li;");
    // The same <li> for the row's whole life: its children replaced, hidden left alone.
    Assert.Contains("const li = state.li || h('li');", build);
    Assert.Contains("li.replaceChildren(", build);
    Assert.Contains("state.li = li;", build);
    Assert.DoesNotContain("replaceWith", build + Lookup());
    Assert.DoesNotContain(".hidden", build + Lookup());

    // Undo builds the restored row with the lookup answer of the row it replaces, and moves its key in pages.
    var restore = Between(MyMarks(), "const restore = edit(async (m, li) => {", "const start = () => {");
    Assert.Contains("const title = rows.get(m.key)?.title ?? null;\n      rows.delete(m.key);\n      rows.set(mark.key, { li: null, mark, title });", restore);
    Assert.Contains("if (mark.key !== m.key) view.pages = view.pages.map((keys) => keys.map((k) => (k === m.key ? mark.key : k)));", restore);
    Assert.True(restore.IndexOf("rows.set(mark.key", StringComparison.Ordinal) < restore.IndexOf("li.replaceWith(build(mark.key));", StringComparison.Ordinal));
  }

  [Fact]
  public void EmptyStates_HaveTheThreeTexts()
  {
    var section = MyMarks();
    Assert.Contains("watchlist: 'Nothing on your watchlist yet. Add a film or series with the bookmark in its card.',", section);
    Assert.Contains("all: 'No marks yet. Mark a film or series as seen, or give it a thumb, in the search or a group.',", section);
    Assert.Contains("else foot.replaceChildren(status(MARK_EMPTY[view.filter] || 'No marks of this kind.'));", section);
  }
}
