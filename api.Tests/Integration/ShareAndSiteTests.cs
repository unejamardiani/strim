using Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Api.Tests.Integration;

public class ShareAndSiteTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private Task<(ApiClient Owner, PlaylistDto Playlist)> CreateSharedAsync(string userName, params string[] disabled) =>
    CreateSharedFromAsync(userName, StrimAppFactory.UpstreamUrl, disabled);

  private async Task<(ApiClient Owner, PlaylistDto Playlist)> CreateSharedFromAsync(string userName, string sourceUrl, params string[] disabled)
  {
    var owner = new ApiClient(factory.CreateClient());
    await owner.RegisterAsync(userName);
    var response = await owner.PostAsync("/api/playlists", new
    {
      name = "Shared",
      sourceUrl,
      disabledGroups = disabled,
    });
    response.EnsureSuccessStatusCode();
    return (owner, (await response.Content.ReadFromJsonAsync<PlaylistDto>())!);
  }

  [Fact]
  public async Task Share_ServesTheFilteredPlaylistAnonymously_AndCountsViews()
  {
    var (owner, playlist) = await CreateSharedAsync("sharer", "Germany · Series");

    var anonymous = factory.CreateClient();
    var text = await anonymous.GetStringAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}");
    Assert.Contains("ARD", text);
    Assert.DoesNotContain("Tatort", text);

    var stats = await owner.Http.GetFromJsonAsync<StatsDto>($"/api/playlists/{playlist.Id}/stats");
    Assert.Equal(1, stats!.ViewCount);
  }

  [Fact]
  public async Task Share_KeepsSourceHeaderLine()
  {
    var (_, playlist) = await CreateSharedFromAsync("guide-sharer", StrimAppFactory.GuideHeaderUpstreamUrl, "Kids");

    var text = await factory.CreateClient().GetStringAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}");
    var lines = text.Replace("\r\n", "\n").Split('\n');
    Assert.Equal(TestPlaylists.GuideHeader, lines[0]);
    Assert.Equal("# Created with Strim (https://strim.plis.dev)", lines[1]);
    Assert.Contains("ARD", text);
    Assert.DoesNotContain("KiKA", text);
  }

  [Fact]
  public async Task Share_WithWrongCodeOrInactiveLink_IsNotFound()
  {
    var (owner, playlist) = await CreateSharedAsync("inactive");
    var anonymous = factory.CreateClient();

    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{playlist.Id}/share/wrong")).StatusCode);

    (await owner.SendAsync(HttpMethod.Patch, $"/api/playlists/{playlist.Id}/toggle-active")).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}")).StatusCode);
  }

  [Fact]
  public async Task Health_And_Config_Respond()
  {
    var client = factory.CreateClient();
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    var config = await client.GetStringAsync("/api/config");
    Assert.Contains("publicShareBaseUrl", config);
  }

  [Fact]
  public async Task UnknownApiRoute_IsNotFound()
  {
    var response = await factory.CreateClient().GetAsync("/api/does-not-exist");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task UnknownPage_IsNotFound()
  {
    var response = await factory.CreateClient().GetAsync("/no-such-page");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  // The blog was removed; its old addresses must end in the normal 404, not in an error.
  [Theory]
  [InlineData("/blog/")]
  [InlineData("/blog/fix-iptv-playlist-not-loading.html")]
  public async Task RemovedBlogPage_IsNotFound(string path)
  {
    var response = await factory.CreateClient().GetAsync(path);
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task AppWithTrailingSlash_RedirectsToApp()
  {
    var client = factory.CreateClient(new() { AllowAutoRedirect = false });
    var response = await client.GetAsync("/app/?api=x");
    Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
    Assert.Equal("/app?api=x", response.Headers.Location!.OriginalString);
  }

  private sealed record StatsDto(long ViewCount);

  // ---- Last good copy of a share link (#109) ----

  /// <summary>
  /// A fresh app that revalidates the source on every share fetch and gives up on a silent upstream
  /// after one second, so each request reaches the (faulty) stub.
  /// </summary>
  private sealed class FallbackApp : IDisposable
  {
    public StrimAppFactory Upstream { get; } = new();
    public WebApplicationFactory<Program> App { get; }

    public FallbackApp()
    {
      App = Upstream.WithWebHostBuilder(builder => builder
        .UseSetting("PlaylistCache:RevalidationIntervalMinutes", "0")
        .UseSetting("PlaylistCache:HeaderTimeoutSeconds", "1"));
    }

    public async Task<(ApiClient Owner, PlaylistDto Playlist)> CreateSharedAsync(string userName)
    {
      var owner = new ApiClient(App.CreateClient());
      await owner.RegisterAsync(userName);
      var response = await owner.PostAsync("/api/playlists", new
      {
        name = "Shared",
        sourceUrl = StrimAppFactory.UpstreamUrl,
        disabledGroups = new[] { "Kids" },
      });
      response.EnsureSuccessStatusCode();
      return (owner, (await response.Content.ReadFromJsonAsync<PlaylistDto>())!);
    }

    public static string SharePath(PlaylistDto playlist) => $"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}";

    public void Dispose()
    {
      App.Dispose();
      Upstream.Dispose();
    }
  }

  [Fact]
  public Task Share_UpstreamError_ServesLastGood() => AssertServesLastGood(UpstreamFault.ServerError);

  [Fact]
  public Task Share_UpstreamNetworkError_ServesLastGood() => AssertServesLastGood(UpstreamFault.NetworkError);

  [Fact]
  public Task Share_UpstreamTimeout_ServesLastGood() => AssertServesLastGood(UpstreamFault.Timeout);

  private static async Task AssertServesLastGood(UpstreamFault fault)
  {
    using var fallback = new FallbackApp();
    var (_, playlist) = await fallback.CreateSharedAsync("lastgood");
    var anonymous = fallback.App.CreateClient();
    var good = await anonymous.GetStringAsync(FallbackApp.SharePath(playlist));
    var requestsBefore = fallback.Upstream.UpstreamRequests.Count;

    fallback.Upstream.Fault = fault;
    var response = await anonymous.GetAsync(FallbackApp.SharePath(playlist));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(good, await response.Content.ReadAsStringAsync());
    Assert.True(fallback.Upstream.UpstreamRequests.Count > requestsBefore, "The second fetch must revalidate upstream.");
  }

  [Theory]
  [InlineData(UpstreamFault.ServerError, HttpStatusCode.BadGateway)]
  [InlineData(UpstreamFault.NetworkError, HttpStatusCode.BadGateway)]
  [InlineData(UpstreamFault.Timeout, HttpStatusCode.GatewayTimeout)]
  public async Task Share_UpstreamError_WithoutLastGood_ReturnsError(UpstreamFault fault, HttpStatusCode expected)
  {
    using var fallback = new FallbackApp();
    var (_, playlist) = await fallback.CreateSharedAsync("nolastgood");

    fallback.Upstream.Fault = fault;
    var response = await fallback.App.CreateClient().GetAsync(FallbackApp.SharePath(playlist));

    Assert.Equal(expected, response.StatusCode);
    Assert.False(response.Headers.Contains("X-Strim-Stale"));
  }

  [Fact]
  public async Task Share_Fallback_MarksResponseStale_AndKeepsMetadata()
  {
    using var fallback = new FallbackApp();
    var (owner, playlist) = await fallback.CreateSharedAsync("stale");
    var anonymous = fallback.App.CreateClient();

    var normal = await anonymous.GetAsync(FallbackApp.SharePath(playlist));
    normal.EnsureSuccessStatusCode();
    Assert.False(normal.Headers.Contains("X-Strim-Stale"));
    var before = await owner.Http.GetFromJsonAsync<SourceMetadataDto>($"/api/playlists/{playlist.Id}");
    Assert.NotNull(before!.SourceCheckedUtc);

    fallback.Upstream.Fault = UpstreamFault.ServerError;
    var stale = await anonymous.GetAsync(FallbackApp.SharePath(playlist));

    Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
    Assert.Equal("1", Assert.Single(stale.Headers.GetValues("X-Strim-Stale")));
    Assert.Equal(normal.Content.Headers.ContentType, stale.Content.Headers.ContentType);
    Assert.Equal(normal.Content.Headers.ContentDisposition, stale.Content.Headers.ContentDisposition);
    Assert.Equal(before, await owner.Http.GetFromJsonAsync<SourceMetadataDto>($"/api/playlists/{playlist.Id}"));

    var (level, warning) = Assert.Single(fallback.Upstream.LogRecords, r => r.Entry.Contains("last good copy of playlist"));
    Assert.Equal(LogLevel.Warning, level);
    Assert.Contains(playlist.Id.ToString(), warning);
    Assert.DoesNotContain(playlist.ShareCode!, warning);
    Assert.DoesNotContain("93.184.215.14", warning);
  }

  [Fact]
  public async Task Share_WrongCodeOrInactive_IsNotFound_EvenWithLastGood()
  {
    using var fallback = new FallbackApp();
    var (owner, playlist) = await fallback.CreateSharedAsync("lastgood404");
    var anonymous = fallback.App.CreateClient();
    (await anonymous.GetAsync(FallbackApp.SharePath(playlist))).EnsureSuccessStatusCode();
    fallback.Upstream.Fault = UpstreamFault.ServerError;

    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{playlist.Id}/share/wrong")).StatusCode);

    (await owner.SendAsync(HttpMethod.Patch, $"/api/playlists/{playlist.Id}/toggle-active")).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(FallbackApp.SharePath(playlist))).StatusCode);

    (await owner.SendAsync(HttpMethod.Patch, $"/api/playlists/{playlist.Id}/toggle-active")).EnsureSuccessStatusCode();
    (await owner.SendAsync(HttpMethod.Delete, $"/api/playlists/{playlist.Id}")).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(FallbackApp.SharePath(playlist))).StatusCode);
  }

  [Fact]
  public async Task DeletePlaylist_ForgetsLastGood()
  {
    using var fallback = new FallbackApp();
    var (owner, playlist) = await fallback.CreateSharedAsync("forget");
    (await fallback.App.CreateClient().GetAsync(FallbackApp.SharePath(playlist))).EnsureSuccessStatusCode();
    var cache = fallback.App.Services.GetRequiredService<PlaylistFileCache>();
    await using (var lease = cache.TryLeaseLastGood(playlist.Id))
    {
      Assert.NotNull(lease);
    }

    (await owner.SendAsync(HttpMethod.Delete, $"/api/playlists/{playlist.Id}")).EnsureSuccessStatusCode();

    Assert.Null(cache.TryLeaseLastGood(playlist.Id));
  }

  [Fact]
  public async Task Analyze_UpstreamError_StillReturnsError()
  {
    using var fallback = new FallbackApp();
    var (owner, playlist) = await fallback.CreateSharedAsync("editor");
    (await fallback.App.CreateClient().GetAsync(FallbackApp.SharePath(playlist))).EnsureSuccessStatusCode();
    var cache = fallback.App.Services.GetRequiredService<PlaylistFileCache>();
    await using (var lease = cache.TryLeaseLastGood(playlist.Id))
    {
      Assert.NotNull(lease);
    }

    fallback.Upstream.Fault = UpstreamFault.ServerError;
    var response = await owner.PostAsync("/api/playlist/analyze", new { sourceUrl = StrimAppFactory.UpstreamUrl });

    Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    Assert.False(response.Headers.Contains("X-Strim-Stale"));
  }

  private sealed record SourceMetadataDto(
    string? SourceHash, string? SourceETag, DateTimeOffset? SourceLastModifiedUtc, long? SourceLengthBytes, DateTimeOffset? SourceCheckedUtc);
}
