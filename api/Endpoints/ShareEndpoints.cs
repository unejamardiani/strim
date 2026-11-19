using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Api.Configuration;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <summary>Creates an invitation; the label is the owner's name for the person.</summary>
public record ShareRequest(string? Label);

/// <summary>
/// The owner's invitations to read-only access of one playlist (epic #373). Every route answers anyone but the
/// playlist's owner with 404.
/// <list type="bullet">
/// <item><c>GET /api/playlists/{id}/shares</c> lists the shares, without token or hash.</item>
/// <item><c>POST /api/playlists/{id}/shares</c> creates an invitation; the token is returned once and only its
/// SHA-256 is stored. 409 at <see cref="MaxSharesPerPlaylist"/>.</item>
/// <item><c>DELETE /api/playlists/{id}/shares/{shareId}</c> revokes access or withdraws the invitation.</item>
/// </list>
/// Neither the token, its hash nor the label is ever logged.
/// </summary>
public static class ShareEndpoints
{
  public const int MaxSharesPerPlaylist = 10;
  public const int MaxLabelLength = 100;
  public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

  public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder app)
  {
    var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Strim.Sharing");
    var shares = app.MapGroup("/api/playlists/{id:guid}/shares").RequireAuthorization();
    var mutations = shares.MapGroup("").RequireCsrfToken().RequireRateLimiting(Security.RateLimits.Sensitive);

    shares.MapGet("", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
    {
      if (!await IsOwnerAsync(db, id, user)) return Results.NotFound();
      var rows = await db.PlaylistShares.Where(s => s.PlaylistId == id)
        .Select(s => new { Share = s, ViewerName = db.Users.Where(u => u.Id == s.ViewerUserId).Select(u => u.UserName).FirstOrDefault() })
        .ToListAsync();
      // SQLite cannot ORDER BY DateTimeOffset; at most MaxSharesPerPlaylist rows, so sort in memory.
      var now = DateTimeOffset.UtcNow;
      return Results.Ok(rows.OrderBy(r => r.Share.CreatedUtc).Select(r => ToDto(r.Share, r.ViewerName, now)));
    });

    mutations.MapPost("", async (Guid id, ShareRequest input, ClaimsPrincipal user, AppDbContext db) =>
    {
      if (!await IsOwnerAsync(db, id, user)) return Results.NotFound();
      var label = input.Label?.Trim();
      if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength)
      {
        return Results.BadRequest(new { error = $"Give a label of 1 to {MaxLabelLength} characters." });
      }
      if (await db.PlaylistShares.CountAsync(s => s.PlaylistId == id) >= MaxSharesPerPlaylist)
      {
        return Results.Problem(
          $"This playlist already has {MaxSharesPerPlaylist} shares. Delete one to invite someone else.",
          statusCode: StatusCodes.Status409Conflict);
      }

      var token = SecurityHelpers.GenerateSecureShareCode();
      var now = DateTimeOffset.UtcNow;
      var share = new PlaylistShare
      {
        Id = Guid.NewGuid(), PlaylistId = id, Label = label, Role = "viewer", TokenHash = HashToken(token),
        TokenExpiresUtc = now.Add(InvitationLifetime), CreatedUtc = now,
      };
      db.PlaylistShares.Add(share);
      await db.SaveChangesAsync();

      logger.LogInformation("User {UserId} created invitation {ShareId} for playlist {PlaylistId}", user.UserId(), share.Id, id);
      return Results.Json(new { share = ToDto(share, null, now), inviteToken = token }, statusCode: StatusCodes.Status201Created);
    });

    mutations.MapDelete("/{shareId:guid}", async (Guid id, Guid shareId, ClaimsPrincipal user, AppDbContext db) =>
    {
      if (!await IsOwnerAsync(db, id, user)) return Results.NotFound();
      var deleted = await db.PlaylistShares.Where(s => s.Id == shareId && s.PlaylistId == id).ExecuteDeleteAsync();
      if (deleted == 0) return Results.NotFound();

      logger.LogInformation("User {UserId} deleted share {ShareId} of playlist {PlaylistId}", user.UserId(), shareId, id);
      return Results.NoContent();
    });

    return app;
  }

  /// <summary>SHA-256 of the token as lowercase hex, the only form of it that is stored.</summary>
  public static string HashToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

  private static Task<bool> IsOwnerAsync(AppDbContext db, Guid id, ClaimsPrincipal user)
  {
    var userId = user.UserId();
    return db.Playlists.AnyAsync(p => p.Id == id && p.OwnerId == userId);
  }

  private static object ToDto(PlaylistShare share, string? viewerName, DateTimeOffset now) => new
  {
    id = share.Id,
    label = share.Label,
    role = share.Role,
    status = share.ViewerUserId is not null ? "accepted" : share.TokenExpiresUtc <= now ? "expired" : "invited",
    createdUtc = share.CreatedUtc,
    expiresUtc = share.ViewerUserId is null ? share.TokenExpiresUtc : null,
    acceptedUtc = share.AcceptedUtc,
    viewerName,
    lastOpenedUtc = share.LastOpenedUtc,
  };
}
