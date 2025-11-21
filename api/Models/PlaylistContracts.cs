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

public record GroupResult(string Name, int Count);

public record GeneratePlaylistRequest(string? SourceUrl, string? CacheKey, List<string>? DisabledGroups);

public record GeneratePlaylistResponse(string FilteredText, int TotalChannels, int KeptChannels);

public record PlaylistFilterResult(string Text, int TotalChannels, int KeptChannels, DateTimeOffset? ExpirationUtc = null);
