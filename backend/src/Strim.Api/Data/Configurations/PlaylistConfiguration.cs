using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strim.Api.Domain;

namespace Strim.Api.Data.Configurations;

public class PlaylistConfiguration : IEntityTypeConfiguration<Playlist>
{
    public void Configure(EntityTypeBuilder<Playlist> builder)
    {
        builder.ToTable("playlists");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(p => p.Source)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(p => p.SourceType)
            .HasConversion<string>()
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.HasMany(p => p.Channels)
            .WithOne(c => c.Playlist)
            .HasForeignKey(c => c.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Channels).AutoInclude(false);

        builder.HasIndex(p => p.CreatedAt);
        builder.HasIndex(p => p.Source);
        builder.HasIndex(p => p.Name);
    }
}
