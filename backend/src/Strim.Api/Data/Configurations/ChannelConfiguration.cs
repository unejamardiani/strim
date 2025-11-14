using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strim.Api.Domain;

namespace Strim.Api.Data.Configurations;

public class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("channels");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(300);

        builder.Property(c => c.Url)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(c => c.RawExtinf)
            .IsRequired();

        builder.Property(c => c.GroupTitle)
            .HasMaxLength(300);

        builder.Property(c => c.TvgId)
            .HasMaxLength(200);

        builder.Property(c => c.TvgName)
            .HasMaxLength(300);

        builder.Property(c => c.TvgLogo)
            .HasMaxLength(1024);

        builder.Property(c => c.SortOrder)
            .IsRequired();

        builder.HasIndex(c => new { c.PlaylistId, c.SortOrder }).IsUnique();
        builder.HasIndex(c => new { c.PlaylistId, c.Name });
        builder.HasIndex(c => c.GroupTitle);
        builder.HasIndex(c => new { c.PlaylistId, c.Url });
    }
}
