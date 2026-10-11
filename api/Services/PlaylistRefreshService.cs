using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Every six hours, revalidates the sources of playlists with auto-refresh on so their channel
/// counts stay current and a share link usually finds a warm cache.
/// </summary>
public class PlaylistRefreshService(
  IServiceScopeFactory scopeFactory,
  PlaylistPipeline pipeline,
  PlaylistJobGate jobGate,
  ILogger<PlaylistRefreshService> logger) : BackgroundService
{
  private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation("PlaylistRefreshService started");

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await RefreshPlaylistsAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Error during playlist refresh cycle");
      }

      try
      {
        await Task.Delay(Interval, stoppingToken);
      }
      catch (OperationCanceledException)
      {
        return;
      }
    }
  }

  private async Task RefreshPlaylistsAsync(CancellationToken cancellationToken)
  {
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var playlists = await db.Playlists
      .Where(p => p.AutoRefreshEnabled && p.SourceUrl != null && p.SourceUrl != "")
      .ToListAsync(cancellationToken);

    logger.LogInformation("Refreshing {Count} playlists", playlists.Count);

    foreach (var playlist in playlists)
    {
      var job = await jobGate.TryEnterAsync(cancellationToken);
      if (job is null)
      {
        logger.LogInformation("Skipping automatic refresh for playlist {PlaylistId}; playlist processor is busy", playlist.Id);
        continue;
      }

      await using (job)
      {
        try
        {
          var source = await pipeline.GetSourceAsync(playlist.SourceUrl!, cancellationToken, reuseRecent: false);
          var analysis = await pipeline.GetAnalysisAsync(source, cancellationToken);

          var oldCount = playlist.TotalChannels;
          var oldHash = playlist.SourceHash;
          playlist.LastRefreshedUtc = DateTimeOffset.UtcNow;
          playlist.TotalChannels = analysis.TotalChannels;
          playlist.GroupCount = analysis.Groups.Count;
          PlaylistPipeline.CopySourceMetadata(playlist, source);
          await db.SaveChangesAsync(cancellationToken);

          if (!string.Equals(oldHash, source.ContentHash, StringComparison.OrdinalIgnoreCase) || oldCount != analysis.TotalChannels)
          {
            logger.LogInformation(
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
          logger.LogWarning(ex, "Failed to refresh playlist {PlaylistId} ({Name})", playlist.Id, playlist.Name);
        }
      }
    }
  }
}
