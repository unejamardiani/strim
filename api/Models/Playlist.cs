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

  public int TotalChannels { get; set; }

  public int GroupCount { get; set; }

  public DateTimeOffset? ExpirationUtc { get; set; }

  public DateTimeOffset CreatedAt { get; set; }

  public DateTimeOffset UpdatedAt { get; set; }
}
