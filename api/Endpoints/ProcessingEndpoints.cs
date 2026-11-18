using Api.Configuration;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Api.Endpoints;

/// <summary>
/// Reading, filtering and serving playlists: the editor's analyze/generate calls, the generated
/// download and the public share link. All of them run through <see cref="PlaylistPipeline"/>
/// and the single-job <see cref="PlaylistJobGate"/>, except the entry search and the group entries,
/// which only read the analysis's in-memory index.
/// </summary>
public static class ProcessingEndpoints
{
  private const string AnalyzePath = "/api/playlist/analyze";
  private const string M3uContentType = "application/x-mpegurl; charset=utf-8";
  private const string SessionExpired = "The playlist session expired. Analyze the source again.";

  /// <summary>
  /// Builds the report of the report endpoint. It observes the request's abort token, so a client that gives up
  /// releases the shared job gate between entries (story #183).
  /// </summary>
  public static PlaylistReportResponse BuildReport(IReadOnlyList<PlaylistEntry> entries, HttpContext context) =>
    PlaylistReport.Build(entries, cancellationToken: context.RequestAborted);

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

    app.MapPost(AnalyzePath, async (AnalyzePlaylistRequest input, HttpContext context, PlaylistPipeline pipeline, PlaylistJobGate jobGate, PlaylistSourceFetcher fetcher) =>
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
          var account = string.IsNullOrWhiteSpace(input.RawText)
            ? await ProviderAccountLookup.LookUpAsync(fetcher, input.SourceUrl, context.RequestAborted)
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
            account is { NeverExpires: true } ? null : account?.ExpiresAt ?? expiration,
            PlaylistProcessor.ToGroupResults(analysis.Groups),
            account?.Status,
            account?.MaxConnections,
            account?.ActiveConnections,
            account is null ? null : DateTimeOffset.UtcNow));
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
      if (InvalidDisabledGroups(input.DisabledGroups, pipeline.Cache) is { } invalidGroups) return invalidGroups;

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
            return Results.BadRequest(new { error = SessionExpired });
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

    // Read-only: reports duplicates and quality variants; nothing is stored and no playlist is changed.
    app.MapPost("/api/playlist/report", async (PlaylistReportRequest input, HttpContext context, PlaylistPipeline pipeline, PlaylistJobGate jobGate) =>
    {
      if (string.IsNullOrWhiteSpace(input.CacheKey) && string.IsNullOrWhiteSpace(input.SourceUrl))
        return Results.BadRequest(new { error = "Provide a cacheKey or sourceUrl." });
      if (input.SourceUrl?.Length > Limits.MaxUrlLength)
        return Results.BadRequest(new { error = $"Source URL must be {Limits.MaxUrlLength} characters or less." });

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
            return Results.BadRequest(new { error = SessionExpired });
          }

          // Reads the file once per pass (PlaylistReport.StreamingPassCount) while the job gate stays held.
          var filePath = source.FilePath;
          var maxLineLengthChars = pipeline.Cache.MaxLineLengthChars;
          return Results.Ok(await PlaylistReport.BuildStreamingAsync(
            ct => PlaylistProcessor.StreamEntriesFromFileAsync(filePath, ct, maxLineLengthChars),
            pipeline.Cache.MaxReportBytes, PlaylistReport.MaxListedSetsPerKind, context.RequestAborted));
        }
        catch (Exception ex)
        {
          return EndpointHelpers.PlaylistFailure(ex, logger, "report");
        }
      }
    }).RequireCsrfToken().RequireRateLimiting(Security.RateLimits.Fetch);

    // Read-only: searches the index the analysis keeps in memory, so it needs no job gate.
    app.MapPost("/api/playlist/search", (PlaylistSearchRequest input, PlaylistPipeline pipeline) =>
    {
      if (string.IsNullOrWhiteSpace(input.CacheKey))
        return Results.BadRequest(new { error = "Provide a cacheKey." });
      if (input.Query?.Length > Limits.MaxSearchQueryLength)
        return Results.BadRequest(new { error = $"Search must be {Limits.MaxSearchQueryLength} characters or less." });
      if (PlaylistSearchText.Fold(input.Query).Length < Limits.MinSearchQueryLength)
        return Results.BadRequest(new { error = $"Search needs at least {Limits.MinSearchQueryLength} letters or digits." });
      if (InvalidDisabledGroups(input.DisabledGroups, pipeline.Cache) is { } invalidGroups) return invalidGroups;

      var source = pipeline.Cache.TryGetSessionSource(input.CacheKey);
      var analysis = source is null ? null : pipeline.Cache.TryGetAnalysis(source);
      if (analysis is null)
        return Results.BadRequest(new { error = SessionExpired });
      if (analysis.SearchIndex is not { } index)
        return Results.BadRequest(new { error = "Search is not available for this playlist because it is too large." });

      var disabled = new HashSet<string>(input.DisabledGroups ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
      var result = index.Search(input.Query, disabled, Limits.MaxSearchResults, input.HideDisabled == true);
      return Results.Ok(new PlaylistSearchResponse(
        result.Matches.Select(m => new PlaylistSearchMatchResult(
          m.Name, m.OutputGroup, m.GroupKey, m.Position, m.ContentType.ToString(), m.Enabled)).ToList(),
        result.HasMore, result.HiddenCount));
    }).RequireRateLimiting(Security.RateLimits.Fetch);

    // Read-only like the search: one page of a group's display names from the same index, so the
    // browser never receives the whole playlist. A limit above the search's page size is clamped.
    app.MapGet("/api/playlist/group-entries", (string? cacheKey, string? group, int? offset, int? limit, PlaylistPipeline pipeline) =>
    {
      if (string.IsNullOrWhiteSpace(cacheKey))
        return Results.BadRequest(new { error = "Provide a cacheKey." });
      var maxGroupKeyLength = PlaylistProcessor.MaxGroupKeyLength(pipeline.Cache.MaxGroupTitleLengthChars);
      if (string.IsNullOrWhiteSpace(group) || group.Length > maxGroupKeyLength)
        return Results.BadRequest(new { error = $"The group must be between 1 and {maxGroupKeyLength} characters." });
      if (offset < 0 || limit < 1)
        return Results.BadRequest(new { error = "Offset must be 0 or more and limit 1 or more." });

      var source = pipeline.Cache.TryGetSessionSource(cacheKey);
      var analysis = source is null ? null : pipeline.Cache.TryGetAnalysis(source);
      if (analysis is null)
        return Results.BadRequest(new { error = SessionExpired });
      if (analysis.SearchIndex is not { } index)
        return Results.BadRequest(new { error = "Search is not available for this playlist because it is too large." });

      var page = Math.Min(limit ?? Limits.MaxSearchResults, Limits.MaxSearchResults);
      var result = index.GroupEntries(group, offset ?? 0, page);
      return Results.Ok(new PlaylistGroupEntriesResponse(group, offset ?? 0, page, result.Names, result.Total));
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

      // Runs after the access check above, so a busy gate never turns a 404 into a copy.
      var job = await jobGate.TryEnterAsync(context.RequestAborted);
      if (job is null)
      {
        return LastGoodOrNull(context, pipeline.Cache, playlist, logger, "job gate busy")
          ?? EndpointHelpers.Busy();
      }

      await using (job)
      {
        try
        {
          var source = await pipeline.GetSourceAsync(playlist.SourceUrl, context.RequestAborted);
          var output = await pipeline.GetOutputAsync(source, playlist.DisabledGroups ?? new List<string>(), context.RequestAborted);

          // Lease the output before persisting anything, so an output that expired in between
          // falls back without having touched the playlist's metadata.
          var lease = pipeline.Cache.TryLeaseOutput(output.OutputKey, ShareFileName(playlist));
          if (lease is null)
          {
            return LastGoodOrNull(context, pipeline.Cache, playlist, logger, "output expired")
              ?? Results.Problem("The generated playlist expired before it could be downloaded.", statusCode: StatusCodes.Status503ServiceUnavailable);
          }

          try
          {
            PlaylistPipeline.CopySourceMetadata(playlist, source);
            playlist.ViewCount++;
            playlist.LastViewedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(context.RequestAborted);
          }
          catch
          {
            await lease.DisposeAsync();
            throw;
          }

          RememberLastGood(pipeline.Cache, playlist.Id, output, logger);
          return new PlaylistFileResult(lease, M3uContentType);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
          return LastGoodOrNull(context, pipeline.Cache, playlist, logger, ex.GetType().Name)
            ?? EndpointHelpers.PlaylistFailure(ex, logger, "serve");
        }
        catch (Exception ex)
        {
          return EndpointHelpers.PlaylistFailure(ex, logger, "serve");
        }
      }
    }).RequireRateLimiting(Security.RateLimits.Share);

    return app;
  }

  private static string ShareFileName(Playlist playlist) =>
    $"{playlist.SourceName ?? playlist.Name ?? "playlist"}-filtered.m3u";

  /// <summary>Pins a served share output as the link's last good copy; a failure here never fails the answer.</summary>
  private static void RememberLastGood(PlaylistFileCache cache, Guid playlistId, PlaylistOutputFile output, ILogger logger)
  {
    try
    {
      cache.RememberLastGood(playlistId, output);
    }
    catch (Exception ex)
    {
      logger.LogWarning("Could not keep a last good copy of playlist {PlaylistId} ({Error})", playlistId, ex.GetType().Name);
    }
  }

  /// <summary>
  /// The share link's last good copy, marked stale, or null when there is none. It leaves the
  /// playlist's source metadata untouched and logs the playlist id only, never the URL or code.
  /// </summary>
  private static IResult? LastGoodOrNull(HttpContext context, PlaylistFileCache cache, Playlist playlist, ILogger logger, string reason)
  {
    if (cache.TryLeaseLastGood(playlist.Id, ShareFileName(playlist)) is not { } lease)
    {
      return null;
    }

    logger.LogWarning("Serving the last good copy of playlist {PlaylistId} instead of a fresh one ({Reason})", playlist.Id, reason);
    context.Response.Headers["X-Strim-Stale"] = "1";
    return new PlaylistFileResult(lease, M3uContentType);
  }

  /// <summary>The disabledGroups limits shared by generate and search.</summary>
  private static IResult? InvalidDisabledGroups(List<string>? disabledGroups, PlaylistFileCache cache)
  {
    if (disabledGroups?.Count > Limits.MaxDisabledGroupsCount)
      return Results.BadRequest(new { error = $"Disabled groups list cannot exceed {Limits.MaxDisabledGroupsCount} items." });
    var maxGroupKeyLength = PlaylistProcessor.MaxGroupKeyLength(cache.MaxGroupTitleLengthChars);
    if (disabledGroups?.Any(group => string.IsNullOrWhiteSpace(group) || group.Length > maxGroupKeyLength) == true)
      return Results.BadRequest(new { error = $"Each disabled group name must be between 1 and {maxGroupKeyLength} characters." });
    return null;
  }
}
