using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public class AppDbContext : DbContext
{
  public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

  public DbSet<Playlist> Playlists => Set<Playlist>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<Playlist>(entity =>
    {
      entity.ToTable("playlists");
      entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
      entity.Property(p => p.SourceUrl).HasMaxLength(2048);
      entity.Property(p => p.SourceName).HasMaxLength(200);
      entity.Property(p => p.DisabledGroups).HasColumnType("jsonb");
    });
  }

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
