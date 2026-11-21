using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Library's Home tab (#425) in library.js, main.js and library.css at their source, as the other
/// Library UI tests do (api.Tests has no JS engine).</summary>
public class LibraryHomeUiTests
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

  private static string Home() => Between(Read("library.js"), "function homeSection(ctx) {", "// My marks (#299)");

  [Fact]
  public void Home_IsTheFirstTabAndTheDefaultRoute()
  {
    var library = Read("library.js");
    var tabs = Between(library, "const TABS = [", "];");
    Assert.StartsWith("const TABS = [\n    { name: 'home', label: 'Home', icon: 'home' },\n    { name: 'search'", tabs);
    Assert.Contains("const tab = TABS.some((t) => t.name === name) ? name : 'home';", Between(library, "function show(sub, query)", "function inPanels("));
    var main = Read("main.js");
    Assert.Contains("home: '<path d=\"M4 11l8-6.5 8 6.5V20h-5.5v-5.5h-5V20H4z\"/>',", main);
    var tab = Between(main, "function libraryTab(target)", "function closeSheets()");
    Assert.Contains("const hash = `${base}/home`;", tab);
    Assert.Contains("return 'home';", tab);
    // On Home the owner's head shows "Library · <name>" and no h1; the cover carries the name as the h1.
    var head = Between(library, "function ownerHead(ctx, nav)", "function libraryMenu(ctx)");
    Assert.Contains("kicker.textContent = home ? `Library · ${name}` : 'Library';", head);
    Assert.Contains("title.hidden = home;", head);
    Assert.Contains("view.homeHead?.(tab === 'home');", library);
    Assert.Contains("h('h1', { class: 'lib-hero-name', text: name, title: name })", Home());
  }

  [Fact]
  public void Cover_SearchFieldHandsOverToTheSearchTab()
  {
    var home = Home();
    Assert.Contains("placeholder: 'Search this list', 'aria-label': 'Search this list',", home);
    Assert.Contains("onfocus: toSearch, oninput: toSearch,", home);
    var hand = Between(home, "const toSearch = () => {", "};");
    Assert.Contains("if (q) link.href = `${base}/search?${new URLSearchParams({ q })}`;", hand);
    Assert.Contains("location.hash = hash;", hand);
    Assert.Contains("show('search', hash.split('?')[1] || '');", hand);
    Assert.Contains("view.focusSearch] = [true, true];", hand);
    Assert.Contains("if (view.focusSearch && view.tab === 'search') view.panels.search.querySelector('input')?.focus();", Read("library.js"));
    // Facts: entries and groups that are on; "Loading the list" before. A viewer gets the plain field under the cover.
    Assert.Contains("facts.textContent = `${plural(on.reduce((sum, g) => sum + g.count, 0), 'entry', 'entries')} in ${plural(on.length, 'group')} · read-only`;", home);
    Assert.Contains("|| 'Loading the list';", home);
    Assert.Contains("const cover = isViewer(ctx) ? field", home);
    Assert.Contains("const field = h('div', { class: 'lib-search' }, input);", home);
  }

  [Fact]
  public void Watchlist_AsksWatchlistedMarksNewestFirst_AndLooksThemUp()
  {
    var home = Home();
    Assert.Contains("const watch = marked(part('Your watchlist', `${base}/marks?view=watchlist`, 'All'),", home);
    Assert.Contains("{ watchlist: true, sort: 'watchlisted', pageSize: 12 }, (m) => m.watchlist === true && !m.seen,", home);
    Assert.Contains("page = await ctx.listMarks(params, ctx.controller.signal);", home);
    Assert.Contains("answer = await ctx.lookupTitles({ cacheKey: ctx.list.cacheKey, includeDisabled: includeOff, keys: s.items.map((m) => m.key) });", home);
    Assert.Contains("items.slice(0, 5).map((m) => markRow(m, s.titles?.[m.key] || null))", home);
    Assert.Contains("const where = !t ? 'Not in this list'", home);
    Assert.Contains("'Nothing on your watchlist yet. Add a film or series with the bookmark in its card.'", home);
    // Marks set anywhere repaint the rows; a title marked seen no longer fits and leaves at once.
    Assert.Contains("const before = view.marksChanged;", home);
    Assert.Contains("before?.(key, mark, how);", home);
    Assert.Contains("if (item) s.paint();", home);
    Assert.Contains("const kept = s.items.filter(s.fits);", home);
  }

  [Fact]
  public void Follow_UsesLikedSeriesInTheList()
  {
    var home = Home();
    Assert.Contains("const follow = marked(part('Series you follow', `${base}/marks?view=liked`, 'All'),", home);
    Assert.Contains("{ rating: 1, pageSize: 200 }, (m) => m.rating === 1,", home);
    Assert.Contains("const kept = s.items.filter((m) => m.kind === 'series' && s.fits(m) && s.titles?.[m.key]);", home);
    Assert.Contains("s.p.el.hidden = !kept.length;", home);
    Assert.Contains("s.p.n.textContent = `${fmt(kept.length)} liked series`;", home);
    Assert.Contains("[h('b', { text: seasonsLine(ctx, t) }), ' · ', t.group]", home);
  }

  [Fact]
  public void NewGroups_ReadTheDeviceCountsWithoutWriting()
  {
    var home = Home();
    Assert.Contains("const fresh = part('New in your groups', `${base}/groups`, 'All groups');", home);
    Assert.Contains("const seen = readCounts(ctx) || {};", home);
    Assert.Contains(".sort((a, b) => b[1] - a[1]).slice(0, 3);", home);
    Assert.Contains("fresh.el.hidden = !grown.length;", home);
    Assert.Contains("h('span', { class: 'lib-new', text: `+${fmt(n)} new` })", home);
    Assert.Contains("const href = `${base}/groups/${encodeURIComponent(g.key)}`;", home);
    Assert.Contains("e.currentTarget.href = String(p) ? `${href}?${p}` : href;", home);
    Assert.DoesNotContain("writeCounts", home);
    Assert.DoesNotContain("seenCounts", home);
    Assert.DoesNotContain("localStorage", home);
    // The section reads the counts again each time Home is shown, so a group opened since (which stored its count) drops
    // out as it does in the Groups tab; and when the list arrives.
    var fresh = Between(home, "view.homeFresh = () => {", "view.homeList = (again) => {");
    Assert.Contains("fresh.el.hidden = true;", fresh);
    Assert.Contains("const seen = readCounts(ctx) || {};", fresh);
    Assert.Contains("view.homeFresh();", Between(home, "view.homeList = (again) => {", "// Marks set anywhere"));
    Assert.Contains("if (tab === 'home') view.homeFresh?.();", Between(Read("library.js"), "function show(sub, query)", "function inPanels("));
  }

  [Fact]
  public void Browse_TilesOpenTheGroupsTabWithTheType()
  {
    var home = Home();
    Assert.Contains("const types = part('Browse');", home);
    Assert.Contains("const tiles = GROUP_TYPES.map((t) => ({ ...t, on: on.filter((g) => g.type === t.type) })).filter((t) => t.on.length);", home);
    Assert.Contains("h('a', { class: 'lib-type', href: `${base}/groups?type=${t.value}` },", home);
    Assert.Contains("h('span', { class: 'lib-dot', 'aria-hidden': 'true', dataset: { type: t.type } }),", home);
    Assert.Contains("`${plural(t.on.length, 'group')} · ${fmt(t.on.reduce((sum, g) => sum + g.count, 0))}`", home);
    // Sections in their order; from 1100px the two lists sit side by side.
    Assert.Contains("h('div', { class: 'lib-home-lists' }, watch.p.el, follow.p.el), fresh.el, types.el);", home);
  }

  [Fact]
  public void Sections_FailIndependently()
  {
    var home = Home();
    Assert.Contains("h('p', { class: 'lib-empty', role: 'status', text: 'Loading…' })", home);
    Assert.Contains("h('button', { class: 'btn btn-sm', type: 'button', onclick: again }, 'Try again')", home);
    Assert.Contains("if (seq === s.seq && live()) failed(p, err, s.load);", home);
    Assert.Contains("if (!list) return failed(types, null, again);", home);
    Assert.Contains("watch.load();\n    follow.load();", home);
    // A failed list still paints the marks rows, as "Not in this list".
    Assert.Contains("s.paint = () => (!s.loaded || (!s.titles && ctx.listState === 'loading') ? busy(p) : paint(s));", home);
    var library = Read("library.js");
    Assert.Contains("view.homeList(() => load(ctx, own));", Between(library, "async function load(ctx, own)", "// The type control's choices"));
  }

  [Fact]
  public void Css_CoverGradientAndTypeTiles()
  {
    var css = Read("library.css");
    foreach (var rule in new[]
    {
      ".lib-hero { margin-bottom: 14px; padding: 18px 18px 16px; border-radius: var(--radius-lg); background: linear-gradient(135deg, color-mix(in srgb, var(--accent) 24%, var(--surface)), color-mix(in srgb, var(--series) 18%, var(--surface))); }",
      ".lib-hero-name { margin: 0; font-family: var(--font-display); font-weight: 800; font-size: 34px;",
      "-webkit-line-clamp: 2;",
      ".lib-hero .input-search { background-color: var(--surface); }",
      ".lib-types { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }",
      ".lib-type { display: flex;",
      ".lib-type b {",
      ".lib-type small {",
      ".lib-empty { margin: 0 2px; font-size: 13.5px; color: var(--ink-3); }",
      ".lib-hero .lib-search { flex: 1; max-width: 520px; margin-left: auto; }",
      ".lib-types { grid-template-columns: repeat(3, 220px); }",
      ".lib-panel[data-tab=\"home\"] { max-width: 1280px; margin: 0 auto; } .lib-home-lists { display: grid; grid-template-columns: 1fr 1fr;",
    })
      Assert.Contains(rule, css);
  }
}
