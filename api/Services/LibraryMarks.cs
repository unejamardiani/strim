using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// One user's film and series marks for one Library request, read with a single query (a user holds at most
/// <c>MarkEndpoints.MaxMarksPerUser</c>). Entries are matched by <see cref="MarkKey"/>, so every variant of a film
/// shows the same mark. Live channels and names without a title have no key and no mark.
/// </summary>
public sealed class LibraryMarks(Dictionary<string, (bool Seen, int Rating, bool Watchlist)> marks)
{
  public static async Task<LibraryMarks> LoadAsync(AppDbContext db, string userId, CancellationToken cancellationToken)
  {
    var rows = await db.TitleMarks.Where(m => m.UserId == userId)
      .Select(m => new { m.TitleKey, m.Seen, m.Rating, m.Watchlist }).ToListAsync(cancellationToken);
    var byKey = new Dictionary<string, (bool, int, bool)>(rows.Count, StringComparer.Ordinal);
    foreach (var row in rows) byKey.TryAdd(row.TitleKey, (row.Seen, row.Rating, row.Watchlist));
    return new LibraryMarks(byKey);
  }

  /// <summary>The entry's mark key (null for live channels and names without a title) and, when marked, its mark.</summary>
  public (string? Key, bool? Seen, int? Rating, bool? Watchlist) Lookup(string name, PlaylistContentType type)
  {
    var key = type switch
    {
      PlaylistContentType.Movies => MarkKey.From(name, MarkKind.Film)?.Key,
      PlaylistContentType.Series => MarkKey.From(name, MarkKind.Series)?.Key,
      _ => null,
    };
    return key is not null && marks.TryGetValue(key, out var mark) ? (key, mark.Seen, mark.Rating, mark.Watchlist) : (key, null, null, null);
  }

  /// <summary>
  /// The index filter for the Library's options, or null when none is set. <paramref name="liked"/> keeps only titles
  /// with a thumbs up; <paramref name="hideSeen"/> hides seen titles. Entries without a key are never hidden as seen.
  /// </summary>
  public Func<string, PlaylistContentType, MarkVerdict>? Filter(bool hideSeen, bool liked)
  {
    hideSeen &= marks.Values.Any(m => m.Seen);
    if (liked && !marks.Values.Any(m => m.Rating == 1)) return (_, _) => MarkVerdict.Skip;
    if (!hideSeen && !liked) return null;
    return (name, type) =>
    {
      var (_, seen, rating, _) = Lookup(name, type);
      if (liked && rating != 1) return MarkVerdict.Skip;
      return hideSeen && seen == true ? MarkVerdict.HideSeen : MarkVerdict.Keep;
    };
  }
}
