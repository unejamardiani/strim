using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins an open group's own address in the Groups tab (#442): the route a row navigates to, the query of the
/// mounted address, Back with the focus on the group's row after the list is drawn again, and the fall-back for a
/// group that is gone. Source pins like the other Library UI tests (no JS engine).</summary>
public class LibraryGroupViewUiTests
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

  private static string Library() => Read("library.js");
  private static string Show() => Between(Library(), "function show(sub, query) {", "// Search and Groups both wait on the list");
  private static string Browse() => Between(Library(), "function browseSection(ctx, list) {", "function hit(");
  private static string Handler() => Between(Browse(), "view.groupAddress = (key, query) => {", "if (view.groupQuery) {");

  private static void InOrder(string source, params string[] parts)
  {
    var at = -1;
    foreach (var part in parts)
    {
      var next = source.IndexOf(part, at + 1, StringComparison.Ordinal);
      Assert.True(next > at, $"'{part}' is missing or out of order.");
      at = next;
    }
  }

  [Fact]
  public void Group_HasItsOwnRoute()
  {
    var browse = Browse();
    // The row navigates to the group's address, its key encoded, as a history entry.
    Assert.Contains("const groupHash = (key) => `${view.base}/groups/${encodeURIComponent(key)}`;", browse);
    Assert.Contains("onclick: () => go(g) },", Between(browse, "const groupRow = (g, t) => {", "h('span', { class: 'lib-dot'"));
    var go = Between(browse, "const go = (g) => {", "\n    };");
    Assert.Contains("location.hash = `${groupHash(g.key)}${query ? `?${query}` : ''}`;", go);
    Assert.DoesNotContain("replaceState", go);
    Assert.DoesNotContain("onclick: () => open(g)", browse);

    // show() decodes everything after "groups/", a malformed escape as written, and hands it to the Groups tab.
    var show = Show();
    Assert.Contains("view.groupKey = groupKeyOf(String(sub));", show);
    Assert.Contains("if (tab === 'groups') view.groupAddress?.(view.groupKey, view.groupQuery);", show);
    var decode = Between(show, "function groupKeyOf(sub) {", "\n  }\n");
    Assert.Contains("if (!sub.startsWith('groups/')) return null;", decode);
    Assert.Contains("const raw = sub.slice('groups/'.length);", decode);
    Assert.Contains("return decodeURIComponent(raw);", decode);
    Assert.Contains("} catch {\n      return raw;", decode);
    // main.js already passes "groups/<encoded key>" and the query; it is not changed.
    Assert.Contains("(?:\\/([^?#]*))?(?:\\?(.*))?$/i", Read("main.js"));

    // The address opens the group with the group's type chosen; an open group's address is kept, never the list's.
    var handler = Handler();
    Assert.Contains("if (state.open?.group.key === key) return;", handler);
    InOrder(handler, "groupType = t.value;", "open(g, true, {", "renderGroups();");
    Assert.Contains("if (state.open) return;", Between(browse, "const keepGroupsAddress = () => {", "};"));
    // A list still loading applies the address once it is loaded.
    Assert.Contains("if (view.tab === 'groups' && view.groupKey !== null) view.groupAddress(view.groupKey, view.groupQuery);", browse);
  }

  [Fact]
  public void Reload_KeepsTheGroupsAddress()
  {
    // A reload or first visit at a group's address: the group opens before the list is first drawn, so the first
    // renderGroups() finds it open and keepGroupsAddress() leaves the group's address (and its query) alone.
    var tail = Between(Browse(), "const reload = () => {", "return { el, refresh, reload };");
    InOrder(tail,
      "if (view.tab === 'groups' && view.groupKey !== null) view.groupAddress(view.groupKey, view.groupQuery);",
      "else renderGroups();");
    // No renderGroups() runs before the pending address is applied.
    var before = tail[..tail.IndexOf("if (view.tab === 'groups' && view.groupKey !== null)", StringComparison.Ordinal)];
    Assert.DoesNotContain("renderGroups();", before);
    // The handler opens the group before it draws the list.
    InOrder(Between(Handler(), "if (state.open?.group.key === key) return;", "};"), "open(g, true, {", "renderGroups();");
  }

  [Fact]
  public void MountedAddress_KeepsItsQuery()
  {
    var library = Library();
    var mount = Between(library, "function mount(ctx) {", "// Opens a tab of the Library");
    Assert.Contains("show(ctx.sub, ctx.query);", mount);
    Assert.DoesNotContain("show(ctx.sub);", mount);
    // show() takes the query as given for the Groups tab and never blanks one it was given.
    var show = Show();
    Assert.Contains("view.groupQuery = query || '';", show);
    Assert.DoesNotContain("view.groupQuery = '';", show);
    // The Groups address handler gets that query: the type of "…/groups?type=series" after a reload, and an open
    // group keeps the query it came with on the tab's link.
    Assert.Contains("view.groupAddress?.(view.groupKey, view.groupQuery);", show);
    var handler = Handler();
    Assert.Contains("const value = new URLSearchParams(query).get('type');", handler);
    // An open group's query (#444) is kept on the tab's link by keepOpenAddress().
    Assert.Contains("view.nav.querySelector('[data-tab=\"groups\"]').href = hash;", Between(Browse(), "const keepOpenAddress = (cur) => {", "\n    };"));
    var browse = Browse();
    Assert.Contains("const value = new URLSearchParams(view.groupQuery).get('type');", browse);
  }

  [Fact]
  public void Back_FocusesTheGroupsRowAfterTheListIsRedrawn()
  {
    var browse = Browse();
    // close() itself focuses nothing: renderGroups() replaces the rows afterwards.
    var close = Between(browse, "const close = () => {", "};");
    Assert.DoesNotContain("focus", close);

    // Back (the list's address) in the handler: close, renderGroups(), then the row's focus.
    var list = Between(Handler(), "if (key === null) {", "return;\n      }");
    InOrder(list, "const was = state.open?.group.key;", "close();", "renderGroups();", "if (was !== undefined) focusRow(was);");

    // The shared toggle closing a switched-off group: the same order.
    var refresh = Between(browse, "const refresh = () => {", "};");
    InOrder(refresh, "if (gone) close();", "renderGroups();", "if (gone) focusRow(gone.key);");

    // "All groups" (#443) is the head's back link to the list's address, a history entry.
    Assert.Contains("h('a', { class: 'lib-ib', href: `${view.base}/groups?type=${t.value}`, 'aria-label': 'All groups' }, icon('back')),", browse);
    Assert.Contains("const focusRow = (key) => {\n      for (const b of groupsEl.querySelectorAll('.is-group')) if (b.dataset.key === key) b.focus();", browse);
    // The rebuilt close drops a pending answer and the sentinel, and no longer hides or shows the list itself.
    InOrder(close, "state.open = null;", "state.seq++;", "state.observer?.disconnect();", "el.classList.remove('has-open');");
    Assert.DoesNotContain("groupsEl.hidden", Library());
  }

  private static string Open() => Between(Browse(), "const open = (g, scroll = true, from = { q: '', sort: 'playlist' }) => {", "\n\n    // The shared toggle");

  [Fact]
  public void Group_LoadsTitlesByThirtyWithASentinel()
  {
    var open = Open();
    // The titles route with the group, by 30, with the shared mark filters (and, #444, the filter and the sort).
    Assert.Contains("const params = { cacheKey: list.cacheKey, includeDisabled: includeOff, group: cur.group.key, offset: 0, limit: 30 };", open);
    Assert.Contains("if (hideSeen) params.hideSeen = true;\n      if (likedOnly) params.liked = true;", open);
    Assert.Contains("first = await ctx.titles(params, ctx.controller.signal);", open);
    // An overtaken answer is dropped; a mark filter or another address starts again from the first page.
    InOrder(open, "const seq = ++state.seq;", "state.observer?.disconnect();", "if (seq !== state.seq || ctx.controller.signal.aborted", "if (seq !== state.seq || !ctx.isCurrent()) return;");
    Assert.Contains("const reload = () => { if (state.open) loadGroup(state.open); };", Browse());
    // A failed first page: the error with Try again in place of the rows.
    InOrder(open, "cur.body.replaceChildren(h('div', { class: 'notice error', role: 'alert' },", "onclick: () => loadGroup(cur) }, 'Try again')");
    // Later pages come from pages() of #421: the sentinel within 600px, Try again below the rows, the optional end.
    Assert.Contains("cur.body.replaceChildren(pages(ctx, state, params, first, entryRow, seq, foot(`All ${fmt(total)} shown`)));", open);
    var pages = Between(Library(), "function pages(ctx, state, params, first, rowOf, seq, end) {", "// Marks (#298)");
    Assert.Contains("new IntersectionObserver(", pages);
    Assert.Contains("{ rootMargin: '600px' }", pages);
    Assert.Contains("offset: ul.children.length, limit: 30", pages);
    InOrder(pages, "if (page.hasMore !== true || !page.titles?.length) {", "if (end) foot.append(end);", "'Try again'");
    // Rows: a series is one row with its seasons, a film in several entries says its versions, others no meta.
    Assert.Contains("if (x.contentType === 'Series') return h('b', { text: seasonsLine(ctx, x) });", open);
    Assert.Contains("return x.contentType === 'Movies' && x.entries > 1 ? plural(x.entries, 'version') : null;", open);
    Assert.Contains("const entryRow = (x) => row(ctx, x, x.group, list.cacheKey, groupMeta(x), null);", open);
    // The three foot texts.
    Assert.Contains("const foot = (text) => h('p', { class: 'lib-foot', text });", open);
    Assert.Contains("foot(hideSeen || likedOnly || cur.q ? 'Nothing in this group matches.' : 'No entries in this group.')", open);
    Assert.Contains("`All ${fmt(total)} shown`", open);
    // The old paging is gone: no More button, no group-entries, no PAGE_SIZE, no scrollIntoView; the page goes to the top.
    var browse = Browse();
    Assert.DoesNotContain("'More'", browse);
    Assert.DoesNotContain("PAGE_SIZE", Library());
    Assert.DoesNotContain("groupEntries", Library());
    Assert.DoesNotContain("scrollIntoView", Library());
    Assert.Contains("if (scroll) window.scrollTo(0, 0);", open);
    var main = Read("main.js");
    Assert.DoesNotContain("groupEntries", main);
  }

  [Fact]
  public void Head_IsStickyUnderTheTopBar()
  {
    var open = Open();
    InOrder(open, "h('div', { class: 'lib-sticky' },", "h('div', { class: 'lib-ghead' },",
      "'aria-label': 'All groups' }, icon('back')),",
      "h('div', { class: 'lib-ghead-text' }, h('h2', { text: name, title: name }), cur.count),",
      "list.disabled.has(g.key) ? h('span', { class: 'lib-off', text: 'Off' }) : null),",
      "h('div', { class: 'lib-search lib-gfilter' }, input, clear),");
    Assert.Contains("const name = g.title || 'Untitled group';", open);
    // The count line: the group's count with the type word, plus the seen titles left out while Unseen is on.
    Assert.Contains("count: h('p', { text: plural(g.count, t.one, t.many) })", open);
    Assert.Contains("const seen = hideSeen && first.seenHiddenCount > 0 ? ` · ${fmt(first.seenHiddenCount)} seen hidden` : '';", open);
    Assert.Contains("cur.count.textContent = plural(cur.group.count, cur.t.one, cur.t.many) + seen;", open);

    var css = Read("library.css");
    Assert.Contains(".lib-sticky { position: sticky; top: var(--topbar-h);", css);
    Assert.Contains(".lib-ghead { display: flex; align-items: center; gap: 10px; }", css);
    Assert.Contains(".lib-ghead-text { flex: 1; min-width: 0; }", css);
    Assert.Contains(".lib-ghead h2 { margin: 0; font-family: var(--font-display); font-size: 20px; font-weight: 800; letter-spacing: -.03em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }", css);
    Assert.Contains(".lib-ghead p { margin: 0; font-size: 13px; color: var(--ink-3); font-variant-numeric: tabular-nums; }", css);
    Assert.Contains("padding: 12px; border-top: 1px solid var(--line); text-align: center; font-size: 13px; color: var(--ink-3); }", css);
    foreach (var gone in new[] { ".library-entries-head", ".library-group-title", ".library-open" })
    {
      Assert.DoesNotContain(gone, css);
      Assert.DoesNotContain(gone.TrimStart('.'), Library());
    }
  }

  [Fact]
  public void Wide_KeepsTheGroupListBesideTheEntries()
  {
    var browse = Browse();
    Assert.Contains("const el = h('section', { class: 'library-browse lib-groups-cols' },\n      h('div', { class: 'lib-groups-side' }, h('h2', { class: 'library-title', text: 'Browse groups' }), groupsEl), entriesEl);", browse);
    InOrder(Open(), "el.classList.add('has-open');", "loadGroup(cur);");

    var css = Read("library.css");
    var wide = Between(css, "@media (min-width: 1100px) {\n  .page:has(.lib-panel)", "\n}\n");
    Assert.Contains(".lib-groups-cols.has-open { display: grid; grid-template-columns: minmax(300px, 0.4fr) minmax(0, 1fr); gap: 24px; align-items: start; }", wide);
    Assert.Contains(".lib-groups-cols.has-open .lib-groups-side { position: sticky; top: calc(var(--topbar-h) + 12px); max-height: calc(100dvh - var(--topbar-h) - 30px); overflow-y: auto; }", wide);
    Assert.Contains(".lib-groups-cols.has-open .lib-ghead .lib-ib { display: none; }", wide);
    Assert.Contains(".lib-groups-cols.has-open .lib-sticky { margin: 0; padding: 0 0 10px; top: calc(var(--topbar-h) + 12px); background: var(--bg); }", wide);
    Assert.Contains("@media (max-width: 1099px) { .lib-groups-cols.has-open .lib-groups-side { display: none; } }", css);
    // The old [hidden] override is gone.
    Assert.DoesNotContain("!important", Between(css, "/* Browse (#287)", "/* Stage:"));
  }

  private static string LoadGroup() => Between(Open(), "const loadGroup = async (cur) => {", "cur.body.replaceChildren(status(");

  [Fact]
  public void Filter_SendsQ_AfterTwoLettersWithDebounce()
  {
    var open = Open();
    // The field of #421 inside the sticky head, below the title, labelled with the group's title.
    InOrder(open, "h('div', { class: 'lib-sticky' },", "h('div', { class: 'lib-ghead-text' }",
      "h('div', { class: 'lib-search lib-gfilter' }, input, clear),", "h('div', { class: 'lib-chips', role: 'group', 'aria-label': 'Filters' }");
    Assert.Contains("placeholder: `Filter ${name}`, 'aria-label': `Filter ${name}`,", open);
    Assert.Contains("class: 'lib-clear', type: 'button', 'aria-label': 'Clear filter', hidden: !cur.q,", open);
    // 300ms after the last key; below 2 letters or digits (searchHint, the server's rule) the unfiltered group.
    Assert.Contains("clearTimeout(state.timer); state.timer = setTimeout(filtered, 300);", open);
    Assert.Contains("const q = ctx.searchHint(text) ? '' : text;", open);
    Assert.Contains("if ((query.match(/[\\p{L}\\p{N}]/gu) || []).length < 2)", Read("main.js"));
    InOrder(Between(open, "const filtered = () => {", "\n      };"), "if (q === cur.q) return;", "cur.q = q;", "loadGroup(cur);");
    Assert.Contains("if (cur.q) params.q = cur.q;", LoadGroup());
    // A pending filter stops when the group closes or the Library is left.
    Assert.Contains("clearTimeout(state.timer);", Between(Browse(), "const close = () => {", "};"));
    Assert.Contains("'abort', () => { clearTimeout(state.timer); state.observer?.disconnect(); }", Browse());
    Assert.Contains(".lib-gfilter { margin-top: 10px; }", Read("library.css"));
  }

  [Fact]
  public void Sort_CyclesThreeOrders()
  {
    var library = Library();
    InOrder(Between(library, "const SORTS = [", "];"),
      "{ value: 'playlist', label: 'Playlist order' },", "{ value: 'title', label: 'A to Z' },", "{ value: 'year', label: 'Newest year' },");
    // The same values the titles route accepts.
    Assert.Contains("Sorts = [\"playlist\", \"title\", \"year\"];", Read("api/Endpoints/LibraryTitlesEndpoints.cs"));
    var open = Open();
    Assert.Contains("onclick: () => { cur.sort = SORTS[(SORTS.findIndex((s) => s.value === cur.sort) + 1) % SORTS.length].value; loadGroup(cur); },", open);
    Assert.Contains("}, icon('sort'), sortLabel);", open);
    Assert.DoesNotContain("'aria-pressed'", Between(open, "cur.sortChip = h('button', {", "sortLabel);"));
    var load = LoadGroup();
    Assert.Contains("cur.sortLabel.textContent = sort.label;", load);
    Assert.Contains("cur.sortChip.setAttribute('aria-label', `Sort: ${sort.label}`);", load);
    Assert.Contains("if (cur.sort !== 'playlist') params.sort = cur.sort;", load);
    Assert.Contains("sort: '<path d=\"M7 5v14M7 19l-3-3M7 19l3-3M17 19V5M17 5l-3 3M17 5l3 3\"/>',", Read("main.js"));
  }

  [Fact]
  public void Chips_ShareTheSearchTabsMarkFilters()
  {
    var open = Open();
    // Exactly Unseen, Liked, sort; both toggles set the shared filters and tell the Search tab.
    Assert.Contains("'aria-label': 'Filters' }, cur.seenChip, cur.likedChip, cur.sortChip)),", open);
    Assert.Contains("class: 'lib-chip is-toggle', type: 'button', 'aria-pressed': String(get()),", open);
    Assert.Contains("onclick: () => { set(!get()); view.marksChanged?.(); loadGroup(cur); },", open);
    Assert.Contains("cur.seenChip = markChip('Unseen', () => hideSeen, (v) => { hideSeen = v; });", open);
    Assert.Contains("cur.likedChip = markChip('Liked', () => likedOnly, (v) => { likedOnly = v; });", open);
    // The Search tab's chips follow and its search runs again; its own chips reload the open group (reload()).
    var marks = Between(Library(), "view.marksChanged = () => {", "};");
    InOrder(marks, "seenFilter.setAttribute('aria-pressed', String(hideSeen));", "likedFilter.setAttribute('aria-pressed', String(likedOnly));", "run();");
    var load = LoadGroup();
    InOrder(load, "cur.seenChip.setAttribute('aria-pressed', String(hideSeen));", "cur.likedChip.setAttribute('aria-pressed', String(likedOnly));",
      "if (hideSeen) params.hideSeen = true;", "if (likedOnly) params.liked = true;");
  }

  [Fact]
  public void Address_KeepsFilterSortAndChips_AndOpeningReadsThem()
  {
    var browse = Browse();
    // Written on every load, replaced, never a history entry; defaults left out.
    var query = Between(browse, "const groupQueryOf = (cur) => {", "\n    };");
    InOrder(query, "if (cur.q) p.set('q', cur.q);", "if (cur.sort !== 'playlist') p.set('sort', cur.sort);",
      "if (hideSeen) p.set('unseen', '1');", "if (likedOnly) p.set('liked', '1');", "return String(p);");
    var keep = Between(browse, "const keepOpenAddress = (cur) => {", "\n    };");
    Assert.Contains("const hash = `${groupHash(cur.group.key)}${query ? `?${query}` : ''}`;", keep);
    Assert.Contains("if (view.tab === 'groups' && location.hash !== hash) history.replaceState(history.state, '', hash);", keep);
    Assert.DoesNotContain("location.hash =", keep);
    InOrder(LoadGroup(), "keepOpenAddress(cur);", "const params =");

    // mount() passes the address's query to show(), show() to the handler, the handler to the opened group.
    Assert.Contains("show(ctx.sub, ctx.query);", Between(Library(), "function mount(ctx) {", "// Opens a tab"));
    Assert.Contains("view.groupQuery = query || '';", Show());
    Assert.Contains("view.groupAddress?.(view.groupKey, view.groupQuery);", Show());
    var handler = Between(Handler(), "if (state.open?.group.key === key) return;", "renderGroups();\n    };");
    InOrder(handler, "const p = new URLSearchParams(query);",
      "const unseen = p.get('unseen') === '1';", "const liked = p.get('liked') === '1';",
      "hideSeen = unseen;", "likedOnly = liked;", "view.marksChanged?.();",
      "const q = (p.get('q') || '').trim();",
      "const sort = SORTS.find((s) => s.value === p.get('sort')) || SORTS[0];",
      "open(g, true, { q: ctx.searchHint(q) ? '' : q, sort: sort.value });");
    // The field shows the address's text; the group starts with its filter and order.
    var open = Open();
    Assert.Contains("q: from.q, sort: from.sort };", open);
    Assert.Contains("value: cur.q,", open);
  }

  [Fact]
  public void UnknownGroup_FallsBackToTheListWithAToast()
  {
    var handler = Handler();
    // Only a group the list shows opens: not in the list, or switched off while those are left out, falls back.
    Assert.Contains("const g = browseGroups(list, includeOff).sorted.find((x) => x.key === key);", handler);
    var unknown = Between(handler, "if (!g || !t) {", "return;");
    InOrder(unknown, "close();", "renderGroups();", "ctx.toast('That group is not in this list any more.', { error: true });");
    // renderGroups() with no group open replaces the address by the list's, so a reload does not repeat the toast.
    var keep = Between(Browse(), "const keepGroupsAddress = () => {", "};");
    InOrder(keep, "if (state.open) return;", "const hash = listHash();",
      "if (view.tab === 'groups' && location.hash !== hash) history.replaceState(history.state, '', hash);");
    // show() sets the tab before the handler runs, so the replacement applies on the first visit too.
    InOrder(Show(), "view.tab = tab;", "if (tab === 'groups') view.groupAddress?.(view.groupKey, view.groupQuery);");
  }
}
