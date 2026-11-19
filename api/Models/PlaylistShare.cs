namespace Api.Models;

/// <summary>
/// A playlist shared read-only with one person (table <c>playlistshares</c>). Status is derived: accepted when
/// <see cref="ViewerUserId"/> is set, expired when <see cref="TokenExpiresUtc"/> has passed, otherwise invited.
/// The row goes with its playlist and with the viewer's account. The table is created idempotently in
/// <c>Configuration/Database.cs</c>.
/// </summary>
public class PlaylistShare
{
  public Guid Id { get; set; }

  public Guid PlaylistId { get; set; }

  /// <summary>The owner's name for the person; never matched, never logged.</summary>
  public string Label { get; set; } = string.Empty;

  /// <summary>Only "viewer" for now.</summary>
  public string Role { get; set; } = "viewer";

  /// <summary>SHA-256 hex of the invitation token; null once accepted.</summary>
  public string? TokenHash { get; set; }

  public DateTimeOffset? TokenExpiresUtc { get; set; }

  public string? ViewerUserId { get; set; }

  public DateTimeOffset CreatedUtc { get; set; }

  public DateTimeOffset? AcceptedUtc { get; set; }

  public DateTimeOffset? LastOpenedUtc { get; set; }
}
