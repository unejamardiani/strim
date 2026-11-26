using System.Linq.Expressions;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Api.Configuration;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

/// <summary>Who reads a playlist's Library; for a viewer, <paramref name="DisabledGroups"/> are the groups hidden from them.</summary>
internal sealed record LibraryAccess(Guid PlaylistId, string UserId, bool IsViewer, List<string>? DisabledGroups);

/// <summary>A person as sharing shows them: name fields and avatar of the account, never its e-mail address.</summary>
internal sealed record SharePerson(string? UserName, string? DisplayName, string? Avatar)
{
  public string Name => ProfileEndpoints.ShownName(DisplayName, UserName);
}

/// <summary>Creates an invitation; the label is the owner's name for the person.</summary>
public record ShareRequest(string? Label);

/// <summary>Accepts an invitation; the token comes from the link's fragment, posted in the body.</summary>
public record AcceptShareRequest(string? Token);

/// <summary>
/// The owner's invitations to read-only access of one playlist (epic #373). Every route answers anyone but the
/// playlist's owner with 404.
/// <list type="bullet">
/// <item><c>GET /api/playlists/{id}/shares</c> lists the shares, without token or hash.</item>
/// <item><c>POST /api/playlists/{id}/shares</c> creates an invitation; the token is returned once and only its
/// SHA-256 is stored. 409 at <see cref="MaxSharesPerPlaylist"/>.</item>
/// <item><c>DELETE /api/playlists/{id}/shares/{shareId}</c> revokes access or withdraws the invitation.</item>
/// </list>
/// Any signed-in user:
/// <list type="bullet">
/// <item><c>POST /api/shares/accept</c> binds an open invitation to the caller (single use); unknown, expired, used
/// and deleted tokens all give the same 404.</item>
/// <item><c>GET /api/shares/mine</c> lists the playlists shared with the caller: name, owner and entry count only.</item>
/// <item><c>DELETE /api/shares/mine/{playlistId}</c> leaves a share.</item>
/// </list>
/// The owner or an accepted viewer: <c>POST /api/playlists/{id}/library/open</c> resolves the source through the
/// pipeline and returns a <c>lib-</c> key bound to playlist and user, which no editor route accepts.
/// Neither the token, its hash nor the label is ever logged.
/// </summary>
public static class ShareEndpoints
{
  public const int MaxSharesPerPlaylist = 10;
  public const int MaxLabelLength = 100;
  public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
  public const string ViewerLoadFailure = "The list cannot be loaded right now";

  public static IEndpointRouteBuilder MapShareEndpoints(this IEndpointRouteBuilder app)
  {
    var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Strim.Sharing");
    var shares = app.MapGroup("/api/playlists/{id:guid}/shares").RequireAuthorization();
    var mutations = shares.MapGroup("").RequireCsrfToken().RequireRateLimiting(Security.RateLimits.Sensitive);

    shares.MapGet("", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
    {
      if (!await IsOwnerAsync(db, id, user)) return Results.NotFound();
      var rows = await db.PlaylistShares.Where(s => s.PlaylistId == id)
        .Select(s => new { Share = s, Viewer = db.Users.Where(u => u.Id == s.ViewerUserId).Select(PersonOf).FirstOrDefault() })
        .ToListAsync();
      // SQLite cannot ORDER BY DateTimeOffset; at most MaxSharesPerPlaylist rows, so sort in memory.
      var now = DateTimeOffset.UtcNow;
      return Results.Ok(rows.OrderBy(r => r.Share.CreatedUtc).Select(r => ToDto(r.Share, r.Viewer, now)));
    });

    mutations.MapPost("", async (Guid id, ShareRequest input, ClaimsPrincipal user, AppDbContext db) =>
    {
      // Count and insert in one transaction that first writes the playlist row: SQLite's write lock or
      // PostgreSQL's row lock makes concurrent creations for one playlist wait, so the limit holds.
      await using var transaction = await db.Database.BeginTransactionAsync();
      var userId = user.UserId();
      var owned = await db.Playlists.Where(p => p.Id == id && p.OwnerId == userId)
        .ExecuteUpdateAsync(set => set.SetProperty(p => p.UpdatedAt, p => p.UpdatedAt));
      if (owned == 0) return Results.NotFound();
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
      await transaction.CommitAsync();

      logger.LogInformation("User {UserId} created invitation {ShareId} for playlist {PlaylistId}", userId, share.Id, id);
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

    var viewer = app.MapGroup("/api/shares").RequireAuthorization();
    var viewerMutations = viewer.MapGroup("").RequireCsrfToken().RequireRateLimiting(Security.RateLimits.Sensitive);

    viewerMutations.MapPost("/accept", async (AcceptShareRequest input, ClaimsPrincipal user, AppDbContext db) =>
    {
      var userId = user.UserId();
      var token = input.Token?.Trim();
      var hash = string.IsNullOrEmpty(token) ? null : HashToken(token);
      var share = hash is null ? null : await db.PlaylistShares.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == hash);
      var now = DateTimeOffset.UtcNow;
      if (share is null || share.TokenExpiresUtc is null || share.TokenExpiresUtc <= now)
      {
        logger.LogWarning("Refused an invitation: {Reason}", share is null ? "unknown" : "expired");
        return Results.NotFound();
      }

      var playlist = await db.Playlists.AsNoTracking().Where(p => p.Id == share.PlaylistId)
        .Select(p => new { p.Id, p.Name, p.OwnerId }).FirstAsync();
      if (playlist.OwnerId == userId)
      {
        logger.LogWarning("Refused an invitation: {Reason}", "own playlist");
        return Results.BadRequest(new { error = "This is your own playlist. Send the link to the person you invited." });
      }

      if (await db.PlaylistShares.AnyAsync(s => s.PlaylistId == playlist.Id && s.ViewerUserId == userId))
      {
        // The caller already has access: the spare invitation is removed instead of bound twice.
        await db.PlaylistShares.Where(s => s.Id == share.Id && s.TokenHash == hash).ExecuteDeleteAsync();
        logger.LogInformation("User {UserId} already had access to playlist {PlaylistId}; deleted spare invitation {ShareId}", userId, playlist.Id, share.Id);
        return Results.Ok(new { playlistId = playlist.Id, name = playlist.Name });
      }

      // Conditional on the hash, so two concurrent uses of one token bind it at most once.
      var bound = await db.PlaylistShares.Where(s => s.Id == share.Id && s.TokenHash == hash)
        .ExecuteUpdateAsync(set => set
          .SetProperty(s => s.ViewerUserId, userId)
          .SetProperty(s => s.AcceptedUtc, now)
          .SetProperty(s => s.TokenHash, (string?)null)
          .SetProperty(s => s.TokenExpiresUtc, (DateTimeOffset?)null));
      if (bound == 0)
      {
        logger.LogWarning("Refused an invitation: {Reason}", "used");
        return Results.NotFound();
      }

      logger.LogInformation("User {UserId} accepted share {ShareId} of playlist {PlaylistId}", userId, share.Id, playlist.Id);
      return Results.Ok(new { playlistId = playlist.Id, name = playlist.Name });
    });

    viewer.MapGet("/mine", async (ClaimsPrincipal user, AppDbContext db) =>
    {
      var userId = user.UserId();
      var rows = await (
        from s in db.PlaylistShares
        where s.ViewerUserId == userId
        join p in db.Playlists on s.PlaylistId equals p.Id
        select new
        {
          playlistId = p.Id,
          name = p.Name,
          owner = db.Users.Where(u => u.Id == p.OwnerId).Select(PersonOf).FirstOrDefault(),
          totalChannels = p.TotalChannels,
          sharedSinceUtc = s.AcceptedUtc,
        }).AsNoTracking().ToListAsync();
      // A handful of rows; sorted in memory like the owner's list.
      return Results.Ok(rows.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase)
        .Select(r => new { r.playlistId, r.name, ownerName = r.owner?.Name, ownerAvatar = r.owner?.Avatar, r.totalChannels, r.sharedSinceUtc }));
    });

    viewerMutations.MapDelete("/mine/{playlistId:guid}", async (Guid playlistId, ClaimsPrincipal user, AppDbContext db) =>
    {
      var userId = user.UserId();
      var deleted = await db.PlaylistShares.Where(s => s.PlaylistId == playlistId && s.ViewerUserId == userId).ExecuteDeleteAsync();
      if (deleted == 0) return Results.NotFound();

      logger.LogInformation("User {UserId} left playlist {PlaylistId}", userId, playlistId);
      return Results.NoContent();
    });

    app.MapPost("/api/playlists/{id:guid}/library/open", async (
      Guid id, HttpContext context, ClaimsPrincipal user, AppDbContext db, PlaylistPipeline pipeline, PlaylistJobGate jobGate) =>
    {
      var userId = user.UserId()!;
      // One query: the owner or an accepted viewer, else the same 404 as for a stranger.
      var access = await db.Playlists.AsNoTracking()
        .Where(p => p.Id == id && (p.OwnerId == userId || db.PlaylistShares.Any(s => s.PlaylistId == p.Id && s.ViewerUserId == userId)))
        .Select(p => new
        {
          p.Name, p.SourceUrl, p.DisabledGroups, p.WatchlistGroup, IsOwner = p.OwnerId == userId,
          Owner = db.Users.Where(u => u.Id == p.OwnerId).Select(PersonOf).FirstOrDefault(),
          Share = db.PlaylistShares.Where(s => s.PlaylistId == p.Id && s.ViewerUserId == userId)
            .Select(s => new { s.Id, s.LastOpenedUtc }).FirstOrDefault(),
        }).FirstOrDefaultAsync(context.RequestAborted);
      if (access is null) return Results.NotFound();

      var job = await jobGate.TryEnterAsync(context.RequestAborted);
      if (job is null) return EndpointHelpers.Busy();

      await using (job)
      {
        try
        {
          if (string.IsNullOrWhiteSpace(access.SourceUrl)) throw new InvalidOperationException("Playlist is missing sourceUrl.");
          var source = await pipeline.GetSourceAsync(access.SourceUrl, context.RequestAborted);
          var analysis = await pipeline.GetAnalysisAsync(source, context.RequestAborted);
          var groups = PlaylistProcessor.ToGroupResults(analysis.Groups);
          var totalChannels = analysis.TotalChannels;
          if (!access.IsOwner)
          {
            // A viewer sees the visible groups only; the hidden ones come from the database, never the request.
            // A stored plain group-title hides every content type of that title, as in the player link.
            var disabled = new HashSet<string>(access.DisabledGroups ?? [], StringComparer.OrdinalIgnoreCase);
            groups = PlaylistProcessor.ToGroupResults(
              analysis.Groups.Where(g => !PlaylistProcessor.IsDisabled(disabled, g.GroupTitle, g.ContentType)));
            totalChannels = groups.Sum(g => g.Count);

            var now = DateTimeOffset.UtcNow;
            if (access.Share is { } share && (share.LastOpenedUtc is null || share.LastOpenedUtc <= now.AddHours(-1)))
            {
              await db.PlaylistShares.Where(s => s.Id == share.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastOpenedUtc, now), context.RequestAborted);
            }
          }

          var libraryKey = pipeline.Cache.CreateLibrarySession(source, id, userId);
          return Results.Ok(new
          {
            libraryKey, name = access.Name, role = access.IsOwner ? "owner" : "viewer",
            ownerName = access.Owner?.Name, ownerAvatar = access.Owner?.Avatar, totalChannels, groups, watchlistGroup = access.WatchlistGroup,
          });
        }
        catch (Exception ex) when (!access.IsOwner)
        {
          // An upstream message can name the provider's host, so a viewer gets one neutral message.
          logger.LogWarning(ex, "Library of playlist {PlaylistId} could not be opened for viewer {UserId}", id, userId);
          return Results.Problem(ViewerLoadFailure, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
          return EndpointHelpers.PlaylistFailure(ex, logger, "open");
        }
      }
    }).RequireAuthorization().RequireCsrfToken().RequireRateLimiting(Security.RateLimits.Fetch);

    return app;
  }

  /// <summary>
  /// The caller's access to a playlist's Library in one query: the owner, an accepted viewer with the hidden groups
  /// from the database, or null (the same 404 as for a stranger).
  /// </summary>
  internal static Task<LibraryAccess?> LibraryAccessAsync(AppDbContext db, Guid id, string userId, CancellationToken ct) =>
    db.Playlists.AsNoTracking()
      .Where(p => p.Id == id && (p.OwnerId == userId || db.PlaylistShares.Any(s => s.PlaylistId == p.Id && s.ViewerUserId == userId)))
      .Select(p => new LibraryAccess(p.Id, userId, p.OwnerId != userId, p.DisabledGroups))
      .FirstOrDefaultAsync(ct);

  /// <summary>The fields of an account that sharing shows, as a projection EF translates into the query.</summary>
  private static readonly Expression<Func<IdentityUser, SharePerson>> PersonOf = u => new SharePerson(
    u.UserName, EF.Property<string?>(u, AppDbContext.DisplayNameProperty), EF.Property<string?>(u, AppDbContext.AvatarProperty));

  /// <summary>SHA-256 of the token as lowercase hex, the only form of it that is stored.</summary>
  public static string HashToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

  private static Task<bool> IsOwnerAsync(AppDbContext db, Guid id, ClaimsPrincipal user)
  {
    var userId = user.UserId();
    return db.Playlists.AnyAsync(p => p.Id == id && p.OwnerId == userId);
  }

  private static object ToDto(PlaylistShare share, SharePerson? viewer, DateTimeOffset now) => new
  {
    id = share.Id,
    label = share.Label,
    role = share.Role,
    status = share.ViewerUserId is not null ? "accepted" : share.TokenExpiresUtc <= now ? "expired" : "invited",
    createdUtc = share.CreatedUtc,
    expiresUtc = share.ViewerUserId is null ? share.TokenExpiresUtc : null,
    acceptedUtc = share.AcceptedUtc,
    viewerName = viewer?.Name,
    avatar = viewer?.Avatar,
    lastOpenedUtc = share.LastOpenedUtc,
  };
}
