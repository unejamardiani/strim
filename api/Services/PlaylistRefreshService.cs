using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class PlaylistRefreshService : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<PlaylistRefreshService> _logger;

  public PlaylistRefreshService(IServiceScopeFactory scopeFactory, ILogger<PlaylistRefreshService> logger)
  {
    _scopeFactory = scopeFactory;
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

  private async Task RefreshPlaylists(CancellationToken ct)
  {
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

    var playlists = await db.Playlists
      .Where(p => p.AutoRefreshEnabled && p.SourceUrl != null && p.SourceUrl != "")
      .ToListAsync(ct);

    _logger.LogInformation("Refreshing {Count} playlists", playlists.Count);

    foreach (var playlist in playlists)
    {
      try
      {
        var client = httpClientFactory.CreateClient("fetcher");
        using var res = await client.GetAsync(playlist.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync(ct);

        var channelCount = CountChannels(text);
        var changed = channelCount != playlist.TotalChannels;

        playlist.LastRefreshedUtc = DateTimeOffset.UtcNow;
        playlist.TotalChannels = channelCount;
        await db.SaveChangesAsync(ct);

        if (changed)
          _logger.LogInformation("Playlist {Id} ({Name}) changed: {Old} → {New} channels",
            playlist.Id, playlist.Name, playlist.TotalChannels, channelCount);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to refresh playlist {Id} ({Name})", playlist.Id, playlist.Name);
      }
    }
  }

  private static int CountChannels(string m3uText)
  {
    var count = 0;
    foreach (var line in m3uText.Split('\n'))
    {
      if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
        count++;
    }
    return count;
  }
}
