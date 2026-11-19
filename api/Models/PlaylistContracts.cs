namespace Api.Models;

public record AnalyzePlaylistRequest(string? SourceUrl, string? RawText, string? SourceName);

/// <param name="ExpirationUtc">From the provider's account answer when it has one, otherwise from URL parameters.</param>
/// <param name="AccountStatus">Account fields come from the provider's player_api.php; all null when it gave no usable answer.</param>
/// <param name="AccountCheckedUtc">When the account answer was read.</param>
public record AnalyzePlaylistResponse(
  string CacheKey,
  string? SourceUrl,
  string? SourceName,
  int TotalChannels,
  int GroupCount,
  DateTimeOffset? ExpirationUtc,
  List<GroupResult> Groups,
  string? AccountStatus = null,
  int? MaxConnections = null,
  int? ActiveConnections = null,
  DateTimeOffset? AccountCheckedUtc = null);

/// <param name="Name">Filter key sent back in disabledGroups.</param>
/// <param name="GroupTitle">The provider's group-title; differs from Name when one title is split by content type.</param>
/// <param name="ContentType">Live, Movies or Series.</param>
/// <param name="IsAdult">The group title looks like adult content (AdultGroupRule); informational, never used for filtering.</param>
/// <param name="Words">Search words of the group title (see GroupSearchWords); empty for entries without a group-title.</param>
/// <param name="SampleTitles">Up to three entry display names of this group in playlist order; entries without a name are skipped.</param>
public record GroupResult(
  string Name,
  int Count,
  string GroupTitle,
  string ContentType,
  bool IsAdult,
  IReadOnlyList<string> Words,
  IReadOnlyList<string> SampleTitles);

public record GeneratePlaylistRequest(string? SourceUrl, string? CacheKey, List<string>? DisabledGroups);

public record GeneratePlaylistResponse(string OutputKey, int TotalChannels, int KeptChannels);

public record PlaylistReportRequest(string? SourceUrl, string? CacheKey);

/// <param name="Truncated">True when more sets exist than are listed; the counts always cover the whole list.</param>
public record PlaylistReportResponse(
  int TotalEntries,
  int DuplicateSetCount,
  int DuplicateEntryCount,
  int VariantGroupCount,
  int VariantEntryCount,
  bool Truncated,
  List<ReportDuplicateSet> DuplicateSets,
  List<ReportVariantGroup> VariantGroups,
  List<ReportGroupCount> GroupCounts);

/// <summary>
/// How many entries of one group are in any duplicate set or variant group, over the whole list. Groups without
/// either are left out.
/// </summary>
/// <param name="Group">The group key, the same as the analysis group's <c>Name</c>.</param>
public record ReportGroupCount(string Group, int DuplicateEntryCount, int VariantEntryCount);

public record ReportDuplicateSet(IReadOnlyList<string> Groups, List<ReportEntry> Entries);

public record ReportVariantGroup(string BaseName, IReadOnlyList<string> Groups, List<ReportEntry> Entries);

/// <param name="Group">The entry's group-title.</param>
public record ReportEntry(string Name, string Group, IReadOnlyList<string> QualityWords);

/// <param name="HideDisabled">Leaves out matches in disabled groups and counts them in HiddenCount.</param>
/// <param name="ContentTypes">Names of <c>PlaylistContentType</c> to keep, combined as OR; absent or empty keeps all.</param>
/// <param name="Lang">
/// Comma-separated languages (de) or prefix tags (UK, EX-YU), combined as OR and compared case-insensitively;
/// an entry matches when its audio or subtitle languages or its prefix tags hold one. Absent or empty keeps all.
/// </param>
public record PlaylistSearchRequest(
  string? CacheKey, string? Query, List<string>? DisabledGroups, bool? HideDisabled = null, List<string>? ContentTypes = null,
  string? Lang = null);

/// <param name="Group">The group-title the generated file writes for the entry.</param>
/// <param name="GroupKey">Filter key sent back in disabledGroups.</param>
/// <param name="Position">1-based position within <paramref name="Group"/> in source order.</param>
/// <param name="AudioLanguages">ISO 639-1 audio languages read from the entry name or group title.</param>
/// <param name="SubtitleLanguages">ISO 639-1 subtitle languages (VOSTFR, "DE SUB"), kept apart from audio.</param>
/// <param name="Markers">Markers such as MULTI.</param>
/// <param name="PrefixTags">Prefix tags kept as written (UK, EX-YU), never mapped to a language.</param>
/// <param name="LanguageSource">"title" when any of the lists holds a value; null when nothing is known.</param>
public record PlaylistSearchMatchResult(
  string Name, string Group, string GroupKey, int Position, string ContentType, bool Enabled,
  IReadOnlyList<string> AudioLanguages, IReadOnlyList<string> SubtitleLanguages, IReadOnlyList<string> Markers,
  IReadOnlyList<string> PrefixTags, string? LanguageSource);

/// <param name="HiddenCount">Matches left out because hideDisabled was set; 0 otherwise.</param>
/// <param name="Total">Every match after the filters, also those past the page; hidden matches not included.</param>
/// <param name="Languages">The values lang accepts that occur in the playlist, with their entry counts.</param>
public record PlaylistSearchResponse(
  List<PlaylistSearchMatchResult> Matches, bool HasMore, int HiddenCount, int Total,
  IReadOnlyList<Api.Services.PlaylistSearchLanguageCount> Languages);

/// <param name="Names">Display names of one page of the group's entries, in playlist order.</param>
/// <param name="Total">Number of entries in the whole group.</param>
public record PlaylistGroupEntriesResponse(string GroupKey, int Offset, int Limit, IReadOnlyList<string> Names, int Total);

/// <summary>The Library search: the editor search's fields and the caller's mark options.</summary>
/// <param name="HideSeen">Leaves out titles the caller marked seen and counts them in SeenHiddenCount.</param>
/// <param name="Liked">Returns only titles the caller gave a thumbs up.</param>
public record LibrarySearchRequest(
  string? CacheKey, string? Query, List<string>? DisabledGroups, bool? HideDisabled = null, List<string>? ContentTypes = null,
  string? Lang = null, bool? HideSeen = null, bool? Liked = null)
{
  public PlaylistSearchRequest ToSearch() => new(CacheKey, Query, DisabledGroups, HideDisabled, ContentTypes, Lang);
}

/// <summary>The playlist-scoped Library search: <see cref="LibrarySearchRequest"/> with a Library key.</summary>
/// <param name="DisabledGroups">Read for the owner only; a viewer's hidden groups come from the database.</param>
public record PlaylistLibrarySearchRequest(
  string? LibraryKey, string? Query, List<string>? DisabledGroups, bool? HideDisabled = null, List<string>? ContentTypes = null,
  string? Lang = null, bool? HideSeen = null, bool? Liked = null)
{
  public PlaylistSearchRequest ToSearch() => new(LibraryKey, Query, DisabledGroups, HideDisabled, ContentTypes, Lang);
}

/// <summary>A search match with the caller's mark (see <see cref="PlaylistSearchMatchResult"/>).</summary>
/// <param name="MarkKey">The film or series mark key; null for live channels and names without a title.</param>
/// <param name="Rating">-1, 0 or 1. Seen and Rating are null when the caller has no mark on the title.</param>
public record LibrarySearchMatchResult(
  string Name, string Group, string GroupKey, int Position, string ContentType, bool Enabled,
  IReadOnlyList<string> AudioLanguages, IReadOnlyList<string> SubtitleLanguages, IReadOnlyList<string> Markers,
  IReadOnlyList<string> PrefixTags, string? LanguageSource, string? MarkKey, bool? Seen, int? Rating);

/// <param name="SeenHiddenCount">Matches left out because hideSeen was set; 0 otherwise.</param>
public record LibrarySearchResponse(
  List<LibrarySearchMatchResult> Matches, bool HasMore, int HiddenCount, int SeenHiddenCount, int Total,
  IReadOnlyList<Api.Services.PlaylistSearchLanguageCount> Languages);

/// <summary>One group entry with the caller's mark; the fields as in <see cref="LibrarySearchMatchResult"/>.</summary>
/// <param name="Group">The group-title the player shows for the entry.</param>
public record LibraryGroupEntry(string Name, string ContentType, string? MarkKey, bool? Seen, int? Rating, string? Group = null);

/// <summary>A page as in <see cref="PlaylistGroupEntriesResponse"/>, with Entries holding the marks.</summary>
/// <param name="Total">Entries of the group after the mark options; SeenHiddenCount as in the Library search.</param>
public record LibraryGroupEntriesResponse(
  string GroupKey, int Offset, int Limit, IReadOnlyList<string> Names, IReadOnlyList<LibraryGroupEntry> Entries,
  int Total, int SeenHiddenCount);

public record PlaylistFilterResult(string Text, int TotalChannels, int KeptChannels, DateTimeOffset? ExpirationUtc = null);
