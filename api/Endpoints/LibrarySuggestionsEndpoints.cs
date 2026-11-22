using System.Globalization;
using System.Security.Claims;
using Api.Configuration;
using Api.Data;
using Api.Services;

namespace Api.Endpoints;

/// <param name="PosterUrl">The poster on TMDB's image host (<see cref="TmdbPosterUrl"/>); null when TMDB has none.</param>
/// <param name="Title">The list title, as the titles route returns it, with the caller's marks.</param>
public sealed record LibrarySuggestion(int TmdbId, string? PosterUrl, LibraryTitleResult Title);

/// <param name="Title">The mark's display title, for the rail's heading.</param>
/// <param name="Kind">"film" or "series".</param>
public sealed record LibrarySuggestionSeed(string Key, string Title, string Kind);

/// <param name="Seed">The thumbs-up title "liked" starts from; null for trending and when no seed gave enough.</param>
/// <param name="Providers">The streaming services a later source asks for; empty for trending and liked.</param>
public sealed record LibrarySuggestionsResponse(
  string Source, LibrarySuggestionSeed? Seed, IReadOnlyList<string> Providers, IReadOnlyList<LibrarySuggestion> Suggestions)
{
  /// <summary>Whether a suggestion came from TMDB, so the browser shows the TMDB credit line.</summary>
  public bool TmdbData => Suggestions.Count > 0;
}

/// <summary>Suggestion rails for the Library's Home: TMDB titles that are in the caller's list; read-only.</summary>
public static class LibrarySuggestionsEndpoints
{
  public const string Trending = "trending";
  public const string Liked = "liked";
  /// <summary>The most TMDB calls one request makes, whether the cache answers or not.</summary>
  public const int MaxTmdbCalls = 8;
  public const int MaxTrendingPages = 3;
  /// <summary>The fewest suggestions with which a seed of "liked" wins.</summary>
  public const int MinLikedResults = 3;

  public static IEndpointRouteBuilder MapLibrarySuggestionsEndpoints(this IEndpointRouteBuilder app)
  {
    // Only fixed paths, a TMDB id, a page number and, for a seed without a stored id, its cleaned title and year go to
    // TMDB; the list is matched here, so only titles that are in it reach the browser. Nothing is logged that names a
    // title or a user.
    app.MapGet("/api/playlists/{id:guid}/library/suggestions", async (
      Guid id, string? source, string? cacheKey, string? libraryKey, bool? includeDisabled,
      ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb, PlaylistPipeline pipeline, TimeProvider time, CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      if (string.IsNullOrWhiteSpace(cacheKey) && string.IsNullOrWhiteSpace(libraryKey))
        return Results.BadRequest(new { error = "Provide a cacheKey or a libraryKey." });
      if (source is not (Trending or Liked)) return Results.BadRequest(new { error = $"Unknown source. Use {Trending} or {Liked}." });
      var userId = user.UserId()!;
      if (await LibraryEndpoints.AccessAsync(id, userId, cacheKey, libraryKey, includeDisabled, db, pipeline, ct) is not { } access)
        return Results.NotFound();
      if (access.Index is not { } index) return Results.Ok(new LibrarySuggestionsResponse(source, null, [], []));

      var marks = await LibraryEndpoints.MarksAsync(db, userId, ct);
      var marksByKey = marks.DistinctBy(m => m.TitleKey).ToDictionary(m => m.TitleKey, StringComparer.Ordinal);
      var disabled = new HashSet<string>(access.DisabledGroups, StringComparer.OrdinalIgnoreCase);
      List<(SuggestionCandidate Candidate, PlaylistTitle Title)> Match(List<SuggestionCandidate> candidates)
      {
        var keys = candidates.SelectMany(c => c.Keys).ToHashSet(StringComparer.Ordinal);
        // Not memoised, as the lookup route, so suggestions do not push the Library's pages out of the memo.
        var found = keys.Count == 0 ? [] : index.Titles(new PlaylistTitleQuery(Keys: keys), disabled, access.HideDisabled).Titles;
        return LibrarySuggestions.Match(candidates, found, marksByKey);
      }

      var calls = new CallBudget();
      var (seed, matched) = source == Trending
        ? (null, await TrendingAsync(tmdb, calls, marks, Match, ct))
        : await LikedAsync(tmdb, calls, marks, time.GetUtcNow(), Match, ct);
      var suggestions = matched.Select(m =>
      {
        var mark = m.Title.MarkKey is { } key ? marksByKey.GetValueOrDefault(key) : null;
        return new LibrarySuggestion(m.Candidate.TmdbId, TmdbPosterUrl.From(m.Candidate.PosterPath),
          LibraryTitlesEndpoints.Result(m.Title, mark?.Seen, mark?.Rating, mark?.Watchlist));
      }).ToList();
      return Results.Ok(new LibrarySuggestionsResponse(source, seed, [], suggestions));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);
    return app;
  }

  /// <summary>
  /// The request's TMDB calls: at most <see cref="MaxTmdbCalls"/>, cache hits included. An answer that ends the request
  /// stops every later call; nothing is retried (<see cref="TmdbClient"/> keeps its own back-off).
  /// </summary>
  private sealed class CallBudget
  {
    private int _calls;
    public bool Stopped { get; private set; }
    public bool Left => !Stopped && _calls < MaxTmdbCalls;

    public async Task<T?> AskAsync<T>(Func<Task<TmdbResult<T>>> call, Func<TmdbStatus, bool>? ends = null) where T : class
    {
      if (!Left) return null;
      _calls++;
      var answer = await call();
      if (answer.Status == TmdbStatus.Ok) return answer.Value;
      Stopped = ends?.Invoke(answer.Status) ?? true;
      return null;
    }
  }

  /// <summary>
  /// Page 1 of trending films and series, then the next pages while fewer than <see cref="LibrarySuggestions.MaxResults"/>
  /// match, up to page 3 and never past a kind's last page. After any answer but Ok no further call is made.
  /// </summary>
  private static async Task<List<(SuggestionCandidate Candidate, PlaylistTitle Title)>> TrendingAsync(TmdbClient tmdb,
    CallBudget calls, IReadOnlyList<LibraryEndpoints.FilmMark> marks,
    Func<List<SuggestionCandidate>, List<(SuggestionCandidate, PlaylistTitle)>> match, CancellationToken ct)
  {
    var (films, series) = (new List<TmdbMovieSummary>(), new List<TmdbTvSummary>());
    var (filmPages, seriesPages) = (1, 1);
    List<(SuggestionCandidate, PlaylistTitle)> matched = [];
    for (var page = 1; page <= MaxTrendingPages && !calls.Stopped && (page <= filmPages || page <= seriesPages); page++)
    {
      var asked = page;
      if (asked <= filmPages && await calls.AskAsync(() => tmdb.GetTrendingMoviesAsync(asked, ct)) is { } filmPage)
      {
        films.AddRange(filmPage.Results ?? []);
        filmPages = filmPage.TotalPages;
      }
      if (asked <= seriesPages && await calls.AskAsync(() => tmdb.GetTrendingTvAsync(asked, ct)) is { } seriesPage)
      {
        series.AddRange(seriesPage.Results ?? []);
        seriesPages = seriesPage.TotalPages;
      }
      matched = match(LibrarySuggestions.Trending(films, series, marks));
      if (matched.Count >= LibrarySuggestions.MaxResults) break;
    }
    return matched;
  }

  /// <summary>
  /// Up to 3 thumbs-up seeds (<see cref="LibrarySuggestions.Seeds"/>); the first whose recommendations put at least
  /// <see cref="MinLikedResults"/> titles of the list on the rail wins. A seed takes its stored TMDB id, else a clear
  /// search match, else it is skipped; page 2 is asked only while short. A search that is rate limited or unavailable
  /// and any answer but Ok on recommendations end the calls; a seed whose pages do not all come is dropped.
  /// </summary>
  private static async Task<(LibrarySuggestionSeed?, List<(SuggestionCandidate Candidate, PlaylistTitle Title)>)> LikedAsync(TmdbClient tmdb,
    CallBudget calls, IReadOnlyList<LibraryEndpoints.FilmMark> marks, DateTimeOffset now,
    Func<List<SuggestionCandidate>, List<(SuggestionCandidate, PlaylistTitle)>> match, CancellationToken ct)
  {
    static bool SearchEnds(TmdbStatus status) => status is TmdbStatus.RateLimited or TmdbStatus.Unavailable;
    foreach (var mark in LibrarySuggestions.Seeds(marks, now))
    {
      if (!calls.Left) break;
      if (LibrarySuggestions.KeyOf(mark) is not { } key) continue;
      var isFilm = key.Kind == MarkKind.Film;
      int? tmdbId = int.TryParse(mark.FilmDbId, NumberStyles.None, CultureInfo.InvariantCulture, out var stored) && stored > 0
        ? stored : null;
      if (tmdbId is null && isFilm && await calls.AskAsync(() => tmdb.SearchMovieAsync(key.Title, key.Year, ct), SearchEnds) is { } films)
        tmdbId = LibraryEndpoints.ClearMatchId(key, (films.Results ?? []).OfType<TmdbMovieSummary>().Select(r => (r.Id, r.Title, r.ReleaseDate)));
      else if (tmdbId is null && !isFilm && await calls.AskAsync(() => tmdb.SearchTvAsync(key.Title, key.Year, ct), SearchEnds) is { } shows)
        tmdbId = LibraryEndpoints.ClearMatchId(key, (shows.Results ?? []).OfType<TmdbTvSummary>().Select(r => (r.Id, r.Name, r.FirstAirDate)));
      if (tmdbId is not { } seedId) continue;

      var (filmResults, seriesResults) = (new List<TmdbMovieSummary?>(), new List<TmdbTvSummary?>());
      List<(SuggestionCandidate, PlaylistTitle)>? matched = null;
      for (var page = 1; page <= 2; page++)
      {
        var asked = page;
        int? totalPages = null;
        if (isFilm && await calls.AskAsync(() => tmdb.GetMovieRecommendationsAsync(seedId, asked, ct)) is { } filmPage)
          totalPages = Collect(filmResults, filmPage.Results, filmPage.TotalPages);
        else if (!isFilm && await calls.AskAsync(() => tmdb.GetTvRecommendationsAsync(seedId, asked, ct)) is { } seriesPage)
          totalPages = Collect(seriesResults, seriesPage.Results, seriesPage.TotalPages);
        // A page that did not come leaves the seed unfinished, so it is dropped whichever page it was.
        if (totalPages is null) { matched = null; break; }
        matched = match(LibrarySuggestions.Recommended(filmResults, seriesResults, seedId, key.Key, marks));
        if (matched.Count >= LibrarySuggestions.MaxResults || totalPages <= 1) break;
      }
      if (matched?.Count >= MinLikedResults)
        return (new LibrarySuggestionSeed(key.Key, mark.DisplayTitle, isFilm ? "film" : "series"), matched);
    }
    return (null, []);
  }

  /// <summary>Adds a page's results and passes on its page count.</summary>
  private static int Collect<T>(List<T?> results, IReadOnlyList<T>? page, int totalPages) where T : class
  {
    results.AddRange(page ?? []);
    return totalPages;
  }
}
