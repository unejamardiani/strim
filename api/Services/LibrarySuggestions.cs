using System.Globalization;
using Api.Endpoints;

namespace Api.Services;

/// <summary>A title from TMDB that may be in the list, with the mark keys it can be found under, best first.</summary>
public sealed record SuggestionCandidate(int TmdbId, string? PosterPath, IReadOnlyList<string> Keys);

/// <summary>
/// The matching of the Library's suggestion rails: TMDB titles become candidates with mark keys, and one index pass
/// tells which of them the list holds. Pure; every source (trending now, more later) shares it.
/// </summary>
public static class LibrarySuggestions
{
  public const int MaxResults = 12;

  /// <summary>
  /// Films and series interleaved by TMDB's rank (film 1, series 1, film 2, …); adult, unknown and repeated items are
  /// skipped afterwards, so a skipped item does not move the later ones forward.
  /// </summary>
  internal static List<SuggestionCandidate> Trending(
    IReadOnlyList<TmdbMovieSummary> films, IReadOnlyList<TmdbTvSummary> series, IReadOnlyList<LibraryEndpoints.FilmMark> marks)
  {
    var (seenFilms, seenSeries) = (new HashSet<int>(), new HashSet<int>());
    var all = new List<SuggestionCandidate>(films.Count + series.Count);
    for (var i = 0; i < Math.Max(films.Count, series.Count); i++)
    {
      if (i < films.Count && films[i] is { Id: > 0, Adult: false } film && seenFilms.Add(film.Id))
        all.Add(new SuggestionCandidate(film.Id, film.PosterPath, FilmKeys(film, marks)));
      if (i < series.Count && series[i] is { Id: > 0, Adult: false } show && seenSeries.Add(show.Id))
        all.Add(new SuggestionCandidate(show.Id, show.PosterPath, SeriesKeys(show, marks)));
    }
    return all;
  }

  public const int MaxSeedCandidates = 10;
  public const int MaxSeedsTried = 3;

  /// <summary>
  /// The seeds "Because you liked" tries, in order: of the 10 newest thumbs up, up to 3 starting at the whole UTC days
  /// since the epoch modulo their count, wrapping around, so the first seed holds through a day and moves the next.
  /// </summary>
  internal static List<LibraryEndpoints.FilmMark> Seeds(IReadOnlyList<LibraryEndpoints.FilmMark> marks, DateTimeOffset now)
  {
    var liked = marks.Where(m => m.Rating == 1).OrderByDescending(m => m.UpdatedUtc)
      .ThenBy(m => m.TitleKey, StringComparer.Ordinal).Take(MaxSeedCandidates).ToList();
    if (liked.Count == 0) return [];
    var start = (int)(Math.Max(0, now.ToUnixTimeSeconds()) / 86400 % liked.Count);
    return Enumerable.Range(0, Math.Min(MaxSeedsTried, liked.Count)).Select(i => liked[(start + i) % liked.Count]).ToList();
  }

  /// <summary>A mark's key read back into kind, year and normalised title, as <see cref="MarkKey.From"/> wrote it.</summary>
  internal static MarkKey? KeyOf(LibraryEndpoints.FilmMark mark)
  {
    var parts = mark.TitleKey.Split(':', 3);
    MarkKind? kind = parts[0] switch { "film" => MarkKind.Film, "series" => MarkKind.Series, _ => null };
    if (parts.Length != 3 || kind is null || parts[2].Length == 0) return null;
    int? year = int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
    return new MarkKey(mark.TitleKey, parts[2], year, mark.DisplayTitle, kind.Value);
  }

  /// <summary>A seed's recommendations as candidates in TMDB's order, without the seed itself (by its id or its mark key).</summary>
  internal static List<SuggestionCandidate> Recommended(IReadOnlyList<TmdbMovieSummary> films, IReadOnlyList<TmdbTvSummary> series,
    int seedId, string seedKey, IReadOnlyList<LibraryEndpoints.FilmMark> marks) =>
    Trending(films.Where(f => f.Id != seedId).ToList(), series.Where(s => s.Id != seedId).ToList(), marks)
      .Where(c => !c.Keys.Contains(seedKey, StringComparer.Ordinal)).ToList();

  /// <summary>A film is found only with its release year, by its title and its original title.</summary>
  private static List<string> FilmKeys(TmdbMovieSummary film, IReadOnlyList<LibraryEndpoints.FilmMark> marks)
  {
    var year = YearOf(film.ReleaseDate);
    IEnumerable<string?> derived = year is null ? [] : new[] { film.Title, film.OriginalTitle }
      .Select(t => string.IsNullOrWhiteSpace(t) ? null : MarkKey.From($"{t} ({year})", MarkKind.Film)?.Key);
    return Keys(derived, film.Id, "film:", marks);
  }

  /// <summary>A series is found with and without its first-air year, by its name and its original name.</summary>
  private static List<string> SeriesKeys(TmdbTvSummary series, IReadOnlyList<LibraryEndpoints.FilmMark> marks)
  {
    var year = YearOf(series.FirstAirDate);
    var derived = new[] { series.Name, series.OriginalName }.Where(n => !string.IsNullOrWhiteSpace(n))
      .SelectMany(n => new[] { year is null ? null : $"{n} ({year})", n })
      .Select(n => MarkKey.From(n, MarkKind.Series)?.Key);
    return Keys(derived, series.Id, "series:", marks);
  }

  /// <summary>The derived keys, then the keys of the caller's marks of the same kind storing this TMDB id (film and tv ids overlap).</summary>
  private static List<string> Keys(IEnumerable<string?> derived, int id, string prefix, IReadOnlyList<LibraryEndpoints.FilmMark> marks)
  {
    var text = id.ToString(CultureInfo.InvariantCulture);
    var stored = marks.Where(m => m.FilmDbId == text && m.TitleKey.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.TitleKey);
    return derived.OfType<string>().Concat(stored).Distinct(StringComparer.Ordinal).ToList();
  }

  /// <summary>
  /// The candidates the list holds, in candidate order, at most <see cref="MaxResults"/>. A candidate takes the title of
  /// the first of its keys the pass found and is left out when that title's mark stores another TMDB id (the user said
  /// it is another film), or the title is taken by an earlier candidate, in an adult group, seen or thumbs down.
  /// </summary>
  internal static List<(SuggestionCandidate Candidate, PlaylistTitle Title)> Match(
    IReadOnlyList<SuggestionCandidate> candidates, IReadOnlyList<PlaylistTitle> found,
    IReadOnlyDictionary<string, LibraryEndpoints.FilmMark> marks)
  {
    var byKey = new Dictionary<string, PlaylistTitle>(StringComparer.Ordinal);
    foreach (var title in found)
      if (title.MarkKey is { } key) byKey.TryAdd(key, title);
    var taken = new HashSet<string>(StringComparer.Ordinal);
    var result = new List<(SuggestionCandidate, PlaylistTitle)>();
    foreach (var candidate in candidates)
    {
      if (result.Count >= MaxResults) break;
      var title = candidate.Keys.Select(k => byKey.GetValueOrDefault(k)).FirstOrDefault(t => t is not null);
      if (title is null || StoresAnotherId(marks.GetValueOrDefault(title.MarkKey!), candidate.TmdbId)) continue;
      if (!taken.Add(title.MarkKey!) || AdultGroupRule.IsAdultGroupTitle(title.OutputGroup)) continue;
      if (marks.GetValueOrDefault(title.MarkKey!) is { } mark && (mark.Seen || mark.Rating == -1)) continue;
      result.Add((candidate, title));
    }
    return result;
  }

  private static bool StoresAnotherId(LibraryEndpoints.FilmMark? mark, int id) =>
    int.TryParse(mark?.FilmDbId, NumberStyles.None, CultureInfo.InvariantCulture, out var stored) && stored > 0 && stored != id;

  private static int? YearOf(string? date) =>
    date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
      ? year : null;
}
