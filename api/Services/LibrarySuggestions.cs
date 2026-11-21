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

  /// <summary>Films and series interleaved by rank, TMDB's order within each kind; adult, unknown and repeated items are skipped.</summary>
  internal static List<SuggestionCandidate> Trending(
    IReadOnlyList<TmdbMovieSummary> films, IReadOnlyList<TmdbTvSummary> series, IReadOnlyList<LibraryEndpoints.FilmMark> marks)
  {
    var filmCandidates = films.Where(f => f.Id > 0 && !f.Adult).DistinctBy(f => f.Id)
      .Select(f => new SuggestionCandidate(f.Id, f.PosterPath, FilmKeys(f, marks))).ToList();
    var seriesCandidates = series.Where(s => s.Id > 0 && !s.Adult).DistinctBy(s => s.Id)
      .Select(s => new SuggestionCandidate(s.Id, s.PosterPath, SeriesKeys(s, marks))).ToList();
    var all = new List<SuggestionCandidate>(filmCandidates.Count + seriesCandidates.Count);
    for (var i = 0; i < Math.Max(filmCandidates.Count, seriesCandidates.Count); i++)
    {
      if (i < filmCandidates.Count) all.Add(filmCandidates[i]);
      if (i < seriesCandidates.Count) all.Add(seriesCandidates[i]);
    }
    return all;
  }

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
  /// The candidates the list holds, in candidate order, at most <see cref="MaxResults"/>. A list title whose mark stores
  /// another TMDB id is not this candidate; a candidate takes the first other title its keys found and is left out when
  /// that title is in an adult group, seen, thumbs down, or taken by an earlier candidate.
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
      var title = candidate.Keys.Select(k => byKey.GetValueOrDefault(k))
        .FirstOrDefault(t => t is not null && !StoresAnotherId(marks.GetValueOrDefault(t.MarkKey!), candidate.TmdbId));
      if (title is null || !taken.Add(title.MarkKey!) || AdultGroupRule.IsAdultGroupTitle(title.OutputGroup)) continue;
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
