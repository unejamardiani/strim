using Api.Configuration;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Api.Endpoints;

/// <summary>
/// Reading, filtering and serving playlists: the editor's analyze/generate calls, the generated
/// download and the public share link. All of them run through <see cref="PlaylistPipeline"/>
/// and the single-job <see cref="PlaylistJobGate"/>.
/// </summary>
public static class ProcessingEndpoints
{
  private const string AnalyzePath = "/api/playlist/analyze";
  private const string M3uContentType = "application/x-mpegurl; charset=utf-8";

  /// <summary>
  /// Model binding turns RawText JSON into a managed string before the handler and its job gate
  /// run. Bound the body first and admit one analyze request at a time, so a burst of uploads
  /// cannot allocate several 50 MiB strings at once. Remote URL sources stream to disk instead.
  /// </summary>
  public static IApplicationBuilder UseAnalyzeBodyGate(this IApplicationBuilder app)
  {
    var gate = new SemaphoreSlim(1, 1);
    return app.Use(async (context, next) =>
    {
      if (!HttpMethods.IsPost(context.Request.Method) ||
          !string.Equals(context.Request.Path.Value, AnalyzePath, StringComparison.OrdinalIgnoreCase))
      {
        await next();
        return;
      }

      if (context.Request.ContentLength is > Limits.MaxAnalyzeRequestBodySize)
      {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { error = $"Analyze request exceeds the {Limits.MaxAnalyzeRequestBodySize / (1024 * 1024)} MiB body limit." });
        return;
      }

      if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxBodySize)
      {
        maxBodySize.MaxRequestBodySize = Limits.MaxAnalyzeRequestBodySize;
      }

      if (!gate.Wait(0))
      {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new { error = "Playlist analysis is already in progress. Please retry shortly." });
        return;
      }

      try
      {
        await next();
      }
      finally
      {
        gate.Release();
      }
    });
  }

  public static IEndpointRouteBuilder MapProcessingEndpoints(this IEndpointRouteBuilder app)
  {
    var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Strim.Processing");

    app.MapPost(AnalyzePath, async (AnalyzePlaylistRequest input, HttpContext context, PlaylistPipeline pipeline, PlaylistJobGate jobGate) =>
    {
      if (string.IsNullOrWhiteSpace(input.SourceUrl) && string.IsNullOrWhiteSpace(input.RawText))
        return Results.BadRequest(new { error = "Provide a sourceUrl or rawText." });
      if (input.SourceUrl?.Length > Limits.MaxUrlLength)
        return Results.BadRequest(new { error = $"Source URL must be {Limits.MaxUrlLength} characters or less." });
      if (input.RawText?.Length > Limits.MaxPlaylistTextSize)
        return Results.BadRequest(new { error = $"Playlist text exceeds maximum size of {Limits.MaxPlaylistTextSize / (1024 * 1024)} MB." });

      var job = await jobGate.TryEnterAsync(context.RequestAborted);
      if (job is null) return EndpointHelpers.Busy();

      await using (job)
      {
        try
        {
          var cache = pipeline.Cache;
          PlaylistSourceFile source;
          if (string.IsNullOrWhiteSpace(input.RawText))
          {
            source = await pipeline.GetSourceAsync(input.SourceUrl!, context.RequestAborted);
          }
          else
          {
            var raw = await cache.WriteRawTextAsync(input.RawText, context.RequestAborted);
            source = cache.StoreRawText(Guid.NewGuid().ToString("N"), raw);
          }

          var analysis = await pipeline.GetAnalysisAsync(source, context.RequestAborted);
          var cacheKey = cache.CreateSession(source);

          var expiration = Uri.TryCreate(input.SourceUrl, UriKind.Absolute, out var sourceUri)
            ? PlaylistProcessor.TryExtractExpiration(sourceUri)
            : null;
          var friendlyName = string.IsNullOrWhiteSpace(input.SourceName)
            ? PlaylistProcessor.DeriveNameFromUrl(input.SourceUrl)
            : input.SourceName.Trim();

          return Results.Ok(new AnalyzePlaylistResponse(
            cacheKey,
            input.SourceUrl,
            friendlyName,
            analysis.TotalChannels,
            analysis.Groups.Count,
            expiration,
            PlaylistProcessor.ToGroupResults(analysis.Groups)));
        }
        catch (Exception ex)
        {
          return EndpointHelpers.PlaylistFailure(ex, logger, "analyze");
        }
      }
    }).RequireRateLimiting(Security.RateLimits.Fetch);

    app.MapPost("/api/playlist/generate", async (GeneratePlaylistRequest input, HttpContext context, PlaylistPipeline pipeline, PlaylistJobGate jobGate) =>
    {
      if (string.IsNullOrWhiteSpace(input.CacheKey) && string.IsNullOrWhiteSpace(input.SourceUrl))
        return Results.BadRequest(new { error = "Provide a cacheKey or sourceUrl." });
      if (input.SourceUrl?.Length > Limits.MaxUrlLength)
        return Results.BadRequest(new { error = $"Source URL must be {Limits.MaxUrlLength} characters or less." });
      if (input.DisabledGroups?.Count > Limits.MaxDisabledGroupsCount)
        return Results.BadRequest(new { error = $"Disabled groups list cannot exceed {Limits.MaxDisabledGroupsCount} items." });
      var maxGroupKeyLength = PlaylistProcessor.MaxGroupKeyLength(pipeline.Cache.MaxGroupTitleLengthChars);
      if (input.DisabledGroups?.Any(group => string.IsNullOrWhiteSpace(group) || group.Length > maxGroupKeyLength) == true)
        return Results.BadRequest(new { error = $"Each disabled group name must be between 1 and {maxGroupKeyLength} characters." });

      var job = await jobGate.TryEnterAsync(context.RequestAborted);
      if (job is null) return EndpointHelpers.Busy();

      await using (job)
      {
        try
        {
          var source = string.IsNullOrWhiteSpace(input.CacheKey) ? null : pipeline.Cache.TryGetSessionSource(input.CacheKey);
          if (source is null && !string.IsNullOrWhiteSpace(input.SourceUrl))
          {
            source = await pipeline.GetSourceAsync(input.SourceUrl, context.RequestAborted);
          }
          if (source is null)
          {
            return Results.BadRequest(new { error = "The playlist session expired. Analyze the source again." });
          }

          var output = await pipeline.GetOutputAsync(source, input.DisabledGroups ?? new List<string>(), context.RequestAborted);
          return Results.Ok(new GeneratePlaylistResponse(output.OutputKey, output.TotalChannels, output.KeptChannels));
        }
        catch (Exception ex)
        {
          return EndpointHelpers.PlaylistFailure(ex, logger, "generate");
        }
      }
    }).RequireRateLimiting(Security.RateLimits.Fetch);

    // The random, short-lived output key is a capability issued only after a source was processed,
    // so this stays anonymous and works for editors who are not signed in.
    app.MapGet("/api/playlist/output/{outputKey}", (string outputKey, PlaylistFileCache cache) =>
      cache.TryLeaseOutput(outputKey) is { } lease
        ? new PlaylistFileResult(lease, M3uContentType)
        : Results.NotFound())
      .RequireRateLimiting(Security.RateLimits.Fetch);

    app.MapGet("/api/playlists/sample", () => Results.Ok(new { text = SamplePlaylist.Build() }))
      .RequireRateLimiting(Security.RateLimits.General);

    // Public share link used by IPTV players. Every failure to match is the same 404 so that
    // playlist IDs cannot be probed.
    app.MapGet("/api/playlists/{id:guid}/share/{code}", async (Guid id, string code, HttpContext context, AppDbContext db, PlaylistPipeline pipeline, PlaylistJobGate jobGate) =>
    {
      var playlist = await db.Playlists.FindAsync(id);
      if (playlist is null || !playlist.IsActive || !string.Equals(playlist.ShareCode, code, StringComparison.Ordinal))
      {
        return Results.NotFound();
      }
      if (string.IsNullOrWhiteSpace(playlist.SourceUrl))
      {
        return Results.BadRequest(new { error = "Playlist is missing sourceUrl." });
      }

      var job = await jobGate.TryEnterAsync(context.RequestAborted);
      if (job is null) return EndpointHelpers.Busy();

      await using (job)
      {
        try
        {
          var source = await pipeline.GetSourceAsync(playlist.SourceUrl, context.RequestAborted);
          var output = await pipeline.GetOutputAsync(source, playlist.DisabledGroups ?? new List<string>(), context.RequestAborted);

          PlaylistPipeline.CopySourceMetadata(playlist, source);
          playlist.ViewCount++;
          playlist.LastViewedUtc = DateTimeOffset.UtcNow;
          await db.SaveChangesAsync(context.RequestAborted);

          var fileName = $"{playlist.SourceName ?? playlist.Name ?? "playlist"}-filtered.m3u";
          return pipeline.Cache.TryLeaseOutput(output.OutputKey, fileName) is { } lease
            ? new PlaylistFileResult(lease, M3uContentType)
            : Results.Problem("The generated playlist expired before it could be downloaded.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
          return EndpointHelpers.PlaylistFailure(ex, logger, "serve");
        }
      }
    }).RequireRateLimiting(Security.RateLimits.Share);

    return app;
  }
}
