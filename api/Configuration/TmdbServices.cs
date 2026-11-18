using Api.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Api.Configuration;

/// <summary>The outbound TMDB client; the token comes from the TMDB_READ_ACCESS_TOKEN environment variable.</summary>
public static class TmdbServices
{
  public static IServiceCollection AddStrimTmdb(this IServiceCollection services)
  {
    services.TryAddSingleton(TimeProvider.System);
    services.AddSingleton<TmdbClient>();
    services.AddHttpClient(TmdbClient.HttpClientName, client =>
      {
        client.BaseAddress = TmdbClient.BaseAddress;
        // TmdbClient enforces its own shorter timeout; this is only the outer bound.
        client.Timeout = TimeSpan.FromSeconds(15);
      })
      // The client logs status codes and error types itself; no default request logging.
      .RemoveAllLoggers()
      .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler);
    return services;
  }

  internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false, UseProxy = false };
}
