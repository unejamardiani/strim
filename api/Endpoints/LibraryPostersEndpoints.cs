using System.Globalization;
using System.Security.Claims;
using Api.Configuration;
using Api.Data;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <param name="Type"><c>Movies</c> or <c>Series</c>; anything else has no poster.</param>
public sealed record LibraryPosterItem(string? Name, string? Group, string? Type);
public sealed record LibraryPostersRequest(string? CacheKey, string? LibraryKey, List<LibraryPosterItem?>? Items);

/// <param name="Status"><c>ok</c>, <c>none</c> or <c>pending</c> (not looked up in this call; ask again later).</param>
/// <param name="PosterUrl">The poster on TMDB's image host (<see cref="TmdbPosterUrl"/>); the browser loads it directly.</param>
public sealed record LibraryPoster(string? Name, string? Type, string Status, string? PosterUrl);

/// <param name="TmdbData">Whether a poster came from TMDB, so the browser shows the TMDB credit line.</param>
public sealed record LibraryPostersResponse(IReadOnlyList<LibraryPoster> Posters, bool TmdbData);

/// <summary>Poster addresses for a batch of Library entries; read-only, nothing here changes a playlist.</summary>
public static class LibraryPostersEndpoints
{
  public const int MaxItems = 24;
  /// <summary>The most TMDB calls one request makes; the rest is pending.</summary>
  public const int MaxTmdbCalls = 8;
  private const int MaxEntryTextLength = 1024;

  public static IEndpointRouteBuilder MapLibraryPostersEndpoints(this IEndpointRouteBuilder app)
  {
    // Read-only like POST /api/library/search, so no CSRF filter. Only the cleaned title and year of each entry's mark
    // key (or the caller's stored TMDB id) go to TMDB; name and group are only read here, never checked against the
    // index, as on the cards. Nothing is logged that names a title or a user.
    app.MapPost("/api/playlists/{id:guid}/library/posters", async (
      Guid id, LibraryPostersRequest input, ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb,
      LibraryPosterMemo memo, PlaylistPipeline pipeline, CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      if (input.Items is not { Count: >= 1 and <= MaxItems } items || items.Any(i => i is null
        || string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > MaxEntryTextLength || i.Group?.Length > MaxEntryTextLength))
        return Results.BadRequest(new { error = $"Give 1 to {MaxItems} entries, each with a name and group of at most {MaxEntryTextLength} characters." });
      var userId = user.UserId()!;
      if (await LibraryEndpoints.AccessAsync(id, userId, input.CacheKey, input.LibraryKey, null, db, pipeline, ct) is null)
        return Results.NotFound();
      var keys = items.Select(i => KeyOf(i!)).ToList();
      var wanted = keys.OfType<MarkKey>().Select(k => k.Key).Distinct().ToList();
      // One marks query per call: the caller's own stored TMDB ids win over a search.
      var stored = wanted.Count == 0 ? [] : await db.TitleMarks.AsNoTracking()
        .Where(m => m.UserId == userId && wanted.Contains(m.TitleKey) && m.FilmDbId != null)
        .Select(m => new { m.TitleKey, m.FilmDbId }).ToListAsync(ct);
      var storedIds = stored.Select(m => (m.TitleKey, Id: int.TryParse(m.FilmDbId, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0))
        .Where(m => m.Id > 0).DistinctBy(m => m.TitleKey).ToDictionary(m => m.TitleKey, m => m.Id, StringComparer.Ordinal);
      var lookup = new Lookup(tmdb, memo, storedIds);
      var posters = new List<LibraryPoster>(items.Count);
      for (var i = 0; i < items.Count; i++)
      {
        var (status, path) = keys[i] is { } key ? await lookup.FindAsync(key, ct) : ("none", null);
        posters.Add(new LibraryPoster(items[i]!.Name, items[i]!.Type, status, TmdbPosterUrl.From(path)));
      }
      return Results.Ok(new LibraryPostersResponse(posters, posters.Any(p => p.PosterUrl is not null)));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);
    return app;
  }

  /// <summary>The mark key of a film or series entry outside an adult group; null when it has no poster.</summary>
  private static MarkKey? KeyOf(LibraryPosterItem item)
  {
    bool Is(PlaylistContentType type) => string.Equals(item.Type, type.ToString(), StringComparison.OrdinalIgnoreCase);
    MarkKind? kind = Is(PlaylistContentType.Movies) ? MarkKind.Film : Is(PlaylistContentType.Series) ? MarkKind.Series : null;
    return kind is { } k && !AdultGroupRule.IsAdultGroupTitle(item.Group) ? MarkKey.From(item.Name, k) : null;
  }

  /// <summary>One request's lookups: at most <see cref="MaxTmdbCalls"/> calls, none after RateLimited or Unavailable, no
  /// retries (<see cref="TmdbClient"/> keeps its own back-off).</summary>
  private sealed class Lookup(TmdbClient tmdb, LibraryPosterMemo memo, IReadOnlyDictionary<string, int> storedIds)
  {
    private readonly Dictionary<string, string?> _found = new(StringComparer.Ordinal);
    private int _calls;
    private bool _stopped;
    private static (string, string?) Answer(string? path) => (path is null ? "none" : "ok", path);

    public async Task<(string Status, string? Path)> FindAsync(MarkKey key, CancellationToken ct)
    {
      if (_found.TryGetValue(key.Key, out var known)) return Answer(known);
      var stored = storedIds.TryGetValue(key.Key, out var id);
      if (!stored && memo.TryGet(key.Key, out var remembered)) return Answer(remembered);
      if (_stopped || _calls >= MaxTmdbCalls) return ("pending", null);
      _calls++;
      var (status, path) = await CallAsync(key, stored ? id : null, ct);
      if (status is TmdbStatus.RateLimited or TmdbStatus.Unavailable)
      {
        _stopped = true;
        return ("pending", null);
      }
      path = TmdbPosterUrl.From(path) is null ? null : path;
      // A stored id is the caller's own, so only search answers are remembered for everyone.
      if (!stored && status == TmdbStatus.Ok) memo.Set(key.Key, path);
      _found[key.Key] = path;
      return Answer(path);
    }

    /// <summary>One TMDB call: details for a stored id, else a search whose card-style clear match gives the poster.</summary>
    private async Task<(TmdbStatus, string?)> CallAsync(MarkKey key, int? id, CancellationToken ct) => (key.Kind, id) switch
    {
      (MarkKind.Series, int tvId) => Of(await tmdb.GetTvWithSimilarAsync(tvId, ct), tv => tv.PosterPath),
      (_, int movieId) => Of(await tmdb.GetMovieWithSimilarAsync(movieId, ct), movie => movie.PosterPath),
      (MarkKind.Series, _) => Of(await tmdb.SearchTvAsync(key.Title, key.Year, ct),
        page => PosterOf(key, page.Results?.Select(r => (r.Id, r.Name, r.FirstAirDate, r.PosterPath)))),
      _ => Of(await tmdb.SearchMovieAsync(key.Title, key.Year, ct),
        page => PosterOf(key, page.Results?.Select(r => (r.Id, r.Title, r.ReleaseDate, r.PosterPath)))),
    };

    private static (TmdbStatus, string?) Of<T>(TmdbResult<T> result, Func<T, string?> poster) where T : class =>
      (result.Status, result.Value is null ? null : poster(result.Value));

    /// <summary>No clear match is "none" here; the card still offers the choice.</summary>
    private static string? PosterOf(MarkKey key, IEnumerable<(int Id, string? Title, string? Date, string? Poster)>? results)
    {
      var list = results?.ToList() ?? [];
      var match = LibraryEndpoints.ClearMatchId(key, list.Select(r => (r.Id, r.Title, r.Date)));
      return match is null ? null : list.First(r => r.Id == match).Poster;
    }
  }
}
