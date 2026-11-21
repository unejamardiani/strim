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
    Assert.Contains("if (!opens(ctx, e)) return ctx.h('span', { class: 'lib-title' }, title);", card);
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
  public void Card_IsAlwaysModal()
  {
    // #430: the side pane from 1100px is gone; the card opens with showModal() at every width.
    var library = Read("library.js");
    var card = Card();
    Assert.DoesNotContain("card.el.show()", library);
    Assert.DoesNotContain(".show();", card);
    Assert.DoesNotContain("matchMedia('(min-width: 1100px)')", library);
    Assert.Contains("card.el.showModal();", card);
    // No reopen on a breakpoint change and no Escape handler for a non-modal card.
    Assert.DoesNotContain("const reopen = ", card);
    Assert.DoesNotContain(".addEventListener('change', () => { if (el.open)", card);
    Assert.DoesNotContain("!el.matches(':modal')", card);
  }

  [Fact]
  public void Card_Wide_IsCentredAt560()
  {
    var css = Read("library.css");
    Assert.Contains("@media (min-width: 1100px) {\n  .film-card { --film-poster-w: 208px; width: 560px; max-width: none; height: auto; max-height: min(88dvh, 860px); margin: auto; }\n  .film-card.is-viewer { width: 588px; }\n}", css);
    // The pane's fixed placement is gone; the viewer keeps its larger poster.
    Assert.DoesNotContain("right: max(18px, calc((100vw - 1440px) / 2 + 18px))", css);
    Assert.Contains(".film-card.is-viewer { --film-poster-w: 244px; }", css);
    Assert.Contains("from 1100px a centred 560px dialog", Read("DESIGN.md"));
    Assert.DoesNotContain("side pane", Read("DESIGN.md"));
  }

  [Fact]
  public void Page_ReservesNoRoomForThePane()
  {
    var css = Read("library.css");
    Assert.DoesNotContain("--film-pane-w", css);
    var rules = System.Text.RegularExpressions.Regex.Matches(css, @"(?<sel>[^{}]*)\{(?<body>[^{}]*)\}");
    Assert.NotEmpty(rules);
    foreach (System.Text.RegularExpressions.Match rule in rules)
    {
      if (!rule.Groups["sel"].Value.Contains(".film-card[open]", StringComparison.Ordinal)) continue;
      Assert.DoesNotContain("padding-right", rule.Groups["body"].Value);
    }
    Assert.DoesNotContain("@media (min-width: 1100px) and (max-width: 1439px)", css);
  }

  [Fact]
  public void Page_DoesNotScrollBehindTheCard()
  {
    // Review of #430: wheel and touch over the backdrop scrolled the Library; the page now keeps its position and width.
    var css = Read("library.css");
    Assert.Contains("html:has(.film-card[open]) { overflow: hidden; scrollbar-gutter: stable; }", css);
    // Closing restores the focus without scrolling the page.
    Assert.Contains("?.focus({ preventScroll: true });", Card());
  }

  [Fact]
  public void Closing_RefocusesTheTileByKey_InItsOwnRail()
  {
    // Review of #430: a thumbs-up from a Home watchlist card redraws the rail; the focus goes to the new tile with the
    // same key in the same rail, else to that rail, never to Home's search field (which opens the Search tab).
    var card = Card();
    Assert.Contains("card.tile = button.classList.contains('lib-tile') ? { key: button.dataset.key, rail: railOf(button) } : null;", card);
    Assert.Contains("const again = card.tile ? sameTile(ctx, card.tile)", card);
    Assert.Contains("const tiles = [...ctx.root.querySelectorAll('button.lib-tile')].filter((t) => t.dataset.key === key);", card);
    Assert.Contains("return tiles.find((t) => railOf(t) === rail) || tiles[0]", card);
    Assert.Contains("|| [...ctx.root.querySelectorAll('.lib-rail')].find((r) => r.getAttribute('aria-label') === rail);", card);
  }

  [Fact]
  public void Design_DescribesTheModalCard_InTheSeriesAndMarksSections()
  {
    var design = Read("DESIGN.md");
    Assert.DoesNotContain("another episode row swaps it in place", design);
    Assert.DoesNotContain("Below 1100px a change made in the", design);
    Assert.Contains("At every width a change made in the\nmodal card offers its toast when the card closes.", design);
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
  public void TheCard_IsClosedByBackdropOrBack_WithoutMovingThePage()
  {
    var card = Card();
    Assert.Contains("history.pushState({ filmCard: true }, '');", card);
    Assert.Contains("window.addEventListener('popstate', () => { if (el.open) el.close(); }, { signal });", card);
    Assert.Contains("el.addEventListener('click', (e) => { if (e.target === el) el.close(); });", card);
    Assert.Contains("?.focus({ preventScroll: true });", card);
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
    Assert.Contains("const markEntry = { name: entry.name, contentType: entry.type === 'Series' ? 'Series' : 'Movies', markKey: entry.markKey, ...given, keepMark: pending, asked: answer?.asked };", card);
    Assert.Contains("const el = markControls(cardCtx, markEntry);", card);
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
    Assert.Contains("const given = pending || !answer?.marks || owner === key ? now\n      : (owner && marks.get(owner)) || { seen: answer.marks.seen === true, rating: answer.marks.rating || 0, watchlist: answer.marks.watchlist === true };", card);
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
