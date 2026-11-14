namespace Api.Models;

public record AnalyzePlaylistRequest(string? SourceUrl, string? RawText, string? SourceName);

public record AnalyzePlaylistResponse(
  string CacheKey,
  string? SourceUrl,
  string? SourceName,
  int TotalChannels,
  int GroupCount,
  DateTimeOffset? ExpirationUtc,
  List<GroupResult> Groups);

/// <param name="Name">Filter key sent back in disabledGroups.</param>
/// <param name="GroupTitle">The provider's group-title; differs from Name when one title is split by content type.</param>
/// <param name="ContentType">Live, Movies or Series.</param>
/// <param name="IsAdult">The group title looks like adult content (AdultGroupRule); informational, never used for filtering.</param>
public record GroupResult(string Name, int Count, string GroupTitle, string ContentType, bool IsAdult);

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

public record PlaylistFilterResult(string Text, int TotalChannels, int KeptChannels, DateTimeOffset? ExpirationUtc = null);
