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

public record PlaylistSearchRequest(string? CacheKey, string? Query, List<string>? DisabledGroups);

/// <param name="Group">The group-title the generated file writes for the entry.</param>
/// <param name="GroupKey">Filter key sent back in disabledGroups.</param>
/// <param name="Position">1-based position within <paramref name="Group"/> in source order.</param>
public record PlaylistSearchMatchResult(string Name, string Group, string GroupKey, int Position, string ContentType, bool Enabled);

public record PlaylistSearchResponse(List<PlaylistSearchMatchResult> Matches, bool HasMore);

public record PlaylistFilterResult(string Text, int TotalChannels, int KeptChannels, DateTimeOffset? ExpirationUtc = null);
