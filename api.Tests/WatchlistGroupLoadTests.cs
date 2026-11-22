using Api.Data;
using Api.Models;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>The watchlist of a playlist's owner and accepted viewers, merged into one ordered list (#520).</summary>
public class WatchlistGroupLoadTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

  private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> run)
  {
    using var scope = factory.Services.CreateScope();
    return await run(scope.ServiceProvider.GetRequiredService<AppDbContext>());
  }

  private Task WithDbAsync(Func<AppDbContext, Task> run) => WithDbAsync(async db => { await run(db); return 0; });

  private Task<string> UserAsync() => WithDbAsync(async db =>
  {
    var id = Guid.NewGuid().ToString();
    db.Users.Add(new IdentityUser { Id = id, UserName = $"u{id}", NormalizedUserName = $"U{id}".ToUpperInvariant() });
    await db.SaveChangesAsync();
    return id;
  });

  private Task<Guid> PlaylistAsync(string ownerId) => WithDbAsync(async db =>
  {
    var playlist = new Playlist { Name = "Sofa list", OwnerId = ownerId, ShareCode = Guid.NewGuid().ToString("N") };
    db.Playlists.Add(playlist);
    await db.SaveChangesAsync();
    return playlist.Id;
  });

  private Task<Guid> ShareAsync(Guid playlistId, string? viewerId, bool expired = false) => WithDbAsync(async db =>
  {
    var share = new PlaylistShare
    {
      Id = Guid.NewGuid(), PlaylistId = playlistId, Label = "Guest", ViewerUserId = viewerId, CreatedUtc = T0,
      TokenHash = viewerId == null ? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") : null,
      TokenExpiresUtc = viewerId == null ? DateTimeOffset.UtcNow.AddDays(expired ? -1 : 7) : null,
      AcceptedUtc = viewerId == null ? null : T0,
    };
    db.PlaylistShares.Add(share);
    await db.SaveChangesAsync();
    return share.Id;
  });

  private Task MarkAsync(string userId, string key, int? minutes, bool seen = false, int rating = 0) => WithDbAsync(async db =>
  {
    db.TitleMarks.Add(new TitleMark
    {
      Id = Guid.NewGuid(), UserId = userId, Kind = key.Split(':')[0], TitleKey = key, DisplayTitle = key, Seen = seen, Rating = rating,
      Watchlist = minutes != null, WatchlistedUtc = minutes is { } m ? T0.AddMinutes(m) : null, CreatedUtc = T0, UpdatedUtc = T0,
    });
    await db.SaveChangesAsync();
  });

  private Task<WatchlistGroup?> LoadAsync(Guid playlistId, string ownerId) =>
    WithDbAsync(db => WatchlistGroup.LoadAsync(db, playlistId, ownerId, CancellationToken.None));

  private static string Fingerprint(params string[] keys) => new WatchlistGroup(keys).Fingerprint;

  [Fact]
  public async Task Load_ReturnsOnlyWatchlistedKeys_NewestFirst()
  {
    var owner = await UserAsync();
    var playlist = await PlaylistAsync(owner);
    await MarkAsync(owner, "film:2001:old", 1);
    await MarkAsync(owner, "film:2002:new", 5);
    await MarkAsync(owner, "series:b tie", 3);
    await MarkAsync(owner, "film:2003:a tie", 3);
    await MarkAsync(owner, "film:2004:seen", null, seen: true);
    await MarkAsync(owner, "film:2005:liked", null, rating: 1);

    var group = await LoadAsync(playlist, owner);

    Assert.Equal(Fingerprint("film:2002:new", "film:2003:a tie", "series:b tie", "film:2001:old"), group!.Fingerprint);
  }

  [Fact]
  public async Task Load_MergesTheOwnerAndAcceptedViewers_NewestFirstAcrossAll()
  {
    var (owner, first, second) = (await UserAsync(), await UserAsync(), await UserAsync());
    var playlist = await PlaylistAsync(owner);
    await ShareAsync(playlist, first);
    await ShareAsync(playlist, second);
    await MarkAsync(owner, "film:2001:o1", 1);
    await MarkAsync(first, "film:2001:f2", 2);
    await MarkAsync(second, "series:s3", 3);
    await MarkAsync(owner, "film:2001:o4", 4);
    await MarkAsync(first, "film:2001:f5", 5);

    var group = await LoadAsync(playlist, owner);

    Assert.Equal(Fingerprint("film:2001:f5", "film:2001:o4", "series:s3", "film:2001:f2", "film:2001:o1"), group!.Fingerprint);
  }

  [Fact]
  public async Task Load_TheSameTitleFromSeveralPeople_IsOneKey_AtItsNewestTime()
  {
    var (owner, viewer) = (await UserAsync(), await UserAsync());
    var playlist = await PlaylistAsync(owner);
    await ShareAsync(playlist, viewer);
    await MarkAsync(owner, "film:1999:shared", 1);
    await MarkAsync(owner, "film:2001:middle", 5);
    await MarkAsync(viewer, "film:1999:shared", 9);
    await MarkAsync(viewer, "series:shared", 3);

    var group = await LoadAsync(playlist, owner);

    Assert.Equal(Fingerprint("film:1999:shared", "film:2001:middle", "series:shared"), group!.Fingerprint);
  }

  [Fact]
  public async Task Load_OpenAndExpiredInvitations_ContributeNothing()
  {
    var (owner, other) = (await UserAsync(), await UserAsync());
    var playlist = await PlaylistAsync(owner);
    await ShareAsync(playlist, null);
    await ShareAsync(playlist, null, expired: true);
    await MarkAsync(owner, "film:2001:owner", 1);
    await MarkAsync(other, "film:2001:other", 2);

    Assert.Equal(Fingerprint("film:2001:owner"), (await LoadAsync(playlist, owner))!.Fingerprint);
  }

  [Fact]
  public async Task Load_ViewersOfAnotherPlaylist_AndStrangers_ContributeNothing()
  {
    var (owner, otherViewer, stranger) = (await UserAsync(), await UserAsync(), await UserAsync());
    var playlist = await PlaylistAsync(owner);
    await ShareAsync(await PlaylistAsync(owner), otherViewer);
    await MarkAsync(owner, "film:2001:owner", 1);
    await MarkAsync(otherViewer, "film:2001:viewer", 2);
    await MarkAsync(stranger, "film:2001:stranger", 3);

    Assert.Equal(Fingerprint("film:2001:owner"), (await LoadAsync(playlist, owner))!.Fingerprint);
  }

  [Fact]
  public async Task Load_AfterTheShareRowIsDeleted_TheViewerNoLongerContributes()
  {
    var (owner, viewer) = (await UserAsync(), await UserAsync());
    var playlist = await PlaylistAsync(owner);
    var share = await ShareAsync(playlist, viewer);
    await MarkAsync(owner, "film:2001:owner", 1);
    await MarkAsync(viewer, "film:2001:viewer", 2);
    Assert.Equal(Fingerprint("film:2001:viewer", "film:2001:owner"), (await LoadAsync(playlist, owner))!.Fingerprint);

    await WithDbAsync(db => db.PlaylistShares.Where(s => s.Id == share).ExecuteDeleteAsync());

    Assert.Equal(Fingerprint("film:2001:owner"), (await LoadAsync(playlist, owner))!.Fingerprint);
  }

  [Fact]
  public async Task Load_StopsAtMaxTitles_KeepingTheNewestOfTheUnion()
  {
    var (owner, viewer) = (await UserAsync(), await UserAsync());
    var playlist = await PlaylistAsync(owner);
    await ShareAsync(playlist, viewer);
    // Owner holds the even minutes, the viewer the odd ones: 300 titles, the newest 200 come from both.
    await WithDbAsync(async db =>
    {
      for (var i = 0; i < 300; i++)
      {
        db.TitleMarks.Add(new TitleMark
        {
          Id = Guid.NewGuid(), UserId = i % 2 == 0 ? owner : viewer, Kind = "film", TitleKey = $"film:2000:t{i:D3}", DisplayTitle = "t",
          Watchlist = true, WatchlistedUtc = T0.AddMinutes(i), CreatedUtc = T0, UpdatedUtc = T0,
        });
      }
      await db.SaveChangesAsync();
    });

    var group = await LoadAsync(playlist, owner);

    var expected = Enumerable.Range(100, 200).Reverse().Select(i => $"film:2000:t{i:D3}").ToArray();
    Assert.Equal(WatchlistGroup.MaxTitles, expected.Length);
    Assert.Equal(Fingerprint(expected), group!.Fingerprint);
  }

  [Fact]
  public async Task Load_WithNothingOnAnyWatchlist_IsNull()
  {
    var (owner, viewer) = (await UserAsync(), await UserAsync());
    var playlist = await PlaylistAsync(owner);
    await ShareAsync(playlist, viewer);
    await MarkAsync(owner, "film:2001:seen", null, seen: true);
    await MarkAsync(viewer, "film:2001:liked", null, rating: 1);

    Assert.Null(await LoadAsync(playlist, owner));
  }
}
