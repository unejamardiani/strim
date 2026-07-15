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

  [MaxLength(450)]
  [Column("ownerid")]
  public string? OwnerId { get; set; }

  [MaxLength(64)]
  [Column("sharecode")]
  public string? ShareCode { get; set; } = string.Empty;

  [Column("isactive")]
  public bool IsActive { get; set; } = true;

  [Column("viewcount")]
  public long ViewCount { get; set; }

  [Column("lastviewedutc")]
  public DateTimeOffset? LastViewedUtc { get; set; }

  [Column("autorefreshenabled")]
  public bool AutoRefreshEnabled { get; set; }

  [Column("lastrefreshedutc")]
  public DateTimeOffset? LastRefreshedUtc { get; set; }

  [MaxLength(64)]
  [Column("sourcehash")]
  public string? SourceHash { get; set; }

  [MaxLength(512)]
  [Column("sourceetag")]
  public string? SourceETag { get; set; }

  [Column("sourcelastmodifiedutc")]
  public DateTimeOffset? SourceLastModifiedUtc { get; set; }

  [Column("sourcelengthbytes")]
  public long? SourceLengthBytes { get; set; }

  [Column("sourcecheckedutc")]
  public DateTimeOffset? SourceCheckedUtc { get; set; }

  public DateTimeOffset CreatedAt { get; set; }

  public DateTimeOffset UpdatedAt { get; set; }
}
