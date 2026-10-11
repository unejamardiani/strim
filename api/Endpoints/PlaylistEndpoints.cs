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
  string? ShareCode);

public record AutoRefreshRequest(bool Enabled);

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

    playlists.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
      await FindOwnedAsync(db, id, user) is { } playlist ? Results.Ok(playlist) : Results.NotFound());

    playlists.MapGet("/{id:guid}/stats", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
      await FindOwnedAsync(db, id, user) is { } playlist
        ? Results.Ok(new { viewCount = playlist.ViewCount, lastViewedUtc = playlist.LastViewedUtc })
        : Results.NotFound())
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
        ShareCode = string.IsNullOrWhiteSpace(input.ShareCode) ? SecurityHelpers.GenerateSecureShareCode() : input.ShareCode.Trim(),
        OwnerId = user.UserId(),
      };

      db.Playlists.Add(playlist);
      await db.SaveChangesAsync();
      return Results.Created($"/api/playlists/{playlist.Id}", playlist);
    });

    mutations.MapPut("/{id:guid}", async (Guid id, PlaylistRequest input, ClaimsPrincipal user, AppDbContext db) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();
      if (Validate(input) is { } invalid) return invalid;

      playlist.Name = input.Name.Trim();
      playlist.SourceUrl = input.SourceUrl;
      playlist.SourceName = input.SourceName;
      playlist.DisabledGroups = input.DisabledGroups ?? new List<string>();
      playlist.TotalChannels = input.TotalChannels ?? playlist.TotalChannels;
      playlist.GroupCount = input.GroupCount ?? playlist.GroupCount;
      playlist.ExpirationUtc = input.ExpirationUtc ?? playlist.ExpirationUtc;
      if (!string.IsNullOrWhiteSpace(input.ShareCode))
      {
        playlist.ShareCode = input.ShareCode.Trim();
      }
      if (string.IsNullOrWhiteSpace(playlist.ShareCode))
      {
        playlist.ShareCode = SecurityHelpers.GenerateSecureShareCode();
      }

      await db.SaveChangesAsync();
      return Results.Ok(playlist);
    });

    mutations.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
    {
      var playlist = await FindOwnedAsync(db, id, user);
      if (playlist is null) return Results.NotFound();
      db.Playlists.Remove(playlist);
      await db.SaveChangesAsync();
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

    return app;
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
    return null;
  }
}
