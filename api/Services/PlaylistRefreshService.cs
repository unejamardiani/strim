using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Every six hours, revalidates the sources of playlists with auto-refresh on so their channel
/// counts stay current and a share link usually finds a warm cache. Also re-reads the provider's
/// account facts (expiry, status, connections) for <c>get.php</c> links.
/// </summary>
public class PlaylistRefreshService(
  IServiceScopeFactory scopeFactory,
  PlaylistPipeline pipeline,
  PlaylistJobGate jobGate,
  PlaylistSourceFetcher fetcher,
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

  internal async Task RefreshPlaylistsAsync(CancellationToken cancellationToken)
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
          playlist.RefreshFailureCount = 0;
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
          await CountFailureAsync(db, playlist, cancellationToken);
        }
      }

      // The account lookup does not touch the playlist files, so it runs after the gate is released.
      await RefreshAccountAsync(db, playlist, cancellationToken);
    }
  }

  /// <summary>
  /// Adds one to the failed refreshes in a row. The reload drops values a failed save left on the
  /// tracked playlist, so only the count is written.
  /// </summary>
  private async Task CountFailureAsync(AppDbContext db, Models.Playlist playlist, CancellationToken cancellationToken)
  {
    try
    {
      await db.Entry(playlist).ReloadAsync(cancellationToken);
      playlist.RefreshFailureCount++;
      await db.SaveChangesAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to count the failed refresh of playlist {PlaylistId}", playlist.Id);
    }
  }

  /// <summary>
  /// Updates the stored account facts from the provider's account answer. An unusable answer
  /// leaves the previously stored values as they are.
  /// </summary>
  private async Task RefreshAccountAsync(AppDbContext db, Models.Playlist playlist, CancellationToken cancellationToken)
  {
    try
    {
      var account = await ProviderAccountLookup.LookUpAsync(fetcher, playlist.SourceUrl, cancellationToken);
      if (account is null) return;

      playlist.ExpirationUtc = account.NeverExpires ? null : account.ExpiresAt ?? playlist.ExpirationUtc;
      playlist.AccountStatus = account.Status is { Length: <= 32 } status ? status : playlist.AccountStatus;
      playlist.AccountMaxConnections = account.MaxConnections ?? playlist.AccountMaxConnections;
      playlist.AccountActiveConnections = account.ActiveConnections ?? playlist.AccountActiveConnections;
      playlist.AccountCheckedUtc = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to refresh account info for playlist {PlaylistId}", playlist.Id);
    }
  }
}
