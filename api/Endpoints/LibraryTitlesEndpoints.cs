using System.Security.Claims;
using Api.Configuration;
using Api.Data;
using Api.Services;

namespace Api.Endpoints;

/// <summary>One collapsed title with the caller's marks; <c>Seen</c>, <c>Rating</c> and <c>Watchlist</c> are null without a mark.</summary>
public sealed record LibraryTitleResult(
  string Name, string Title, int? Year, string ContentType, string? MarkKey, string Group, string GroupKey, bool Enabled,
  int Entries, int Groups, int? SeasonFirst, int? SeasonLast, int? Episodes, bool? Seen, int? Rating, bool? Watchlist,
  IReadOnlyList<string> AudioLanguages, IReadOnlyList<string> SubtitleLanguages, IReadOnlyList<string> Markers,
  IReadOnlyList<string> PrefixTags, string? LanguageSource);

/// <summary>Titles per content type after every filter except the type filter.</summary>
public sealed record LibraryTitleCounts(int Movies, int Series, int Live);

/// <summary>Total: titles of the requested types. HiddenCount: only in switched-off groups (0 for a viewer).</summary>
/// <param name="Languages">The index's language counts, as in the Library search.</param>
public sealed record LibraryTitlesResponse(
  IReadOnlyList<LibraryTitleResult> Titles, int Total, LibraryTitleCounts Counts, int Offset, int Limit, bool HasMore,
  int HiddenCount, int SeenHiddenCount, IReadOnlyList<PlaylistSearchLanguageCount> Languages);

/// <param name="Keys">Mark keys to look up in the visible list.</param>
public sealed record LibraryTitlesLookupRequest(string? CacheKey, string? LibraryKey, bool? IncludeDisabled, List<string?>? Keys);

/// <param name="Titles">The titles found, by mark key; keys not in the visible list are left out.</param>
public sealed record LibraryTitlesLookupResponse(IReadOnlyDictionary<string, LibraryTitleResult> Titles);

/// <summary>The read-only titles of a playlist for its owner or an accepted viewer; nothing here changes a playlist.</summary>
public static class LibraryTitlesEndpoints
{
  public const int DefaultLimit = 30;
  public const int MaxLimit = 60;
  public const int MaxPerType = 10;
  public const int MaxLookupKeys = 200;
  private static readonly string[] Sorts = ["playlist", "title", "year"];
  private const string SessionExpired = "The playlist session expired. Analyze the source again.";
  private const string TooLarge = "Search is not available for this playlist because it is too large.";
  private static readonly PlaylistContentType[] TypeOrder = [PlaylistContentType.Movies, PlaylistContentType.Series, PlaylistContentType.Live];

  public static IEndpointRouteBuilder MapLibraryTitlesEndpoints(this IEndpointRouteBuilder app)
  {
    // The index is scanned once per distinct question (the index's memo); marks are applied per request.
    app.MapGet("/api/playlists/{id:guid}/library/titles", async (
      Guid id, string? cacheKey, string? libraryKey, bool? includeDisabled, string? group, string? q, string? types,
      bool? hideSeen, bool? liked, int? perType, int? offset, int? limit, string? sort, string? lang,
      ClaimsPrincipal user, AppDbContext db, PlaylistPipeline pipeline, CancellationToken ct) =>
    {
      if (string.IsNullOrWhiteSpace(cacheKey) && string.IsNullOrWhiteSpace(libraryKey))
        return Results.BadRequest(new { error = "Provide a cacheKey or a libraryKey." });
      if (q is not null && q.Length > Limits.MaxSearchQueryLength)
        return Results.BadRequest(new { error = $"Search must be {Limits.MaxSearchQueryLength} characters or less." });
      if (q is not null && PlaylistSearchText.Fold(q).Length < Limits.MinSearchQueryLength)
        return Results.BadRequest(new { error = $"Search needs at least {Limits.MinSearchQueryLength} letters or digits." });
      var maxGroupKeyLength = PlaylistProcessor.MaxGroupKeyLength(pipeline.Cache.MaxGroupTitleLengthChars);
      if (group?.Length > maxGroupKeyLength)
        return Results.BadRequest(new { error = $"The group must be at most {maxGroupKeyLength} characters." });
      if (perType is < 1 or > MaxPerType)
        return Results.BadRequest(new { error = $"perType must be between 1 and {MaxPerType}." });
      if (offset < 0 || limit is < 1 or > MaxLimit)
        return Results.BadRequest(new { error = $"Offset must be 0 or more and limit between 1 and {MaxLimit}." });
      if (sort is not null && !Sorts.Contains(sort, StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = $"Unknown sort. Use {string.Join(", ", Sorts)}." });
      // The Library search's language check and message.
      var languages = (lang ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      if (lang?.Length > ProcessingEndpoints.MaxSearchLanguages * 8 || languages.Length > ProcessingEndpoints.MaxSearchLanguages
        || !languages.All(ProcessingEndpoints.IsLanguageCode))
        return Results.BadRequest(new { error = ProcessingEndpoints.LanguageFormat });
      var wanted = new HashSet<PlaylistContentType>();
      foreach (var name in (types ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
      {
        // Only the names, so numbers such as "1" are refused like any other unknown value.
        var known = Enum.GetNames<PlaylistContentType>().FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (known is null)
          return Results.BadRequest(new { error = $"Unknown content type. Use {string.Join(", ", TypeOrder)}." });
        wanted.Add(Enum.Parse<PlaylistContentType>(known));
      }
      var userId = user.UserId()!;
      if (await LibraryEndpoints.AccessAsync(id, userId, cacheKey, libraryKey, includeDisabled, db, pipeline, ct) is not { } access)
        return Results.NotFound();
      if (access.Index is not { } index) return Missing(pipeline, id, userId, cacheKey, libraryKey, access.IsOwner);
      // A viewer's disabled groups come from the database and are always hidden, so a hidden group answers empty.
      var disabled = new HashSet<string>(access.DisabledGroups, StringComparer.OrdinalIgnoreCase);
      var found = index.MemoTitles(
        new PlaylistTitleQuery(q, string.IsNullOrWhiteSpace(group) ? null : group.Trim(), Languages: languages), disabled, access.HideDisabled);
      var marks = await LibraryMarks.LoadAsync(db, userId, ct);
      var counts = new Dictionary<PlaylistContentType, int>();
      var kept = new List<LibraryTitleResult>();
      var seenHidden = 0;
      foreach (var title in found.Titles)
      {
        var (_, seen, rating, watchlist) = marks.Lookup(title.Name, title.ContentType);
        if (liked == true && rating != 1) continue;
        var isWanted = wanted.Count == 0 || wanted.Contains(title.ContentType);
        if (hideSeen == true && seen == true)
        {
          if (isWanted) seenHidden++;
          continue;
        }
        counts[title.ContentType] = counts.GetValueOrDefault(title.ContentType) + 1;
        if (isWanted) kept.Add(Result(title, seen, rating, watchlist));
      }
      // Sorted per request on the memoised list (OrderBy is stable, so ties keep the source order).
      if (string.Equals(sort, "title", StringComparison.OrdinalIgnoreCase))
        kept = kept.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
      else if (string.Equals(sort, "year", StringComparison.OrdinalIgnoreCase))
        kept = kept.OrderBy(t => t.Year is null).ThenByDescending(t => t.Year).ToList();

      var byType = TypeOrder.Select(t => kept.Where(k => k.ContentType == t.ToString()).ToList()).ToList();
      var (pageOffset, pageLimit) = perType is { } n ? (0, n) : (offset ?? 0, limit ?? DefaultLimit);
      var page = perType is null ? kept.Skip(pageOffset).Take(pageLimit).ToList() : byType.SelectMany(t => t.Take(pageLimit)).ToList();
      var hasMore = perType is null ? pageOffset + page.Count < kept.Count : byType.Any(t => t.Count > pageLimit);
      return Results.Ok(new LibraryTitlesResponse(
        page, kept.Count,
        new LibraryTitleCounts(counts.GetValueOrDefault(PlaylistContentType.Movies), counts.GetValueOrDefault(PlaylistContentType.Series),
          counts.GetValueOrDefault(PlaylistContentType.Live)),
        pageOffset, pageLimit, hasMore, access.IsOwner && access.HideDisabled ? found.HiddenCount : 0, seenHidden,
        index.LanguageCounts));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);

    // Read-only like POST /api/library/search, so no CSRF filter: where marked titles are, in one index scan.
    app.MapPost("/api/playlists/{id:guid}/library/titles/lookup", async (
      Guid id, LibraryTitlesLookupRequest input, ClaimsPrincipal user, AppDbContext db, PlaylistPipeline pipeline, CancellationToken ct) =>
    {
      if (string.IsNullOrWhiteSpace(input.CacheKey) && string.IsNullOrWhiteSpace(input.LibraryKey))
        return Results.BadRequest(new { error = "Provide a cacheKey or a libraryKey." });
      if (input.Keys is not { Count: >= 1 and <= MaxLookupKeys } keys
        || keys.Any(k => string.IsNullOrEmpty(k) || k.Length > MarkKey.MaxKeyLength))
        return Results.BadRequest(new { error = $"Give 1 to {MaxLookupKeys} keys of at most {MarkKey.MaxKeyLength} characters." });
      var userId = user.UserId()!;
      if (await LibraryEndpoints.AccessAsync(id, userId, input.CacheKey, input.LibraryKey, input.IncludeDisabled, db, pipeline, ct) is not { } access)
        return Results.NotFound();
      if (access.Index is not { } index) return Missing(pipeline, id, userId, input.CacheKey, input.LibraryKey, access.IsOwner);
      var disabled = new HashSet<string>(access.DisabledGroups, StringComparer.OrdinalIgnoreCase);
      // Not memoised, so a lookup does not push the pages of the Library out of the memo.
      var found = index.Titles(new PlaylistTitleQuery(Keys: keys.Select(k => k!).ToHashSet(StringComparer.Ordinal)), disabled, access.HideDisabled);
      var marks = await LibraryMarks.LoadAsync(db, userId, ct);
      var titles = new Dictionary<string, LibraryTitleResult>(StringComparer.Ordinal);
      foreach (var title in found.Titles)
      {
        var (_, seen, rating, watchlist) = marks.Lookup(title.Name, title.ContentType);
        titles[title.MarkKey!] = Result(title, seen, rating, watchlist);
      }
      return Results.Ok(new LibraryTitlesLookupResponse(titles));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);

    return app;
  }

  private static LibraryTitleResult Result(PlaylistTitle t, bool? seen, int? rating, bool? watchlist) => new(
    t.Name, t.Title, t.Year, t.ContentType.ToString(), t.MarkKey, t.OutputGroup, t.GroupKey, t.Enabled, t.Entries, t.Groups,
    t.SeasonFirst, t.SeasonLast, t.Episodes, seen, rating, watchlist,
    t.Languages.Audio, t.Languages.Subtitles, t.Languages.Markers, t.Languages.PrefixTags, t.Languages.Source);

  /// <summary>Without an index: 404 for a viewer's editor key or an unknown Library key, else the Library search's errors.</summary>
  private static IResult Missing(PlaylistPipeline pipeline, Guid id, string userId, string? cacheKey, string? libraryKey, bool isOwner)
  {
    var byLibrary = !string.IsNullOrWhiteSpace(libraryKey);
    if (!byLibrary && !isOwner) return Results.NotFound();
    var key = byLibrary ? libraryKey! : cacheKey!;
    var source = key.Length > 1024 ? null : byLibrary ? pipeline.Cache.TryGetLibrarySource(key, id, userId) : pipeline.Cache.TryGetSessionSource(key);
    if (source is null && byLibrary) return Results.NotFound();
    var analysis = source is null ? null : pipeline.Cache.TryGetAnalysis(source);
    return Results.BadRequest(new { error = analysis is null ? SessionExpired : TooLarge });
  }
}
