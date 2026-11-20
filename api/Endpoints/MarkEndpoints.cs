using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <summary>Sets a mark from an entry name; a null field keeps the stored value (seen false, rating 0 for a new mark).</summary>
public record MarkRequest(string? Name, string? Kind, bool? Seen, int? Rating, bool? Watchlist = null);

/// <summary>
/// Confirms the TMDB film for an entry name; a null id clears the choice. The id is read as raw JSON so that a
/// string, fraction or negative number is a 400 from the handler rather than a binding failure.
/// </summary>
public record FilmMatchRequest(string? Name, JsonElement? TmdbId);

/// <summary>
/// A signed-in user's film and series marks, the same for every playlist and device. Marks are keyed by
/// <see cref="MarkKey"/>, derived on the server from the entry name, and never touch a playlist or player link.
/// <list type="bullet">
/// <item><c>PUT /api/marks</c> sets or updates a mark; a mark left unseen, unrated, off the watchlist and without a
/// confirmed film is removed (204).</item>
/// <item><c>PUT /api/marks/film-match</c> stores (or, with a null id, clears) the TMDB film the user picked for a
/// film's card; 404 without a TMDB token. <c>PUT /api/marks/series-match</c> does the same for a series.</item>
/// <item><c>DELETE /api/marks/{key}</c> removes a mark by its key (404 when the caller has none).</item>
/// <item><c>GET /api/marks?seen=&amp;rating=&amp;watchlist=&amp;sort=&amp;page=&amp;pageSize=</c> lists the caller's marks,
/// newest change first, or with <c>sort=watchlisted</c> newest on the watchlist first; a record kept only for its
/// confirmed film is not listed.</item>
/// </list>
/// </summary>
public static class MarkEndpoints
{
  /// <summary>The most marks one user can hold; past it, new marks are refused while existing ones stay editable.</summary>
  public const int MaxMarksPerUser = 5_000;
  public const int MaxNameLength = 500;
  public const int MaxPageSize = 500;
  public const int DefaultPageSize = 100;

  public static IEndpointRouteBuilder MapMarkEndpoints(this IEndpointRouteBuilder app)
  {
    var marks = app.MapGroup("/api/marks").RequireAuthorization();
    var mutations = marks.MapGroup("").RequireCsrfToken();

    marks.MapGet("", async (bool? seen, int? rating, bool? watchlist, string? sort, int? page, int? pageSize,
      ClaimsPrincipal user, AppDbContext db) =>
    {
      var size = pageSize ?? DefaultPageSize;
      var number = page ?? 1;
      if (number < 1 || size < 1 || size > MaxPageSize || rating is < -1 or > 1 || sort is not (null or "watchlisted"))
      {
        return Results.BadRequest(new { error = $"Give page >= 1, pageSize 1 to {MaxPageSize}, rating -1, 0 or 1 and sort=watchlisted or none." });
      }

      var userId = user.UserId();
      // A record kept only for a confirmed TMDB match (#424) is no mark: never listed, never counted.
      var query = db.TitleMarks.Where(m => m.UserId == userId && (m.Seen || m.Rating != 0 || m.Watchlist));
      if (seen is { } s) query = query.Where(m => m.Seen == s);
      if (rating is { } r) query = query.Where(m => m.Rating == r);
      if (watchlist is { } w) query = query.Where(m => m.Watchlist == w);
      // Newest change first (#299). SQLite cannot ORDER BY DateTimeOffset, so the caller's marks (at most
      // MaxMarksPerUser) are sorted in memory, as the playlist list is.
      var all = await query.ToListAsync();
      var ordered = sort == "watchlisted"
        ? all.OrderByDescending(m => m.WatchlistedUtc)
        : all.OrderByDescending(m => m.UpdatedUtc);
      var items = ordered.ThenBy(m => m.TitleKey, StringComparer.Ordinal).Skip((number - 1) * size).Take(size);
      return Results.Ok(new { items = items.Select(ToDto), total = all.Count, page = number, pageSize = size });
    });

    mutations.MapPut("", async (MarkRequest input, ClaimsPrincipal user, AppDbContext db) =>
    {
      if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > MaxNameLength)
      {
        return Results.BadRequest(new { error = $"Give an entry name of at most {MaxNameLength} characters." });
      }
      if (ParseKind(input.Kind) is not { } kind)
      {
        return Results.BadRequest(new { error = "Kind must be \"film\" or \"series\"." });
      }
      if (input.Rating is < -1 or > 1)
      {
        return Results.BadRequest(new { error = "Rating must be -1, 0 or 1." });
      }
      if (MarkKey.From(input.Name, kind) is not { } key)
      {
        return Results.BadRequest(new { error = "The entry name holds no title." });
      }

      var userId = user.UserId()!;
      var mark = await db.TitleMarks.FirstOrDefaultAsync(m => m.UserId == userId && m.TitleKey == key.Key);
      var seen = input.Seen ?? mark?.Seen ?? false;
      var rating = input.Rating ?? mark?.Rating ?? 0;
      // Seen wins: a seen title leaves the watchlist in the same save, whatever the request says.
      var watchlist = !seen && (input.Watchlist ?? mark?.Watchlist ?? false);

      // Undo: an unseen, unrated, unlisted mark without a confirmed film is no mark, so no row stays behind.
      if (!seen && rating == 0 && !watchlist && mark?.FilmDbId is null)
      {
        if (mark is not null)
        {
          db.TitleMarks.Remove(mark);
          await db.SaveChangesAsync();
        }
        return Results.NoContent();
      }

      var now = DateTimeOffset.UtcNow;
      if (mark is null)
      {
        if (await db.TitleMarks.CountAsync(m => m.UserId == userId) >= MaxMarksPerUser) return LimitReached();
        mark = new TitleMark
        {
          Id = Guid.NewGuid(), UserId = userId, Kind = KindText(kind), TitleKey = key.Key, Year = key.Year,
          DisplayTitle = key.DisplayTitle, CreatedUtc = now,
        };
        db.TitleMarks.Add(mark);
      }
      mark.Seen = seen;
      mark.Rating = rating;
      mark.WatchlistedUtc = !watchlist ? null : mark.Watchlist ? mark.WatchlistedUtc ?? now : now;
      mark.Watchlist = watchlist;
      mark.UpdatedUtc = now;
      return await SaveAsync(db, mark);
    });

    // The card's "choose" answer lists TMDB candidates; the picked id is kept on the film's mark, so the card uses it
    // from then on without a search. Only the id is stored; nothing goes to TMDB here.
    mutations.MapPut("/film-match", (FilmMatchRequest input, ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb) =>
      MatchAsync(MarkKind.Film, input, user, db, tmdb));
    // The same for a series' card: the TMDB tv id goes into the same column of the series mark (film and tv ids
    // overlap, so every reader filters by kind).
    mutations.MapPut("/series-match", (FilmMatchRequest input, ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb) =>
      MatchAsync(MarkKind.Series, input, user, db, tmdb));

    mutations.MapDelete("/{key}", async (string key, ClaimsPrincipal user, AppDbContext db) =>
    {
      var userId = user.UserId();
      var deleted = await db.TitleMarks.Where(m => m.UserId == userId && m.TitleKey == key).ExecuteDeleteAsync();
      return deleted > 0 ? Results.NoContent() : Results.NotFound();
    });

    return app;
  }

  private static async Task<IResult> MatchAsync(
    MarkKind kind, FilmMatchRequest input, ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb)
  {
    if (!tmdb.IsEnabled) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > MaxNameLength)
    {
      return Results.BadRequest(new { error = $"Give an entry name of at most {MaxNameLength} characters." });
    }
    var clear = input.TmdbId is null or { ValueKind: JsonValueKind.Null };
    int? tmdbId = !clear && input.TmdbId!.Value.ValueKind == JsonValueKind.Number
      && input.TmdbId.Value.TryGetInt32(out var parsed) && parsed > 0 ? parsed : null;
    if (!clear && tmdbId is null)
    {
      return Results.BadRequest(new { error = "The TMDB id must be a positive whole number." });
    }
    if (MarkKey.From(input.Name, kind) is not { } key)
    {
      return Results.BadRequest(new { error = "The entry name holds no title." });
    }

    var userId = user.UserId()!;
    var mark = await db.TitleMarks.FirstOrDefaultAsync(m => m.UserId == userId && m.TitleKey == key.Key);
    var filmDbId = tmdbId?.ToString(CultureInfo.InvariantCulture);
    if (filmDbId is null && (mark is null || (!mark.Seen && mark.Rating == 0 && !mark.Watchlist)))
    {
      // Clearing the choice of a mark that holds nothing else leaves no row behind, like the undo of PUT /api/marks.
      if (mark is not null)
      {
        db.TitleMarks.Remove(mark);
        await db.SaveChangesAsync();
      }
      return Results.NoContent();
    }

    var now = DateTimeOffset.UtcNow;
    if (mark is null)
    {
      if (await db.TitleMarks.CountAsync(m => m.UserId == userId) >= MaxMarksPerUser) return LimitReached();
      mark = new TitleMark
      {
        Id = Guid.NewGuid(), UserId = userId, Kind = KindText(kind), TitleKey = key.Key, Year = key.Year,
        DisplayTitle = key.DisplayTitle, CreatedUtc = now,
      };
      db.TitleMarks.Add(mark);
    }
    mark.FilmDbId = filmDbId;
    mark.UpdatedUtc = now;
    return await SaveAsync(db, mark);
  }

  private static IResult LimitReached() => Results.Problem(
    $"You have reached the limit of {MaxMarksPerUser} marks. Remove a mark to add a new one.",
    statusCode: StatusCodes.Status409Conflict);

  private static async Task<IResult> SaveAsync(AppDbContext db, TitleMark mark)
  {
    try
    {
      await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
      // Two requests created the same mark at once; the unique index kept one.
      return Results.Problem("The mark changed at the same time. Please retry.", statusCode: StatusCodes.Status409Conflict);
    }
    return Results.Ok(ToDto(mark));
  }

  private static MarkKind? ParseKind(string? kind) => kind switch
  {
    "film" => MarkKind.Film,
    "series" => MarkKind.Series,
    _ => null,
  };

  private static string KindText(MarkKind kind) => kind == MarkKind.Film ? "film" : "series";

  private static object ToDto(TitleMark mark) => new
  {
    key = mark.TitleKey,
    kind = mark.Kind,
    title = mark.DisplayTitle,
    year = mark.Year,
    seen = mark.Seen,
    rating = mark.Rating,
    watchlist = mark.Watchlist,
    watchlistedUtc = mark.WatchlistedUtc,
    tmdbId = int.TryParse(mark.FilmDbId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (int?)null,
    updatedUtc = mark.UpdatedUtc,
  };
}
