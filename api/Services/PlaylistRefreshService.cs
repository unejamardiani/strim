using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class PlaylistRefreshService : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly PlaylistFileCache _cache;
  private readonly PlaylistSourceFetcher _fetcher;
  private readonly PlaylistJobGate _jobGate;
  private readonly ILogger<PlaylistRefreshService> _logger;

  public PlaylistRefreshService(
    IServiceScopeFactory scopeFactory,
    PlaylistFileCache cache,
    PlaylistSourceFetcher fetcher,
    PlaylistJobGate jobGate,
    ILogger<PlaylistRefreshService> logger)
  {
    _scopeFactory = scopeFactory;
    _cache = cache;
    _fetcher = fetcher;
    _jobGate = jobGate;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("PlaylistRefreshService started");

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await RefreshPlaylists(stoppingToken);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error during playlist refresh cycle");
      }

      await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
    }
  }

  private async Task RefreshPlaylists(CancellationToken cancellationToken)
  {
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var playlists = await db.Playlists
      .Where(p => p.AutoRefreshEnabled && p.SourceUrl != null && p.SourceUrl != "")
      .ToListAsync(cancellationToken);

    _logger.LogInformation("Refreshing {Count} playlists", playlists.Count);

    foreach (var playlist in playlists)
    {
      var job = await _jobGate.TryEnterAsync(cancellationToken);
      if (job is null)
      {
        _logger.LogInformation("Skipping automatic refresh for playlist {PlaylistId}; playlist processor is busy", playlist.Id);
        continue;
      }

      await using (job)
      {
        try
        {
          var source = await FetchAndCacheSourceAsync(playlist.SourceUrl!, cancellationToken);
          var analysis = _cache.TryGetAnalysis(source)
            ?? await PlaylistProcessor.AnalyzeFileAsync(
              source.FilePath,
              cancellationToken,
              _cache.MaxLineLengthChars,
              _cache.MaxGroupCount,
              _cache.MaxGroupTitleLengthChars,
              _cache.MaxGroupMetadataBytes);
          _cache.StoreAnalysis(source, analysis);

          var oldCount = playlist.TotalChannels;
          var oldHash = playlist.SourceHash;
          playlist.LastRefreshedUtc = DateTimeOffset.UtcNow;
          playlist.TotalChannels = analysis.TotalChannels;
          playlist.GroupCount = analysis.Groups.Count;
          playlist.SourceHash = source.ContentHash;
          playlist.SourceETag = ClampDatabaseString(source.ETag, 512);
          playlist.SourceLastModifiedUtc = source.LastModifiedUtc;
          playlist.SourceLengthBytes = source.LengthBytes;
          playlist.SourceCheckedUtc = DateTimeOffset.UtcNow;
          await db.SaveChangesAsync(cancellationToken);

          if (!string.Equals(oldHash, source.ContentHash, StringComparison.OrdinalIgnoreCase) || oldCount != analysis.TotalChannels)
          {
            _logger.LogInformation(
              "Playlist {PlaylistId} refreshed: {OldChannels} -> {NewChannels} channels, sha256 {HashPrefix}",
              playlist.Id,
              oldCount,
              analysis.TotalChannels,
              source.ContentHash[..12]);
          }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Failed to refresh playlist {PlaylistId} ({Name})", playlist.Id, playlist.Name);
        }
      }
    }
  }

  private async Task<PlaylistSourceFile> FetchAndCacheSourceAsync(string sourceUrl, CancellationToken cancellationToken)
  {
    var existing = _cache.TryGetSource(sourceUrl);
    var fetched = await _fetcher.FetchAsync(sourceUrl, existing, cancellationToken);
    if (fetched.NotModified)
    {
      if (existing is null)
      {
        throw new HttpRequestException("The playlist source returned an unusable cache validation response.");
      }

      _cache.TouchNotModified(existing, fetched.ETag, fetched.LastModifiedUtc);
      return existing;
    }

    return _cache.StoreDownloaded(sourceUrl, fetched.DownloadedFile
      ?? throw new InvalidOperationException("Playlist source did not return a body."));
  }

  private static string? ClampDatabaseString(string? value, int maxLength) =>
    string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
