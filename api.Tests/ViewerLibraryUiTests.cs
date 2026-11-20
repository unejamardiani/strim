using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the viewer's Library (#383) in main.js, library.js and library.css at their source, as the other UI tests do
/// (api.Tests has no JS engine). The playlist-scoped routes themselves are covered by SharedLibraryReadTests.
/// </summary>
public class ViewerLibraryUiTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"'{start}' not found.");
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    Assert.True(to > from, $"'{end}' not found after '{start}'.");
    return source[from..to];
  }

  private static string Shared() => Between(Read("main.js"), "function showSharedLibrary(id)", "function linkCard(");

  [Fact]
  public void Mounts_WithoutEditorLinksOrOffToggle()
  {
    var main = Read("main.js");
    // #/shared/<playlistId> opens the same Library as a viewer; the "Shared with you" card and menu link there.
    Assert.Contains("const shared = /^\\/shared\\/([0-9a-f-]{36})(?:\\/([^?#]*))?(?:\\?(.*))?$/i.exec(path);", Between(main, "function parseRoute()", "function navigate("));
    Assert.Contains("if (target.name === 'shared') return showSharedLibrary(target.id);", Between(main, "function route()", "function closeSheets()"));
    var cards = Between(main, "// ---------- Shared with you (#382) ----------", "// ---------- Editor ----------");
    Assert.Equal(2, Regex.Matches(cards, Regex.Escape("href: `#/shared/${item.playlistId}`")).Count);
    Assert.DoesNotContain("/library`", cards);
    Assert.Contains("...playlist.scope,", Between(main, "function showLibrary(playlist)", "function showSharedLibrary(id)"));
    var shared = Shared();
    Assert.Contains("role: 'viewer',", shared);
    Assert.Contains("return target.name === 'shared' && target.id === id;", shared);
    // The owner's name comes from library/open (#407); no call to /shares/mine.
    Assert.Contains("ownerName: open.ownerName", shared);
    Assert.DoesNotContain("/shares/mine", shared);

    var library = Read("library.js");
    var mount = Between(library, "function mount(ctx)", "function show(sub)");
    Assert.Contains("if (isViewer(ctx)) ctx.cover = viewerCover(ctx);", mount);
    Assert.Contains("const head = ctx.cover ? [ctx.cover.el, h('div', { class: 'lib-tabs-row' }, nav)] : [ownerHead(ctx, nav)];", mount);
    // The viewer's head: the cover, "My lists", Read-only, "Shared by", no "Edit groups" or player link.
    var cover = Between(library, "function viewerCover(ctx)", "function accessGone(");
    Assert.Contains("`cover library-cover cover-${ctx.pick(playlist.id, 6) + 1}`", cover);
    Assert.Contains("h('a', { href: '#/' }, icon('back'), 'My lists')", cover);
    Assert.Contains("'Read-only'", cover);
    Assert.Contains("'Shared by ', h('b', { text: owner })", cover);
    foreach (var editorOnly in new[] { "Edit groups", "#/p/", "shareUrl", "player", "your list", "style" })
      Assert.DoesNotContain(editorOnly, cover);

    // No switched-off toggle, no Off badges and "this list" in the viewer's copy.
    Assert.DoesNotContain("libraryMenu", Between(library, "function viewerCover(ctx)", "function accessGone("));
    Assert.Contains("m.enabled === false && !isViewer(ctx) ? h('span', { class: 'search-badge off', text: 'Off' }) : null,", library);
    Assert.Contains("const listWord = (ctx) => (isViewer(ctx) ? 'this list' : 'your list');", library);
    Assert.Contains("`${f.similar.filter((s) => s.inList).length} in ${listWord(ctx)}`", library);
    Assert.Contains("if (isViewer(ctx)) return groupsEl.replaceChildren(...groupTiles(ctx, sorted, open));", library);
    // The group on every item, and as the first row of the film card.
    Assert.Contains("groupChip(ctx, m.group),", library);
    Assert.Contains("groupChip(ctx, e.group || view.group.title), markControls(ctx, e)", library);
    Assert.Contains("h('span', null, h('small', { text: 'In group' }), h('b', { text: entry.group })),", library);
    // Row marks use the film card's capsule in both Libraries.
    Assert.Contains("const el = h('span', { class: 'library-marks film-marks', role: 'group',", library);

    var css = Read("library.css");
    Assert.Contains(".library-hit .film-marks button { width: 32px; height: 32px;", css);
    Assert.Contains(".group-chip { display: inline-flex; align-items: center; gap: 6px; max-width: 190px;", css);
    // Below 600px no chip before the marks, neither in search hits nor in a group's entries.
    Assert.Contains("@media (max-width: 599px) { .library-search .group-chip, .library-entries .group-chip { display: none; } }", css);
    Assert.Contains("@media (min-width: 600px) { .search-hit-meta .group-label { display: none; } }", css);
    Assert.Contains(".library-cover .cover-name { font-size: 48px; letter-spacing: -.045em; }", css);
    Assert.Contains("@media (max-width: 599px) { .group-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } }", css);
    Assert.Contains(".film-card.is-viewer { --film-poster-w: 244px; }", css);
    Assert.Contains("  folder: '<path", main);
  }

  [Fact]
  public void Uses_PlaylistScopedRoutes()
  {
    var shared = Shared();
    Assert.Contains("const base = `/playlists/${id}/library`;", shared);
    // library/open is the POST main.js sends with the CSRF token; library.js itself sends no state-changing call.
    Assert.Contains("await api(`${base}/open`, { method: 'POST', signal });", shared);
    Assert.Contains("search: (body, signal) => api(`${base}/search`, { method: 'POST', body: scoped(body), signal }),", shared);
    Assert.Contains("groupEntries: (params, signal) => api(`${base}/group-entries?${new URLSearchParams(scoped(params))}`, { signal }),", shared);
    Assert.Contains("filmCard: (params, signal) => api(`${base}/film-card?${new URLSearchParams(scoped(params))}`, { signal }),", shared);
    Assert.Contains("seriesCard: (params, signal) => api(`${base}/series-card?${new URLSearchParams(scoped(params))}`, { signal }),", shared);
    // The Library key from library/open replaces the editor's cacheKey; no editor route, no hidden groups from the client.
    Assert.Contains("const scoped = ({ cacheKey, ...rest }) => ({ ...rest, libraryKey: cacheKey });", shared);
    Assert.Contains("disabled: new Set(), totalChannels: open.totalChannels, cacheKey: open.libraryKey", shared);
    foreach (var editorRoute in new[] { "'/library/search'", "'/library/group-entries", "/playlist/analyze", "fetchAnalysis", "sourceUrl" })
      Assert.DoesNotContain(editorRoute, shared);
    Assert.DoesNotMatch(@"\b(PUT|PATCH|DELETE|POST)\b", Read("library.js"));
  }

  [Fact]
  public void AccessGone_ShowsMessageAndReturns()
  {
    var library = Read("library.js");
    var gone = Between(library, "function accessGone(ctx, err)", "const groupChip");
    Assert.Contains("if (!isViewer(ctx) || err?.status !== 404) return false;", gone);
    Assert.Contains("ctx.controller.abort();", gone);
    Assert.Contains("h('h2', { text: 'This list is no longer shared with you' })", gone);
    Assert.Contains("h('a', { class: 'btn btn-primary', href: '#/' }, 'Back to my lists')", gone);

    // Any 404 from library/*: the open, the search, a group's page and the film or series card.
    Assert.Contains("if (accessGone(ctx, err)) return;", Between(library, "async function load(", "const TYPES"));
    Assert.Contains("if (seq !== state.seq || ctx.controller.signal.aborted || accessGone(ctx, err)) return;", library);
    Assert.Contains("if (view !== state.open || ctx.controller.signal.aborted || accessGone(ctx, err)) return;", library);
    Assert.Contains("if (seq === card.seq && !accessGone(ctx, err)) renderFilm(ctx, { status: 'failed' });", library);

    // Loading and unavailable states: the viewer sees the server's neutral message and Try again.
    var load = Between(library, "async function load(", "const TYPES");
    Assert.Contains("isViewer(ctx) ? 'Loading the list' : 'Loading the playlist from your provider'", load);
    Assert.Contains("h('b', { text: 'The list cannot be loaded right now' }), ' ', err.message || ''", load);
    Assert.Contains("'Try again'", load);

    Assert.Contains(".library-gone { display: flex; flex-direction: column;", Read("library.css"));
  }
}
