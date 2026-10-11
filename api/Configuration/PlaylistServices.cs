using System.Net.Http.Headers;
using Api.Services;

namespace Api.Configuration;

/// <summary>Playlist download, cache and processing services.</summary>
public static class PlaylistServices
{
  public const string FetcherClient = "fetcher";

  public static IServiceCollection AddStrimPlaylistServices(this IServiceCollection services, IConfiguration configuration)
  {
    // Large playlists are spooled to private disk files; only metadata stays in memory.
    services.Configure<PlaylistCacheOptions>(configuration.GetSection(PlaylistCacheOptions.SectionName));
    services.AddSingleton<PlaylistFileCache>();
    services.AddSingleton<PlaylistSourceFetcher>();
    services.AddSingleton<PlaylistJobGate>();
    services.AddSingleton<PlaylistPipeline>();
    services.AddHostedService<PlaylistCacheCleanupService>();
    services.AddHostedService<PlaylistRefreshService>();

    var headerTimeout = TimeSpan.FromSeconds(Math.Max(
      1,
      configuration.GetValue<int?>($"{PlaylistCacheOptions.SectionName}:HeaderTimeoutSeconds") ?? 15));

    services.AddHttpClient(FetcherClient, client =>
      {
        // With ResponseHeadersRead this bounds DNS/connect/header stalls; PlaylistSourceFetcher owns
        // the longer, cancellable body-read timeout for legitimately large downloads.
        client.Timeout = headerTimeout;
        // Some IPTV providers answer 403 to the default .NET user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
          "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-mpegurl"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
      })
      .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
      {
        // Redirects are followed by PlaylistSourceFetcher so each target is validated.
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) =>
          SecurityHelpers.ConnectToPublicAddressAsync(context.DnsEndPoint, cancellationToken),
      });

    return services;
  }
}
