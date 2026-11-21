using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the poster loader, the poster tiles and Home's rails (#427) in library.js, main.js and library.css at
/// their source, as the other Library UI tests do (api.Tests has no JS engine).</summary>
public class LibraryPostersUiTests
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

  private static string Loader() => Between(Read("library.js"), "function posterLoader(ctx) {", "function showPoster(");
  private static string Tile() => Between(Read("library.js"), "function showPoster(", "// Home (#425)");
  private static string Home() => Between(Read("library.js"), "function homeSection(ctx) {", "// My marks (#299)");

  [Fact]
  public void Loader_BatchesUpToTwentyFourAfter150ms_OneInFlight()
  {
    var loader = Loader();
    Assert.Contains("timer ||= setTimeout(flush, 150);", loader);
    Assert.Contains("if (busy || stopped() || !queue.length) return;", loader);
    Assert.Contains("const batch = queue.splice(0, 24);", loader);
    Assert.Contains("answer = await ctx.posters({ cacheKey: ctx.list?.cacheKey ?? null, items: batch.map((b) => b.item) }, signal);", loader);
    Assert.Contains("if (queue.length && !timer) timer = setTimeout(flush, 150);", loader);
    // Answers stay for the page session by mark key, ok and none alike: declared outside posterLoader, so a remount keeps
    // them, while the loader itself is one per mounted Library, only with TMDB on.
    Assert.Contains("posterAnswers.set(b.key, p);", loader);
    Assert.Contains("const p = posterAnswers.get(b.key);", loader);
    Assert.DoesNotContain("new Map()", loader);
    Assert.Contains("const posterAnswers = new Map();\n  let postersOff = false;\n  function posterLoader(ctx) {", Read("library.js"));
    Assert.Contains("if (p) return void (p.status === 'ok' && showPoster(ctx, b.tile, p.posterUrl));", loader);
    var library = Read("library.js");
    Assert.Contains("view.posters = ctx.tmdbEnabled === true ? posterLoader(ctx) : null;", Between(library, "function mount(ctx) {", "function show(sub, query)"));
    var main = Read("main.js");
    Assert.Contains("posters: (body, signal) => api(`/playlists/${playlist.id}/library/posters`, { method: 'POST', body, signal }),", main);
    Assert.Contains("posters: (body, signal) => api(`${base}/posters`, { method: 'POST', body: scoped(body), signal }),", Between(main, "function showSharedLibrary(id)", "function linkCard("));
  }

  [Fact]
  public void Loader_AsksOnlyForTilesNearTheViewport()
  {
    var loader = Loader();
    Assert.Contains("}, { rootMargin: '300px' });", loader);
    Assert.Contains("for (const e of seen.filter((x) => x.isIntersecting)) {", loader);
    Assert.Contains("io.unobserve(e.target);", loader);
    Assert.Contains("io.observe(tile);", Between(loader, "request: (tile, item) => {", "},"));
    Assert.DoesNotContain("ask(", Between(loader, "request: (tile, item) => {", "},"));
    // It stops with the route.
    Assert.Contains("signal.addEventListener('abort', () => { io.disconnect(); clearTimeout(timer); queue = []; });", loader);
  }

  [Fact]
  public void Loader_RetriesPendingThreeTimesEveryFourSeconds()
  {
    var loader = Loader();
    Assert.Contains("if (p.status !== 'pending') {", loader);
    Assert.Contains("} else if (b.tries < 3) setTimeout(() => ask({ ...b, tries: b.tries + 1 }), 4000);", loader);
    Assert.Contains("wanted.set(tile, { tile, item, key: tile.dataset.key, tries: 0 });", loader);
  }

  [Fact]
  public void Loader_A404SwitchesItOff_FailuresAreSilent()
  {
    var loader = Loader();
    var failure = Between(loader, "} catch (err) {", "busy = false;");
    Assert.Contains("if (err?.status === 404) postersOff = true;", failure);
    Assert.Contains("const stopped = () => postersOff || signal.aborted;", loader);
    // The flag is declared outside posterLoader, so it holds for the page session, not only for one mount.
    Assert.DoesNotContain("let postersOff", loader);
    Assert.Contains("let postersOff = false;\n  function posterLoader(ctx) {", Read("library.js"));
    Assert.DoesNotContain("toast", loader);
    Assert.DoesNotContain("setTimeout(flush", failure);
  }

  [Fact]
  public void Tile_StartsAsFallback_ImageHasNoReferrerAndFallsBackOnError()
  {
    var tile = Tile();
    Assert.Contains("const art = h('span', { class: 'lib-art', dataset: { type } },\n      h('span', { class: 'lib-dot', 'aria-hidden': 'true', dataset: { type } }),", tile);
    Assert.Contains("ctx.h('img', { referrerpolicy: 'no-referrer', loading: 'lazy', decoding: 'async', alt: '', width: '342', height: '513', src: url });", tile);
    Assert.Contains("img.addEventListener('load', () => img.classList.add('is-loaded'));", tile);
    Assert.Contains("img.addEventListener('error', () => img.remove());", tile);
    // A title that opens its card is a button labelled with title, year and group; any other a div.
    Assert.Contains("const label = [title, year, group].filter(Boolean).join(', ');", tile);
    Assert.Contains("h('button', { class: 'lib-tile', type: 'button', 'aria-haspopup': 'dialog', dataset: { key, label }, onclick: () => openFilm(ctx, film, tile) }, art, text)", tile);
    Assert.Contains(": h('div', { class: 'lib-tile', dataset: { key: key || '' } }, art, text);", tile);
    Assert.Contains("view.posters?.request(tile, { name: entry?.name || (year ? `${title} (${year})` : title), group: group || '', type });", tile);
  }

  [Fact]
  public void Tile_BadgesFollowTheMarkState()
  {
    var tile = Tile();
    Assert.Contains("h('span', { class: 'lib-art-seen', 'aria-hidden': 'true', hidden: true }, icon('check')),", tile);
    Assert.Contains("h('span', { class: 'lib-art-watch', 'aria-hidden': 'true', hidden: true }, icon('bookmark')));", tile);
    var paint = Between(tile, "function paintTile(tile) {", "\n  }");
    Assert.Contains("const m = marks.get(tile.dataset.key) || markOf({});", paint);
    Assert.Contains("tile.classList.toggle('is-seen', m.seen);", paint);
    Assert.Contains("tile.querySelector('.lib-art-seen').hidden = !m.seen;", paint);
    Assert.Contains("tile.querySelector('.lib-art-watch').hidden = !m.watchlist;", paint);
    Assert.Contains("`${tile.dataset.label}, seen`", paint);
    // Every mark change repaints the tiles of its key.
    Assert.Contains("for (const el of ctx.root.querySelectorAll('.lib-tile')) if (el.dataset.key === key) paintTile(el);",
      Between(Read("library.js"), "function paintKey(ctx, key) {", "\n  }"));
  }

  [Fact]
  public void Home_UsesRailsOnlyWhenTmdbIsOn()
  {
    var home = Home();
    Assert.Contains("const rails = ctx.tmdbEnabled === true;", home);
    // Watchlist and followed series: up to 12 tiles in a rail with TMDB on, the rows of #425 otherwise.
    Assert.Contains("kept.length && rails ? rail(s.p, 'Your watchlist', kept.slice(0, 12).map((m) => markTile(m, s.titles?.[m.key] || null, () => (m.year ? String(m.year) : ''))))\n          : kept.length ? rows(s, kept)", home);
    Assert.Contains("rails ? rail(s.p, 'Series you follow', kept.slice(0, 12).map((m) => markTile(m, s.titles[m.key], (t) => t.group,", home);
    Assert.Contains("(t) => (t.episodes ? `${plural(t.episodes, 'episode')} in list` : null)))) : rows(s, kept));", home);
    Assert.Contains("meta: t ? meta(t) : 'Not in this list'", home);
    // New in <group>: the largest "+N new" group, its first 12 titles in playlist order; the rows keep the others.
    Assert.Contains("const newIn = part('', `${base}/groups`, 'Open group');", home);
    Assert.Contains("if (rails) paintNewIn(ranked.shift());", home);
    Assert.Contains("group: g.key, sort: 'playlist', offset: 0, limit: 12 }", home);
    Assert.Contains("newTitle.textContent = newTitle.title = `New in ${g.title || 'Untitled group'}`;", home);
    Assert.Contains("newIn.n.textContent = `+${fmt(n)} since your last visit`;", home);
    Assert.Contains("newLink.href = `${base}/groups/${encodeURIComponent(g.key)}`;", home);
    Assert.Contains("if (!top) return void (shown.key = null);", home);
    // Rails carry the similar rail's Previous and Next.
    var rail = Between(home, "const rail = (p, label, tiles) => {", "return el;");
    Assert.Contains("h('div', { class: 'lib-rail', role: 'group', 'aria-label': label, tabindex: '0' }, tiles);", rail);
    Assert.Contains("p.head.append(h('span', { class: 'film-rail-nav' },", rail);
    Assert.Contains("'aria-label': 'Previous'", rail);
    Assert.Contains("'aria-label': 'Next'", rail);
  }

  [Fact]
  public void Css_TileAndRail()
  {
    var css = Read("library.css");
    foreach (var rule in new[]
    {
      ".lib-rail { display: flex; gap: 12px; margin: 0 -18px; padding: 2px 18px 6px; overflow-x: auto; scroll-snap-type: x proximity; scroll-padding-left: 18px; scrollbar-width: none; }",
      ".lib-rail::-webkit-scrollbar { display: none; }",
      ".lib-tile { flex: 0 0 112px; min-width: 0; display: flex; flex-direction: column; gap: 5px; padding: 0; border: 0; background: none; color: inherit; font: inherit; text-align: left; scroll-snap-align: start; }",
      ".lib-art { position: relative; aspect-ratio: 2 / 3; overflow: hidden; border-radius: 12px; display: grid; place-items: center; background: var(--movies-soft); box-shadow: 0 10px 20px -14px rgba(29, 26, 22, .6); }",
      ".lib-art[data-type=\"Series\"] { background: var(--series-soft); }",
      ".lib-art img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0; transition: opacity .2s; }",
      ".lib-art img.is-loaded { opacity: 1; }",
      ".lib-tile.is-seen .lib-art img.is-loaded { opacity: .45; }",
      ".lib-art-seen { position: absolute; top: 6px; right: 6px;",
      ".lib-art-watch { position: absolute; left: 6px; bottom: 6px;",
      ".lib-art-bar { position: absolute; left: 0; right: 0; bottom: 0; padding: 14px 8px 6px; background: linear-gradient(transparent, rgba(0, 0, 0, .7)); color: #fff; font: 700 11.5px var(--font-ui); }",
      ".lib-art-bar ~ .lib-art-watch { bottom: auto; top: 6px; }",
      ".lib-tile:focus-visible .lib-art { outline: 2px solid var(--accent); outline-offset: 2px; }",
      "@media (prefers-color-scheme: dark) { .lib-art { box-shadow: none; border: 1px solid var(--line); } }",
      "@media (prefers-reduced-motion: reduce) { .lib-art img { transition: none; } }",
      "@media (min-width: 768px) { .lib-tile { flex-basis: 148px; } }",
      ".lib-sec-head .lib-sec-one { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }",
    })
      Assert.Contains(rule, css);
    // No inline style: neither a style property nor a style attribute.
    var library = Read("library.js");
    Assert.DoesNotContain("style:", library);
    Assert.DoesNotContain("'style'", library);
    Assert.DoesNotContain("style=", library);
  }
}
