using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the series card (#390) in library.js, main.js, library.css and DESIGN.md at their source, as the film card's
/// UI tests do (api.Tests has no JS engine); the series-card endpoint is covered by LibrarySeriesCardTests.
/// </summary>
public class LibrarySeriesCardUiTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string Card() => Read("library.js")[Read("library.js").IndexOf("// Film card (#367)", StringComparison.Ordinal)..];

  [Fact]
  public void SeriesRows_OpenACard_OnlyWhenTmdbIsOn()
  {
    var card = Card();
    Assert.Contains("const opensSeriesCard = (ctx, e) => ctx.tmdbEnabled === true && e.contentType === 'Series' && !!e.markKey;", card);
    Assert.Contains("if (!opens(ctx, e)) return ctx.h('span', { class: 'search-hit-name', text: e.name });", card);
    Assert.Contains("type: opensSeriesCard(ctx, e) ? 'Series' : 'Movies' };", card);
    Assert.Contains("const filmChevron = (ctx, e) => (opens(ctx, e) ?", card);
    Assert.Contains("if (film.type === 'Series') answer = await ctx.seriesCard({ ...params, type: 'Series' }, own.signal);", card);
    // A similar series opens in place as a series too.
    Assert.Contains("markKey: `similar:${s.entry}`, off: s.enabled === false, type: card.film.type };", card);
    Assert.Contains("seriesCard: (params, signal) => api(`/playlists/${playlist.id}/library/series-card?${new URLSearchParams(params)}`, { signal }),", Read("main.js"));
  }

  [Fact]
  public void Kicker_UsesSeriesTypeAndYears()
  {
    var card = Card();
    Assert.Contains("h('p', { class: 'film-kicker', dataset: { type: 'Series' } }, h('span', { class: 'dot', 'aria-hidden': 'true' }), seriesYears(f))", card);
    Assert.Contains(": s.state === 'returning' && !s.lastYear ? `Series · since ${s.firstYear}`", card);
    Assert.Contains("s.lastYear && s.lastYear !== s.firstYear ? `Series · ${s.firstYear}–${s.lastYear}` : `Series · ${s.firstYear}`);", card);
    // The facts line joins state, seasons, episodes and network and leaves out what is missing.
    Assert.Contains("s.episodeCount ? `${s.episodeCount} ${s.episodeCount === 1 ? 'episode' : 'episodes'}` : '', s.network].filter(Boolean).join(' · ');", card);
    Assert.Contains("facts ? h('p', { class: 'series-facts', text: facts }) : null", card);
    var css = Read("library.css");
    Assert.Contains(".series-facts { margin: 4px 0 0; font-size: 13px; font-weight: 550; color: var(--ink-3); }", css);
    Assert.Contains(".film-poster-empty.is-series { background: linear-gradient(160deg, var(--series-soft), var(--surface-3)); }", css);
    Assert.Contains("Series card (#390):", Read("DESIGN.md"));
  }

  [Fact]
  public void Marks_UseSeriesKind()
  {
    var card = Card();
    Assert.Contains("contentType: entry.type === 'Series' ? 'Series' : 'Movies', markKey: entry.markKey", card);
    Assert.Contains("if (film.type === 'Series') await ctx.setSeriesMatch({ name: film.name, tmdbId });", card);
    Assert.Contains("entry.type === 'Series' ? 'Series data and posters: ' : 'Film data and posters: '", card);
    Assert.Contains("const label = card.film.type === 'Series' ? 'Similar series' : 'Similar films';", card);
    Assert.Contains("setSeriesMatch: (body) => api('/marks/series-match', { method: 'PUT', body }),", Read("main.js"));
    // The shared mark controls map the Series type to the series kind.
    Assert.Contains("Series: 'series'", Read("library.js"));
  }

  [Fact]
  public void FilmCard_Unchanged()
  {
    var card = Card();
    Assert.Contains("const opensCard = (ctx, e) => ctx.tmdbEnabled === true && e.contentType === 'Movies' && !!e.markKey;", card);
    Assert.Contains("else answer = await ctx.filmCard(params, own.signal);", card);
    Assert.Contains("const params = { name: film.name, group: film.group, type: 'Movies', cacheKey: film.cacheKey, includeDisabled: includeOff, hideSeen };", card);
    Assert.Contains(": f ? h('p', { class: 'film-kicker', dataset: { type: 'Movies' } }, h('span', { class: 'dot', 'aria-hidden': 'true' }), f.year ? `Film · ${f.year}` : 'Film') : null,", card);
    Assert.Contains("const word = (film) => (film.type === 'Series' ? 'series' : 'film');", card);
    Assert.Contains("? Object.fromEntries(Object.entries(FILM_NOTES).map(([k, v]) => [k, v.replace('film', 'series')])) : FILM_NOTES);", card);
    Assert.Contains(".film-poster-empty::after { content: \"\"; width: 22%; aspect-ratio: 1; border-radius: 2px; background: var(--movies); transform: rotate(45deg); }", Read("library.css"));
  }
}
