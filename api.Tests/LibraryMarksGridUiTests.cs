using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Marks tab as a poster grid and the search rows' poster thumbnails (#429): only with TMDB on, the
/// view button remembered per device, tiles of titles not in the list as non-buttons that still get a poster, a tile
/// leaving with Undo, thumbnails only for films and series and the binding CSS. Source pins like the other Library UI
/// tests (no JS engine).</summary>
public class LibraryMarksGridUiTests
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
  private static string MyMarks() => Between(Library(), "function myMarks(ctx) {", "function syncRows(");
  private static string Tile() => Between(MyMarks(), "const buildTile = (key) => {", "return cell;");
  private static string Thumb() => Between(Library(), "function thumbRow(ctx, li, e) {", "\n  }\n");
  private static string Css() => Read("library.css");

  [Fact]
  public void Marks_GridWithTmdbOn_ListOtherwise()
  {
    var marks = MyMarks();
    // No button and no grid with TMDB off: the rows of phase 1.
    Assert.Contains("const viewBtn = ctx.tmdbEnabled === true ? h('button', { class: 'lib-ib lib-view', type: 'button', onclick: () => switchView() }) : null;", marks);
    Assert.Contains("view.grid = !!viewBtn && readMarksView() === 'grid';", marks);
    Assert.Contains("if (view.grid) return buildTile(key);", Between(marks, "const build = (key) => {", "const state"));
    // The grid of #428: the list becomes its items, the wrapper the grid; the button sits in the head.
    var paint = Between(marks, "const paintView = () => {", "};");
    Assert.Contains("section.classList.toggle('is-grid', view.grid);", paint);
    Assert.Contains("body.className = view.grid ? 'lib-grid' : 'lib-list';", paint);
    Assert.Contains("list.classList.toggle('lib-grid-items', view.grid);", paint);
    Assert.Contains("if (viewBtn) section.querySelector('.lib-ghead').append(viewBtn);", marks);
    // Tiles of a title in the list: the S15 tile with the year, opening the card with the lookup's name.
    var tile = Tile();
    Assert.Contains("const entry = t && { name: t.name, title: m.title, year: m.year, contentType: type, markKey: key, enabled: t.enabled };", tile);
    Assert.Contains("cell.replaceChildren(posterTile(ctx, { key, entry, title: m.title, year: m.year, group: t?.group, type,", tile);
    Assert.Contains("meta: !t ? 'Not in this list' : m.year ? String(m.year) : '', poster: !!state.looked }));", tile);
  }

  [Fact]
  public void Marks_ViewButton_RemembersPerDevice()
  {
    var library = Library();
    Assert.Contains("const MARKS_VIEW_KEY = 'strim.library.marksView';", library);
    var read = Between(library, "function readMarksView() {", "function writeMarksView(");
    Assert.Contains("try {", read);
    Assert.Contains("return localStorage.getItem(MARKS_VIEW_KEY) === 'list' ? 'list' : 'grid';", read);
    Assert.Contains("return 'grid';", read);
    var write = Between(library, "function writeMarksView(value) {", "\n  }\n");
    Assert.Contains("try {", write);
    Assert.Contains("localStorage.setItem(MARKS_VIEW_KEY, value);", write);

    var marks = MyMarks();
    var paint = Between(marks, "const paintView = () => {", "};");
    Assert.Contains("viewBtn.setAttribute('aria-label', view.grid ? 'Show as list' : 'Show as posters');", paint);
    Assert.Contains("viewBtn.replaceChildren(icon(view.grid ? 'list' : 'grid'));", paint);
    var toggle = Between(marks, "const switchView = () => {", "};");
    Assert.Contains("view.grid = !view.grid;", toggle);
    Assert.Contains("writeMarksView(view.grid ? 'grid' : 'list');", toggle);
    // The loaded marks are drawn again in place and the page starts at the top.
    Assert.Contains("for (const key of rows.keys()) build(key);", toggle);
    Assert.Contains("window.scrollTo(0, 0);", toggle);
  }

  [Fact]
  public void Marks_TilesNotInTheList_AreNotButtons_ButStillGetAPoster()
  {
    // Without a lookup answer the entry is null: posterTile draws a div and asks with "<title> (<year>)", the kind and
    // no group; with one, it asks with the lookup's name and group.
    var tile = Tile();
    Assert.Contains("const type = m.kind === 'series' ? 'Series' : 'Movies';", tile);
    Assert.Contains("const entry = t && {", tile);
    var posterTile = Between(Library(), "function posterTile(ctx, {", "function paintTile(");
    Assert.Contains("const tile = film\n", posterTile);
    Assert.Contains("const film = entry && opens(ctx, entry) ?", posterTile);
    Assert.Contains(": h('div', { class: 'lib-tile', dataset: { key: key || '' } }, art, text);", posterTile);
    Assert.Contains("if (poster) view.posters?.request(tile, { name: entry?.name || (year ? `${title} (${year})` : title), group: group || '', type });", posterTile);
    Assert.Contains("function posterTile(ctx, { key, entry, title, year, group, type, meta, bar, poster = true, posterUrl }) {", Library());
  }

  [Fact]
  public void Marks_PendingLookup_AsksNoPoster_MatchUsesItsGroup_ConfirmedMissUsesTheFallback()
  {
    // Pending (or failed) lookup: state.looked is not set, so the tile asks for nothing; a fallback with an empty group
    // would bypass the endpoint's exclusion of a group the title turns out to be in.
    var marks = MyMarks();
    var tile = Tile();
    // The first render: a new row starts without looked and is drawn at once, so its tile asks for no poster yet.
    var load = Between(marks, "const loadPage = ", "const left = () =>");
    Assert.Contains("rows.set(m.key, { li: null, mark: m, title: null });\n        list.append(build(m.key));", load);
    Assert.DoesNotContain("looked", load);
    Assert.Contains("poster: !!state.looked }));", tile);
    var posterTile = Between(Library(), "function posterTile(ctx, {", "function paintTile(");
    Assert.Contains("if (poster) view.posters?.request(tile, { name: entry?.name || (year ? `${title} (${year})` : title), group: group || '', type });", posterTile);

    var lookup = Between(marks, "const lookup = async (keys, seq) => {", "ctx.lookupMarks = () => {");
    // An answer sets looked; only a failed lookup (answer null) leaves it false.
    Assert.Contains("const looked = !!answer;", lookup);
    Assert.Contains("if (state) state.looked = looked;\n        if (!state || JSON.stringify(title) === JSON.stringify(state.title)) continue;", lookup);
    // A tile is drawn again once looked changes, so it now asks; a list row only when its title changed (focus kept).
    Assert.Contains("if (state && view.grid && looked !== !!state.looked) state.title = undefined;", lookup);
    Assert.DoesNotContain("if (state && state.looked !== !!answer)", lookup);
    // A delayed match: the request carries the lookup's name and group.
    Assert.Contains("const entry = t && { name: t.name,", tile);
    Assert.Contains("group: t?.group,", tile);
    // A confirmed miss (an answer without the key): looked with no title, so entry and group are empty and the request
    // is "<title> (<year>)" with no group.
    Assert.Contains("const title = answer?.titles?.[key] || null;", lookup);
    Assert.Contains("meta: !t ? 'Not in this list'", tile);
    // Switching to the grid draws every row again with the looked state recorded meanwhile.
    Assert.Contains("for (const key of rows.keys()) build(key);", Between(marks, "const switchView = () => {", "};"));
    // A cached answer is kept per name, group and type, so a fallback's poster never stands in for the resolved group.
    var loader = Between(Library(), "function posterLoader(ctx) {", "function showPoster(");
    Assert.Contains("key: [tile.dataset.key, item.name, item.group, item.type].join('\\n')", loader);
    Assert.Contains("const p = posterAnswers.get(b.key);", loader);
  }

  [Fact]
  public void Marks_EmptyMark_LeavesTheGridWithUndo()
  {
    var marks = MyMarks();
    // The tile lives in the row's <li> (state.li), so marksChanged hides it and offers Undo as for a row.
    var tile = Tile();
    Assert.Contains("const cell = state.li || h('li');", tile);
    Assert.Contains("Object.assign(cell.dataset, { type, key });", tile);
    Assert.Contains("state.li = cell;", tile);
    var changed = Between(marks, "tab.marksChanged = (key, mark, how) => {", "tab.marksCleared");
    Assert.Contains("r.li.hidden = !fits;", changed);
    Assert.Contains("action: { label: 'Undo'", changed);
    // A hidden cell stays hidden: [hidden] wins over the cell's display.
    Assert.Contains("display: none !important;", Read("theme.css"));
  }

  [Fact]
  public void SearchRows_HaveAThumbnailForFilmsAndSeriesOnly()
  {
    var library = Library();
    // Only search results get one; group entries keep the marker.
    Assert.Contains("const rowOf = (t) => thumbRow(ctx, hit(ctx, t, list.cacheKey), t);", library);
    Assert.Contains("const entryRow = (x) => row(ctx, x, x.group, list.cacheKey, groupMeta(x), null);", library);
    Assert.DoesNotContain("thumbRow(", Between(library, "function row(ctx, e, group, cacheKey, meta, badge)", "// mount() runs"));
    // Only with TMDB on (a loader) and only for films and series; channels keep the marker.
    var thumb = Thumb();
    Assert.Contains("if (!view.posters || (type !== 'Movies' && type !== 'Series')) return li;", thumb);
    Assert.Contains("const thumb = ctx.h('span', { class: 'lib-thumb', 'aria-hidden': 'true', dataset: { type, key: e.markKey || '' } });", thumb);
    // The marker moves into the thumbnail, centred over the type's soft colour.
    Assert.Contains("dot.replaceWith(thumb);", thumb);
    Assert.Contains("thumb.append(dot);", thumb);
    Assert.Contains("li.classList.add('has-thumb');", thumb);
    Assert.Contains("view.posters.request(thumb, { name: e.name, group: e.group || '', type });", thumb);
    // The loader's image goes into the thumbnail itself.
    Assert.Contains("const art = tile.matches('.lib-thumb') ? tile : tile.querySelector('.lib-art');", library);
  }

  [Fact]
  public void Css_ThumbAndTallRow()
  {
    var css = Css();
    Assert.Contains(".lib-thumb { position: relative; flex-shrink: 0; width: 40px; height: 60px; overflow: hidden; border-radius: 8px; display: grid; place-items: center; background: var(--movies-soft); }", css);
    Assert.Contains(".lib-thumb[data-type=\"Series\"] { background: var(--series-soft); }", css);
    Assert.Contains(".lib-thumb img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0; transition: opacity .2s; }", css);
    Assert.Contains(".lib-thumb img.is-loaded { opacity: 1; }", css);
    Assert.Contains(".lib-row.has-thumb { min-height: 76px; padding-left: 10px; }", css);
    Assert.Contains(".lib-row.is-seen .lib-thumb img.is-loaded { opacity: .45; }", css);
    // The Marks grid: 6px below the head, the foot across all columns, at most 1280px from 1100px.
    Assert.Contains(".library-mylist > .lib-sticky + .lib-grid { margin-top: -6px; }", css);
    Assert.Contains(".lib-grid > .library-foot { grid-column: 1 / -1; }", css);
    Assert.Contains("@media (min-width: 1100px) { .library-mylist.is-grid { max-width: 1280px; } }", css);
  }
}
