using System.Security.Claims;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <summary>Sets a mark from an entry name; a null field keeps the stored value (seen false, rating 0 for a new mark).</summary>
public record MarkRequest(string? Name, string? Kind, bool? Seen, int? Rating);

/// <summary>
/// A signed-in user's film and series marks, the same for every playlist and device. Marks are keyed by
/// <see cref="MarkKey"/>, derived on the server from the entry name, and never touch a playlist or player link.
/// <list type="bullet">
/// <item><c>PUT /api/marks</c> sets or updates a mark; a mark that ends up unseen and unrated is removed (204).</item>
/// <item><c>DELETE /api/marks/{key}</c> removes a mark by its key (404 when the caller has none).</item>
/// <item><c>GET /api/marks?seen=&amp;rating=&amp;page=&amp;pageSize=</c> lists the caller's marks.</item>
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

    marks.MapGet("", async (bool? seen, int? rating, int? page, int? pageSize, ClaimsPrincipal user, AppDbContext db) =>
    {
      var size = pageSize ?? DefaultPageSize;
      var number = page ?? 1;
      if (number < 1 || size < 1 || size > MaxPageSize || rating is < -1 or > 1)
      {
        return Results.BadRequest(new { error = $"Give page >= 1, pageSize 1 to {MaxPageSize} and rating -1, 0 or 1." });
      }

      var userId = user.UserId();
      var query = db.TitleMarks.Where(m => m.UserId == userId);
      if (seen is { } s) query = query.Where(m => m.Seen == s);
      if (rating is { } r) query = query.Where(m => m.Rating == r);
      var total = await query.CountAsync();
      var items = await query
        .OrderBy(m => m.TitleKey).ThenBy(m => m.Id)
        .Skip((number - 1) * size).Take(size)
        .ToListAsync();
      return Results.Ok(new { items = items.Select(ToDto), total, page = number, pageSize = size });
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

      // Undo: an unseen, unrated mark is no mark, so no row stays behind.
      if (!seen && rating == 0)
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
        if (await db.TitleMarks.CountAsync(m => m.UserId == userId) >= MaxMarksPerUser)
        {
          return Results.Problem(
            $"You have reached the limit of {MaxMarksPerUser} marks. Remove a mark to add a new one.",
            statusCode: StatusCodes.Status409Conflict);
        }
        mark = new TitleMark
        {
          Id = Guid.NewGuid(), UserId = userId, Kind = KindText(kind), TitleKey = key.Key, Year = key.Year,
          DisplayTitle = key.DisplayTitle, CreatedUtc = now,
        };
        db.TitleMarks.Add(mark);
      }
      mark.Seen = seen;
      mark.Rating = rating;
      mark.UpdatedUtc = now;
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
    });

    mutations.MapDelete("/{key}", async (string key, ClaimsPrincipal user, AppDbContext db) =>
    {
      var userId = user.UserId();
      var deleted = await db.TitleMarks.Where(m => m.UserId == userId && m.TitleKey == key).ExecuteDeleteAsync();
      return deleted > 0 ? Results.NoContent() : Results.NotFound();
    });

    return app;
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
    updatedUtc = mark.UpdatedUtc,
  };
}
