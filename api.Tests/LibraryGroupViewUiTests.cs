using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the open group (#423): its own address, paging by 30 with a sentinel, the sticky head with filter,
/// mark chips and sort, and the wide two-column layout, at their source as the other UI tests do (no JS engine).</summary>
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

  private static string Browse() => Between(Read("library.js"), "// Browse (#287)", "function hit(");
  private static string Open() => Between(Browse(), "const open = (g, p) => {", "const entryRow = (e) => {");
  private static string LoadGroup() => Between(Browse(), "const loadGroup = async (o) => {", "const refresh = () => {");

  [Fact]
  public void Group_HasItsOwnRoute()
  {
    var library = Read("library.js");
    var browse = Browse();
    Assert.Contains("const groupHash = (key, p) => `${view.base}/groups/${encodeURIComponent(key)}${String(p) ? `?${p}` : ''}`;", browse);
    Assert.Contains("onclick: () => { location.hash = groupHash(g.key, markParams()); } }", browse);
    var show = Between(library, "function show(sub, query)", "function inPanels(");
    Assert.Contains("view.groupKey = groupKeyOf(sub);", show);
    Assert.Contains("view.groupRoute?.(view.groupQuery);", show);
    Assert.Contains("try { return raw ? decodeURIComponent(raw) : null; } catch { return raw; }", library);
    var load = LoadGroup();
    Assert.Contains("if (o.q) p.set('q', o.q);", load);
    Assert.Contains("if (o.sort.value !== 'playlist') p.set('sort', o.sort.value);", load);
    Assert.Contains("new URLSearchParams([...(hideSeen ? [['unseen', '1']] : []), ...(likedOnly ? [['liked', '1']] : [])]);", browse);
    Assert.Contains("history.replaceState(history.state, '', o.hash);", load);
    Assert.Contains("window.scrollTo(0, 0);", Open());
    Assert.DoesNotContain("scrollIntoView", library);
    Assert.Contains("h('a', { class: 'lib-ib', href: `${view.base}/groups?type=${groupType}`, 'aria-label': 'All groups' }, icon('back'))", Open());
    // The mounted address's query (a reload or pasted address) reaches show() and so open(); show() takes it as given.
    Assert.Contains("show(ctx.sub, ctx.query);", Between(library, "function mount(ctx)", "function show(sub, query)"));
    Assert.Contains("view.groupQuery = query || '';", show);
    Assert.Contains("return open(g, new URLSearchParams(raw));", Between(browse, "view.groupRoute = (raw) => {", "\n    };"));
    // Back focuses the row of the group that was open after the list is drawn again, not before.
    Assert.Contains("return o.group.key;", Between(browse, "const close = () => {", "const focusRow = "));
    Assert.DoesNotContain("focus()", Between(browse, "const close = () => {", "const focusRow = "));
    Assert.Contains("if (b.dataset.key === key) b.focus();", Between(browse, "const focusRow = (key) => {", "};"));
    var route = Between(browse, "view.groupRoute = (raw) => {", "\n    };");
    var closed = route.IndexOf("const was = close();", StringComparison.Ordinal);
    var drawn = route.IndexOf("renderGroups();", StringComparison.Ordinal);
    var focused = route.IndexOf("focusRow(was);", StringComparison.Ordinal);
    Assert.True(closed >= 0 && drawn > closed && focused > drawn, "groupRoute must close, redraw, then focus the row.");
    var refresh = Between(browse, "const refresh = () => {", "};");
    Assert.True(refresh.IndexOf("renderGroups();", StringComparison.Ordinal) is var r && r >= 0 && refresh.IndexOf("focusRow(was);", StringComparison.Ordinal) > r,
      "refresh must redraw before it focuses the row.");
  }

  [Fact]
  public void Group_LoadsTitlesByThirtyWithASentinel()
  {
    var library = Read("library.js");
    var load = LoadGroup();
    Assert.Contains("const params = { cacheKey: list.cacheKey, includeDisabled: includeOff, group: o.group.key, sort: o.sort.value, offset: 0, limit: 30 };", load);
    Assert.Contains("found = await ctx.titles(params, ctx.controller.signal);", load);
    Assert.Contains("pages(ctx, o, params, { ...found, titles }, entryRow, seq, foot)", load);
    var pages = Between(library, "function pages(ctx, state, params, first, rowOf, seq, end) {", "// Marks (#298)");
    Assert.Contains("new IntersectionObserver(", pages);
    Assert.Contains("{ rootMargin: '600px' }", pages);
    Assert.Contains("'Try again'", pages);
    Assert.Contains("return end && foot.append(end());", pages);
    Assert.Contains("text: total ? `All ${fmt(total)} shown` : empty", load);
    Assert.Contains("'Nothing in this group matches.' : 'No entries in this group.'", load);
    var entryRow = Between(Browse(), "const entryRow = (e) => {", "};");
    Assert.Contains("e.contentType === 'Series' ? h('b', { text: seasonsLine(ctx, e) })", entryRow);
    Assert.Contains("e.contentType === 'Movies' && e.entries > 1 ? plural(e.entries, 'version') : null", entryRow);
    Assert.DoesNotContain("'More'", Browse());
    Assert.DoesNotContain("groupEntries", library);
  }

  [Fact]
  public void Head_IsStickyUnderTheTopBar()
  {
    var open = Open();
    var sticky = Between(open, "h('div', { class: 'lib-sticky' },", "o.body);");
    Assert.Contains("h('div', { class: 'lib-ghead' },", sticky);
    Assert.Contains("h('div', { class: 'lib-ghead-text' }, h('h2', { text: title, title }), o.count)", sticky);
    Assert.Contains("h('div', { class: 'lib-search lib-gfilter' }, input, clear)", sticky);
    Assert.Contains("hideSeen && seenHidden > 0 ? ` · ${fmt(seenHidden)} seen hidden` : '',", open);
    var css = Read("library.css");
    Assert.Contains(".lib-sticky { position: sticky; top: var(--topbar-h);", css);
    Assert.Contains(".lib-ghead { display: flex; align-items: center; gap: 10px; }", css);
    Assert.Contains(".lib-ghead h2 { margin: 0; font-family: var(--font-display); font-size: 20px; font-weight: 800; letter-spacing: -.03em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }", css);
  }

  [Fact]
  public void Filter_SendsQ_AfterTwoLettersWithDebounce()
  {
    Assert.Contains("const filterOf = (raw) => ((raw.trim().match(/[\\p{L}\\p{N}]/gu) || []).length >= 2 ? raw.trim() : '');", Read("library.js"));
    var open = Open();
    Assert.Contains("placeholder: `Filter ${title}`", open);
    Assert.Contains("const q = filterOf(input.value);", open);
    Assert.Contains("if (q !== o.q) o.timer = setTimeout(() => { o.q = q; loadGroup(o); }, 300);", open);
    Assert.Contains("if (o.q) params.q = o.q;", LoadGroup());
  }

  [Fact]
  public void Sort_CyclesThreeOrders()
  {
    Assert.Contains("const SORTS = [{ value: 'playlist', label: 'Playlist order' }, { value: 'title', label: 'A to Z' }, { value: 'year', label: 'Newest year' }];", Read("library.js"));
    var open = Open();
    Assert.Contains("o.sort = SORTS[(SORTS.indexOf(o.sort) + 1) % SORTS.length]; loadGroup(o);", open);
    Assert.Contains("sortChip.replaceChildren(icon('sort'), o.sort.label);", open);
    Assert.Contains("sortChip.setAttribute('aria-label', `Sort: ${o.sort.label}`);", open);
    Assert.Contains("sort: '<path d=\"M7 5v14M7 19l-3-3M7 19l3-3M17 19V5M17 5l-3 3M17 5l3 3\"/>',", Read("main.js"));
    Assert.Contains("private static readonly string[] Sorts = [\"playlist\", \"title\", \"year\"];", Read("api/Endpoints/LibraryTitlesEndpoints.cs"));
  }

  [Fact]
  public void Chips_ShareTheSearchTabsMarkFilters()
  {
    var open = Open();
    Assert.Contains("const seenChip = mark('Unseen', () => hideSeen, (v) => { hideSeen = v; });", open);
    Assert.Contains("const likedChip = mark('Liked', () => likedOnly, (v) => { likedOnly = v; });", open);
    Assert.Contains("onclick: () => { set(!get()); view.marksChanged?.(); loadGroup(o); },", open);
    Assert.Contains("const unseen = p.get('unseen') === '1';", open);
    var search = Between(Read("library.js"), "function searchSection(", "async function runSearch(");
    Assert.Contains("view.marksChanged = () => {\n      seenFilter.setAttribute('aria-pressed', String(hideSeen));", search);
    Assert.Contains("const reload = () => { if (state.open) loadGroup(state.open); };", Browse());
    Assert.Contains("if (hideSeen) params.hideSeen = true;\n      if (likedOnly) params.liked = true;", LoadGroup());
  }

  [Fact]
  public void Wide_KeepsTheGroupListBesideTheEntries()
  {
    var css = Read("library.css");
    var wide = Between(css, "@media (min-width: 1100px) {\n  .page:has(.lib-panel)", "\n}");
    Assert.Contains(".lib-groups-cols.has-open { display: grid; grid-template-columns: minmax(300px, 0.4fr) minmax(0, 1fr); gap: 24px; align-items: start; }", wide);
    Assert.Contains(".lib-groups-cols.has-open .lib-ghead .lib-ib { display: none; }", wide);
    Assert.Contains("@media (max-width: 1099px) { .lib-groups-cols.has-open .lib-groups-side { display: none; } }", css);
    Assert.Contains("const side = h('div', { class: 'lib-groups-side' }, h('h2', { class: 'library-title', text: 'Browse groups' }), groupsEl);", Browse());
  }

  [Fact]
  public void UnknownGroup_FallsBackToTheListWithAToast()
  {
    var route = Between(Browse(), "view.groupRoute = (raw) => {", "\n    };");
    Assert.Contains("const g = key === null ? null : browseGroups(list, includeOff).sorted.find((x) => x.key === key);", route);
    Assert.Contains("if (g) return open(g, new URLSearchParams(raw));", route);
    Assert.Contains("ctx.toast('That group is not in this list any more.', { error: true });", route);
    Assert.Contains("view.groupKey = null;", route);
    Assert.Contains("if (view.tab === 'groups' && !state.open && location.hash !== hash) history.replaceState(history.state, '', hash);", Browse());
  }
}
