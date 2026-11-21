using Api.Configuration;
using Api.Services;

namespace Api.Endpoints;

public sealed record LibraryProvider(int Id, string Name);

/// <param name="TmdbData">Whether a list came from TMDB, so the browser shows the credit line.</param>
public sealed record LibraryProvidersResponse(string Region, IReadOnlyList<LibraryProvider> Providers, bool TmdbData);

/// <summary>The streaming services TMDB knows for <see cref="TmdbClient.WatchRegion"/>; read-only, stores nothing.</summary>
public static class LibraryProvidersEndpoints
{
  public const int MaxProviders = 40;

  public static IEndpointRouteBuilder MapLibraryProvidersEndpoints(this IEndpointRouteBuilder app)
  {
    // Only the two fixed provider paths with the fixed region go to TMDB; nothing about the user or a playlist.
    app.MapGet("/api/library/providers", async (TmdbClient tmdb, CancellationToken ct) =>
    {
      if (!tmdb.IsEnabled) return Results.NotFound();
      var (providers, tmdbData) = await ListAsync(tmdb, ct);
      return Results.Ok(new LibraryProvidersResponse(TmdbClient.WatchRegion, providers, tmdbData));
    }).RequireAuthorization().RequireRateLimiting(Security.RateLimits.General);
    return app;
  }

  /// <summary>
  /// The film and series lists merged by id (the film list's name wins), entries without a name or a positive id
  /// dropped, ordered by the lower regional priority (missing last) and then by name, at most <see cref="MaxProviders"/>.
  /// TmdbData is false when neither list answered Ok. Nothing is retried.
  /// </summary>
  public static async Task<(IReadOnlyList<LibraryProvider> Providers, bool TmdbData)> ListAsync(
    TmdbClient tmdb, CancellationToken ct)
  {
    var movies = await tmdb.GetMovieProvidersAsync(ct);
    var tv = await tmdb.GetTvProvidersAsync(ct);
    var merged = new Dictionary<int, (string Name, int? Priority)>();
    foreach (var answer in new[] { movies, tv })
    {
      if (answer.Status != TmdbStatus.Ok) continue;
      foreach (var p in answer.Value!.Results ?? [])
      {
        if (p is null || p.ProviderId <= 0 || string.IsNullOrWhiteSpace(p.ProviderName)) continue;
        var priority = p.DisplayPriorities?.TryGetValue(TmdbClient.WatchRegion, out var value) == true ? value : (int?)null;
        merged[p.ProviderId] = merged.TryGetValue(p.ProviderId, out var known)
          ? (known.Name, known.Priority is int k && priority is int n ? Math.Min(k, n) : known.Priority ?? priority)
          : (p.ProviderName.Trim(), priority);
      }
    }
    var providers = merged.OrderBy(p => p.Value.Priority is null).ThenBy(p => p.Value.Priority)
      .ThenBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase)
      .Take(MaxProviders).Select(p => new LibraryProvider(p.Key, p.Value.Name)).ToList();
    return (providers, movies.Status == TmdbStatus.Ok || tv.Status == TmdbStatus.Ok);
  }
}
