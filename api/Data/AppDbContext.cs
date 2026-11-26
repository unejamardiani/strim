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

  public DbSet<TitleMark> TitleMarks => Set<TitleMark>();

  public DbSet<PlaylistShare> PlaylistShares => Set<PlaylistShare>();

  /// <summary>Shadow properties of the profile columns on <c>AspNetUsers</c>; the app keeps the plain IdentityUser.</summary>
  public const string DisplayNameProperty = "DisplayName";
  public const string AvatarProperty = "Avatar";
  public const string ThemeProperty = "Theme";

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    base.OnModelCreating(modelBuilder);

    // Matches the columns Database patches onto older AspNetUsers tables.
    modelBuilder.Entity<IdentityUser>(entity =>
    {
      entity.Property<string?>(DisplayNameProperty).HasMaxLength(40).HasColumnName("display_name");
      entity.Property<string?>(AvatarProperty).HasMaxLength(32).HasColumnName("avatar");
      entity.Property<string?>(ThemeProperty).HasMaxLength(16).HasColumnName("theme");
    });

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
      entity.Property(p => p.SourceHash).HasMaxLength(64).HasColumnName("sourcehash");
      entity.Property(p => p.SourceETag).HasMaxLength(512).HasColumnName("sourceetag");
      entity.Property(p => p.SourceLastModifiedUtc).HasColumnName("sourcelastmodifiedutc");
      entity.Property(p => p.SourceLengthBytes).HasColumnName("sourcelengthbytes");
      entity.Property(p => p.SourceCheckedUtc).HasColumnName("sourcecheckedutc");
      entity.Property(p => p.AccountStatus).HasMaxLength(32).HasColumnName("accountstatus");
      entity.Property(p => p.AccountMaxConnections).HasColumnName("accountmaxconnections");
      entity.Property(p => p.AccountActiveConnections).HasColumnName("accountactiveconnections");
      entity.Property(p => p.AccountCheckedUtc).HasColumnName("accountcheckedutc");
      entity.Property(p => p.WatchlistGroup).HasColumnName("watchlistgroup");
      entity.HasIndex(p => new { p.OwnerId, p.UpdatedAt });
      entity.HasIndex(p => p.ShareCode).HasFilter("\"sharecode\" IS NOT NULL");
    });

    // Matches the SQL in Database.EnsureTitleMarksSchema. Uniqueness of (userid, kind, titlekey, year), with a
    // null year as its own value, comes from two partial unique indexes that SQL always creates.
    modelBuilder.Entity<TitleMark>(entity =>
    {
      entity.ToTable("titlemarks");
      entity.HasKey(m => m.Id);
      entity.Property(m => m.Id).HasColumnName("id");
      entity.Property(m => m.UserId).IsRequired().HasMaxLength(450).HasColumnName("userid");
      entity.Property(m => m.Kind).IsRequired().HasMaxLength(16).HasColumnName("kind");
      entity.Property(m => m.TitleKey).IsRequired().HasMaxLength(256).HasColumnName("titlekey");
      entity.Property(m => m.Year).HasColumnName("year");
      entity.Property(m => m.DisplayTitle).IsRequired().HasMaxLength(256).HasColumnName("displaytitle");
      entity.Property(m => m.Seen).HasColumnName("seen");
      entity.Property(m => m.Rating).HasColumnName("rating");
      entity.Property(m => m.FilmDbId).HasMaxLength(64).HasColumnName("filmdbid");
      entity.Property(m => m.Watchlist).HasColumnName("watchlist");
      entity.Property(m => m.WatchlistedUtc).HasColumnName("watchlistedutc");
      entity.Property(m => m.CreatedUtc).HasColumnName("createdutc");
      entity.Property(m => m.UpdatedUtc).HasColumnName("updatedutc");
      entity.HasIndex(m => m.UserId).HasDatabaseName("IX_titlemarks_userid");
      entity.HasOne<IdentityUser>().WithMany().HasForeignKey(m => m.UserId)
        .HasConstraintName("FK_titlemarks_AspNetUsers_userid").OnDelete(DeleteBehavior.Cascade);
    });

    // Matches the SQL in Database.EnsurePlaylistSharesSchema.
    modelBuilder.Entity<PlaylistShare>(entity =>
    {
      entity.ToTable("playlistshares");
      entity.HasKey(s => s.Id);
      entity.Property(s => s.Id).HasColumnName("id");
      entity.Property(s => s.PlaylistId).HasColumnName("playlistid");
      entity.Property(s => s.Label).IsRequired().HasMaxLength(100).HasColumnName("label");
      entity.Property(s => s.Role).IsRequired().HasMaxLength(16).HasDefaultValue("viewer").HasColumnName("role");
      entity.Property(s => s.TokenHash).HasMaxLength(64).HasColumnName("tokenhash");
      entity.Property(s => s.TokenExpiresUtc).HasColumnName("tokenexpiresutc");
      entity.Property(s => s.ViewerUserId).HasMaxLength(450).HasColumnName("vieweruserid");
      entity.Property(s => s.CreatedUtc).HasColumnName("createdutc");
      entity.Property(s => s.AcceptedUtc).HasColumnName("acceptedutc");
      entity.Property(s => s.LastOpenedUtc).HasColumnName("lastopenedutc");
      entity.HasIndex(s => s.PlaylistId).HasDatabaseName("IX_playlistshares_playlistid");
      entity.HasIndex(s => s.ViewerUserId).HasDatabaseName("IX_playlistshares_vieweruserid");
      entity.HasIndex(s => s.TokenHash).IsUnique().HasFilter("\"tokenhash\" IS NOT NULL")
        .HasDatabaseName("IX_playlistshares_tokenhash");
      entity.HasIndex(s => new { s.PlaylistId, s.ViewerUserId }).IsUnique().HasFilter("\"vieweruserid\" IS NOT NULL")
        .HasDatabaseName("IX_playlistshares_playlistid_vieweruserid");
      entity.HasOne<Playlist>().WithMany().HasForeignKey(s => s.PlaylistId)
        .HasConstraintName("FK_playlistshares_playlists_playlistid").OnDelete(DeleteBehavior.Cascade);
      entity.HasOne<IdentityUser>().WithMany().HasForeignKey(s => s.ViewerUserId)
        .HasConstraintName("FK_playlistshares_AspNetUsers_vieweruserid").OnDelete(DeleteBehavior.Cascade);
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
