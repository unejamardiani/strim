using Microsoft.EntityFrameworkCore;
using Strim.Api.Domain;

namespace Strim.Api.Data;

public class StrimDbContext(DbContextOptions<StrimDbContext> options) : DbContext(options)
{
    public DbSet<Playlist> Playlists => Set<Playlist>();

    public DbSet<Channel> Channels => Set<Channel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StrimDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
