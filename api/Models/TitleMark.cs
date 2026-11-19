namespace Api.Models;

/// <summary>
/// A user's mark on a film or series (table <c>titlemarks</c>): seen, and a thumbs up (1), down (-1) or none (0).
/// The row belongs to an <c>AspNetUsers</c> row and is deleted with it. The table is created idempotently in
/// <c>Configuration/Database.cs</c>; one mark per (user, kind, title key, year), a null year counting as its own value.
/// </summary>
public class TitleMark
{
  public Guid Id { get; set; }

  public string UserId { get; set; } = string.Empty;

  /// <summary>"film" or "series".</summary>
  public string Kind { get; set; } = string.Empty;

  /// <summary>The normalised key from <c>MarkKey</c>.</summary>
  public string TitleKey { get; set; } = string.Empty;

  public int? Year { get; set; }

  public string DisplayTitle { get; set; } = string.Empty;

  public bool Seen { get; set; }

  /// <summary>-1, 0 or 1.</summary>
  public int Rating { get; set; }

  /// <summary>Reserved for a film database id; unused for now.</summary>
  public string? FilmDbId { get; set; }

  public DateTimeOffset CreatedUtc { get; set; }

  public DateTimeOffset UpdatedUtc { get; set; }
}
