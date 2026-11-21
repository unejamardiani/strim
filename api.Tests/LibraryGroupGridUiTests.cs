using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins an open group as a poster grid (#428): only for film and series groups with TMDB on, the view button
/// remembered per device, the same title pages as the list, the 3/5/7 columns and the wide grid without the group list.
/// Source pins like the other Library UI tests (no JS engine).</summary>
public class LibraryGroupGridUiTests
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
  private static string Browse() => Between(Library(), "function browseSection(ctx, list) {", "function hit(");
  private static string Pages() => Between(Library(), "function pages(ctx, state, params, first, rowOf, seq, end) {", "// Marks (#298)");
  private static string Css() => Read("library.css");
  private static string Grid() => Between(Css(), "/* An open film or series group as posters (#428)", "\n}\n");

  [Fact]
  public void Grid_OnlyForFilmAndSeriesGroupsWithTmdbOn()
  {
    var browse = Browse();
    Assert.Contains("const gridable = (g) => ctx.tmdbEnabled === true && (g.type === 'Movies' || g.type === 'Series');", browse);
    // No button, no grid for any other group: it keeps the list.
    Assert.Contains("cur.viewBtn = gridable(g) ? h('button', { class: 'lib-ib lib-view', type: 'button', onclick: () => switchView(cur) }) : null;", browse);
    Assert.Contains("cur.grid = !!cur.viewBtn && readGroupView() === 'grid';", browse);
    Assert.Contains("else cur.body.replaceChildren(pages(ctx, state, params, first, entryRow, seq, foot(`All ${fmt(total)} shown`)));", browse);
    // The button sits at the head's right end, after the Off badge.
    Assert.Contains("if (cur.viewBtn) entriesEl.querySelector('.lib-ghead').append(cur.viewBtn);", browse);
    // Closing a group leaves the grid.
    var close = Between(browse, "const close = () => {", "};");
    Assert.Contains("el.classList.remove('is-grid');", close);
    Assert.Contains("state.grid = false;", close);
  }

  [Fact]
  public void ViewButton_TogglesAndRemembersPerDevice()
  {
    var library = Library();
    Assert.Contains("const GROUP_VIEW_KEY = 'strim.library.groupView';", library);
    var read = Between(library, "function readGroupView() {", "function writeGroupView(");
    Assert.Contains("try {", read);
    Assert.Contains("return localStorage.getItem(GROUP_VIEW_KEY) === 'list' ? 'list' : 'grid';", read);
    Assert.Contains("return 'grid';", read);
    var write = Between(library, "function writeGroupView(value) {", "\n  }\n");
    Assert.Contains("try {", write);
    Assert.Contains("localStorage.setItem(GROUP_VIEW_KEY, value);", write);

    var browse = Browse();
    var paint = Between(browse, "const paintView = (cur) => {", "};");
    Assert.Contains("cur.viewBtn.setAttribute('aria-label', cur.grid ? 'Show as list' : 'Show as posters');", paint);
    Assert.Contains("cur.viewBtn.replaceChildren(icon(cur.grid ? 'list' : 'grid'));", paint);
    Assert.Contains("el.classList.toggle('is-grid', cur.grid);", paint);
    var toggle = Between(browse, "const switchView = (cur) => {", "window.scrollTo(0, 0);");
    Assert.Contains("cur.grid = !cur.grid;", toggle);
    Assert.Contains("writeGroupView(cur.grid ? 'grid' : 'list');", toggle);
  }

  [Fact]
  public void Grid_UsesTheSameTitlePagesAsTheList()
  {
    var browse = Browse();
    // The first page and its follow-ups come from the same pages() of 30 titles, with tiles instead of rows.
    Assert.Contains("if (cur.grid) cur.body.replaceChildren(pages(ctx, state, params, first, entryTile, seq, cur.end()));", browse);
    Assert.Contains("return posterTile(ctx, { key: x.markKey, entry: x, title: x.title || x.name, year: x.year, group: x.group, type: x.contentType,", browse);
    Assert.Contains("meta: x.contentType === 'Series' ? seasonsLine(ctx, x) : x.year ? String(x.year) : '' });", browse);
    // Switching redraws the loaded titles without asking again and starts at the top.
    var toggle = Between(browse, "const switchView = (cur) => {", "};");
    Assert.Contains("cur.body.replaceChildren(pages(ctx, state, cur.params, cur.first, cur.grid ? entryTile : entryRow, ++state.seq, cur.end()));", toggle);
    Assert.Contains("window.scrollTo(0, 0);", toggle);

    var pages = Pages();
    Assert.Contains("offset: ul.children.length, limit: 30", pages);
    Assert.Contains("{ rootMargin: '600px' }", pages);
    Assert.Contains("first.titles.push(...(page.titles || []));", pages);
    Assert.Contains("first.hasMore = page.hasMore;", pages);
    Assert.Contains("const ul = state.grid ? h('div', { class: 'lib-grid-items' }, first.titles.map(rowOf)) : h('ul', { class: 'search-list' }, first.titles.map(rowOf));", pages);
    Assert.Contains("return h('div', { class: state.grid ? 'lib-grid' : 'lib-list' }, ul, foot);", pages);
    // The sentinel and the foot span all columns.
    Assert.Contains(".lib-grid > .lib-pages-foot { grid-column: 1 / -1; }", Grid());
  }

  [Fact]
  public void Grid_ColumnsAreThreeFiveSeven()
  {
    var grid = Grid();
    Assert.Contains(".lib-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px 12px; margin-top: 6px; }", grid);
    Assert.Contains("@media (min-width: 768px) { .lib-grid { grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 20px 14px; } }", grid);
    Assert.Contains("@media (min-width: 1100px) {\n  .lib-grid { grid-template-columns: repeat(7, minmax(0, 1fr)); gap: 22px 16px; }", grid);
    Assert.Contains(".lib-grid .lib-tile { flex: none; }", grid);
  }

  [Fact]
  public void Grid_Wide_HidesTheGroupListBeside()
  {
    var grid = Grid();
    var wide = grid[grid.IndexOf("@media (min-width: 1100px) {", StringComparison.Ordinal)..];
    Assert.Contains(".lib-groups-cols.has-open.is-grid { display: block; }", wide);
    Assert.Contains(".lib-groups-cols.has-open.is-grid .lib-groups-side { display: none; }", wide);
    Assert.Contains(".lib-groups-cols.has-open.is-grid .lib-ghead .lib-ib { display: grid; }", wide);
    Assert.Contains(".lib-panel[data-tab=\"groups\"]:has(.is-grid) { width: 100%; max-width: 1280px; margin: 0 auto; }", wide);
    // List view keeps the two columns, with its view button.
    Assert.Contains(".lib-groups-cols.has-open .lib-ghead .lib-ib.lib-view { display: grid; }", wide);
  }

  [Fact]
  public void GridPanel_TakesThePageWidth()
  {
    // Without width: 100% the centred panel shrinks to the width of its content (#482).
    var css = Css();
    Assert.Contains(".lib-panel[data-tab=\"groups\"]:has(.is-grid) { width: 100%; max-width: 1280px; margin: 0 auto; }", css);
    Assert.DoesNotContain(".lib-panel[data-tab=\"groups\"]:has(.is-grid) { max-width: 1280px; margin: 0 auto; }", css);
  }
}
