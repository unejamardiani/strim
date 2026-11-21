using System.Security.Claims;
using Api.Configuration;
using Api.Data;
using Api.Services;

namespace Api.Endpoints;

/// <param name="PosterUrl">The poster on TMDB's image host (<see cref="TmdbPosterUrl"/>); null when TMDB has none.</param>
/// <param name="Title">The list title, as the titles route returns it, with the caller's marks.</param>
public sealed record LibrarySuggestion(int TmdbId, string? PosterUrl, LibraryTitleResult Title);

/// <param name="Seed">The title a later source starts from; null for trending.</param>
/// <param name="Providers">The streaming services a later source asks for; empty for trending.</param>
public sealed record LibrarySuggestionsResponse(
  string Source, string? Seed, IReadOnlyList<string> Providers, IReadOnlyList<LibrarySuggestion> Suggestions)
{
  /// <summary>Whether a suggestion came from TMDB, so the browser shows the TMDB credit line.</summary>
  public bool TmdbData => Suggestions.Count > 0;
}

/// <summary>Suggestion rails for the Library's Home: TMDB titles that are in the caller's list; read-only.</summary>
public static class LibrarySuggestionsEndpoints
{
  public const string Trending = "trending";
  /// <summary>The most TMDB calls one request makes, whether the cache answers or not.</summary>
  public const int MaxTmdbCalls = 8;
  public const int MaxTrendingPages = 3;

  public static IEndpointRouteBuilder MapLibrarySuggestionsEndpoints(this IEndpointRouteBuilder app)
  {
    // Only the fixed trending paths and a page number go to TMDB; the list is matched here, so only titles that are in
    // it reach the browser. Nothing is logged that names a title or a user.
    app.MapGet("/api/playlists/{id:guid}/library/suggestions", async (
      Guid id, string? source, string? cacheKey, string? libraryKey, bool? includeDisabled,
      ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb, PlaylistPipeline pipeline, CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      if (string.IsNullOrWhiteSpace(cacheKey) && string.IsNullOrWhiteSpace(libraryKey))
        return Results.BadRequest(new { error = "Provide a cacheKey or a libraryKey." });
      if (source != Trending) return Results.BadRequest(new { error = $"Unknown source. Use {Trending}." });
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

      var matched = await TrendingAsync(tmdb, marks, Match, ct);
      var suggestions = matched.Select(m =>
      {
        var mark = m.Title.MarkKey is { } key ? marksByKey.GetValueOrDefault(key) : null;
        return new LibrarySuggestion(m.Candidate.TmdbId, TmdbPosterUrl.From(m.Candidate.PosterPath),
          LibraryTitlesEndpoints.Result(m.Title, mark?.Seen, mark?.Rating, mark?.Watchlist));
      }).ToList();
      return Results.Ok(new LibrarySuggestionsResponse(source, null, [], suggestions));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);
    return app;
  }

  /// <summary>
  /// Page 1 of trending films and series, then the next pages while fewer than <see cref="LibrarySuggestions.MaxResults"/>
  /// match, up to page 3 and never past a kind's last page. After any answer but Ok no further call is made; nothing is
  /// retried (<see cref="TmdbClient"/> keeps its own back-off).
  /// </summary>
  private static async Task<List<(SuggestionCandidate Candidate, PlaylistTitle Title)>> TrendingAsync(TmdbClient tmdb,
    IReadOnlyList<LibraryEndpoints.FilmMark> marks,
    Func<List<SuggestionCandidate>, List<(SuggestionCandidate, PlaylistTitle)>> match, CancellationToken ct)
  {
    var (calls, stopped) = (0, false);
    async Task<T?> AskAsync<T>(Func<Task<TmdbResult<T>>> call) where T : class
    {
      if (stopped || calls >= MaxTmdbCalls) return null;
      calls++;
      var answer = await call();
      stopped = answer.Status != TmdbStatus.Ok;
      return stopped ? null : answer.Value;
    }

    var (films, series) = (new List<TmdbMovieSummary>(), new List<TmdbTvSummary>());
    var (filmPages, seriesPages) = (1, 1);
    List<(SuggestionCandidate, PlaylistTitle)> matched = [];
    for (var page = 1; page <= MaxTrendingPages && !stopped && (page <= filmPages || page <= seriesPages); page++)
    {
      var asked = page;
      if (asked <= filmPages && await AskAsync(() => tmdb.GetTrendingMoviesAsync(asked, ct)) is { } filmPage)
      {
        films.AddRange(filmPage.Results ?? []);
        filmPages = filmPage.TotalPages;
      }
      if (asked <= seriesPages && await AskAsync(() => tmdb.GetTrendingTvAsync(asked, ct)) is { } seriesPage)
      {
        series.AddRange(seriesPage.Results ?? []);
        seriesPages = seriesPage.TotalPages;
      }
      matched = match(LibrarySuggestions.Trending(films, series, marks));
      if (matched.Count >= LibrarySuggestions.MaxResults) break;
    }
    return matched;
  }
}
