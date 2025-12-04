using System.Text.Json;
using Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Api.Data;

public class AppDbContext : IdentityDbContext<IdentityUser>
{
  public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

  public DbSet<Playlist> Playlists => Set<Playlist>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    base.OnModelCreating(modelBuilder);

    var disabledGroupsConverter = new ValueConverter<List<string>, string>(
      v => SerializeDisabledGroups(v),
      v => DeserializeDisabledGroups(v));

    var disabledGroupsComparer = new ValueComparer<List<string>>(
      (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
      v => (v ?? new List<string>()).Aggregate(0, (hash, item) => HashCode.Combine(hash, item == null ? 0 : item.GetHashCode())),
      v => v == null ? new List<string>() : v.ToList());

    modelBuilder.Entity<Playlist>(entity =>
    {
      entity.ToTable("playlists");
      entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
      entity.Property(p => p.SourceUrl).HasMaxLength(2048);
      entity.Property(p => p.SourceName).HasMaxLength(200);
      var disabled = entity.Property(p => p.DisabledGroups)
        .HasConversion(disabledGroupsConverter);
      disabled.HasColumnType("jsonb");
      disabled.Metadata.SetValueComparer(disabledGroupsComparer);
      entity.Property(p => p.TotalChannels).HasDefaultValue(0);
      entity.Property(p => p.GroupCount).HasDefaultValue(0);
      entity.Property(p => p.ExpirationUtc);
      entity.Property(p => p.ShareCode).HasMaxLength(64);
      entity.Property(p => p.OwnerId).HasMaxLength(450).HasColumnName("ownerid");
      entity.HasIndex(p => new { p.OwnerId, p.UpdatedAt });
      entity.HasIndex(p => p.ShareCode).HasFilter("\"ShareCode\" IS NOT NULL");
    });
  }

  private static string SerializeDisabledGroups(List<string>? groups) =>
    JsonSerializer.Serialize(groups ?? new List<string>());

  private static List<string> DeserializeDisabledGroups(string? value) =>
    string.IsNullOrWhiteSpace(value) ? new List<string>() : (JsonSerializer.Deserialize<List<string>>(value) ?? new List<string>());

  public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
  {
    StampTimestamps();
    return base.SaveChangesAsync(cancellationToken);
  }

  public override int SaveChanges()
  {
    StampTimestamps();
    return base.SaveChanges();
  }

  private void StampTimestamps()
  {
    var now = DateTimeOffset.UtcNow;
    foreach (var entry in ChangeTracker.Entries<Playlist>())
    {
      if (entry.State == EntityState.Added)
      {
        if (entry.Entity.Id == Guid.Empty)
        {
          entry.Entity.Id = Guid.NewGuid();
        }
        entry.Entity.CreatedAt = now;
        entry.Entity.UpdatedAt = now;
      }
      else if (entry.State == EntityState.Modified)
      {
        entry.Entity.UpdatedAt = now;
      }
    }
  }
}
