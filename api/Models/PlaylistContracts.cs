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
  List<ReportVariantGroup> VariantGroups);

public record ReportDuplicateSet(IReadOnlyList<string> Groups, List<ReportEntry> Entries);

public record ReportVariantGroup(string BaseName, IReadOnlyList<string> Groups, List<ReportEntry> Entries);

/// <param name="Group">The entry's group-title.</param>
public record ReportEntry(string Name, string Group, IReadOnlyList<string> QualityWords);

/// <param name="HideDisabled">Leaves out matches in disabled groups and counts them in HiddenCount.</param>
public record PlaylistSearchRequest(string? CacheKey, string? Query, List<string>? DisabledGroups, bool? HideDisabled = null);

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
public record PlaylistSearchResponse(List<PlaylistSearchMatchResult> Matches, bool HasMore, int HiddenCount);

/// <param name="Names">Display names of one page of the group's entries, in playlist order.</param>
/// <param name="Total">Number of entries in the whole group.</param>
public record PlaylistGroupEntriesResponse(string GroupKey, int Offset, int Limit, IReadOnlyList<string> Names, int Total);

public record PlaylistFilterResult(string Text, int TotalChannels, int KeptChannels, DateTimeOffset? ExpirationUtc = null);
