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
  public void Rows_AskLookupOncePerPage()
  {
    var section = MyMarks();
    var load = Between(section, "const loadPage = async () => {", "const build = (key) => {");
    // One call site, with the keys of the whole page, after the loop that skips repeated rows and before a page that
    // added nothing moves on: an overlapping or fully repeated page is looked up as well.
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
    var failure = Between(lookup, "} catch (err) {", "return;\n      }");
    Assert.Contains("if (seq === view.seq && ask === view.asks && ctx.isCurrent() && !ctx.controller.signal.aborted) accessGone(ctx, err);", failure);
    var gone = failure.IndexOf("accessGone(", StringComparison.Ordinal);
    foreach (var check in new[] { "seq === view.seq", "ctx.isCurrent()", "!ctx.controller.signal.aborted" })
    {
      var at = failure.IndexOf(check, StringComparison.Ordinal);
      Assert.True(at >= 0 && at < gone, $"'{check}' must come before accessGone.");
    }
    // Nothing else happens: an owner's rows keep "Not in this list".
    Assert.DoesNotContain("build(", failure);
    Assert.DoesNotContain("toast", failure);
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
    Assert.Contains("if (!saving.has(m.key)) marks.set(m.key, { seen: m.seen === true, rating: m.rating || 0, watchlist: m.watchlist === true });", load);
    Assert.True(load.IndexOf("marks.set(m.key", StringComparison.Ordinal) < load.IndexOf("list.append(build(m.key));", StringComparison.Ordinal));
    var setMark = Between(library, "async function setMark(ctx, name, kind, key, next) {", "// Browse (#287)");
    Assert.Contains("saving.set(key, (saving.get(key) || 0) + 1);", setMark);
    Assert.Contains("} finally {\n      if (saving.get(key) > 1) saving.set(key, saving.get(key) - 1);\n      else saving.delete(key);\n    }", setMark);
    Assert.True(setMark.IndexOf("saving.set(key, (saving", StringComparison.Ordinal) < setMark.IndexOf("await ctx.setMark", StringComparison.Ordinal));

    // A rebuilt row keeps the object in marks, so a failed save still rolls back and says so.
    Assert.Contains("if (!entry.keepMark) marks.set(key, {", Between(library, "function markControls(ctx, entry) {", "function paintMarks("));
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
