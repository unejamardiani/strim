using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>
/// A stalled or slow upstream under the job gate (#544): idle body reads, the short timeout for small answers and
/// provider account lookups outside the gate.
/// </summary>
public sealed class FetchIdleTimeoutTests : IDisposable
{
  private const string Base = "http://93.184.215.14";
  private const string StalledUrl = $"{Base}/stalled.m3u";
  private const string SlowUrl = $"{Base}/slow.m3u";
  private const string AccountPlaylistUrl = $"{Base}/get.php?username=idle544&password=NotReal544";
  private const string AccountUrl = $"{Base}/player_api.php?username=idle544&password=NotReal544";

  private readonly StrimAppFactory _upstream = new();
  private WebApplicationFactory<Program>? _app;

  public void Dispose()
  {
    _app?.Dispose();
    _upstream.Dispose();
  }

  /// <summary>
  /// Starts the app with a one-second idle timeout. A gated request waits three seconds: long enough to outlast a
  /// stalled read, shorter than a slow account answer.
  /// </summary>
  private WebApplicationFactory<Program> StartApp(int smallTextTimeoutSeconds = 20)
  {
    _app = _upstream.WithWebHostBuilder(builder => builder
      .UseSetting("PlaylistCache:IdleReadTimeoutSeconds", "1")
      .UseSetting("PlaylistCache:QueueTimeoutSeconds", "3")
      .UseSetting("PlaylistCache:SmallTextTimeoutSeconds", smallTextTimeoutSeconds.ToString()));
    return _app;
  }

  [Fact]
  public async Task StalledBody_FailsWithinSeconds_AndTheNextGatedRequestSucceeds()
  {
    _upstream.UpstreamResponses[StalledUrl] = () => Body(Encoding.UTF8.GetBytes(TestPlaylists.Mixed), Timeout.InfiniteTimeSpan);
    var owner = new ApiClient(StartApp().CreateClient());
    await owner.RegisterAsync("idle-editor");

    var watch = Stopwatch.StartNew();
    var stalled = await owner.PostAsync("/api/playlist/analyze", new { sourceUrl = StalledUrl });
    watch.Stop();

    Assert.Equal(HttpStatusCode.GatewayTimeout, stalled.StatusCode);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The stalled fetch took {watch.Elapsed}.");
    var next = await owner.PostAsync("/api/playlist/analyze", new { sourceUrl = StrimAppFactory.UpstreamUrl });
    Assert.Equal(HttpStatusCode.OK, next.StatusCode);
  }

  [Fact]
  public async Task SlowButSteadyBody_StillCompletes()
  {
    // 64-byte chunks every 200 ms take well over the one-second idle timeout in total.
    _upstream.UpstreamResponses[SlowUrl] = () => Body(Encoding.UTF8.GetBytes(TestPlaylists.Mixed), TimeSpan.FromMilliseconds(200));
    var owner = new ApiClient(StartApp().CreateClient());
    await owner.RegisterAsync("slow-editor");

    var watch = Stopwatch.StartNew();
    var response = await owner.PostAsync("/api/playlist/analyze", new { sourceUrl = SlowUrl });
    watch.Stop();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Contains("\"totalChannels\":4", await response.Content.ReadAsStringAsync());
    Assert.True(watch.Elapsed > TimeSpan.FromSeconds(1), "The body must take longer than the idle timeout.");
  }

  [Fact]
  public async Task RefreshPass_WithStalledPlaylist_FinishesWithinIdleTimeout_AndServesAnotherPlayerLink()
  {
    _upstream.UpstreamResponses[StalledUrl] = () => Body(Encoding.UTF8.GetBytes(TestPlaylists.Mixed), Timeout.InfiniteTimeSpan);
    var app = StartApp();
    await WaitForStartupPassAsync();
    await SeedAsync(app, StalledUrl, autoRefresh: true);
    var shared = await SeedAsync(app, StrimAppFactory.UpstreamUrl, autoRefresh: false);

    var watch = Stopwatch.StartNew();
    var pass = ActivatorUtilities.CreateInstance<PlaylistRefreshService>(app.Services).RefreshPlaylistsAsync(CancellationToken.None);
    await WaitForRequestAsync(StalledUrl);
    var share = await app.CreateClient().GetAsync($"/api/playlists/{shared.Id}/share/{shared.ShareCode}");
    await pass.WaitAsync(TimeSpan.FromSeconds(10));
    watch.Stop();

    Assert.Equal(HttpStatusCode.OK, share.StatusCode);
    Assert.Contains("#EXTINF", await share.Content.ReadAsStringAsync());
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The refresh pass took {watch.Elapsed}.");
  }

  [Fact]
  public async Task Analyze_LooksUpTheAccountAfterReleasingTheGate()
  {
    _upstream.UpstreamPlaylists[AccountPlaylistUrl] = TestPlaylists.Mixed;
    _upstream.UpstreamResponses[AccountUrl] = EndlessTrickle;
    var app = StartApp(smallTextTimeoutSeconds: 8);
    var owner = new ApiClient(app.CreateClient());
    await owner.RegisterAsync("account-editor");

    var analyze = owner.PostAsync("/api/playlist/analyze", new { sourceUrl = AccountPlaylistUrl });
    await WaitForRequestAsync(AccountUrl);
    await using (var lease = await app.Services.GetRequiredService<PlaylistJobGate>().TryEnterAsync(CancellationToken.None))
    {
      Assert.NotNull(lease);
    }

    Assert.Equal(HttpStatusCode.OK, (await analyze).StatusCode);
  }

  [Fact]
  public async Task RefreshPass_LooksUpTheAccountAfterReleasingTheGate()
  {
    _upstream.UpstreamPlaylists[AccountPlaylistUrl] = TestPlaylists.Mixed;
    _upstream.UpstreamResponses[AccountUrl] = EndlessTrickle;
    var app = StartApp(smallTextTimeoutSeconds: 8);
    await WaitForStartupPassAsync();
    await SeedAsync(app, AccountPlaylistUrl, autoRefresh: true);

    var pass = ActivatorUtilities.CreateInstance<PlaylistRefreshService>(app.Services).RefreshPlaylistsAsync(CancellationToken.None);
    await WaitForRequestAsync(AccountUrl);
    await using (var lease = await app.Services.GetRequiredService<PlaylistJobGate>().TryEnterAsync(CancellationToken.None))
    {
      Assert.NotNull(lease);
    }

    await pass;
  }

  [Fact]
  public async Task SmallText_UsesItsOwnOverallTimeout()
  {
    _upstream.UpstreamResponses[AccountUrl] = EndlessTrickle;
    var fetcher = StartApp(smallTextTimeoutSeconds: 2).Services.GetRequiredService<PlaylistSourceFetcher>();

    var watch = Stopwatch.StartNew();
    var result = await fetcher.FetchSmallTextAsync(AccountUrl, CancellationToken.None);
    watch.Stop();

    Assert.False(result.Success);
    Assert.Equal("The request timed out", result.Error);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"The small text fetch took {watch.Elapsed}.");
  }

  /// <summary>Two bytes every 200 ms forever: never trips the idle timeout, only the small-text timeout ends it.</summary>
  private static HttpResponseMessage EndlessTrickle() =>
    Body("{}"u8.ToArray(), TimeSpan.FromMilliseconds(200), endless: true);

  private static HttpResponseMessage Body(byte[] body, TimeSpan pause, bool endless = false) => new(HttpStatusCode.OK)
  {
    Content = new StreamContent(new PacedStream(body, pause, endless))
    {
      Headers = { ContentType = new MediaTypeHeaderValue("application/x-mpegurl") },
    },
  };

  /// <summary>The host's own first refresh pass would race the pass under test; wait until it has run.</summary>
  private async Task WaitForStartupPassAsync()
  {
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!_upstream.LogEntries.Any(e => e.StartsWith("Refreshing ")) && DateTime.UtcNow < deadline)
    {
      await Task.Delay(20);
    }
  }

  private async Task WaitForRequestAsync(string url)
  {
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!_upstream.UpstreamRequests.Contains(url) && DateTime.UtcNow < deadline)
    {
      await Task.Delay(20);
    }

    Assert.Contains(url, _upstream.UpstreamRequests);
  }

  private static async Task<Playlist> SeedAsync(WebApplicationFactory<Program> app, string sourceUrl, bool autoRefresh)
  {
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var playlist = new Playlist
    {
      Id = Guid.NewGuid(),
      Name = "Idle test",
      SourceUrl = sourceUrl,
      ShareCode = Guid.NewGuid().ToString("N"),
      AutoRefreshEnabled = autoRefresh,
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
    db.Playlists.Add(playlist);
    await db.SaveChangesAsync();
    return playlist;
  }

  /// <summary>
  /// Sends <paramref name="body"/> in 64-byte chunks with <paramref name="pause"/> before each (infinite: no byte
  /// ever arrives), then ends or, when <paramref name="endless"/>, starts over.
  /// </summary>
  private sealed class PacedStream(byte[] body, TimeSpan pause, bool endless) : Stream
  {
    private int _position;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      if (_position >= body.Length)
      {
        if (!endless) return 0;
        _position = 0;
      }

      await Task.Delay(pause, cancellationToken);
      var count = Math.Min(Math.Min(64, buffer.Length), body.Length - _position);
      body.AsMemory(_position, count).CopyTo(buffer);
      _position += count;
      return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
      ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
