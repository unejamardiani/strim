using System.Globalization;
using System.Security.Claims;
using Api.Configuration;
using Api.Data;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <summary>A film's TMDB facts plus the user's marks.</summary>
public sealed record LibraryFilmCard(
  string Status, LibraryFilmDetails? Film, IReadOnlyList<LibraryFilmCandidate>? Candidates, LibraryFilmMarks Marks);

/// <param name="PosterUrl">The poster on TMDB's image host (<see cref="TmdbPosterUrl"/>); the browser loads it directly.</param>
public sealed record LibraryFilmDetails(
  int Id, string? Title, int? Year, string? PosterUrl, double Rating, IReadOnlyList<string> Genres, string? Overview,
  IReadOnlyList<LibrarySimilarFilm> Similar);

/// <summary>A similar film from TMDB; when <paramref name="InList"/>, the list entry that holds it.</summary>
/// <param name="Group">The group-title the player shows for the entry.</param>
/// <param name="GroupKey">The group key the editor toggles.</param>
/// <param name="Enabled">Whether the entry's group is switched on.</param>
public sealed record LibrarySimilarFilm(
  int Id, string? Title, int? Year, string? PosterUrl, bool InList, bool Seen,
  string? Entry = null, string? Group = null, string? GroupKey = null, bool? Enabled = null);

public sealed record LibraryFilmCandidate(int Id, string? Title, int? Year, string? PosterUrl);

/// <param name="Rating">-1, 0 or 1, as stored on the mark.</param>
public sealed record LibraryFilmMarks(bool Seen, int Rating);

/// <summary>Read-only Library routes; nothing here changes a playlist.</summary>
public static class LibraryEndpoints
{
  public const string Matched = "matched";
  public const string Choose = "choose";
  public const string NotFound = "notFound";
  public const string NotApplicable = "notApplicable";
  public const string Unavailable = "unavailable";

  /// <summary>The most candidates a "choose" card lists.</summary>
  public const int MaxCandidates = 5;

  private const int MaxEntryTextLength = 1024;

  public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
  {
    // The card for one entry the Library shows. Only the cleaned title and year from MarkKey (or a stored TMDB id)
    // go to TMDB; the entry's name and group are only read here. Without a TMDB token the route does not exist.
    app.MapGet("/api/playlists/{id:guid}/library/film-card", async (
      Guid id, string? name, string? group, string? type, string? cacheKey, bool? includeDisabled, bool? hideSeen,
      ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb, PlaylistPipeline pipeline, CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      if (string.IsNullOrWhiteSpace(name) || name.Length > MaxEntryTextLength || group?.Length > MaxEntryTextLength)
        return Results.BadRequest(new { error = "Provide the entry's name and group." });

      var userId = user.UserId();
      var disabledGroups = await db.Playlists.Where(p => p.Id == id && p.OwnerId == userId)
        .Select(p => p.DisabledGroups).FirstOrDefaultAsync(ct);
      if (disabledGroups is null) return Results.NotFound();

      var isFilm = string.Equals(type, nameof(PlaylistContentType.Movies), StringComparison.OrdinalIgnoreCase);
      var key = isFilm && !AdultGroupRule.IsAdultGroupTitle(group) ? MarkKey.From(name, MarkKind.Film) : null;
      if (key is null) return Results.Ok(new LibraryFilmCard(NotApplicable, null, null, new LibraryFilmMarks(false, 0)));

      // One query for every mark: the card's own, and the seen marks and stored TMDB ids of the similar films.
      var userMarks = await db.TitleMarks.AsNoTracking().Where(m => m.UserId == userId)
        .Select(m => new FilmMark(m.TitleKey, m.Seen, m.Rating, m.FilmDbId)).ToListAsync(ct);
      var mark = userMarks.FirstOrDefault(m => m.TitleKey == key.Key);
      var marks = new LibraryFilmMarks(mark?.Seen ?? false, mark?.Rating ?? 0);

      int movieId;
      if (int.TryParse(mark?.FilmDbId, NumberStyles.None, CultureInfo.InvariantCulture, out var storedId) && storedId > 0)
      {
        movieId = storedId;
      }
      else
      {
        var search = await tmdb.SearchMovieAsync(key.Title, key.Year, ct);
        if (search.Status != TmdbStatus.Ok) return Results.Ok(new LibraryFilmCard(Unavailable, null, null, marks));
        var results = search.Value!.Results ?? [];
        if (results.Count == 0) return Results.Ok(new LibraryFilmCard(NotFound, null, null, marks));
        if (ClearMatch(key, results) is not { } match)
        {
          var candidates = results.Take(MaxCandidates)
            .Select(r => new LibraryFilmCandidate(r.Id, r.Title, YearOf(r.ReleaseDate), TmdbPosterUrl.From(r.PosterPath))).ToList();
          return Results.Ok(new LibraryFilmCard(Choose, null, candidates, marks));
        }
        movieId = match.Id;
      }

      var details = await tmdb.GetMovieWithSimilarAsync(movieId, ct);
      if (details.Status == TmdbStatus.NotFound) return Results.Ok(new LibraryFilmCard(NotFound, null, null, marks));
      if (details.Status != TmdbStatus.Ok) return Results.Ok(new LibraryFilmCard(Unavailable, null, null, marks));
      var film = details.Value!;
      return Results.Ok(new LibraryFilmCard(Matched, new LibraryFilmDetails(
        film.Id, film.Title, YearOf(film.ReleaseDate), TmdbPosterUrl.From(film.PosterPath), film.VoteAverage,
        (film.Genres ?? []).Select(g => g.Name).OfType<string>().ToList(), film.Overview,
        Similar(film.Similar?.Results ?? [], userMarks, IndexOf(pipeline, cacheKey), disabledGroups,
          includeDisabled != true, hideSeen == true)), null, marks));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);

    return app;
  }

  /// <summary>
  /// The similar films of a card, those in the list first, each part in TMDB's order. Each film is found by its
  /// <see cref="MarkKey"/> from title and year, or by the key of a mark that stores its TMDB id, in one pass over the
  /// index for all films. Without an index (no or an expired cacheKey) no film is in the list.
  /// </summary>
  internal static List<LibrarySimilarFilm> Similar(
    IReadOnlyList<TmdbMovieSummary> results, IReadOnlyList<FilmMark> marks, PlaylistSearchIndex? index,
    IEnumerable<string> disabledGroups, bool hideDisabled, bool hideSeen)
  {
    var films = results.Where(r => r.Id > 0).DistinctBy(r => r.Id).ToList();
    var keysPerFilm = films.Select(f =>
    {
      var year = YearOf(f.ReleaseDate);
      var derived = MarkKey.From(year is null ? f.Title : $"{f.Title} ({year})", MarkKind.Film)?.Key;
      var id = f.Id.ToString(CultureInfo.InvariantCulture);
      var stored = marks.Where(m => m.FilmDbId == id).Select(m => m.TitleKey);
      return (derived is null ? stored : stored.Prepend(derived)).Distinct(StringComparer.Ordinal).ToList();
    }).ToList();
    var found = index?.FindFilms(keysPerFilm.SelectMany(k => k).ToHashSet(StringComparer.Ordinal),
      new HashSet<string>(disabledGroups, StringComparer.OrdinalIgnoreCase), hideDisabled);
    var seenKeys = marks.Where(m => m.Seen).Select(m => m.TitleKey).ToHashSet(StringComparer.Ordinal);

    var similar = films.Select((f, i) =>
    {
      var poster = TmdbPosterUrl.From(f.PosterPath);
      var seen = keysPerFilm[i].Any(seenKeys.Contains);
      var matches = keysPerFilm[i].Select(k => found?.GetValueOrDefault(k)).OfType<PlaylistSearchMatch>().ToList();
      var entry = matches.FirstOrDefault(m => m.Enabled) ?? matches.FirstOrDefault();
      var year = YearOf(f.ReleaseDate);
      return entry is null
        ? new LibrarySimilarFilm(f.Id, f.Title, year, poster, false, seen)
        : new LibrarySimilarFilm(f.Id, f.Title, year, poster, true, seen, entry.Name, entry.OutputGroup, entry.GroupKey, entry.Enabled);
    });
    // OrderBy is stable, so TMDB's order holds within each part.
    return similar.Where(s => !hideSeen || !s.Seen).OrderBy(s => s.InList ? 0 : 1).ToList();
  }

  internal sealed record FilmMark(string TitleKey, bool Seen, int Rating, string? FilmDbId);

  private static PlaylistSearchIndex? IndexOf(PlaylistPipeline pipeline, string? cacheKey)
  {
    if (string.IsNullOrWhiteSpace(cacheKey) || cacheKey.Length > MaxEntryTextLength) return null;
    var source = pipeline.Cache.TryGetSessionSource(cacheKey);
    return source is null ? null : pipeline.Cache.TryGetAnalysis(source)?.SearchIndex;
  }

  /// <summary>
  /// The clear-match rule: exactly one result whose title, normalised like <see cref="MarkKey"/>, equals the key's
  /// title, and whose release year equals the key's year when the key has one. Anything else is a choice.
  /// </summary>
  internal static TmdbMovieSummary? ClearMatch(MarkKey key, IReadOnlyList<TmdbMovieSummary> results)
  {
    var same = results.Where(r => r.Id > 0 && r.Title is not null && MarkKey.NormaliseTitle(r.Title) == key.Title
      && (key.Year is null || YearOf(r.ReleaseDate) == key.Year)).Take(2).ToList();
    return same.Count == 1 ? same[0] : null;
  }

  private static int? YearOf(string? releaseDate) =>
    releaseDate is { Length: >= 4 }
      && int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
      ? year : null;
}
