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

  private static string MyMarks() => Between(Read("library.js"), "// My marks (#299)", "// A mark saved under the API's key");

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
    Assert.Contains("view.loaded = view.counted = false;", Between(section, "const start = () => {", "start();"));

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
  public void Rows_AskLookupOncePerPage()
  {
    var section = MyMarks();
    var load = Between(section, "const loadPage = async () => {", "const build = (key) => {");
    // One call site with the whole page's keys, after the skip of repeated rows: a repeated page is looked up as well.
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(load, @"lookup\("));
    Assert.Contains("const keys = items.map((m) => m.key);\n      view.pages.push(keys);\n      lookup(keys, seq);", load);
    Assert.True(load.IndexOf("added += 1;\n      }", StringComparison.Ordinal) < load.IndexOf("lookup(keys, seq);", StringComparison.Ordinal));
    Assert.True(load.IndexOf("lookup(keys, seq);", StringComparison.Ordinal) < load.IndexOf("if (!view.done && added === 0)", StringComparison.Ordinal));

    var main = Read("main.js");
    Assert.Contains("lookupTitles: (body) => api(`/playlists/${playlist.id}/library/titles/lookup`, { method: 'POST', body }),", Between(main, "function showLibrary(playlist)", "function showSharedLibrary("));
    Assert.Contains("lookupTitles: (body) => api(`${base}/titles/lookup`, { method: 'POST', body: scoped(body) }),", Between(main, "function showSharedLibrary(", "function linkCard("));

    var lookup = Between(section, "const lookup = async (keys, seq) => {", "const start = () => {");
    // Not before the list is there; load() asks for the pages loaded so far once it sets ctx.list, and so does the switch.
    Assert.Contains("if (!ctx.list || !keys.length) return;", lookup);
    Assert.Contains("answer = await ctx.lookupTitles({ cacheKey: ctx.list.cacheKey, includeDisabled: includeOff, keys });", lookup);
    Assert.Contains("ctx.lookupMarks = () => {\n      view.asks += 1;\n      for (const keys of view.pages) lookup(keys, view.seq);\n    };", lookup);
    var library = Read("library.js");
    Assert.Contains("ctx.list = result;\n    // Marks pages loaded before the list get their lookup now (#447).\n    ctx.lookupMarks?.();", library);
    Assert.Contains("run();\n      onToggle();\n      ctx.lookupMarks?.();", Between(library, "ctx.onIncludeOff = () => {", "};"));
    // An answer for an earlier switch setting, a view left since or a Library left since changes nothing.
    Assert.Contains("const ask = view.asks;", lookup);
    Assert.Contains("if (seq !== view.seq || ask !== view.asks || !ctx.isCurrent() || ctx.controller.signal.aborted) return;", lookup);
    Assert.True(lookup.IndexOf("const ask = view.asks;", StringComparison.Ordinal) < lookup.IndexOf("await ctx.lookupTitles", StringComparison.Ordinal));
    Assert.Contains("view.pages = [];\n      rows.clear();", Between(section, "const start = () => {", "keepMarksAddress(view.filter);\n    start();"));
  }

  [Fact]
  public void LookupFailure_LeavesTheNextScreenAlone()
  {
    var lookup = Between(MyMarks(), "const lookup = async (keys, seq) => {", "ctx.lookupMarks = () => {");
    var failure = Between(lookup, "} catch (err) {", "\n      }");
    Assert.Contains("if (!(seq === view.seq && ask === view.asks && ctx.isCurrent() && !ctx.controller.signal.aborted) || accessGone(ctx, err)) return;", failure);
    var gone = failure.IndexOf("accessGone(", StringComparison.Ordinal);
    foreach (var check in new[] { "seq === view.seq", "ask === view.asks", "ctx.isCurrent()", "!ctx.controller.signal.aborted" })
    {
      var at = failure.IndexOf(check, StringComparison.Ordinal);
      Assert.True(at >= 0 && at < gone, $"'{check}' must come before accessGone.");
    }
    Assert.DoesNotContain("toast", failure);
    // A current failure that is no lost access goes on with no answer: every row of the page says "Not in this list".
    Assert.Contains("let answer = null;", lookup);
    Assert.Equal("} catch (err) {\n        if (!(seq === view.seq && ask === view.asks && ctx.isCurrent() && !ctx.controller.signal.aborted) || accessGone(ctx, err)) return;", failure);
    Assert.Contains("|| accessGone(ctx, err)) return;\n      }\n      if (seq !== view.seq || ask !== view.asks || !ctx.isCurrent() || ctx.controller.signal.aborted) return;\n      for (const key of keys) {", lookup);
    var apply = lookup[lookup.IndexOf("if (seq !== view.seq || ask !== view.asks", StringComparison.Ordinal)..];
    Assert.Contains("const title = answer?.titles?.[key] || null;", apply);
    Assert.Contains("state.title = title;\n        build(key);", apply);
    // A match, then a failed refresh: the row is built again without its title (see the next test), static.
    Assert.Contains("if (!state || JSON.stringify(title) === JSON.stringify(state.title)) continue;", apply);
    Assert.Contains("const meta = !t ? 'Not in this list'", Between(MyMarks(), "const build = (key) => {", "const lookup = async"));
    Assert.Contains("e.contentType === 'Movies' && !!e.markKey;", Read("library.js"));
    Assert.Contains("e.contentType === 'Series' && !!e.markKey;", Read("library.js"));
  }

  [Fact]
  public void LookupSuccess_TurnsAnAbsentRowIntoACardLink()
  {
    var section = MyMarks();
    // A row starts without a title; the answer's title is stored in its state and the row is built again in place.
    Assert.Contains("rows.set(m.key, { li: null, mark: m, title: null });\n        list.append(build(m.key));", section);
    var lookup = Between(section, "const lookup = async (keys, seq) => {", "ctx.lookupMarks = () => {");
    Assert.Contains("for (const key of keys) {\n        const state = rows.get(key);\n        const title = answer?.titles?.[key] || null;\n        if (!state || JSON.stringify(title) === JSON.stringify(state.title)) continue;\n        state.title = title;\n        build(key);\n      }", lookup);
    var build = Between(section, "const build = (key) => {", "const lookup = async");
    Assert.Contains("const { mark: m, title: t } = state;", build);
    Assert.Contains("const opened = t ? { ...e, name: t.name, enabled: t.enabled } : { ...e, markKey: null };", build);
    Assert.Contains("class: opens(ctx, opened) ? 'lib-row' : 'lib-row is-static'", build);
    Assert.Contains("state.li?.replaceWith(li);\n      state.li = li;", build);
  }

  [Fact]
  public void EditedRow_KeepsTheCountAndThePaging()
  {
    var section = MyMarks();
    var load = Between(section, "const loadPage = async () => {", "const build = (key) => {");
    // The first page sets the count, the first settled page fixes it: "1 mark" after each answer of a retried Liked.
    Assert.Contains("if (!view.counted && (!overlapped || !view.loaded)) view.total = page.total + left();\n      view.counted = view.counted || !overlapped;\n      view.loaded = true;", load);
    Assert.True(load.IndexOf("const overlapped =", StringComparison.Ordinal) < load.IndexOf("if (!view.counted", StringComparison.Ordinal));
    Assert.Contains("view.loaded = view.counted = false;\n      view.walk = 0;", section);
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(section, @"view\.total = page\.total"));
    // A row edited out of the view moved every later title up by one on the server: the next page counts it out.
    Assert.Contains("requested = view.walk || Math.max(Math.floor((view.shown.size - left()) / MARKS_PAGE) + 1, view.minPage);", load);
    // 102 Liked, 100 shown, title 1 cleared while page 3 loads: page 3 returns 102, 101 moved onto page 2. The retry
    // walks every page from 1 (1, 2, 3, 1, 2), not from the rows shown, so 101 and 102 are both reached.
    Assert.Contains("view.walk = overlapped ? 1 : view.walk && requested + 1;", load);
    Assert.DoesNotContain("view.back", section);
    Assert.Contains("const short = view.shown.size - left() < page.total;\n      view.done = items.length < MARKS_PAGE || !short;", load);
    Assert.Contains("const left = () => [...rows.keys()].filter((key) => marks.has(key) && !MARK_FITS[view.filter](marks.get(key))).length;", section);
    // A page a save overlapped is shown and asked again once no save is on its way; loading keeps the sentinel away.
    var ticks = load.IndexOf("const ticks = saveTicks, at = ++clock;", StringComparison.Ordinal);
    Assert.True(ticks >= 0 && ticks < load.IndexOf("await ctx.listMarks(", StringComparison.Ordinal));
    Assert.Contains("const overlapped = ticks !== saveTicks || saving.size > 0;\n      view.loading = false;", load);
    Assert.DoesNotContain("if (ticks !== saveTicks || saving.size) {", load);
    var retry = load.IndexOf("if (overlapped) {\n        view.loading = true;\n        paint();\n        whenSettled(() => {\n          if (seq !== view.seq || !ctx.isCurrent() || ctx.controller.signal.aborted) return;\n          view.loading = false;\n          view.done = false;\n          loadPage();\n        });\n        return;\n      }", StringComparison.Ordinal);
    Assert.True(retry > load.IndexOf("list.append(build(m.key));", StringComparison.Ordinal));
    Assert.True(retry > load.IndexOf("lookup(keys, seq);", StringComparison.Ordinal));
    Assert.True(retry < load.IndexOf("if (!view.done && added === 0) {", StringComparison.Ordinal));
    Assert.DoesNotContain("short && (added === 0 || view.done)", load);
    Assert.Contains("if (!view.done && added === 0) {\n        view.minPage = requested + 1;\n        view.minAt = ticks;", load);
    // minPage holds only while no save started or ended since it was raised, on every path, Try again included.
    var reset = load.IndexOf("if (view.minAt !== ticks) view.minPage = 1;", StringComparison.Ordinal);
    Assert.True(reset > ticks && reset < load.IndexOf("requested = view.walk", StringComparison.Ordinal));
    Assert.Contains("onclick: loadPage }, 'Try again')", load);
    var library = Read("library.js");
    var setMark = Between(library, "async function setMark(ctx, name, kind, key, next) {", "// Browse (#287)");
    Assert.Contains("saving.set(key, (saving.get(key) || 0) + 1);\n    saveTicks += 1;", setMark);
    Assert.True(setMark.IndexOf("saveTicks += 1;", StringComparison.Ordinal) < setMark.IndexOf("await ctx.setMark", StringComparison.Ordinal));
    Assert.Contains("unsave(key);\n      saveTicks += 1;\n      clock += 1;\n      for (const k of [key, keyAlias.get(key)]) if (k) markAt.set(k, clock);\n      if (!saving.size) [...settleQueue].forEach((fn) => settleQueue.delete(fn) && fn());", Between(setMark, "} finally {", "\n  }"));
    // 101 Liked, the retry walked pages 1 and 2, then a Like cleared moves title 101 onto page 2: the walk starts at 1 again.
    var walk = load.IndexOf("if (view.walkAt !== ticks) [view.walk, view.walkAt] = [view.walk && 1, ticks];", StringComparison.Ordinal);
    Assert.True(walk > ticks && walk < load.IndexOf("requested = view.walk", StringComparison.Ordinal));
    Assert.Contains("const whenSettled = (fn) => (saving.size ? settleQueue.add(fn) : fn());", library);

    var fits = Between(section, "const MARK_FITS = {", "};");
    foreach (var rule in new[]
    {
      "watchlist: (m) => m.watchlist === true", "all: (m) => m.seen || m.rating !== 0 || m.watchlist === true",
      "seen: (m) => m.seen", "liked: (m) => m.rating === 1", "disliked: (m) => m.rating === -1",
    })
      Assert.Contains(rule, fits);
  }

  [Fact]
  public void CardOpenedDuringASave_LeavesThePendingMarkInPlace()
  {
    var library = Read("library.js");
    var card = Between(library, "function cardMarks(ctx, entry, answer) {", "const cardToast");
    // During a save, or after one that overlapped the card's request, the card shows the value in marks and keeps
    // that object, so a failed save still rolls back and the next Seen tap sends the saved Like.
    Assert.Contains("const pending = !freshFor(key, answer?.asked) && marks.has(key);", card);
    var load = Between(library, "async function loadFilm(ctx) {", "function renderFilm(");
    var asked = load.IndexOf("const asked = ++clock;", StringComparison.Ordinal);
    Assert.True(asked >= 0 && asked < load.IndexOf("await ctx.seriesCard(", StringComparison.Ordinal));
    Assert.Contains("if (seq !== card.seq || !ctx.isCurrent()) return;\n      // Stamped like the titles' answers (#447): a save of the title that overlapped the request outranks its marks.\n      answer.asked = asked;\n      if (answer.status === 'matched') answers.set(key, answer);", load);
    Assert.Contains("const given = pending || !answer?.marks || owner === key ? now", card);
    Assert.Contains("markEntry = { name: entry.name, contentType: entry.type === 'Series' ? 'Series' : 'Movies', markKey: entry.markKey, ...given, keepMark: pending };", card);
    Assert.True(card.IndexOf("const pending", StringComparison.Ordinal) < card.IndexOf("markControls(cardCtx, markEntry)", StringComparison.Ordinal));
    Assert.Contains(KeepRule, Between(library, "function markControls(ctx, entry) {", "function paintMarks("));
  }

  private const string KeepRule = "if (!entry.keepMark && (freshFor(key, entry.asked) || !marks.has(key))) marks.set(key, {";
  private const string FreshPage = "if (freshFor(m.key, at)) {\n          marks.set(m.key, { seen: m.seen === true, rating: m.rating || 0, watchlist: m.watchlist === true });\n          markAt.set(m.key, Math.max(markAt.get(m.key) || 0, at));\n          paintKey(ctx, m.key);\n        }";

  [Fact]
  public void AnswerOverlappedByASave_LeavesTheSavedMarkAlone()
  {
    var library = Read("library.js");
    // One save tick per key, moved at the start and the end of every save of it (and of its API key).
    var setMark = Between(library, "async function setMark(ctx, name, kind, key, next) {", "// Browse (#287)");
    Assert.Contains("saveTicks += 1;\n    markAt.set(key, ++clock);", setMark);
    Assert.True(setMark.IndexOf("markAt.set(key, ++clock);", StringComparison.Ordinal) < setMark.IndexOf("await ctx.setMark", StringComparison.Ordinal));
    Assert.Contains("const freshFor = (key, asked) => !blind() && !saving.has(key) && !(markAt.get(key) > (asked ?? Infinity));", library);

    // Search, Groups, card and Marks requests take the next clock value when asked: one that a save overlapped keeps the
    // saved Like, a Search asked after a Marks page (no save since) takes the server's marks again.
    Assert.Contains("const asked = ++clock;\n    const found = await ctx.titles(params, signal);\n    for (const t of found?.titles || []) t.asked = asked;", library);
    Assert.DoesNotContain("asked = saveTicks", library);
    Assert.DoesNotContain("ticks + 0.5", library);
    // Another account signed in in the same tab: the Library starts without the last user's marks.
    Assert.Contains("if (ctx.user !== markUser) { markUser = ctx.user; for (const m of [marks, markAt, keyAlias, answers]) m.clear(); }", Between(library, "function mount(ctx) {", "load(ctx, controller);"));
    Assert.Contains("    plural,\n    user: state.user?.userName || state.user?.email || null,", Read("main.js"));
    // The first Like from a similar film's card has no API key yet (blind): no answer is fresh and a Marks page waits
    // for the save before it renders, so its row shows the Like and its Seen tap sends rating 1.
    Assert.Contains("const blind = () => [...saving.keys()].some((k) => k.startsWith('similar:') && !keyAlias.has(k));", library);
    var hold = Between(MyMarks(), "const loadPage = async () => {", "const build = (key) => {");
    var wait = hold.IndexOf("if (blind()) return void whenSettled(() => { if (seq === view.seq && ctx.isCurrent()) { view.walk = 1; view.loading = false; loadPage(); } });", StringComparison.Ordinal);
    Assert.True(wait > hold.IndexOf("await ctx.listMarks(", StringComparison.Ordinal) && wait < hold.IndexOf("for (const m of items)", StringComparison.Ordinal));
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(library, @"ctx\.titles\("));
    Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(library, @"await askTitles\(ctx, ").Count);
    // A Like from a similar film's card on a Seen title, All opened before the PUT ends: pending under the API key too,
    // so the Marks row shows the Like and its Seen tap sends rating 1; a failed save rolls each key back to its own value
    // (Liked from the card, Seen from the row, the card's failed clear leaves Seen pressed).
    Assert.Contains("const alias = keyAlias.get(key), prior = alias && marks.get(alias);\n    if (alias) { saving.set(alias, (saving.get(alias) || 0) + 1); markAt.set(alias, clock); marks.set(alias, next); }", setMark);
    Assert.True(setMark.IndexOf("marks.set(alias, next)", StringComparison.Ordinal) < setMark.IndexOf("await ctx.setMark", StringComparison.Ordinal));
    Assert.Contains("if (alias && marks.get(alias) === next) marks.set(alias, prior);", setMark);
    Assert.DoesNotContain("marks.set(alias, before)", setMark);
    Assert.Contains("if (alias) unsave(alias);\n      unsave(key);", setMark);
    Assert.Contains("const unsave = (k) => (saving.get(k) > 1 ? saving.set(k, saving.get(k) - 1) : saving.delete(k));", library);
    Assert.Contains(KeepRule, Between(library, "function markControls(ctx, entry) {", "function paintMarks("));

    // Marks: a repeated key takes the server's mark too, before it is skipped. A page Seen, then an older Search answer
    // unseen: stale. Two pages, unseen then Seen: the later one is fresh, Seen shows.
    var load = Between(MyMarks(), "const loadPage = async () => {", "const build = (key) => {");
    Assert.Contains(FreshPage + "\n        if (view.shown.has(m.key)) continue;", load);

    // An overlapped page asks again with minPage from 1: a page skipped as repeated may hold a title the save moved up.
    Assert.Contains("if (view.minAt !== ticks) view.minPage = 1;", load);
  }

  [Fact]
  public void SearchOrGroupsRowDuringASave_LeavesThePendingMarkInPlace()
  {
    var library = Read("library.js");
    // Search and Groups render without keepMark: a row rendered during a Marks-row save must not replace the pending
    // object, or a failed save would skip the rollback and the toast.
    var controls = Between(library, "function markControls(ctx, entry) {", "function paintMarks(");
    Assert.Contains(KeepRule, controls);
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(controls, @"marks\.set\("));
    var setMark = Between(library, "async function setMark(ctx, name, kind, key, next) {", "// Browse (#287)");
    var fail = Between(setMark, "} catch (err) {", "} finally {");
    Assert.Contains("if (marks.get(key) !== next) return;\n      if (alias && marks.get(alias) === next) marks.set(alias, prior);\n      marks.set(key, before);\n      repaint();\n      ctx.toast(", fail);
    // The Search and Groups rows go through markControls (directly or through rowMarks), with the entry they render.
    Assert.DoesNotContain("keepMark", Between(library, "// Browse (#287)", "function myMarks("));
  }

  [Fact]
  public void FreshMarksPage_RepaintsTheTitlesControlsInSearchAndGroups()
  {
    var library = Read("library.js");
    var load = Between(MyMarks(), "const loadPage = async () => {", "const build = (key) => {");
    // The server's mark taken by a fresh page shows on every mounted control of the key, so the next tap on an
    // existing Search row starts from it (Seen cleared on another device stays cleared).
    Assert.Contains(FreshPage, load);
    Assert.True(load.IndexOf("paintKey(ctx, m.key);", StringComparison.Ordinal) < load.IndexOf("list.append(build(m.key));", StringComparison.Ordinal));
    Assert.Contains("function paintKey(ctx, key) {\n    for (const el of ctx.root.querySelectorAll('.library-marks')) if (el.dataset.key === key) paintMarks(el);\n  }", library);
    // The Seen button of a Search row and its capsule read marks at tap time, which paintKey has just repainted from.
    Assert.Contains("onclick: () => setMark(ctx, entry.name, MARK_KINDS[entry.contentType], key, toggleSeen(marks.get(key))),", library);
    Assert.Contains("onclick: () => setMark(ctx, entry.name, kind, key, next(marks.get(key))),", library);
    Assert.Contains("const m = marks.get(el.dataset.key);", Between(library, "function paintMarks(el) {", "function rowMarks("));
  }

  [Fact]
  public void Row_InList_OpensTheCard_OtherwiseSaysNotInThisList()
  {
    var section = MyMarks();
    var build = Between(section, "const build = (key) => {", "const lookup = async");
    Assert.Contains("const type = m.kind === 'series' ? 'Series' : 'Movies';", build);
    Assert.Contains("h('span', { class: 'lib-dot', 'aria-hidden': 'true', dataset: { type } }),", build);
    Assert.Contains("dataset: { type, key } }", build);
    // Film: the group; series: bold seasons and episodes · group; otherwise one wording for owner and viewer.
    Assert.Contains("const meta = !t ? 'Not in this list' : type === 'Series' ? [h('b', { text: seasonsLine(ctx, t) }), ' · ', t.group] : t.group;", build);
    Assert.DoesNotContain("Not in your list", Read("library.js"));
    // Opened with the lookup's name, group and enabled and the list's cacheKey; any other row opens nothing.
    Assert.Contains("const opened = t ? { ...e, name: t.name, enabled: t.enabled } : { ...e, markKey: null };", build);
    Assert.Contains("filmName(ctx, opened, t?.group, ctx.list?.cacheKey)", build);
    Assert.Contains("class: opens(ctx, opened) ? 'lib-row' : 'lib-row is-static'", build);
    // The chevron on every row, as its last child.
    Assert.Contains("h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, icon('chevron')));", build);
    Assert.DoesNotContain("filmChevron", build);
    // A key the answer lacks reads "Not in this list" again.
    Assert.Contains("const title = answer?.titles?.[key] || null;", Between(section, "const lookup = async", "ctx.lookupMarks = () => {"));
    Assert.Contains("h('div', { class: 'lib-list' }, list, foot));", section);

    var css = Read("library.css");
    Assert.Contains(".lib-row.is-static .lib-chev { visibility: hidden; }", css);
  }

  [Fact]
  public void Row_UsesTheSharedMarkControls_NoRemoveButton()
  {
    var library = Read("library.js");
    var section = MyMarks();
    var build = Between(section, "const build = (key) => {", "const lookup = async");
    Assert.Contains("const e = { name: m.year ? `${m.title} (${m.year})` : m.title, title: m.title, year: m.year, contentType: type, markKey: key, keepMark: true };", build);
    Assert.Contains("rowMarks(ctx, e),", build);
    // main.js keeps "Remove" for shared lists only; no mark is removed from the frontend.
    Assert.DoesNotContain("removeMark", library);
    Assert.DoesNotContain("removeMark", Read("main.js"));
    Assert.DoesNotContain("api(`/marks/${encodeURIComponent(key)}`, { method: 'DELETE' })", Read("main.js"));
    Assert.DoesNotContain("Remove", library);
    Assert.DoesNotContain("const undo", section);

    // A fresh page takes the server's mark unless a save for the key is still on its way.
    var load = Between(section, "const loadPage = async () => {", "const build = (key) => {");
    Assert.Contains(FreshPage, load);
    Assert.True(load.IndexOf("marks.set(m.key", StringComparison.Ordinal) < load.IndexOf("list.append(build(m.key));", StringComparison.Ordinal));
    var setMark = Between(library, "async function setMark(ctx, name, kind, key, next) {", "// Browse (#287)");
    Assert.Contains("saving.set(key, (saving.get(key) || 0) + 1);", setMark);
    Assert.Contains("\n      unsave(key);\n", Between(setMark, "} finally {", "\n  }"));
    Assert.True(setMark.IndexOf("saving.set(key, (saving", StringComparison.Ordinal) < setMark.IndexOf("await ctx.setMark", StringComparison.Ordinal));

    // A rebuilt row keeps the object in marks, so a failed save still rolls back and says so.
    Assert.Contains(KeepRule, Between(library, "function markControls(ctx, entry) {", "function paintMarks("));
    Assert.DoesNotContain("marks.set(", build);
    Assert.DoesNotContain("marks.set(", Between(section, "const lookup = async", "const start = () => {"));
    Assert.Contains("if (marks.get(key) !== next) return;", setMark);
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
