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
public record GroupResult(string Name, int Count, string GroupTitle, string ContentType);

public record GeneratePlaylistRequest(string? SourceUrl, string? CacheKey, List<string>? DisabledGroups);

public record GeneratePlaylistResponse(string OutputKey, int TotalChannels, int KeptChannels);

public record PlaylistFilterResult(string Text, int TotalChannels, int KeptChannels, DateTimeOffset? ExpirationUtc = null);
