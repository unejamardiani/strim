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
    Assert.Contains("if (!opensCard(ctx, e)) return ctx.h('span', { class: 'search-hit-name', text: e.name });", card);
    Assert.Contains("const params = { name: film.name, group: film.group, type: 'Movies', cacheKey: film.cacheKey, includeDisabled: includeOff, hideSeen };", card);
    Assert.Contains("answer = await ctx.filmCard(params, own.signal);", card);
    Assert.Contains("h('span', { class: 'sr-only', role: 'status', text: 'Loading film details' })", card);
    var main = Read("main.js");
    Assert.Contains("state.tmdbEnabled = config.status === 'fulfilled' && config.value?.tmdbEnabled === true;", main);
    Assert.Contains("tmdbEnabled: state.tmdbEnabled,", main);
    Assert.Contains("filmCard: (params, signal) => api(`/playlists/${playlist.id}/library/film-card?${new URLSearchParams(params)}`, { signal }),", main);
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
    Assert.Contains("const el = markControls({ ...ctx, toast }, { name: entry.name, contentType: 'Movies', markKey: entry.markKey, ...given });", card);
    Assert.Contains("if (answer?.tmdbData) nodes.push(filmCredit(ctx));", card);
    Assert.Contains("the mark controls and, for films, the name, which opens the film card.", Read("DESIGN.md"));
  }
}
