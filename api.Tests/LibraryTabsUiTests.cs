using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Library's tabs and routes (#418) at their source, as the other UI tests do (no JS engine).</summary>
public class LibraryTabsUiTests
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

  private static string Mount() => Between(Read("library.js"), "function mount(ctx)", "function show(sub, query)");

  [Fact]
  public void Routes_AcceptLibrarySubPaths()
  {
    var main = Read("main.js");
    var parse = Between(main, "function parseRoute()", "function navigate(");
    Assert.Contains(@"/^\/p\/([0-9a-f-]{36})\/library(?:\/([^?#]*))?(?:\?(.*))?$/i", parse);
    Assert.Contains(@"/^\/shared\/([0-9a-f-]{36})(?:\/([^?#]*))?(?:\?(.*))?$/i", parse);
    Assert.Contains("return { name: 'library', id: library[1].toLowerCase(), sub: librarySub(library[2])", parse);
    Assert.Contains("return { name: 'shared', id: shared[1].toLowerCase(), sub: librarySub(shared[2])", parse);
    Assert.Contains("match[2] ? 'sharing' : 'playlist'", parse);
    // A tab change of the mounted Library shows the tab instead of aborting and loading again.
    var route = Between(main, "function route()", "function libraryTab(target)");
    var show = route.IndexOf("window.StrimLibrary.show(target.sub, target.query);", StringComparison.Ordinal);
    Assert.True(show > 0 && show < route.IndexOf("libraryController?.abort();", StringComparison.Ordinal));
    Assert.Contains("!libraryController.signal.aborted\n    && libraryMounted?.name === target.name && libraryMounted.id === target.id", route);
    // The root and an unknown tab open Home (#425) and replace the address.
    var tab = Between(main, "function libraryTab(target)", "function closeSheets()");
    Assert.Contains("if (LIBRARY_TABS.includes(target.sub.split('/')[0])) return target.sub;", tab);
    Assert.Contains("const hash = `${base}/home`;", tab);
    Assert.Contains("return 'home';", tab);
    Assert.Contains("const LIBRARY_TABS = ['home', 'search', 'groups', 'marks'];", main);
  }

  [Fact]
  public void Tabs_AreLinksWithAriaCurrent()
  {
    var library = Read("library.js");
    var tabs = Between(library, "const TABS = [", "];");
    var at = -1;
    foreach (var tab in new[] { "{ name: 'home', label: 'Home', icon: 'home' }", "{ name: 'search', label: 'Search', icon: 'search' }", "{ name: 'groups', label: 'Groups', icon: 'grid' }", "{ name: 'marks', label: 'Marks', icon: 'check' }" })
    {
      var next = tabs.IndexOf(tab, StringComparison.Ordinal);
      Assert.True(next > at, $"'{tab}' missing or out of order.");
      at = next;
    }
    var mount = Mount();
    Assert.Contains("const base = isViewer(ctx) ? `#/shared/${playlist.id}` : `#/p/${playlist.id}/library`;", mount);
    Assert.Contains("h('nav', { class: 'lib-tabs', 'aria-label': 'Library' },", mount);
    Assert.Contains("h('a', { href: `${base}/${t.name}`, dataset: { tab: t.name } }, icon(t.icon), t.label)", mount);
    var show = Between(library, "function show(sub, query)", "function inPanels(");
    Assert.Contains("a.setAttribute('aria-current', 'page');", show);
    Assert.Contains("window.scrollTo(0, 0);", show);
    Assert.Contains("show(ctx.sub, ctx.query);", mount);
    var main = Read("main.js");
    Assert.Contains("search: '<circle cx=\"11\" cy=\"11\" r=\"6.5\"/>", main);
    Assert.Contains("grid: '<rect x=\"4\" y=\"4\" width=\"7\" height=\"7\" rx=\"1.5\"/>", main);
  }

  [Fact]
  public void Panels_StayInTheDomWhenHidden()
  {
    var mount = Mount();
    Assert.Single(Regex.Matches(mount, Regex.Escape("class: 'lib-panel'")));
    Assert.Contains("h('section', { class: 'lib-panel', dataset: { tab: t.name }, hidden: true })", mount);
    Assert.Contains("root.replaceChildren(...head, panels.home, panels.search, panels.groups, panels.marks);", mount);
    var library = Read("library.js");
    Assert.Contains("for (const [key, panel] of Object.entries(view.panels)) panel.hidden = key !== tab;", Between(library, "function show(sub, query)", "function inPanels("));
    // Search and Groups fill their panels once the list is loaded; nothing else replaces the root.
    var load = Between(library, "async function load(ctx, own)", "// The type control's choices");
    Assert.Contains("view.panels.search.replaceChildren(searchSection(", load);
    Assert.Contains("view.panels.groups.replaceChildren(browse.el);", load);
    Assert.DoesNotContain("root.replaceChildren", load);
  }

  [Fact]
  public void Head_HasNoSummaryCardAndNoOffToggleButton()
  {
    var library = Read("library.js");
    var builders = Between(library, "function mount(ctx)", "function libraryMenu(ctx)")
      + Between(library, "async function load(ctx, own)", "async function runSearch(");
    Assert.DoesNotContain("in your list", builders);
    Assert.DoesNotContain("'Include switched-off groups'", builders);
    Assert.DoesNotContain("String(includeOff)", builders);
    Assert.DoesNotContain("editor-head library-head", library);

    var head = Between(library, "function ownerHead(ctx, nav)", "function libraryMenu(ctx)");
    Assert.Contains("h('a', { class: 'lib-ib is-quiet', href: '#/', 'aria-label': 'Back to playlists' }, icon('back'))", head);
    Assert.True(head.IndexOf("nav,", StringComparison.Ordinal) < head.IndexOf("'aria-label': 'More'", StringComparison.Ordinal));
    Assert.Contains("'aria-label': 'More', 'aria-haspopup': 'dialog', onclick: () => libraryMenu(ctx) }, icon('more')", head);

    var menu = Between(library, "function libraryMenu(ctx)", "async function load(");
    Assert.Contains("ctx.openMenu(playlist.name || 'Untitled', [", menu);
    Assert.Contains("of ${plural(list.totalChannels, 'entry', 'entries')} in your list`", menu);
    Assert.Contains("'Loading the list'", menu);
    Assert.Contains("h('a', { class: 'menu-item', href: `#/p/${playlist.id}` },", menu);
    Assert.Contains("icon('list')), h('span', { class: 'grow', text: 'Edit groups' })", menu);
    Assert.Contains("'Include switched-off groups'", menu);
    Assert.Contains("class: 'toggle', type: 'checkbox', role: 'switch', checked: includeOff,", menu);
    Assert.Contains("openMenu: (title, nodes) => {", Read("main.js"));
  }

  [Fact]
  public void Viewer_KeepsCoverAndGetsNoMenu()
  {
    var mount = Mount();
    Assert.Contains("const head = ctx.cover ? [ctx.cover.el, h('div', { class: 'lib-tabs-row' }, nav)] : [ownerHead(ctx, nav)];", mount);
    var library = Read("library.js");
    Assert.Single(Regex.Matches(library, Regex.Escape("libraryMenu(ctx) }")));
    Assert.Contains("function viewerCover(ctx)", library);
    Assert.DoesNotContain("libraryMenu", Between(library, "function viewerCover(ctx)", "function accessGone("));
  }

  [Fact]
  public void Css_BottomBarBelow768_SegmentedFrom768()
  {
    var css = Read("library.css");
    Assert.Contains(".lib-tabs { position: fixed; z-index: 20; left: 12px; right: 12px; bottom: calc(14px + var(--safe-bottom)); height: 62px;", css);
    Assert.Contains("background: var(--dock-bg); border: 1px solid var(--dock-border); color: var(--dock-ink-2);", css);
    Assert.Contains("body:has(.lib-tabs) .site-links { padding-bottom: calc(104px + var(--safe-bottom)); }", css);
    var wide = Between(css, "@media (min-width: 768px) {\n  .lib-tabs {", "\n}");
    Assert.Contains(".lib-tabs { position: static; height: auto; display: inline-flex;", wide);
    Assert.Contains(".lib-tabs a[aria-current=\"page\"] { background: var(--pill-on);", wide);
    Assert.Contains(".lib-tabs-row { display: flex; margin-top: 12px; }", wide);
    foreach (var cls in new[] { ".lib-head {", ".lib-head-text {", ".lib-kicker {", ".lib-ib {", ".lib-ib::after { content: \"\"; position: absolute; inset: -4px; }", ".lib-ib.is-quiet {", ".lib-menu-facts {", ".lib-menu-facts small {" })
      Assert.Contains(cls, css);
  }

  [Fact]
  public void Css_NameDoesNotBreakInsideAWord()
  {
    var css = Read("library.css");
    var name = Between(css, ".lib-name {", "}");
    Assert.Contains("white-space: nowrap;", name);
    Assert.Contains("text-overflow: ellipsis;", name);
    Assert.Contains("@media (min-width: 768px) { .lib-name { font-size: 32px; } }", css);
    Assert.True(css.IndexOf(".lib-name {", StringComparison.Ordinal) < css.IndexOf(".lib-name { font-size: 32px; }", StringComparison.Ordinal));
    Assert.DoesNotContain(".library-head .page-title", css);
    Assert.DoesNotContain("overflow-wrap: anywhere", Between(css, ".lib-head {", "@media (min-width: 768px)"));
  }
}
