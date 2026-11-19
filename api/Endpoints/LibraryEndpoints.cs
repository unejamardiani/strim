using System.Globalization;
using System.Security.Claims;
using Api.Configuration;
using Api.Data;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <summary>A film's TMDB facts plus the user's marks.</summary>
/// <param name="Group">The group of the entry the card was opened for, as the request named it.</param>
public sealed record LibraryFilmCard(
  string Status, LibraryFilmDetails? Film, IReadOnlyList<LibraryFilmCandidate>? Candidates, LibraryFilmMarks Marks,
  string? Group = null)
{
  /// <summary>Whether the card carries data from TMDB, so the browser shows the TMDB credit line.</summary>
  public bool TmdbData => Film is not null || Candidates is { Count: > 0 };
}

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

/// <summary>A series' TMDB facts, the user's marks and the season and episode of the row the card was opened for.</summary>
public sealed record LibrarySeriesCard(
  string Status, LibrarySeriesDetails? Series, IReadOnlyList<LibraryFilmCandidate>? Candidates, LibraryFilmMarks Marks,
  LibrarySeriesContext? Context)
{
  /// <summary>Whether the card carries data from TMDB, so the browser shows the TMDB credit line.</summary>
  public bool TmdbData => Series is not null || Candidates is { Count: > 0 };
}

public sealed record LibrarySeriesContext(int? Season, int? Episode);

/// <summary>State: returning, ended, cancelled, upcoming or null. InList: null without an index.</summary>
public sealed record LibrarySeriesDetails(
  int Id, string? Title, int? FirstYear, int? LastYear, string? State, int? SeasonCount, int? EpisodeCount,
  string? Network, string? PosterUrl, double Rating, IReadOnlyList<string> Genres, string? Overview,
  LibrarySeriesInList? InList, IReadOnlyList<LibrarySimilarFilm> Similar);

public sealed record LibrarySeriesInList(int Episodes, int Unnumbered, IReadOnlyList<LibrarySeriesSeason> Seasons);

/// <summary>Total from TMDB (null when unknown); count and the sorted, cut episode numbers from the list.</summary>
public sealed record LibrarySeriesSeason(int Season, int? Total, int Count, IReadOnlyList<int> Episodes);

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
    // The owner or an accepted viewer; marks and stored TMDB ids are always the caller's own. A viewer's similar films
    // come from their Library key and the visible groups only, which the database names, never the request.
    app.MapGet("/api/playlists/{id:guid}/library/film-card", async (
      Guid id, string? name, string? group, string? type, string? cacheKey, string? libraryKey, bool? includeDisabled,
      bool? hideSeen, ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb, PlaylistPipeline pipeline, CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      if (!IsEntryText(name, group)) return Results.BadRequest(new { error = "Provide the entry's name and group." });

      var userId = user.UserId()!;
      if (await AccessAsync(id, userId, cacheKey, libraryKey, includeDisabled, db, pipeline, ct) is not { } access)
        return Results.NotFound();
      var (disabledGroups, hideDisabled, index) = access;
      var entryGroup = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
      IResult Card(string status, LibraryFilmDetails? film, IReadOnlyList<LibraryFilmCandidate>? candidates, LibraryFilmMarks marks) =>
        Results.Ok(new LibraryFilmCard(status, film, candidates, marks, entryGroup));

      var isFilm = string.Equals(type, nameof(PlaylistContentType.Movies), StringComparison.OrdinalIgnoreCase);
      var key = isFilm && !AdultGroupRule.IsAdultGroupTitle(group) ? MarkKey.From(name, MarkKind.Film) : null;
      if (key is null) return Card(NotApplicable, null, null, new LibraryFilmMarks(false, 0));

      // One query for every mark: the card's own, and the seen marks and stored TMDB ids of the similar films.
      var userMarks = await MarksAsync(db, userId, ct);
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
        if (search.Status != TmdbStatus.Ok) return Card(Unavailable, null, null, marks);
        var results = search.Value!.Results ?? [];
        if (results.Count == 0) return Card(NotFound, null, null, marks);
        if (ClearMatchId(key, results.Select(r => (r.Id, r.Title, r.ReleaseDate))) is not { } match)
        {
          var candidates = results.Take(MaxCandidates)
            .Select(r => new LibraryFilmCandidate(r.Id, r.Title, YearOf(r.ReleaseDate), TmdbPosterUrl.From(r.PosterPath))).ToList();
          return Card(Choose, null, candidates, marks);
        }
        movieId = match;
      }

      var details = await tmdb.GetMovieWithSimilarAsync(movieId, ct);
      if (details.Status == TmdbStatus.NotFound) return Card(NotFound, null, null, marks);
      if (details.Status != TmdbStatus.Ok) return Card(Unavailable, null, null, marks);
      var film = details.Value!;
      return Card(Matched, new LibraryFilmDetails(
        film.Id, film.Title, YearOf(film.ReleaseDate), TmdbPosterUrl.From(film.PosterPath), film.VoteAverage,
        (film.Genres ?? []).Select(g => g.Name).OfType<string>().ToList(), film.Overview,
        Similar(film.Similar?.Results ?? [], userMarks, index, disabledGroups, hideDisabled, hideSeen == true)), null, marks);
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);

    // The card for one series row: the same rules as the film card; only the cleaned title and first-air year (or a
    // stored TMDB tv id) go to TMDB, never the season or episode. What the list holds and which similar series it
    // holds come from one pass over the index.
    app.MapGet("/api/playlists/{id:guid}/library/series-card", async (
      Guid id, string? name, string? group, string? type, string? cacheKey, string? libraryKey, bool? includeDisabled,
      bool? hideSeen, ClaimsPrincipal user, AppDbContext db, TmdbClient tmdb, PlaylistPipeline pipeline, CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      if (!IsEntryText(name, group)) return Results.BadRequest(new { error = "Provide the entry's name and group." });

      var userId = user.UserId()!;
      if (await AccessAsync(id, userId, cacheKey, libraryKey, includeDisabled, db, pipeline, ct) is not { } access)
        return Results.NotFound();
      var (season, episode) = SeriesEpisode.Parse(name);
      var context = season is null && episode is null ? null : new LibrarySeriesContext(season, episode);
      IResult Card(string status, LibrarySeriesDetails? series, IReadOnlyList<LibraryFilmCandidate>? candidates, LibraryFilmMarks marks) =>
        Results.Ok(new LibrarySeriesCard(status, series, candidates, marks, context));

      var isSeries = string.Equals(type, nameof(PlaylistContentType.Series), StringComparison.OrdinalIgnoreCase);
      var key = isSeries && !AdultGroupRule.IsAdultGroupTitle(group) ? MarkKey.From(name, MarkKind.Series) : null;
      if (key is null) return Card(NotApplicable, null, null, new LibraryFilmMarks(false, 0));

      var userMarks = await MarksAsync(db, userId, ct);
      var mark = userMarks.FirstOrDefault(m => m.TitleKey == key.Key);
      var marks = new LibraryFilmMarks(mark?.Seen ?? false, mark?.Rating ?? 0);

      int tvId;
      if (int.TryParse(mark?.FilmDbId, NumberStyles.None, CultureInfo.InvariantCulture, out var storedId) && storedId > 0)
      {
        tvId = storedId;
      }
      else
      {
        var search = await tmdb.SearchTvAsync(key.Title, key.Year, ct);
        if (search.Status != TmdbStatus.Ok) return Card(Unavailable, null, null, marks);
        var results = search.Value!.Results ?? [];
        if (results.Count == 0) return Card(NotFound, null, null, marks);
        if (ClearMatchId(key, results.Select(r => (r.Id, r.Name, r.FirstAirDate))) is not { } match)
        {
          var candidates = results.Take(MaxCandidates)
            .Select(r => new LibraryFilmCandidate(r.Id, r.Name, YearOf(r.FirstAirDate), TmdbPosterUrl.From(r.PosterPath))).ToList();
          return Card(Choose, null, candidates, marks);
        }
        tvId = match;
      }

      var details = await tmdb.GetTvWithSimilarAsync(tvId, ct);
      if (details.Status == TmdbStatus.NotFound) return Card(NotFound, null, null, marks);
      if (details.Status != TmdbStatus.Ok) return Card(Unavailable, null, null, marks);
      var tv = details.Value!;

      var titles = (tv.Similar?.Results ?? []).Where(r => r.Id > 0).DistinctBy(r => r.Id)
        .Select(r => new SimilarTitle(r.Id, r.Name, YearOf(r.FirstAirDate), r.PosterPath)).ToList();
      var keys = KeysOf(titles, MarkKind.Series, userMarks);
      var found = access.Index?.FindSeries(key.Key, keys.SelectMany(k => k).ToHashSet(StringComparer.Ordinal),
        new HashSet<string>(access.DisabledGroups, StringComparer.OrdinalIgnoreCase), access.HideDisabled);
      var state = StateOf(tv.Status);
      return Card(Matched, new LibrarySeriesDetails(
        tv.Id, tv.Name, YearOf(tv.FirstAirDate), state == "returning" ? null : YearOf(tv.LastAirDate), state,
        tv.NumberOfSeasons, tv.NumberOfEpisodes, tv.Networks?.Select(n => n.Name).FirstOrDefault(n => n is not null),
        TmdbPosterUrl.From(tv.PosterPath), tv.VoteAverage, (tv.Genres ?? []).Select(g => g.Name).OfType<string>().ToList(),
        tv.Overview, found is null ? null : InList(found, tv.Seasons),
        Arrange(titles, keys, found?.Similar, userMarks, hideSeen == true)), null, marks);
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);

    return app;
  }

  /// <summary>The most episode numbers a season of <see cref="LibrarySeriesInList"/> lists; its count stays complete.</summary>
  public const int MaxEpisodesPerSeason = 200;

  private static bool IsEntryText(string? name, string? group) =>
    !string.IsNullOrWhiteSpace(name) && name.Length <= MaxEntryTextLength && !(group?.Length > MaxEntryTextLength);

  private sealed record CardAccess(IReadOnlyList<string> DisabledGroups, bool HideDisabled, PlaylistSearchIndex? Index);

  /// <summary>The owner or an accepted viewer in one query, else null (the same 404 as for a stranger).</summary>
  private static async Task<CardAccess?> AccessAsync(Guid id, string userId, string? cacheKey, string? libraryKey,
    bool? includeDisabled, AppDbContext db, PlaylistPipeline pipeline, CancellationToken ct)
  {
    var access = await db.Playlists.AsNoTracking()
      .Where(p => p.Id == id && (p.OwnerId == userId || db.PlaylistShares.Any(s => s.PlaylistId == p.Id && s.ViewerUserId == userId)))
      .Select(p => new { p.DisabledGroups, IsOwner = p.OwnerId == userId }).FirstOrDefaultAsync(ct);
    if (access is null) return null;
    // An editor cacheKey only counts for the owner; a Library key only for the playlist and user it was issued to.
    var index = !string.IsNullOrWhiteSpace(libraryKey) ? LibraryIndexOf(pipeline, libraryKey, id, userId)
      : access.IsOwner ? IndexOf(pipeline, cacheKey) : null;
    return new CardAccess(access.DisabledGroups ?? [], !access.IsOwner || includeDisabled != true, index);
  }

  private static Task<List<FilmMark>> MarksAsync(AppDbContext db, string userId, CancellationToken ct) =>
    db.TitleMarks.AsNoTracking().Where(m => m.UserId == userId)
      .Select(m => new FilmMark(m.TitleKey, m.Seen, m.Rating, m.FilmDbId)).ToListAsync(ct);

  /// <summary>TMDB's seasons joined with the list's; season 0 (specials) appears only when the list has it.</summary>
  private static LibrarySeriesInList InList(PlaylistSeriesResult found, IReadOnlyList<TmdbTvSeason>? tmdbSeasons)
  {
    var totals = new Dictionary<int, int?>();
    foreach (var s in tmdbSeasons ?? []) totals.TryAdd(s.SeasonNumber, s.EpisodeCount);
    var seasons = totals.Keys.Where(n => n > 0).Union(found.Seasons.Keys).Order().Select(n =>
    {
      var episodes = found.Seasons.GetValueOrDefault(n) ?? [];
      return new LibrarySeriesSeason(n, totals.GetValueOrDefault(n), episodes.Count,
        episodes.Order().Take(MaxEpisodesPerSeason).ToList());
    }).ToList();
    return new LibrarySeriesInList(found.Episodes, found.Unnumbered, seasons);
  }

  private static string? StateOf(string? status) => status switch
  {
    "Returning Series" => "returning", "Ended" => "ended", "Canceled" or "Cancelled" => "cancelled",
    "In Production" or "Planned" or "Pilot" => "upcoming", _ => null,
  };

  /// <summary>
  /// The similar films of a card, those in the list first, each part in TMDB's order. Each film is found by its
  /// <see cref="MarkKey"/> from title and year, or by the key of a mark that stores its TMDB id, in one pass over the
  /// index for all films. Without an index (no or an expired cacheKey) no film is in the list.
  /// </summary>
  internal static List<LibrarySimilarFilm> Similar(
    IReadOnlyList<TmdbMovieSummary> results, IReadOnlyList<FilmMark> marks, PlaylistSearchIndex? index,
    IEnumerable<string> disabledGroups, bool hideDisabled, bool hideSeen)
  {
    var films = results.Where(r => r.Id > 0).DistinctBy(r => r.Id)
      .Select(r => new SimilarTitle(r.Id, r.Title, YearOf(r.ReleaseDate), r.PosterPath)).ToList();
    var keys = KeysOf(films, MarkKind.Film, marks);
    var found = index?.FindFilms(keys.SelectMany(k => k).ToHashSet(StringComparer.Ordinal),
      new HashSet<string>(disabledGroups, StringComparer.OrdinalIgnoreCase), hideDisabled);
    return Arrange(films, keys, found, marks, hideSeen);
  }

  private sealed record SimilarTitle(int Id, string? Title, int? Year, string? PosterPath);

  /// <summary>
  /// The mark keys a similar title can be found under: from title and year (a series also without the year), and the
  /// keys of the user's marks of the same kind storing its TMDB id (film and tv ids overlap).
  /// </summary>
  private static List<List<string>> KeysOf(IReadOnlyList<SimilarTitle> titles, MarkKind kind, IReadOnlyList<FilmMark> marks)
  {
    var prefix = kind == MarkKind.Film ? "film:" : "series:";
    return titles.Select(t =>
    {
      var derived = new List<string?> { MarkKey.From(t.Year is null ? t.Title : $"{t.Title} ({t.Year})", kind)?.Key };
      if (kind == MarkKind.Series) derived.Add(MarkKey.From(t.Title, kind)?.Key);
      var id = t.Id.ToString(CultureInfo.InvariantCulture);
      var stored = marks.Where(m => m.FilmDbId == id && m.TitleKey.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.TitleKey);
      return derived.OfType<string>().Concat(stored).Distinct(StringComparer.Ordinal).ToList();
    }).ToList();
  }

  /// <summary>Similar titles with their list entries and seen marks, those in the list first, each part in TMDB's order.</summary>
  private static List<LibrarySimilarFilm> Arrange(IReadOnlyList<SimilarTitle> titles, List<List<string>> keys,
    IReadOnlyDictionary<string, PlaylistSearchMatch>? found, IReadOnlyList<FilmMark> marks, bool hideSeen)
  {
    var seenKeys = marks.Where(m => m.Seen).Select(m => m.TitleKey).ToHashSet(StringComparer.Ordinal);
    var similar = titles.Select((t, i) =>
    {
      var poster = TmdbPosterUrl.From(t.PosterPath);
      var seen = keys[i].Any(seenKeys.Contains);
      var matches = keys[i].Select(k => found?.GetValueOrDefault(k)).OfType<PlaylistSearchMatch>().ToList();
      var entry = matches.FirstOrDefault(m => m.Enabled) ?? matches.FirstOrDefault();
      return entry is null
        ? new LibrarySimilarFilm(t.Id, t.Title, t.Year, poster, false, seen)
        : new LibrarySimilarFilm(t.Id, t.Title, t.Year, poster, true, seen, entry.Name, entry.OutputGroup, entry.GroupKey, entry.Enabled);
    });
    // OrderBy is stable, so TMDB's order holds within each part.
    return similar.Where(s => !hideSeen || !s.Seen).OrderBy(s => s.InList ? 0 : 1).ToList();
  }

  private static PlaylistSearchIndex? LibraryIndexOf(PlaylistPipeline pipeline, string libraryKey, Guid playlistId, string userId)
  {
    if (libraryKey.Length > MaxEntryTextLength) return null;
    var source = pipeline.Cache.TryGetLibrarySource(libraryKey, playlistId, userId);
    return source is null ? null : pipeline.Cache.TryGetAnalysis(source)?.SearchIndex;
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
  private static int? ClearMatchId(MarkKey key, IEnumerable<(int Id, string? Title, string? Date)> results)
  {
    var same = results.Where(r => r.Id > 0 && r.Title is not null && MarkKey.NormaliseTitle(r.Title) == key.Title
      && (key.Year is null || YearOf(r.Date) == key.Year)).Take(2).ToList();
    return same.Count == 1 ? same[0].Id : null;
  }

  private static int? YearOf(string? releaseDate) =>
    releaseDate is { Length: >= 4 }
      && int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
      ? year : null;
}
