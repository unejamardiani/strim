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

public sealed record LibraryFilmDetails(
  int Id, string? Title, int? Year, string? PosterPath, double Rating, IReadOnlyList<string> Genres, string? Overview);

public sealed record LibraryFilmCandidate(int Id, string? Title, int? Year, string? PosterPath);

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
      Guid id, string? name, string? group, string? type, ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb,
      CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      if (string.IsNullOrWhiteSpace(name) || name.Length > MaxEntryTextLength || group?.Length > MaxEntryTextLength)
        return Results.BadRequest(new { error = "Provide the entry's name and group." });

      var userId = user.UserId();
      if (!await db.Playlists.AnyAsync(p => p.Id == id && p.OwnerId == userId, ct)) return Results.NotFound();

      var isFilm = string.Equals(type, nameof(PlaylistContentType.Movies), StringComparison.OrdinalIgnoreCase);
      var key = isFilm && !AdultGroupRule.IsAdultGroupTitle(group) ? MarkKey.From(name, MarkKind.Film) : null;
      if (key is null) return Results.Ok(new LibraryFilmCard(NotApplicable, null, null, new LibraryFilmMarks(false, 0)));

      var mark = await db.TitleMarks.AsNoTracking().FirstOrDefaultAsync(
        m => m.UserId == userId && m.Kind == "film" && m.TitleKey == key.Title && m.Year == key.Year, ct);
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
            .Select(r => new LibraryFilmCandidate(r.Id, r.Title, YearOf(r.ReleaseDate), r.PosterPath)).ToList();
          return Results.Ok(new LibraryFilmCard(Choose, null, candidates, marks));
        }
        movieId = match.Id;
      }

      var details = await tmdb.GetMovieWithSimilarAsync(movieId, ct);
      if (details.Status == TmdbStatus.NotFound) return Results.Ok(new LibraryFilmCard(NotFound, null, null, marks));
      if (details.Status != TmdbStatus.Ok) return Results.Ok(new LibraryFilmCard(Unavailable, null, null, marks));
      var film = details.Value!;
      return Results.Ok(new LibraryFilmCard(Matched, new LibraryFilmDetails(
        film.Id, film.Title, YearOf(film.ReleaseDate), film.PosterPath, film.VoteAverage,
        (film.Genres ?? []).Select(g => g.Name).OfType<string>().ToList(), film.Overview), null, marks));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);

    return app;
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
