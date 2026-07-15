namespace Api.Services;

/// <summary>Expires temporary cache files even during periods without new playlist requests.</summary>
public sealed class PlaylistCacheCleanupService : BackgroundService
{
  private readonly PlaylistFileCache _cache;
  private readonly ILogger<PlaylistCacheCleanupService> _logger;

  public PlaylistCacheCleanupService(PlaylistFileCache cache, ILogger<PlaylistCacheCleanupService> logger)
  {
    _cache = cache;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
    try
    {
      while (await timer.WaitForNextTickAsync(stoppingToken))
      {
        _cache.Cleanup();
      }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // Normal graceful shutdown.
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Playlist cache cleanup service stopped unexpectedly");
    }
  }
}
