using System.Net;
using System.Text;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>One background refresh pass against stubbed playlist and account answers.</summary>
public class PlaylistRefreshAccountTests
{
  private const string Password = "Pw97NotReal";
  private const string PlaylistUrl = $"http://93.184.215.14/get.php?username=acct97&password={Password}&type=m3u_plus";
  private const string AccountUrl = $"http://93.184.215.14/player_api.php?username=acct97&password={Password}";

  private const string GoodAccount =
    """{"user_info":{"auth":1,"status":"Banned","exp_date":"1924992000","max_connections":"3","active_cons":"1"}}""";

  private static readonly DateTimeOffset StoredExpiry = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset StoredCheck = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task GoodAccountAnswer_UpdatesExpiryStatusConnectionsAndCheckTime()
  {
    using var factory = new StrimAppFactory();
    factory.UpstreamPlaylists[PlaylistUrl] = TestPlaylists.Mixed;
    factory.UpstreamPlaylists[AccountUrl] = GoodAccount;
    var id = await SeedAsync(factory);
    var before = DateTimeOffset.UtcNow;

    var playlist = await RunPassAsync(factory, id);

    Assert.Equal(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero), playlist.ExpirationUtc);
    Assert.Equal("Banned", playlist.AccountStatus);
    Assert.Equal(3,playlist.AccountMaxConnections);
    Assert.Equal(1, playlist.AccountActiveConnections);
    Assert.True(playlist.AccountCheckedUtc >= before);
    Assert.Equal(4, playlist.TotalChannels);
    Assert.Contains(AccountUrl, factory.UpstreamRequests);
    AssertNoPasswordInLogs(factory);
  }

  [Theory]
  [InlineData(HttpStatusCode.InternalServerError, GoodAccount)]
  [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
  [InlineData(HttpStatusCode.OK, """{"user_info":{"auth":0}}""")]
  public async Task FailingOrUnparsableAccountAnswer_KeepsStoredValues_AndPlaylistStillRefreshes(HttpStatusCode status, string body)
  {
    using var factory = new StrimAppFactory();
    factory.UpstreamPlaylists[PlaylistUrl] = TestPlaylists.Mixed;
    factory.UpstreamResponses[AccountUrl] = () => new HttpResponseMessage(status)
    {
      Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    var id = await SeedAsync(factory);
    var before = DateTimeOffset.UtcNow;

    var playlist = await RunPassAsync(factory, id);

    AssertStoredAccountUnchanged(playlist);
    Assert.Equal(4, playlist.TotalChannels);
    Assert.NotEqual(9, playlist.GroupCount);
    Assert.True(playlist.LastRefreshedUtc >= before);
    Assert.Contains(AccountUrl, factory.UpstreamRequests);
    AssertNoPasswordInLogs(factory);
  }

  [Fact]
  public async Task FailingPlaylistDownload_KeepsStoredAccountFields()
  {
    using var factory = new StrimAppFactory();
    factory.UpstreamResponses[PlaylistUrl] = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
    factory.UpstreamResponses[AccountUrl] = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
    var id = await SeedAsync(factory);

    var playlist = await RunPassAsync(factory, id);

    AssertStoredAccountUnchanged(playlist);
    Assert.Null(playlist.LastRefreshedUtc);
    Assert.Equal(9, playlist.TotalChannels);
    Assert.Contains(PlaylistUrl, factory.UpstreamRequests);
    AssertNoPasswordInLogs(factory);
  }

  private static async Task<Guid> SeedAsync(StrimAppFactory factory)
  {
    // Starting the host runs the service's first pass; wait for it so it cannot race the pass under test.
    _ = factory.Services;
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!factory.LogEntries.Any(e => e.StartsWith("Refreshing ")) && DateTime.UtcNow < deadline)
    {
      await Task.Delay(20);
    }

    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var playlist = new Playlist
    {
      Id = Guid.NewGuid(),
      Name = "Living room",
      SourceUrl = PlaylistUrl,
      ShareCode = Guid.NewGuid().ToString("N"),
      AutoRefreshEnabled = true,
      TotalChannels = 9,
      GroupCount = 9,
      ExpirationUtc = StoredExpiry,
      AccountStatus = "Active",
      AccountMaxConnections = 1,
      AccountActiveConnections = 0,
      AccountCheckedUtc = StoredCheck,
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
    db.Playlists.Add(playlist);
    await db.SaveChangesAsync();
    return playlist.Id;
  }

  private static async Task<Playlist> RunPassAsync(StrimAppFactory factory, Guid id)
  {
    var service = ActivatorUtilities.CreateInstance<PlaylistRefreshService>(factory.Services);
    await service.RefreshPlaylistsAsync(CancellationToken.None);

    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    return await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == id);
  }

  private static void AssertStoredAccountUnchanged(Playlist playlist)
  {
    Assert.Equal(StoredExpiry, playlist.ExpirationUtc);
    Assert.Equal("Active", playlist.AccountStatus);
    Assert.Equal(1, playlist.AccountMaxConnections);
    Assert.Equal(0, playlist.AccountActiveConnections);
    Assert.Equal(StoredCheck, playlist.AccountCheckedUtc);
  }

  private static void AssertNoPasswordInLogs(StrimAppFactory factory)
  {
    Assert.NotEmpty(factory.LogEntries);
    Assert.DoesNotContain(factory.LogEntries, entry => entry.Contains(Password, StringComparison.Ordinal));
  }
}
