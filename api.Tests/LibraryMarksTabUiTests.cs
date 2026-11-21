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

  private static string MyMarks() => Between(Read("library.js"), "// My marks (#299)", "function syncRows(");

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
    Assert.True(load.IndexOf("if (rows.has(m.key)) continue;", StringComparison.Ordinal) < call);
    Assert.True(call < load.IndexOf("if (!view.done && added === 0) {", StringComparison.Ordinal));

    var main = Read("main.js");
    Assert.Contains("lookupTitles: (body) => api(`/playlists/${playlist.id}/library/titles/lookup`, { method: 'POST', body }),", Between(main, "function showLibrary(playlist)", "function showSharedLibrary("));
    Assert.Contains("lookupTitles: (body) => api(`${base}/titles/lookup`, { method: 'POST', body: scoped(body) }),", Between(main, "function showSharedLibrary(", "function linkCard("));

    // A page loaded before the list waits for it; the switch asks again for every page loaded so far.
    var lookup = Lookup();
    Assert.Contains("if (!ctx.list || !keys.length) return;", lookup);
    Assert.Contains("answer = await ctx.lookupTitles({ cacheKey: ctx.list.cacheKey, includeDisabled: includeOff, keys });", lookup);
    var library = Read("library.js");
    Assert.Contains("ctx.list = result;\n    ctx.listState = 'ready';\n    // Marks pages loaded before the list get their lookup now (#455).\n    ctx.lookupMarks?.();", Between(library, "async function load(", "const TYPES = ["));
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
    Assert.Contains("const opened = t ? { ...e, name: t.name, enabled: t.enabled } : { ...e, markKey: null };", build);
  }

  [Fact]
  public void Row_InList_OpensTheCard_OtherwiseSaysNotInThisList()
  {
    var build = Between(MyMarks(), "const build = (key) => {", "return li;");
    // Film: the group; series: bold seasons and episodes · group; otherwise one wording (#456: no mark words).
    Assert.Contains("const where = !t ? 'Not in this list' : type === 'Series' ? [h('b', { text: seasonsLine(ctx, t) }), ' · ', t.group] : t.group;", build);
    Assert.Contains("h('span', { class: 'lib-meta' }, where)", build);
    Assert.DoesNotContain("'Film'", build);
    // The card with the lookup's name, group and enabled, the list's cacheKey and the mark's key.
    Assert.Contains("filmName(ctx, opened, t?.group, ctx.list?.cacheKey)", build);
    Assert.Contains("li.className = opens(ctx, opened) ? 'lib-row' : 'lib-row is-static';", build);
    Assert.Contains("Object.assign(li.dataset, { type, key });", build);
    // The controls before the chevron that every row renders.
    const string controls = "...rowMarks(ctx, e),";
    const string chev = "h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, icon('chevron')));";
    Assert.Contains(controls, build);
    Assert.True(build.IndexOf(controls, StringComparison.Ordinal) < build.IndexOf(chev, StringComparison.Ordinal));
    Assert.Contains("h('div', { class: 'lib-list' }, list, foot));", MyMarks());
    Assert.DoesNotContain("Not in your list", Read("library.js"));

    var css = Read("library.css");
    Assert.Contains(".lib-row.is-static .lib-chev { visibility: hidden; }", css);
    Assert.DoesNotContain(".library-mylist .lib-row > .btn", css);
    foreach (var gone in new[] { ".library-hit", ".library-mylist > .card", ".library-mylist .search-hit-name" })
      Assert.DoesNotContain(gone, css);
  }

  // The marks core of #298, #420 and #456: the rule every control of a title follows.
  private static string Core() => Between(Read("library.js"), "const MARK_KINDS = {", "// Browse (#287)");
  private static string SetMark() => Between(Core(), "async function setMark(ctx, name, kind, key, next) {", "\n  }\n");
  private static string MarkControls() => Between(Core(), "function markControls(ctx, entry) {", "\n  }\n");
  private static string CardMarks() => Between(Read("library.js"), "function cardMarks(ctx, entry, answer) {", "\n  }\n");

  [Fact]
  public void Row_UsesTheSharedMarkControls_NoRemoveButton()
  {
    var build = Between(MyMarks(), "const build = (key) => {", "return li;");
    // The search rows' controls with the name "<title> (<year>)", or the title alone, and the mark's own key.
    Assert.Contains("const e = { name: m.year ? `${m.title} (${m.year})` : m.title, title: m.title, year: m.year, contentType: type, markKey: key, keepMark: true };", build);
    Assert.Contains("...rowMarks(ctx, e),", build);
    Assert.Contains("paintMarks(li.querySelector('.library-marks'));", build);
    // The same <li> for the row's whole life, rebuilt by a lookup.
    Assert.Contains("const li = state.li || h('li');", build);
    Assert.Contains("li.replaceChildren(", build);

    // A rebuilt row (keepMark) paints from marks and leaves the object in place; only a key marks lacks takes a value.
    var controls = MarkControls();
    Assert.Contains("if (!entry.keepMark) applyMark(ctx, key, entry, entry.asked);\n    else if (!marks.has(key)) marks.set(key, markOf(entry));", controls);
    Assert.DoesNotContain("marks.set(key, { seen: entry.seen", controls);

    // Remove and its bookkeeping are gone (a row's Undo returned with #448); DELETE stays in the API, unused by the frontend.
    var library = Read("library.js");
    var main = Read("main.js");
    foreach (var gone in new[] { "'Remove'", "undo(", "removeMark", "restore(", "view.edits", "view.pending", "view.waiting", "const edit = " })
      Assert.DoesNotContain(gone, library);
    Assert.DoesNotContain("removeMark", main);
    Assert.DoesNotContain("/marks/${encodeURIComponent(key)}", main);
    Assert.Contains("mutations.MapDelete(\"/{key}\"", Read("api/Endpoints/MarkEndpoints.cs"));
  }

  [Fact]
  public void CardOpenedDuringASave_LeavesThePendingMarkInPlace()
  {
    // The card request is stamped with the clock; the answer carries it.
    var library = Read("library.js");
    var load = Between(library, "let answer = answers.get(key);", "renderFilm(ctx, answer);");
    Assert.Contains("const asked = ++clock;", load);
    Assert.True(load.IndexOf("const asked = ++clock;", StringComparison.Ordinal) < load.IndexOf("await ctx.seriesCard", StringComparison.Ordinal));
    Assert.Contains("answer.asked = asked;\n      if (answer.status === 'matched') answers.set(key, answer);", load);

    // While a save of the title is on its way (saving) or one overlapped the request (markAt), the card shows marks'
    // value and its controls keep the object: the failed save then still finds its own object, rolls back and says so.
    var card = CardMarks();
    Assert.Contains("const pending = marks.has(key) && !freshFor(key, answer?.asked);\n    const given = pending || !answer?.marks || owner === key ? now", card);
    Assert.Contains("...given, keepMark: pending, asked: answer?.asked };", card);
    Assert.Contains("const freshFor = (key, asked) => !saving.has(key) && !(markAt.get(key) > (asked ?? Infinity));", Core());
    var set = SetMark();
    Assert.Contains("if (marks.get(key) !== next) return;\n      // The API key goes back to its own value unless a later tap replaced it.\n      if (alias && marks.get(alias) === next) marks.set(alias, prior || before);\n      marks.set(key, before);\n      repaint();\n      ctx.toast(`The mark was not changed. ${err.message || ''}`.trim(), { error: true });", set);
  }

  [Fact]
  public void SearchOrGroupsRowDuringASave_LeavesThePendingMarkInPlace()
  {
    // Search, its next pages, Groups and Home's New in <group> (#427) ask through askTitles, which stamps every title
    // with the clock at the request.
    var library = Read("library.js");
    Assert.Equal(4,System.Text.RegularExpressions.Regex.Matches(library, @"await askTitles\(ctx, ").Count);
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(library, @"ctx\.titles\("));
    var ask = Between(Core(), "async function askTitles(ctx, params, signal) {", "\n  }\n");
    Assert.Contains("const asked = ++clock;\n    const found = await ctx.titles(params, signal);\n    for (const t of found?.titles || []) t.asked = asked;", ask);

    // A row of such an answer (rowMarks → markControls) applies its mark only when fresh; during a save it is not, so
    // the object stays and the failed save rolls back on every control (paintKey) and toasts.
    var apply = Between(Core(), "function applyMark(ctx, key, m, asked) {", "\n  }\n");
    Assert.Contains("if (marks.has(key) && !freshFor(key, asked)) return;", apply);
    var set = SetMark();
    Assert.Contains("saving.set(key, (saving.get(key) || 0) + 1);", set);
    Assert.True(set.IndexOf("saving.set(key, (saving.get(key) || 0) + 1);", StringComparison.Ordinal) < set.IndexOf("await ctx.setMark(", StringComparison.Ordinal));
    Assert.Contains("const repaint = () => [key, alias].forEach((k) => k && paintKey(ctx, k));", set);
    Assert.Contains("for (const el of ctx.root.querySelectorAll('.library-marks')) if (el.dataset.key === key) paintMarks(el);", Between(Core(), "function paintKey(ctx, key) {", "\n  }\n"));
  }

  [Fact]
  public void FreshMarksPage_RepaintsTheTitlesControlsInSearchAndGroups()
  {
    var load = Between(MyMarks(), "const loadPage = async () => {", "const build = (key) => {");
    // Every item, repeated keys included, before the skip; applying repaints every mounted control of the key.
    Assert.Contains("applyMark(ctx, m.key, m, at);\n        if (rows.has(m.key)) continue;", load);
    var apply = Between(Core(), "function applyMark(ctx, key, m, asked) {", "\n  }\n");
    Assert.Contains("marks.set(key, markOf(m));", apply);
    Assert.Contains("paintKey(ctx, key);", apply);
    // The next tap on an earlier Search row starts from marks when it is handled, not from a value taken at render.
    var core = Core();
    Assert.Contains("onclick: () => setMark(ctx, entry.name, kind, key, next),", core);
    Assert.Contains("onclick: () => setMark(ctx, entry.name, MARK_KINDS[entry.contentType], key, toggleSeen),", core);
    Assert.Contains("onclick: () => setMark(ctx, entry.name, MARK_KINDS[entry.contentType], key, (m) => ({ ...m, watchlist: !m.watchlist })),", core);
    Assert.Contains("if (typeof next === 'function') next = next(marks.get(key));", SetMark());
    Assert.DoesNotContain("marks.get(key)))", core);
    // A page fetched during a save is rendered at once: nothing in loadPage waits for saving.
    Assert.DoesNotContain("saving", load);
    Assert.Contains("if (seq !== view.seq || !ctx.isCurrent()) return;\n      // A save that started or ended during the request", load);
    Assert.True(load.IndexOf("view.loaded = true;", StringComparison.Ordinal) < load.IndexOf("if (overlapped) {", StringComparison.Ordinal));
  }

  // The paging and the count after a saved mark (#457).
  private static string LoadPage() => Between(MyMarks(), "const loadPage = async () => {", "const build = (key) => {");

  [Fact]
  public void EditedRow_KeepsTheCountAndThePaging()
  {
    var section = MyMarks();
    var load = LoadPage();
    // What fits each view, and the rows shown that no longer fit it.
    Assert.Contains("watchlist: (m) => m.watchlist === true, all: (m) => m.seen || m.rating !== 0 || m.watchlist === true,", section);
    Assert.Contains("seen: (m) => m.seen, liked: (m) => m.rating === 1, disliked: (m) => m.rating === -1,", section);
    Assert.Contains("const left = () => [...view.shown].filter((key) => marks.has(key) && !MARK_FITS[view.filter](marks.get(key))).length;", load);

    // 51 Liked, a Like on page one cleared, then scroll: page two is computed from the 49 rows that still count
    // (title 51 now sits at position 50), and the view is complete only when those reach the total.
    Assert.Contains("requested = view.walk || Math.max(Math.floor((view.shown.size - left()) / MARKS_PAGE) + 1, view.minPage);", load);
    Assert.Contains("view.done = items.length < MARKS_PAGE || view.shown.size - left() >= page.total;", load);

    // A save moves saveTicks at its start and its end; the queue of pages waiting for saves runs once none is left.
    var set = SetMark();
    Assert.Contains("    }\n    saveTicks += 1;\n    marks.set(key, next);", set);
    Assert.Contains("if (keyAlias.has(key)) markAt.set(keyAlias.get(key), clock);\n        saveTicks += 1;\n        if (!blind()) [...blindQueue].forEach((fn) => blindQueue.delete(fn) && fn());\n        if (settled()) [...settleQueue].forEach((fn) => settleQueue.delete(fn) && fn());", set);
    Assert.Contains("const settled = () => !saving.size;", Core());
    Assert.Contains("const whenSettled = (fn) => (settled() ? fn() : settleQueue.add(fn));", Core());

    // The save overlapping page two, in both orders (committed before page two is read, which then comes back empty;
    // page one answered before the save while page two is asked after it), and a title marked Seen from Search moving
    // to the top while page two loads: the ticks taken before the request differ at the answer, or a save is on its way.
    Assert.True(load.IndexOf("const ticks = saveTicks, at = ++clock;", StringComparison.Ordinal) < load.IndexOf("await ctx.listMarks", StringComparison.Ordinal));
    Assert.Contains("const overlapped = ticks !== saveTicks || !settled();", load);
    // An overlapped page is shown but never ends the paging, even empty or short: it returns before done is used and
    // asks again once saves end. The answer of a view or Library left since starts nothing.
    var branch = Between(load, "if (overlapped) {", "if (!view.done && added === 0) {");
    Assert.Contains("view.loading = true;\n        paint();\n        whenSettled(() => {\n          if (seq !== view.seq || !ctx.isCurrent() || ctx.controller.signal.aborted) return;\n          view.loading = false;\n          loadPage();\n        });\n        return;", branch);
    Assert.True(load.IndexOf("list.append(build(m.key));", StringComparison.Ordinal) < load.IndexOf("if (overlapped) {", StringComparison.Ordinal));

    // 52 Liked, a Like cleared while page two loads, page two brings title 52; 102 Liked with 100 shown, a Like cleared
    // while page three loads: the walk asks 1, 2, 3 in order (requested + 1 after each settled page), not from the
    // rows the overlapped page appended and not 1 then 3, since the walk outranks minPage and the computed page.
    Assert.Contains("view.walk = overlapped ? 1 : view.walk && requested + 1;", load);
    Assert.True(load.IndexOf("view.walk = overlapped", StringComparison.Ordinal) < load.IndexOf("if (overlapped) {", StringComparison.Ordinal));

    // 101 Liked, opened during an unrelated save; after the walk showed 100 rows a Like is cleared and its save ends
    // before the next scroll: a walk under older ticks starts again at page 1.
    Assert.Contains("if (view.walkAt !== ticks) [view.walk, view.walkAt] = [view.walk && 1, ticks];", load);

    // minPage raised to 3 by a fully repeated page, then a mark cleared while page three loads moves title 100 onto
    // page two: minPage holds only under the ticks it was set at, checked before every request, so "Try again" after
    // a failed page three (which calls loadPage) asks page two.
    Assert.Contains("if (view.minAt !== ticks) view.minPage = 1;", load);
    Assert.Contains("view.minPage = requested + 1;\n        view.minAt = ticks;", load);
    var check = load.IndexOf("if (view.minAt !== ticks) view.minPage = 1;", StringComparison.Ordinal);
    Assert.True(check < load.IndexOf("try {", StringComparison.Ordinal));
    Assert.Contains("h('button', { class: 'btn btn-sm', type: 'button', onclick: loadPage }, 'Try again')", load);

    // A new view starts without a walk.
    Assert.Contains("view.walk = 0;", Between(section, "const start = () => {", "keepMarksAddress(view.filter);\n    start();"));
  }

  [Fact]
  public void Count_IsTheFirstSettledPages_UntilTheViewIsLoadedAgain()
  {
    var load = LoadPage();
    // Liked opened while the first Like of a title is saved, the first page empty: the first page's count (0) shows
    // only until the walk's settled page brings the title (1), and the overlapped empty page shows no empty message,
    // because paint() returns before the foot while loading.
    // Liked opened during another title's save with one row shown, that row's Like then cleared: an overlapped page
    // after the first never sets the count (no 1 → 2 → 1 while the walk's page still counts the title); the settled
    // page adds left(), so the count stays one, and after it no later page changes it.
    Assert.Contains("if (!view.counted && (!overlapped || !view.loaded)) view.total = page.total + left();\n      view.counted = view.counted || !overlapped;\n      view.loaded = true;", load);
    Assert.DoesNotContain("view.total = page.total;", load);
    Assert.Contains("if (view.loading) return;\n      if (!view.done) {", Between(MyMarks(), "const paint = () => {", "    };"));
    Assert.Contains("view.loaded = false;\n      view.counted = false;", Between(MyMarks(), "const start = () => {", "keepMarksAddress(view.filter);\n    start();"));
  }

  [Fact]
  public void AnswerOverlappedByASave_LeavesTheSavedMarkAlone()
  {
    var set = SetMark();
    // A save moves the clock at its start and its end and records both for its key: any answer asked before is older.
    Assert.Contains("saving.set(key, (saving.get(key) || 0) + 1);\n    markAt.set(key, ++clock);", set);
    Assert.Contains("} finally {\n      if (epoch === markEpoch) {\n        unsave(key);\n        if (alias) unsave(alias);\n        if (unknown) blindPending -= 1;\n        markAt.set(key, ++clock);", set);
    Assert.Contains("const unsave = (k) => (saving.get(k) > 1 ? saving.set(k, saving.get(k) - 1) : saving.delete(k));", Core());
    // An answer is applied only when markAt is not above its request clock; an applied one records that clock, so an
    // older Marks page arriving last (or the earlier of two pages) leaves it; markAt only grows.
    Assert.Contains("const freshFor = (key, asked) => !saving.has(key) && !(markAt.get(key) > (asked ?? Infinity));", Core());
    var apply = Between(Core(), "function applyMark(ctx, key, m, asked) {", "\n  }\n");
    Assert.Contains("if (asked) markAt.set(key, Math.max(markAt.get(key) || 0, asked));", apply);
    // A Marks page takes its clock before its request; a Search or Groups row passes its title's stamp; a late card
    // answer is checked against the save that happened while it loaded.
    var load = Between(MyMarks(), "const loadPage = async () => {", "const build = (key) => {");
    Assert.True(load.IndexOf("const at = ++clock;", StringComparison.Ordinal) < load.IndexOf("await ctx.listMarks", StringComparison.Ordinal));
    Assert.Contains("applyMark(ctx, key, entry, entry.asked);", MarkControls());
    Assert.Contains("const pending = marks.has(key) && !freshFor(key, answer?.asked);", CardMarks());
    // A lookup rebuild never applies the page's (older) mark again.
    Assert.Contains("keepMark: true", Between(MyMarks(), "const build = (key) => {", "return li;"));
  }

  [Fact]
  public void AnotherUser_StartsWithoutTheLastUsersMarksAndSaves()
  {
    // Both contexts of main.js pass the same user; the shared Library's scope too, so own ↔ shared resets nothing.
    var main = Read("main.js");
    Assert.Contains("user: libraryUser(),", Between(main, "function showLibrary(playlist)", "function libraryUser("));
    Assert.Contains("user: libraryUser(),", Between(main, "function showSharedLibrary(", "function linkCard("));
    Assert.Contains("return state.user?.userName || state.user?.email || null;", Between(main, "function libraryUser(", "\n}"));

    // Only a changed user clears the maps and starts a new epoch.
    var mount = Between(Read("library.js"), "function mount(ctx)", "const base = ");
    Assert.Contains("if (ctx.user !== markUser) {\n      markUser = ctx.user;\n      markEpoch += 1;\n      blindPending = 0;\n      for (const m of [marks, markAt, saving, keyAlias, answers, blindQueue]) m.clear();\n    }", mount);

    // A save of the last account changes nothing when it answers, fails or ends.
    var set = SetMark();
    Assert.True(set.IndexOf("const epoch = markEpoch;", StringComparison.Ordinal) < set.IndexOf("await ctx.setMark(", StringComparison.Ordinal));
    Assert.Contains("await ctx.setMark({ name, kind, seen: next.seen, rating: next.rating, watchlist: next.watchlist });\n      // A save of the last account changes nothing for this one.\n      if (epoch !== markEpoch) return;", set);
    Assert.Contains("} catch (err) {\n      // Another account signed in, or a later tap on the same title already replaced this change.\n      if (epoch !== markEpoch) return;", set);
    Assert.Contains("} finally {\n      if (epoch === markEpoch) {", set);
  }

  // A save from a similar film's card (#368) under its local key, carried to the title's API key (#458).
  private static string SyncRows() => Between(Read("library.js"), "function syncRows(ctx, key, mark) {", "\n  }\n");

  [Fact]
  public void SimilarCardSave_IsPendingUnderTheApiKeyToo()
  {
    var set = SetMark();
    // A Seen film liked from the card, All opened before the save ends: the API key counts the save (saving), carries
    // its stamp and its mark, and every control of it is repainted, so the Marks row (keepMark) shows Like pressed and
    // its Seen tap computes from that mark: rating 1.
    Assert.Contains("const alias = keyAlias.get(key), prior = alias && marks.get(alias);", set);
    Assert.Contains("if (alias) {\n      saving.set(alias, (saving.get(alias) || 0) + 1);\n      markAt.set(alias, clock);\n      marks.set(alias, next);\n    }", set);
    Assert.Contains("const repaint = () => [key, alias].forEach((k) => k && paintKey(ctx, k));", set);
    // All of it before the request goes out, and the repaint after the marks are set.
    var request = set.IndexOf("await ctx.setMark(", StringComparison.Ordinal);
    var aliasMark = set.IndexOf("marks.set(alias, next);", StringComparison.Ordinal);
    Assert.True(set.IndexOf("markAt.set(key, ++clock);", StringComparison.Ordinal) < aliasMark);
    Assert.True(aliasMark < set.IndexOf("repaint();", StringComparison.Ordinal));
    Assert.True(set.IndexOf("repaint();", StringComparison.Ordinal) < request);
    // A row or page answering meanwhile leaves the API key's pending mark (saving.has → not fresh); the save ends for
    // both keys, so a page asked afterwards is fresh again.
    Assert.Contains("const freshFor = (key, asked) => !saving.has(key) && !(markAt.get(key) > (asked ?? Infinity));", Core());
    Assert.Contains("unsave(key);\n        if (alias) unsave(alias);", set);
    Assert.Contains("onclick: () => setMark(ctx, entry.name, MARK_KINDS[entry.contentType], key, toggleSeen),", Core());
    Assert.Contains("const toggleSeen = (m) => (m.seen ? { ...m, seen: false } : { ...m, seen: true, watchlist: false });", Core());
  }

  [Fact]
  public void SimilarCardSave_Failed_RollsTheApiKeyBackToItsOwnValue()
  {
    var set = SetMark();
    var failure = Between(set, "} catch (err) {", "} finally {");
    // Liked from the card, Seen from the Marks row, then a failed request to clear the Like from the card: the row's
    // Seen save replaced the API key's mark, so it stays; untouched, the API key takes its own prior value, not the
    // card's before. prior is read before the save sets the alias, so it is the value the row showed.
    Assert.Contains("if (alias && marks.get(alias) === next) marks.set(alias, prior || before);", failure);
    Assert.True(set.IndexOf("prior = alias && marks.get(alias);", StringComparison.Ordinal) < set.IndexOf("marks.set(alias, next);", StringComparison.Ordinal));
    // Never undefined into marks: paintMarks reads m.seen. An alias is only recorded together with a mark under the
    // API key (syncRows sets it even when no row showed it), so prior is there; before is the fallback.
    var sync = SyncRows();
    Assert.DoesNotContain("if (!marks.has(key)) return;", sync);
    Assert.Contains("marks.set(key, mark);\n    paintKey(ctx, key);", sync);
    var success = Between(set, "const real = saved?.key || keyAlias.get(key);", "} catch (err) {");
    Assert.Contains("keyAlias.set(key, real);\n        syncRows(ctx, real, marks.get(key));", success);
    Assert.Contains("const m = marks.get(el.dataset.key);\n    const [seen, up, down] = el.children;\n    seen.setAttribute('aria-pressed', String(m.seen));", Core());
    // The rollback repaints both keys before the toast.
    Assert.True(failure.IndexOf("marks.set(alias, prior || before);", StringComparison.Ordinal) < failure.IndexOf("repaint();", StringComparison.Ordinal));
    Assert.True(failure.IndexOf("repaint();", StringComparison.Ordinal) < failure.IndexOf("ctx.toast(", StringComparison.Ordinal));
    // A later answer of the card's save does not carry its mark over a later tap on the API key's rows.
    Assert.Contains("if (real && real !== key && (real !== alias || marks.get(alias) === next)) {", success);
  }

  [Fact]
  public void FirstSimilarCardSave_HoldsTapsAndMarksPagesUntilItsKeyIsKnown()
  {
    var core = Core();
    var set = SetMark();
    // Blind: a save of a similar card's local key with no API key yet.
    Assert.Contains("const blind = () => blindPending > 0;", core);
    // (d) Two card taps during the first PUT, then a tap on the title's row: the second card save started without an
    // alias, so it is counted when it starts and stays blind after the first answer set the alias. The row tap waits
    // until the second save has answered too, and then computes from (and sends) the latest mark; the second save's
    // answer cannot paint the card's mark over a row save that went out before it.
    Assert.Contains("const unknown = key.startsWith('similar:') && !alias;\n    if (unknown) blindPending += 1;", set);
    Assert.True(set.IndexOf("if (unknown) blindPending += 1;", StringComparison.Ordinal) < set.IndexOf("await ctx.setMark(", StringComparison.Ordinal));
    var tail = set[set.IndexOf("} finally {", StringComparison.Ordinal)..];
    var counted = tail.IndexOf("if (unknown) blindPending -= 1;", StringComparison.Ordinal);
    Assert.True(counted >= 0 && counted < tail.IndexOf("if (!blind()) [...blindQueue]", StringComparison.Ordinal));
    Assert.DoesNotContain("keyAlias", Between(core, "const blind = ", ";\n"));
    Assert.Contains("markEpoch += 1;\n      blindPending = 0;", Read("library.js"));
    // (b) A tap on any other key, a row already on screen included, waits; it is taken before the next mark is
    // computed, so it computes from the mark in marks when it runs (rating 1 after the Like).
    var hold = "if (blind() && !key.startsWith('similar:')) return void blindQueue.add(() => setMark(ctx, name, kind, key, next));";
    Assert.Contains(hold, set);
    Assert.True(set.IndexOf(hold, StringComparison.Ordinal) < set.IndexOf("if (typeof next === 'function') next = next(marks.get(key));", StringComparison.Ordinal));
    // Held taps run in the order tapped (a Set keeps insertion order) once the save has answered: after syncRows gave the
    // API key its mark, and before the Marks pages so that these wait for the held saves too.
    var end = set[set.IndexOf("} finally {", StringComparison.Ordinal)..];
    var release = end.IndexOf("if (!blind()) [...blindQueue].forEach((fn) => blindQueue.delete(fn) && fn());", StringComparison.Ordinal);
    Assert.True(end.IndexOf("unsave(key);", StringComparison.Ordinal) < release);
    Assert.True(release >= 0 && release < end.IndexOf("if (settled()) [...settleQueue]", StringComparison.Ordinal));
    Assert.Contains("const blindQueue = new Set();", core);
    // (a) A Marks page answering meanwhile is not rendered: it returns before any applyMark or row and walks from
    // page 1 once saves end, so no row shows the title unliked and the walk brings the Like.
    var load = LoadPage();
    var wait = load.IndexOf("if (blind()) {", StringComparison.Ordinal);
    Assert.True(wait >= 0 && wait < load.IndexOf("applyMark(ctx, m.key, m, at);", StringComparison.Ordinal));
    Assert.True(wait < load.IndexOf("view.loaded = true;", StringComparison.Ordinal));
    Assert.Contains("if (blind()) {\n        return void whenSettled(() => {\n          if (seq !== view.seq || !ctx.isCurrent() || ctx.controller.signal.aborted) return;\n          [view.walk, view.loading] = [1, false];\n          loadPage();\n        });\n      }", load);
    // (c) Search, Groups and the card are judged per key: blind is in neither freshFor nor applyMark.
    Assert.Contains("const freshFor = (key, asked) => !saving.has(key) && !(markAt.get(key) > (asked ?? Infinity));", core);
    Assert.DoesNotContain("blind", Between(core, "function applyMark(ctx, key, m, asked) {", "\n  }\n"));
    Assert.DoesNotContain("blind", CardMarks());
    // Another signed-in user starts without held taps.
    Assert.Contains("[marks, markAt, saving, keyAlias, answers, blindQueue]) m.clear();", Read("library.js"));
  }

  [Fact]
  public void FirstSimilarCardSave_AnsweringBeforeAMarksPage_LeavesThePageRenderable()
  {
    var set = SetMark();
    // The save's answer puts the saved mark under the API key even when marks has none, and the end of the save stamps
    // it with the clock: the Marks GET asked earlier finds markAt above its clock and leaves the saved mark.
    Assert.DoesNotContain("if (!marks.has(key)) return;", SyncRows());
    var end = set[set.IndexOf("} finally {", StringComparison.Ordinal)..];
    Assert.Contains("markAt.set(key, ++clock);\n        // The API key takes the same stamp, also the one the first save's answer just gave its mark.\n        if (keyAlias.has(key)) markAt.set(keyAlias.get(key), clock);", end);
    // A page key marks has no value for takes the server's mark, so the row renders; the page overlapped the save
    // (saveTicks moved) and walks again.
    Assert.Contains("if (marks.has(key) && !freshFor(key, asked)) return;\n    marks.set(key, markOf(m));", Core());
    var load = LoadPage();
    Assert.Contains("const overlapped = ticks !== saveTicks || !settled();", load);
    Assert.Contains("view.walk = overlapped ? 1 : view.walk && requested + 1;", load);
  }

  [Fact]
  public void EmptyStates_HaveTheThreeTexts()
  {
    var section = MyMarks();
    Assert.Contains("watchlist: 'Nothing on your watchlist yet. Add a film or series with the bookmark in its card.',", section);
    Assert.Contains("all: 'No marks yet. Mark a film or series as seen, or give it a thumb, in the search or a group.',", section);
    Assert.Contains("else foot.replaceChildren(status(MARK_EMPTY[view.filter] || 'No marks of this kind.'));", section);
  }

  // #448: a Marks row leaves when its mark no longer fits the view.
  private static string Changed() => Between(MyMarks(), "tab.marksChanged = (key, mark, how) => {", "tab.marksCleared =");

  private static void InOrder(string source, params string[] parts)
  {
    var at = -1;
    foreach (var part in parts)
    {
      var next = source.IndexOf(part, at + 1, StringComparison.Ordinal);
      Assert.True(next > at, $"'{part}' not found after the previous part.");
      at = next;
    }
  }

  [Fact]
  public void EmptyMarkOrWrongView_LeavesWithUndoToast()
  {
    // The fit rule of each view.
    var fits = Between(MyMarks(), "const MARK_FITS = {", "};");
    Assert.Contains("watchlist: (m) => m.watchlist === true, all: (m) => m.seen || m.rating !== 0 || m.watchlist === true,", fits);
    Assert.Contains("seen: (m) => m.seen, liked: (m) => m.rating === 1, disliked: (m) => m.rating === -1,", fits);
    var changed = Changed();
    // Hidden in place, out of view.shown, the count lowered; back in view.shown when it fits again.
    InOrder(changed, "const fits = MARK_FITS[view.filter](mark);", "if (fits === !r.li.hidden) return;", "r.li.hidden = !fits;",
      "view.total += fits ? 1 : -1;", "if (fits) view.shown.add(key);", "else view.shown.delete(key);", "paint();",
      // The hidden tab updates row and count silently; the check comes before the toast.
      "if (fits || tab.panels.marks.hidden) return;", "ctx.toast(");
    Assert.Contains("const text = view.filter === 'watchlist' ? `${m.title} is off your watchlist` : `Mark on ${m.title} removed`;", changed);
    Assert.Contains("const name = m.year ? `${m.title} (${m.year})` : m.title;", changed);
    Assert.Contains("const offer = () => ctx.toast(text, { action: { label: 'Undo', onClick: () => {", changed);
    // Undo sets the previous (last fitting) mark again, only while the row is still out.
    Assert.Contains("if (fits) r.fit = markOf(mark);", changed);
    Assert.Contains("if (r.li.hidden) setMark(ctx, name, m.kind, key, markOf(r.fit || m));", changed);
    // A page does not bring a row that left back.
    Assert.Contains("if (rows.has(m.key)) continue;", LoadPage());
    // One hook for every change: setMark and syncRows report to the tab; the Search chips use their own hook.
    Assert.Contains("const report = (how) => [key, alias].forEach((k) => k && view?.marksChanged?.(k, marks.get(k), how));", SetMark());
    InOrder(SetMark(), "marks.set(key, next);\n    repaint();\n    try {\n      report();\n      await ahead;");
    Assert.Contains("view?.marksChanged?.(key, mark);", SyncRows());
    Assert.Contains("const tab = library();", MyMarks());
    Assert.Contains("view.markFiltersChanged = () => {", Read("library.js"));
    // Empty state once every row is gone (paint counts the view).
    Assert.Contains("else if (view.total > 0) foot.replaceChildren();", MyMarks());
  }

  [Fact]
  public void Paging_CountsOnlyRowsInTheList_AcrossASave()
  {
    var load = LoadPage();
    // view.shown holds only rows in the list, so the next page starts after them (100 marks, 50 loaded, one left:
    // page 1 again, which now brings the mark that moved up).
    Assert.Contains("requested = view.walk || Math.max(Math.floor((view.shown.size - left()) / MARKS_PAGE) + 1, view.minPage);", load);
    Assert.Contains("view.done = items.length < MARKS_PAGE || view.shown.size - left() >= page.total;", load);
    // A page answering before the removal is committed overlapped the save: it does not set a counted total and the
    // list walks from page 1 once the save is done, so 101 marks less one show 100 rows with a count of 100.
    InOrder(load, "const overlapped = ticks !== saveTicks || !settled();",
      "if (!view.counted && (!overlapped || !view.loaded)) view.total = page.total + left();",
      "if (rows.has(m.key)) continue;", "view.walk = overlapped ? 1 : view.walk && requested + 1;");
    // The leaving row lowers the count itself; a returning one raises it.
    Assert.Contains("view.total += fits ? 1 : -1;", Changed());
    // A row that left is counted once: it leaves view.shown together with view.total, and left() counts only the rows
    // in view.shown, so neither the total (page.total + left()) nor the page arithmetic (view.shown.size - left()) adds
    // or subtracts it again. Seen with one mark: clear it, tap Seen while the PUT is on its way, the GET brings the old
    // record (row visible, count 1), the PUT succeeds and the row leaves (count 0); the settled page then counts
    // 0 + left() = 0, so the count stays 0 and the empty state shows.
    Assert.Contains("const left = () => [...view.shown].filter(", load);
    Assert.DoesNotContain("[...rows.keys()].filter(", load);
    var changed = Changed();
    InOrder(changed, "r.li.hidden = !fits;", "view.total += fits ? 1 : -1;", "if (fits) view.shown.add(key);\n      else view.shown.delete(key);");
    InOrder(Between(SetMark(), "const saved = await ctx.setMark(", "} finally {"), "if (marks.get(key) === next) report('saved');");
    Assert.Contains("if (!view.counted && (!overlapped || !view.loaded)) view.total = page.total + left();", load);
  }

  [Fact]
  public void Saves_ForOneTitle_GoOutInOrder_AndTheTabReconciles()
  {
    var set = SetMark();
    // The second PUT for a title waits for the first; the lane is released in finally.
    InOrder(set, "const lane = alias || key, ahead = writes.get(lane);", "writes.set(lane, mine);", "await ahead;",
      "const saved = await ctx.setMark(", "} finally {", "done();\n      if (writes.get(lane) === mine) writes.delete(lane);");
    // After a successful save the row is checked against the saved mark; a failure reports the rolled-back mark.
    InOrder(set, "if (marks.get(key) === next) report('saved');", "marks.set(key, before);\n      repaint();",
      "{ error: true });\n      report('failed');");
    var changed = Changed();
    // A fitting mark without a row loads the view again, lazily while the panel is hidden.
    Assert.Contains("const stale = () => (tab.panels.marks.hidden ? (view.stale = true) : start());", MyMarks());
    Assert.Contains("if (!r) return fits ? stale() : undefined;", changed);
    Assert.Contains("if (view.stale) start();", Between(MyMarks(), "ctx.marksView = (query) => {", "};"));
    Assert.Contains("view.stale = false;", Between(MyMarks(), "const start = () => {", "};"));
    // Outside the Watchlist a visible row that still fits moves to the top; a failed save puts it back after the row
    // with the remembered key.
    InOrder(changed, "if (fits && !r.li.hidden && how !== 'saved' && view.filter !== 'watchlist') {",
      "if (how === 'failed') r.back?.();", "const above = r.li.previousElementSibling?.dataset.key;",
      "r.back = () => { const p = rows.get(above)?.li; return p?.isConnected ? p.after(r.li) : list.prepend(r.li); };",
      "list.prepend(r.li);");
    // The 'saved' check comes after the move and before the fit decides the row: a saved mark that does not fit makes
    // a row a page brought back leave.
    InOrder(changed, "how !== 'saved'", "if (fits === !r.li.hidden) return;");
  }

  [Fact]
  public void SimilarFilmClear_FindsItsRowByKindAndKey()
  {
    var set = SetMark();
    Assert.Contains("} else if (!real && key.startsWith('similar:')) view?.marksCleared?.(name, kind, next);", set);
    var cleared = Between(MyMarks(), "tab.marksCleared = (name, kind, mark) => {", "};");
    Assert.Contains("const hit = [...rows].find(([, r]) => r.title?.name === name && r.mark.kind === kind && !r.li.hidden);", cleared);
    // Through syncRows, so the row's key gets the mark and the row leaves with Undo.
    Assert.Contains("if (hit) syncRows(ctx, hit[0], mark);", cleared);
    // The local key itself never reaches a row, and a Library left since changes nothing.
    Assert.Contains("if (key.startsWith('similar:') || !view.loaded || !ctx.isCurrent() || !list.isConnected) return;", Changed());
  }

  [Fact]
  public void Undo_WaitsForAModalCardToClose()
  {
    var changed = Changed();
    Assert.Contains("if (card.el?.matches(':modal')) card.el.addEventListener('close', () => { if (r.li.hidden) offer(); }, { once: true });", changed);
    Assert.Contains("else offer();", changed);
  }
}
