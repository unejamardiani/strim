using System.Security.Claims;
using Api.Configuration;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

public record PlaylistRequest(
  string Name,
  string? SourceUrl,
  string? SourceName,
  List<string>? DisabledGroups,
  int? TotalChannels,
  int? GroupCount,
  DateTimeOffset? ExpirationUtc,
  string? AccountStatus = null,
  int? AccountMaxConnections = null,
  int? AccountActiveConnections = null,
  DateTimeOffset? AccountCheckedUtc = null);

public record AutoRefreshRequest(bool Enabled);

public record WatchlistGroupRequest(bool Enabled);

/// <summary>A signed-in user's saved playlists and their share links.</summary>
public static class PlaylistEndpoints
{
  public static IEndpointRouteBuilder MapPlaylistEndpoints(this IEndpointRouteBuilder app)
  {
    var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Strim.Playlists");
    var playlists = app.MapGroup("/api/playlists").RequireAuthorization();
    // Every state-changing route below also needs the antiforgery header.
    var mutations = playlists.MapGroup("").RequireCsrfToken();

    playlists.MapGet("", async (ClaimsPrincipal user, AppDbContext db) =>
    {
      var userId = user.UserId();
      var items = await db.Playlists.Where(p => p.OwnerId == userId).ToListAsync();
      // SQLite cannot ORDER BY DateTimeOffset, so sort in memory.
      return Results.Ok(items.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.CreatedAt));
    });

    // Read-only facts for the playlist list, from stored values only. The source address is reduced
    // to its host; the address itself and the share code never leave this route.
    playlists.MapGet("/overview", async (ClaimsPrincipal user, AppDbContext db) =>
    {
      var userId = user.UserId();
      var items = await db.Playlists.Where(p => p.OwnerId == userId).ToListAsync();
      return Results.Ok(items
        .OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.CreatedAt)
        .Select(p => new
        {
          id = p.Id,
          name = p.Name,
          groupCount = p.GroupCount > 0 ? p.GroupCount : (int?)null,
          groupsOn = p.GroupCount > 0
            ? Math.Max(0, p.GroupCount - p.DisabledGroups.Distinct(StringComparer.Ordinal).Count())
            : (int?)null,
          totalChannels = p.TotalChannels > 0 ? p.TotalChannels : (int?)null,
          updatedAt = p.UpdatedAt,
          lastViewedUtc = p.LastViewedUtc,
          lastRefreshedUtc = p.LastRefreshedUtc,
          sourceHost = SourceHost.FromAddress(p.SourceUrl),
        }));
    });

    playlists.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
      await FindOwnedAsync(db, id, user) is { } playlist ? Results.Ok(playlist) : Results.NotFound());

    playlists.MapGet("/{id:guid}/stats", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
      await FindOwnedAsync(db, id, user) is { } playlist
        ? Results.Ok(new { viewCount = playlist.ViewCount, lastViewedUtc = playlist.LastViewedUtc })
        : Results.NotFound())
      .RequireRateLimiting(Security.RateLimits.General);

    // Read-only probe: one details request to the playlist's own host, answered with a sanitised
    // summary of language-like fields. Never returns the address, host, credentials or raw body.
    playlists.MapGet("/{id:guid}/provider-language-probe", async (
      Guid id, string? kind, long? titleId, long? episodeId, ClaimsPrincipal user, AppDbContext db,
      PlaylistSourceFetcher fetcher, HttpContext context) =>
    {
      if (await FindOwnedAsync(db, id, user) is not { } playlist) return Results.NotFound();
      if (kind is not ("vod" or "series") || titleId is not > 0 || (kind == "series" && episodeId is not > 0))
      {
        return Results.BadRequest(new { error = "Give kind=vod or kind=series, a titleId and, for a series, an episodeId." });
      }

      if (ProviderLanguageProbe.BuildEndpoint(playlist.SourceUrl, kind, titleId.Value) is not { } endpoint)
      {
        return Results.UnprocessableEntity(new
        {
          error = "Not supported: this probe works only for Xtream-style get.php playlist links.",
        });
      }

      var answer = await fetcher.FetchSmallTextAsync(
        endpoint.AbsoluteUri, context.RequestAborted, ProviderLanguageProbe.MaxDetailsBytes, noRedirects: true);
      var summary = answer.Success
        ? ProviderLanguageProbe.Summarise(kind, answer.Text, episodeId, ProbeSecrets(endpoint))
        : null;
      return summary is not null
        ? Results.Ok(summary)
        : Results.Problem("The provider did not return usable details for this title.", statusCode: StatusCodes.Status502BadGateway);
    })
      .RequireRateLimiting(Security.RateLimits.General);

    mutations.MapPost("", async (PlaylistRequest input, ClaimsPrincipal user, AppDbContext db) =>
    {
      if (Validate(input) is { } invalid) return invalid;

      var playlist = new Playlist
      {
        Name = input.Name.Trim(),
        SourceUrl = input.SourceUrl,
        SourceName = input.SourceName,
        DisabledGroups = input.DisabledGroups ?? new List<string>(),
        TotalChannels = input.TotalChannels ?? 0,
        GroupCount = input.GroupCount ?? 0,
        ExpirationUtc = input.ExpirationUtc,
        AccountStatus = input.AccountStatus,
        AccountMaxConnections = input.AccountMaxConnections,
        AccountActiveConnections = input.AccountActiveConnections,
        AccountCheckedUtc = input.AccountCheckedUtc,
        ShareCode = SecurityHelpers.GenerateSecureShareCode(),
        OwnerId = user.UserId(),
      };

      db.Playlists.Add(playlist);
      await db.SaveChangesAsync();
      return Results.Created($"/api/playlists/{playlist.Id}", playlist);
    });

    mutations.MapPut("/{id:guid}", async (Guid id, PlaylistRequest input, ClaimsPrincipal user, AppDbContext db, PlaylistFileCache cache) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();
      if (Validate(input) is { } invalid) return invalid;

      var disabledGroups = input.DisabledGroups ?? new List<string>();
      var servedChanges = !string.Equals(playlist.SourceUrl, input.SourceUrl, StringComparison.Ordinal)
        || !new HashSet<string>(playlist.DisabledGroups ?? new List<string>(), StringComparer.Ordinal).SetEquals(disabledGroups);

      playlist.Name = input.Name.Trim();
      playlist.SourceUrl = input.SourceUrl;
      playlist.SourceName = input.SourceName;
      playlist.DisabledGroups = disabledGroups;
      playlist.TotalChannels = input.TotalChannels ?? playlist.TotalChannels;
      playlist.GroupCount = input.GroupCount ?? playlist.GroupCount;
      playlist.ExpirationUtc = input.ExpirationUtc ?? playlist.ExpirationUtc;
      playlist.AccountStatus = input.AccountStatus ?? playlist.AccountStatus;
      playlist.AccountMaxConnections = input.AccountMaxConnections ?? playlist.AccountMaxConnections;
      playlist.AccountActiveConnections = input.AccountActiveConnections ?? playlist.AccountActiveConnections;
      playlist.AccountCheckedUtc = input.AccountCheckedUtc ?? playlist.AccountCheckedUtc;

      await db.SaveChangesAsync();
      // A copy made with the old source or groups must never reach the player link.
      if (servedChanges) cache.ForgetLastGood(id);
      return Results.Ok(playlist);
    });

    mutations.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, PlaylistFileCache cache) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();
      db.Playlists.Remove(playlist);
      await db.SaveChangesAsync();
      cache.ForgetLastGood(id);
      return Results.NoContent();
    });

    mutations.MapPatch("/{id:guid}/toggle-active", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();

      playlist.IsActive = !playlist.IsActive;
      playlist.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      logger.LogInformation(
        "User {UserId} turned the share link of playlist {PlaylistId} {State}",
        user.UserId(), id, playlist.IsActive ? "on" : "off");
      return Results.Ok(new { id = playlist.Id, isActive = playlist.IsActive });
    }).RequireRateLimiting(Security.RateLimits.Sensitive);

    mutations.MapPost("/{id:guid}/regenerate-code", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();

      var oldCode = playlist.ShareCode;
      playlist.ShareCode = SecurityHelpers.GenerateSecureShareCode();
      playlist.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      // Only a truncated old code is logged, enough to correlate without exposing the link.
      var truncated = oldCode?.Length > 8 ? $"{oldCode[..4]}...{oldCode[^4..]}" : "****";
      logger.LogWarning(
        "User {UserId} regenerated share code for playlist {PlaylistId}. Old code invalidated (truncated): {OldCodeTruncated}",
        user.UserId(), id, truncated);
      return Results.Ok(new { id = playlist.Id, shareCode = playlist.ShareCode });
    }).RequireRateLimiting(Security.RateLimits.Sensitive);

    mutations.MapPatch("/{id:guid}/auto-refresh", async (Guid id, AutoRefreshRequest input, ClaimsPrincipal user, AppDbContext db) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();

      playlist.AutoRefreshEnabled = input.Enabled;
      playlist.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.Ok(new { id = playlist.Id, autoRefreshEnabled = playlist.AutoRefreshEnabled, lastRefreshedUtc = playlist.LastRefreshedUtc });
    }).RequireRateLimiting(Security.RateLimits.General);

    // Stores the switch; the player link reads it on its next request and must not serve a copy made with the old value.
    mutations.MapPatch("/{id:guid}/watchlist-group", async (Guid id, WatchlistGroupRequest input, ClaimsPrincipal user, AppDbContext db, PlaylistFileCache cache) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();

      var changed = playlist.WatchlistGroup != input.Enabled;
      playlist.WatchlistGroup = input.Enabled;
      playlist.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      if (changed) cache.ForgetLastGood(id);
      return Results.Ok(new { id = playlist.Id, watchlistGroup = playlist.WatchlistGroup });
    }).RequireRateLimiting(Security.RateLimits.General);

    return app;
  }

  // The host and the credentials, raw and decoded, so no sample value can echo them.
  private static string[] ProbeSecrets(Uri endpoint)
  {
    var secrets = new List<string> { endpoint.Host };
    foreach (var pair in endpoint.Query.TrimStart('?').Split('&'))
    {
      var equals = pair.IndexOf('=');
      if (equals <= 0 || pair[..equals] is not ("username" or "password")) continue;
      secrets.Add(pair[(equals + 1)..]);
      secrets.Add(Uri.UnescapeDataString(pair[(equals + 1)..]));
    }

    return secrets.ToArray();
  }

  /// <summary>Another user's playlist is reported as missing, never as forbidden.</summary>
  private static Task<Playlist?> FindOwnedAsync(AppDbContext db, Guid id, ClaimsPrincipal user)
  {
    var userId = user.UserId();
    return db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);
  }

  private static IResult? Validate(PlaylistRequest input)
  {
    if (string.IsNullOrWhiteSpace(input.Name))
      return Results.BadRequest(new { error = "Name is required." });
    if (input.Name.Length > Limits.MaxPlaylistNameLength)
      return Results.BadRequest(new { error = $"Name must be {Limits.MaxPlaylistNameLength} characters or less." });
    if (input.SourceUrl?.Length > Limits.MaxUrlLength)
      return Results.BadRequest(new { error = $"Source URL must be {Limits.MaxUrlLength} characters or less." });
    if (input.DisabledGroups?.Count > Limits.MaxDisabledGroupsCount)
      return Results.BadRequest(new { error = $"Disabled groups list cannot exceed {Limits.MaxDisabledGroupsCount} items." });
    // The accountstatus column is varchar(32) on PostgreSQL.
    if (input.AccountStatus?.Length > 32)
      return Results.BadRequest(new { error = "Account status must be 32 characters or less." });
    return null;
  }
}
