using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Library's film card (#367) in library.js, main.js, library.css and DESIGN.md at their source, as the other
/// Library UI tests do (api.Tests has no JS engine); the film-card endpoint is covered by FilmCardEndpointTests.
/// </summary>
public class LibraryFilmCardUiTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string Card() => Read("library.js")[Read("library.js").IndexOf("// Film card (#367)", StringComparison.Ordinal)..];

  [Fact]
  public void OnlyFilmRows_OpenACard_WithOneRequest_AndOnlyWhenTmdbIsOn()
  {
    var card = Card();
    Assert.Contains("const opensCard = (ctx, e) => ctx.tmdbEnabled === true && e.contentType === 'Movies' && !!e.markKey;", card);
    Assert.Contains("if (!opens(ctx, e)) return ctx.h('span', { class: 'search-hit-name', text: e.name });", card);
    Assert.Contains("const params = { name: film.name, group: film.group, type: 'Movies', cacheKey: film.cacheKey, includeDisabled: includeOff, hideSeen };", card);
    Assert.Contains("answer = await ctx.filmCard(params, own.signal);", card);
    Assert.Contains("h('span', { class: 'sr-only', role: 'status', text: `Loading ${word(entry)} details` })", card);
    var main = Read("main.js");
    Assert.Contains("state.tmdbEnabled = config.status === 'fulfilled' && config.value?.tmdbEnabled === true;", main);
    Assert.Contains("tmdbEnabled: state.tmdbEnabled,", main);
    Assert.Contains("filmCard: (params, signal) => api(`/playlists/${playlist.id}/library/film-card?${new URLSearchParams(params)}`, { signal }),", main);
  }

  [Fact]
  public void AMatchedCard_HasTheBlurredPosterBackdrop_FromTheSameUrl()
  {
    var card = Card();
    Assert.Contains("top = h('div', { class: 'film-stage' }, filmBackdrop(ctx, f?.posterUrl), poster(", card);
    Assert.Contains("el.style.setProperty('--film-art', `url(${JSON.stringify(url)})`);", card);
    Assert.Contains("probe.addEventListener('error', () => el.remove(), { once: true });", card);
    var css = Read("library.css");
    Assert.Contains(".film-backdrop { position: absolute; z-index: -2; inset: -70px -70px 90px; background: var(--film-art) center / cover; filter: blur(44px) saturate(1.5);", css);
    Assert.Contains(".film-stage:has(> .film-backdrop)::before {", css);
  }

  [Fact]
  public void PaneWidth_IsDeclaredWhereThePageCanReadIt()
  {
    // #413: on .film-card the property never reached .page (custom properties do not inherit upwards), so the
    // reserved padding was 0px and the pane covered the rows' marks.
    var css = Read("library.css");
    Assert.Contains("body { --film-pane-w: 420px; }", css);
    Assert.Contains("body:has(.film-card.is-viewer) { --film-pane-w: 448px; }", css);
    Assert.Contains("width: var(--film-pane-w);", css);
    var rules = System.Text.RegularExpressions.Regex.Matches(css, @"(?<sel>[^{}]*\.film-card[^{}]*)\{(?<body>[^{}]*)\}");
    Assert.NotEmpty(rules);
    foreach (System.Text.RegularExpressions.Match rule in rules)
    {
      if (rule.Groups["sel"].Value.TrimStart().StartsWith("body", StringComparison.Ordinal)) continue;
      Assert.DoesNotContain("--film-pane-w:", rule.Groups["body"].Value);
    }
  }

  [Fact]
  public void OpenPane_KeepsTheListStretchedBelow1440()
  {
    var css = Read("library.css");
    var start = css.IndexOf("@media (min-width: 1100px) and (max-width: 1439px) {", StringComparison.Ordinal);
    Assert.True(start >= 0);
    var block = css[start..css.IndexOf("\n}", start, StringComparison.Ordinal)];
    Assert.Contains(".page:has(> .film-card[open]) .library-browse { display: flex; align-items: stretch; }", block);
  }

  [Fact]
  public void TheScrollArea_TakesItsContentHeight_SoSafariDoesNotCollapseTheSheet()
  {
    // 19 Nov 2026: with `flex: 1` (a 0% basis) the phone sheet had no height in iOS Safari; only the backdrop showed.
    var css = Read("library.css");
    Assert.Contains(".film-scroll { display: flex; flex-direction: column; flex: 1 1 auto; min-height: 0;", css);
    Assert.DoesNotContain(".film-scroll { display: flex; flex-direction: column; flex: 1;", css);
  }

  [Fact]
  public void TheCard_IsASheetOrASidePane_ClosedByBackWithoutMovingThePage()
  {
    var card = Card();
    Assert.Contains("const showCard = () => (wide.matches ? card.el.show() : card.el.showModal());", card);
    Assert.Contains("history.pushState({ filmCard: true }, '');", card);
    Assert.Contains("window.addEventListener('popstate', () => { if (el.open) el.close(); }, { signal });", card);
    Assert.Contains("?.focus({ preventScroll: true });", card);
    Assert.Contains("body:has(.film-card[open]) .site-links { padding-right: calc(18px + var(--film-pane-w) + 24px); }", Read("library.css"));
  }

  [Fact]
  public void Details_States_Marks_AndAttribution_FollowTheAnswer()
  {
    var card = Card();
    Assert.Contains("referrerpolicy: 'no-referrer', src: url", card);
    Assert.Contains("`Poster of ${title.textContent}`", card);
    Assert.Contains("`TMDB rating ${rating} out of 10`", card);
    Assert.Contains("c.year ? String(c.year) : 'Year unknown'", card);
    Assert.Contains("notFound: 'TMDB has no film with this title. Your marks still work.',", card);
    Assert.Contains("failed: 'The film details could not be loaded.',", card);
    // The #298 controls and their shared state, so a change in the card repaints the rows.
    Assert.Contains("const el = markControls({ ...ctx, toast }, { name: entry.name, contentType: entry.type === 'Series' ? 'Series' : 'Movies', markKey: entry.markKey, ...given });", card);
    Assert.Contains("if (answer?.tmdbData) body.append(filmCredit(ctx, entry));", card);
    // The approved layout: a glass Close, a kicker, the icon-only marks capsule and a grid of candidate tiles.
    Assert.Contains("h('button', { class: 'icon-btn film-glass', type: 'button', 'aria-label': 'Close'", card);
    Assert.Contains("f.year ? `Film · ${f.year}` : 'Film'", card);
    Assert.DoesNotContain("b.append(label)", card);
    Assert.Contains(".film-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px;", Read("library.css"));
    Assert.Contains("the mark controls and, for films and series, the name, which opens the film or series card.", Read("DESIGN.md"));
  }

  [Fact]
  public void SimilarFilms_ListTheListsFilmsFirst_AndOnlyThoseOpenACardInPlace()
  {
    var card = Card();
    Assert.Contains("if (f?.similar?.length) body.append(similarFilms(ctx, f));", card);
    Assert.Contains("`${f.similar.filter((s) => s.inList).length} in ${listWord(ctx)}`", card);
    Assert.Contains("h('div', { class: 'film-tile is-absent' }, art, title,", card);
    Assert.Contains("[s.year, `Not in ${listWord(ctx)}`].filter(Boolean).join(' · ')", card);
    Assert.Contains("[s.title, s.year, `in ${s.group}`, off ? 'group off' : '', s.seen ? 'seen' : '']", card);
    Assert.Contains("onclick: () => openSimilar(ctx, s, i)", card);
    Assert.Contains("card.film = { name: s.entry, group: s.group, cacheKey: card.film.cacheKey, markKey: `similar:${s.entry}`", card);
    Assert.Contains("'aria-label': `Back to ${prev.title}`, onclick: () => filmBack(ctx)", card);
    // Back from a loading or failed inner card copies the tile's own Seen back, not a placeholder.
    var seed = card.IndexOf("if (!marks.has(card.film.markKey)) marks.set(card.film.markKey, { seen: s.seen === true, rating: 0 });", StringComparison.Ordinal);
    Assert.True(seed >= 0 && seed < card.IndexOf("function filmBack(ctx)", StringComparison.Ordinal));
    Assert.Contains("prev.similar.seen = marks.get(card.film.markKey)?.seen ?? prev.similar.seen;", card);
    // Back renders the stored card without a request and puts the focus on the tapped tile.
    Assert.Contains("renderFilm(ctx, prev.answer);", card);
    Assert.Contains("rail?.querySelector(`[data-i=\"${prev.tile}\"]`)?.focus({ preventScroll: true });", card);
    // A mark in a card opened from a similar film reaches the rows through the key the API returns.
    Assert.Contains("const real = saved?.key || keyAlias.get(key);", Read("library.js"));
    // A kept answer reopened from a similar tile keeps the marks its row used, so a tap there cannot clear Seen.
    Assert.Contains("const answerMarkKey = new WeakMap();", card);
    Assert.Contains("const given = !answer?.marks || owner === key ? now\n      : (owner && marks.get(owner)) || { seen: answer.marks.seen === true, rating: answer.marks.rating || 0 };", card);
    Assert.Contains("if (answer?.marks && !owner) answerMarkKey.set(answer, key);", card);
    Assert.DoesNotContain("if (answer) answer.marks = null;", card);
    var css = Read("library.css");
    Assert.Contains(".film-rail .film-tile { flex: 0 0 112px; scroll-snap-align: start; }", css);
    Assert.Contains(".film-tile.is-absent .film-tile-poster { filter: grayscale(.85) contrast(.85); opacity: .5; box-shadow: none; }", css);
    Assert.Contains("Similar films and the match (#368):", Read("DESIGN.md"));
  }

  [Fact]
  public void TheMatch_IsChosenOrCleared_WithTheCsrfPut_AndAFailureRestoresTheCard()
  {
    var card = Card();
    Assert.Contains("h('button', { class: 'film-tile', type: 'button', onclick: () => setMatch(ctx, c.id) }", card);
    Assert.Contains("h('button', { class: 'film-unmatch', type: 'button', onclick: () => setMatch(ctx, null) }, `Not this ${word(entry)}`)", card);
    Assert.Contains("else await ctx.setFilmMatch({ name: film.name, tmdbId });", card);
    Assert.Contains("renderFilm(ctx, before);", card);
    Assert.Contains("cardToast(ctx)(`The ${word(film)} was not changed. ${err.message || ''}`.trim(), { error: true });", card);
    Assert.Contains("`Strim finds only this ${word(film)} for the title.`", card);
    Assert.Contains("setFilmMatch: (body) => api('/marks/film-match', { method: 'PUT', body }),", Read("main.js"));
  }
}
