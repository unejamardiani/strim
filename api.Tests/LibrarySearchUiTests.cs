using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Search tab in sections (#421): the titles route for owner and viewer, three sections with counts under
/// All, a single type paged by 30, one row per series, and the query in the address.
/// </summary>
public class LibrarySearchUiTests
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

  private static string Library() => Read("library.js");
  private static string Run() => Between(Library(), "async function runSearch(", "// Marks (#298)");

  [Fact]
  public void Search_UsesTheTitlesRouteForBothRoles()
  {
    var js = Read("main.js");
    var show = Between(js, "function showLibrary(playlist)", "function showSharedLibrary(");
    Assert.Contains("titles: (params, signal) => api(`/playlists/${playlist.id}/library/titles?${new URLSearchParams(params)}`, { signal }),", show);
    var shared = Between(js, "function showSharedLibrary(", "function linkCard(");
    Assert.Contains("const scoped = ({ cacheKey, ...rest }) => ({ ...rest, libraryKey: cacheKey });", shared);
    Assert.Contains("titles: (params, signal) => api(`${base}/titles?${new URLSearchParams(scoped(params))}`, { signal }),", shared);

    var run = Run();
    Assert.DoesNotContain("ctx.search(", Library());
    Assert.Contains("found = await ctx.titles(params, ctx.controller.signal);", run);
    Assert.Contains("const params = { cacheKey: list.cacheKey, includeDisabled: includeOff, q: state.query };", run);
  }

  [Fact]
  public void All_AsksPerTypeFour_AndBuildsThreeSections()
  {
    var library = Library();
    Assert.Contains("else params.perType = 4;", Run());
    var order = new[] { "name: 'Films'", "name: 'Series'", "name: 'Channels'" }.Select(n => library.IndexOf(n, StringComparison.Ordinal)).ToArray();
    Assert.All(order, i => Assert.True(i > 0));
    Assert.Equal(order.OrderBy(i => i), order);
    var sections = Between(library, "function sections(", "\n  }\n");
    Assert.Contains("if (n <= 0) return null;", sections);
    Assert.Contains(".slice(0, 4).map(rowOf)", sections);
    Assert.Contains("h('div', { class: 'lib-sections' }, h('div', null, films), h('div', null, series, channels))", sections);
    Assert.Contains("ctx.plural(n, 'series', 'series')", library);
    // The chips carry the counts, All their sum; without a query none.
    Assert.Contains("paintCounts(ctx, state, null);", Run());
    Assert.Contains("b.dataset.value === 'all' ? Object.values(COUNT_OF)", library);
  }

  [Fact]
  public void ShowAll_SwitchesTheTypeChip()
  {
    var library = Library();
    Assert.Contains("`Show all ${ctx.fmt(n)} films`", library);
    Assert.Contains("`Show all ${ctx.fmt(n)} series`", library);
    Assert.Contains("`Show all ${ctx.fmt(n)} channels`", library);
    Assert.Contains("n > 4 ? h('button', { class: 'lib-more', type: 'button', onclick: () => state.pick(s.value) }", library);
    Assert.Contains("state.pick = (value) => { pick(TYPES.find((t) => t.value === value)); run(); };", library);
  }

  [Fact]
  public void SingleType_PagesByThirtyWithASentinel()
  {
    var run = Run();
    Assert.Contains("Object.assign(params, { types: type.types.join(','), offset: 0, limit: 30 });", run);
    var pages = Between(Library(), "function pages(", "// Marks (#298)");
    Assert.Contains("new IntersectionObserver(", pages);
    Assert.Contains("{ rootMargin: '600px' }", pages);
    Assert.Contains("if (page.hasMore !== true || !page.titles?.length) return;", pages);
    Assert.Contains("offset: ul.children.length, limit: 30", pages);
    Assert.Contains("'Try again'", pages);
  }

  [Fact]
  public void SeriesRow_ShowsSeasonsAndEpisodes()
  {
    var line = Between(Library(), "function seasonsLine(", "\n  }\n");
    Assert.Contains("const episodes = ctx.plural(t.episodes || t.entries || 0, 'episode');", line);
    Assert.Contains("if (t.seasonFirst == null) return episodes;", line);
    Assert.Contains("last === t.seasonFirst ? `S${t.seasonFirst}` : `S${t.seasonFirst}–S${last}`} · ${episodes}", line);
    var meta = Between(Library(), "function titleMeta(", "\n  }\n");
    Assert.Contains("if (t.contentType === 'Series') parts.push(h('b', { text: seasonsLine(ctx, t) }));", meta);
    // A series row opens its card without the "Opened from" line of one episode.
    Assert.Contains("card.stack.length === 0 && !entry.collapsed ? answer.context : null", Library());
  }

  [Fact]
  public void FilmRow_ShowsVersions()
  {
    var meta = Between(Library(), "function titleMeta(", "\n  }\n");
    Assert.Contains("const versions = t.contentType === 'Movies' && t.entries > 1;", meta);
    Assert.Contains("t.groups > 1 ? `${plural(t.entries, 'version')} in ${plural(t.groups, 'group')}` : plural(t.entries, 'version')", meta);
    Assert.Contains("if (!isViewer(ctx) && !(versions && t.groups > 1)) parts.push(t.group);", meta);
    Assert.Contains("const where = [isViewer(ctx) ? groupLabel(ctx, m.group) : null, ...titleMeta(ctx, m)];", Library());
    Assert.Contains("const year = collapsed ? [null, e.title, e.year ? String(e.year) : null]", Library());
  }

  [Fact]
  public void Query_IsKeptInTheAddressWithReplaceState()
  {
    var library = Library();
    var address = Between(library, "function keepAddress(state)", "\n  }\n");
    Assert.Contains("if (state.query) p.set('q', state.query);", address);
    Assert.Contains("if (state.type !== 'all') p.set('type', state.type);", address);
    Assert.Contains("history.replaceState(history.state, '', hash);", address);
    Assert.DoesNotContain("pushState", address);
    Assert.Contains("keepAddress(state);", Run());
    // Opening the route with q fills the field and searches; main.js hands the query in.
    Assert.Contains("input.value = (p.get('q') || '').trim();", library);
    Assert.Contains("view.search?.(query);", library);
    Assert.Contains("query: target.query,", Read("main.js"));
  }

  [Fact]
  public void Status_NamesLeftOutTitles()
  {
    var run = Run();
    Assert.Contains("`${fmt(hidden)} more in switched-off groups`", run);
    Assert.Contains("`${plural(seen, 'seen title')} hidden`", run);
    Assert.Contains("if (total) text = [plural(total, 'title'), left].filter(Boolean).join(' · ');", run);
    Assert.Contains("else if (left) text = `Nothing in ${listWord(ctx)}. ${left}.`;", run);
    Assert.Contains("onclick: () => { includeOff = true; ctx.onIncludeOff(); } }, 'Show them')", run);
    Assert.Contains("const hidden = !isViewer(ctx) && found.hiddenCount > 0 ? found.hiddenCount : 0;", run);
    Assert.Contains("role: 'status'", run);
    // The line wraps instead of clipping the button (app.css gives .search-head-status nowrap and an ellipsis).
    Assert.Contains(".library-search .search-head-status, .lib-status { white-space: normal; overflow: visible; text-overflow: clip; }", Read("library.css"));
  }

  [Fact]
  public void LanguageFilter_StillSendsLang()
  {
    var run = Run();
    Assert.Contains("const lang = state.host.searchLang.join(',');", run);
    Assert.Contains("if (lang) params.lang = lang;", run);
    Assert.Contains("state.host.searchLanguages = found.languages.filter(", run);
    Assert.Contains("else if (lang) text = 'Nothing matches your search in the chosen languages.';", run);
  }
}
