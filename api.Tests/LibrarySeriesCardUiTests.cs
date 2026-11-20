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
    Assert.Contains("if (!opens(ctx, e)) return ctx.h('span', { class: 'lib-title' }, title);", card);
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
  public void Seasons_ArePills_OneOpenAtATime()
  {
    var card = Card();
    Assert.Contains("class: s.count > 0 ? 'series-season' : 'series-season is-absent', type: 'button', 'aria-expanded': 'false',", card);
    Assert.Contains("const seasonLabel = (n) => (n === 0 ? 'Specials' : `S${n}`);", card);
    Assert.Contains("s.total != null ? `${s.count}/${s.total}` : String(s.count)", card);
    // Opening a pill closes every other; the open one closes again.
    Assert.Contains("for (const p of pills) p.setAttribute('aria-expanded', String(open && p === pill));", card);
    Assert.Contains("panel.replaceChildren(...(open ? seasonEpisodes(ctx, s, here?.season === s.season ? here.episode : null) : []));", card);
    // The episodes are list items, not buttons, cut at 200 with "and N more", and a legend below.
    Assert.Contains("const shown = all.slice(0, 200);", card);
    Assert.Contains("const items = shown.map((n) => h('li', {", card);
    Assert.Contains("h('li', { class: 'series-more', text: `and ${more} more` })", card);
    Assert.Contains("h('p', { class: 'series-legend' }", card);
    Assert.Contains("`${list.unnumbered} ${list.unnumbered === 1 ? 'entry' : 'entries'} without an episode number`", card);
    Assert.Contains("f.episodeCount > 0 ? `${list.episodes} of ${f.episodeCount} episodes`", card);
    var css = Read("library.css");
    Assert.Contains(".series-season { display: inline-flex; flex: none; align-items: center; gap: 6px; height: 38px; padding: 0 14px; border: 0; border-radius: 999px; background: var(--surface-2);", css);
    Assert.Contains(".series-episodes { display: grid; grid-template-columns: repeat(auto-fill, minmax(42px, 1fr)); gap: 6px;", css);
    Assert.Contains(".series-episode { display: grid; place-items: center; height: 36px; border-radius: 10px; background: var(--surface-2); font-size: 13.5px; font-weight: 650; font-variant-numeric: tabular-nums; }", css);
    Assert.Contains(".series-season.is-absent, .series-episode.is-absent, .series-key.is-absent { background: none; box-shadow: inset 0 0 0 1.5px var(--line-strong); color: var(--ink-3); }", css);
    Assert.Contains("In your list (#391):", Read("DESIGN.md"));
  }

  [Fact]
  public void ContextLine_OnlyFromAnEpisodeRow()
  {
    var card = Card();
    // Only the row the card was opened from carries a context; a similar series opened in place has none.
    Assert.Contains("seriesInList(ctx, f, card.stack.length === 0 && !entry.collapsed ? answer.context : null)", card);
    Assert.Contains("const here = context?.season != null ? context : null;", card);
    Assert.Contains("const line = here ? h('p', { class: 'series-context' }, 'Opened from ',", card);
    Assert.Contains("here?.season === s.season ? h('span', { class: 'series-here', 'aria-hidden': 'true' }) : null,", card);
    Assert.Contains("n === episode ? 'is-here' : ''", card);
    // The line shows only while no season is open.
    Assert.Contains("if (line) line.hidden = open;", card);
    var css = Read("library.css");
    Assert.Contains(".series-episode.is-here { background: var(--series-soft); box-shadow: inset 0 0 0 2px var(--series); }", css);
    Assert.Contains(".series-here { width: 7px; height: 7px;", css);
    Assert.Contains(".series-context, .series-note { margin: 0; font-size: 13px; color: var(--ink-2); }", css);
  }

  [Fact]
  public void Section_HiddenWithoutInList()
  {
    var card = Card();
    Assert.Contains("const hasInList = (f) => !!f?.inList && (f.inList.episodes > 0 || f.inList.unnumbered > 0);", card);
    Assert.Contains("if (series && hasInList(f)) body.append(seriesInList(", card);
    // Between the overview and the similar series.
    var overview = card.IndexOf("if (f?.overview) body.append(", StringComparison.Ordinal);
    var section = card.IndexOf("if (series && hasInList(f))", StringComparison.Ordinal);
    var similar = card.IndexOf("if (f?.similar?.length) body.append(similarFilms(ctx, f));", StringComparison.Ordinal);
    Assert.True(overview < section && section < similar);
  }

  [Fact]
  public void SimilarSeries_InListOpenInPlace()
  {
    var card = Card();
    Assert.Contains("const label = card.film.type === 'Series' ? 'Similar series' : 'Similar films';", card);
    Assert.Contains("poster(ctx, s.posterUrl, '', 'film-tile-poster', card.film.type)", card);
    // Only in-list tiles are buttons; they open the card in place as a series, Back returns to the previous one.
    Assert.Contains("return h('div', { class: 'film-tile is-absent' }, art, title,", card);
    Assert.Contains("onclick: () => openSimilar(ctx, s, i) }", card);
    Assert.Contains("markKey: `similar:${s.entry}`, off: s.enabled === false, type: card.film.type };", card);
    Assert.Contains("'aria-label': `Back to ${prev.title}`, onclick: () => filmBack(ctx)", card);
    // The series opened in place has no "Opened from" context.
    Assert.Contains("seriesInList(ctx, f, card.stack.length === 0 && !entry.collapsed ? answer.context : null)", card);
    Assert.Contains("Similar series and the match (#392):", Read("DESIGN.md"));
  }

  [Fact]
  public void Choose_StoresSeriesMatch()
  {
    var card = Card();
    Assert.Contains("text: choose ? `Which ${word(entry)} is it?` : f?.title || entry.name", card);
    Assert.Contains("onclick: () => setMatch(ctx, c.id) },", card);
    Assert.Contains("poster(ctx, c.posterUrl, '', 'film-tile-poster', entry.type)", card);
    Assert.Contains("if (film.type === 'Series') await ctx.setSeriesMatch({ name: film.name, tmdbId });", card);
    Assert.Contains("onclick: () => setMatch(ctx, null) }, `Not this ${word(entry)}`)", card);
    // Every episode row shares the series match, so no kept series answer survives a change.
    Assert.Contains("const stale = (k) => (film.type === 'Series' ? k[4] === 'Series' : k[0] === film.name);", card);
    Assert.Contains("for (const k of [...answers.keys()]) if (stale(JSON.parse(k))) answers.delete(k);", card);
    Assert.Contains("const key = JSON.stringify([film.name, film.group, includeOff, hideSeen, film.type]);", card);
    Assert.Contains("`Strim finds only this ${word(film)} for the title.`", card);
  }

  [Fact]
  public void FailedMatch_RestoresCard()
  {
    var card = Card();
    var match = card[card.IndexOf("async function setMatch(ctx, tmdbId)", StringComparison.Ordinal)..];
    match = match[..match.IndexOf("await loadFilm(ctx);", StringComparison.Ordinal)];
    Assert.Contains("const before = card.answer;", match);
    Assert.Contains("renderFilm(ctx, before);", match);
    Assert.Contains("cardToast(ctx)(`The ${word(film)} was not changed. ${err.message || ''}`.trim(), { error: true });", match);
    // The kept answers are dropped only after the match was stored.
    Assert.True(match.IndexOf("renderFilm(ctx, before);", StringComparison.Ordinal) < match.IndexOf("answers.delete", StringComparison.Ordinal));
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
