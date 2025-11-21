using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

public class Playlist
{
  [Key]
  public Guid Id { get; set; }

  [Required]
  [MaxLength(200)]
  public string Name { get; set; } = string.Empty;

  [MaxLength(2048)]
  public string? SourceUrl { get; set; }

  [MaxLength(200)]
  public string? SourceName { get; set; }

  [Column(TypeName = "jsonb")]
  public List<string> DisabledGroups { get; set; } = new();

  [Column("totalchannels")]
  public int TotalChannels { get; set; }

  [Column("groupcount")]
  public int GroupCount { get; set; }

  [Column("expirationutc")]
  public DateTimeOffset? ExpirationUtc { get; set; }

  [MaxLength(64)]
  [Column("sharecode")]
  public string? ShareCode { get; set; } = string.Empty;

  public DateTimeOffset CreatedAt { get; set; }

  public DateTimeOffset UpdatedAt { get; set; }
}
