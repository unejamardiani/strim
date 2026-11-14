using System.Net;
using System.Text;
using Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests.Integration;

public sealed class SmallTextFetchTests : IDisposable
{
  private const string Base = "http://93.184.215.14";
  private readonly StrimAppFactory _factory = new();
  private readonly CapturingLogger _logger = new();
  private readonly PlaylistSourceFetcher _fetcher;

  public SmallTextFetchTests()
  {
    _fetcher = ActivatorUtilities.CreateInstance<PlaylistSourceFetcher>(_factory.Services, _logger);
  }

  public void Dispose() => _factory.Dispose();

  [Fact]
  public async Task OkAnswer_ReturnsBodyText()
  {
    _factory.UpstreamResponses[$"{Base}/player_api.php?username=u&password=p"] =
      () => Text(HttpStatusCode.OK, "{\"user_info\":{\"status\":\"Active\"}}");

    var result = await _fetcher.FetchSmallTextAsync($"{Base}/player_api.php?username=u&password=p", default);

    Assert.True(result.Success);
    Assert.Equal("{\"user_info\":{\"status\":\"Active\"}}", result.Text);
    Assert.Equal(200, result.StatusCode);
  }

  [Theory]
  [InlineData("http://127.0.0.1/player_api.php")]
  [InlineData("http://10.0.0.5/player_api.php")]
  [InlineData("http://localhost/player_api.php")]
  public async Task RedirectToPrivateOrLoopbackAddress_IsRefused(string target)
  {
    _factory.UpstreamResponses[$"{Base}/start"] = () => Redirect(target);
    var reached = false;
    _factory.UpstreamResponses[target] = () =>
    {
      reached = true;
      return Text(HttpStatusCode.OK, "internal");
    };

    var result = await _fetcher.FetchSmallTextAsync($"{Base}/start", default);

    Assert.False(result.Success);
    Assert.Null(result.Text);
    Assert.Contains("internal or private", result.Error);
    Assert.False(reached);
  }

  [Fact]
  public async Task FiveRedirects_AreFollowed_ButSixAreRefused()
  {
    for (var i = 0; i < 6; i++)
    {
      var next = $"{Base}/r{i + 1}";
      _factory.UpstreamResponses[$"{Base}/r{i}"] = () => Redirect(next);
    }

    _factory.UpstreamResponses[$"{Base}/r6"] = () => Text(HttpStatusCode.OK, "end");
    _factory.UpstreamResponses[$"{Base}/r5"] = () => Text(HttpStatusCode.OK, "five");

    var five = await _fetcher.FetchSmallTextAsync($"{Base}/r0", default);
    Assert.True(five.Success);
    Assert.Equal("five", five.Text);

    _factory.UpstreamResponses[$"{Base}/r5"] = () => Redirect($"{Base}/r6");
    var six = await _fetcher.FetchSmallTextAsync($"{Base}/r0", default);
    Assert.False(six.Success);
    Assert.Equal("Too many redirects", six.Error);
  }

  [Fact]
  public async Task BodyAboveCap_WithoutLength_IsRefusedWithoutReadingItAll()
  {
    var stream = new EndlessStream(10 * 1024 * 1024);
    _factory.UpstreamResponses[$"{Base}/big"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    var result = await _fetcher.FetchSmallTextAsync($"{Base}/big", default);

    Assert.False(result.Success);
    Assert.Equal("Response is too large", result.Error);
    Assert.True(stream.BytesRead <= PlaylistSourceFetcher.SmallTextMaxBytes + 64 * 1024, $"read {stream.BytesRead} bytes");
  }

  [Fact]
  public async Task BodyAboveCap_WithDeclaredLength_IsRefusedBeforeReading()
  {
    var stream = new EndlessStream(PlaylistSourceFetcher.SmallTextMaxBytes + 1);
    _factory.UpstreamResponses[$"{Base}/declared"] = () =>
    {
      var content = new StreamContent(stream);
      content.Headers.ContentLength = PlaylistSourceFetcher.SmallTextMaxBytes + 1;
      return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    };

    var result = await _fetcher.FetchSmallTextAsync($"{Base}/declared", default);

    Assert.False(result.Success);
    Assert.Equal("Response is too large", result.Error);
    Assert.Equal(0, stream.BytesRead);
  }

  [Theory]
  [InlineData(HttpStatusCode.NotFound)]
  [InlineData(HttpStatusCode.Forbidden)]
  [InlineData(HttpStatusCode.InternalServerError)]
  public async Task NonSuccessStatus_ReturnsFailureResult(HttpStatusCode status)
  {
    _factory.UpstreamResponses[$"{Base}/status"] = () => Text(status, "nope");

    var result = await _fetcher.FetchSmallTextAsync($"{Base}/status", default);

    Assert.False(result.Success);
    Assert.Null(result.Text);
    Assert.Equal((int)status, result.StatusCode);
    Assert.Equal($"Upstream returned {(int)status}", result.Error);
  }

  [Fact]
  public async Task FailureMessagesAndLogs_DoNotContainTheQueryString()
  {
    var failing = $"{Base}/player_api.php?username=u&password=secret";
    var redirecting = $"{Base}/get.php?username=u&password=secret";
    var tooLarge = $"{Base}/big.php?username=u&password=secret";
    _factory.UpstreamResponses[failing] = () => Text(HttpStatusCode.Unauthorized, "denied");
    _factory.UpstreamResponses[redirecting] = () => Redirect("http://127.0.0.1/player_api.php?username=u&password=secret");
    _factory.UpstreamResponses[tooLarge] = () => Text(HttpStatusCode.OK, new string('x', PlaylistSourceFetcher.SmallTextMaxBytes + 1));

    var results = new[]
    {
      await _fetcher.FetchSmallTextAsync(failing, default),
      await _fetcher.FetchSmallTextAsync(redirecting, default),
      await _fetcher.FetchSmallTextAsync(tooLarge, default),
      await _fetcher.FetchSmallTextAsync("http://127.0.0.1/player_api.php?username=u&password=secret", default),
    };

    Assert.All(results, r =>
    {
      Assert.False(r.Success);
      Assert.DoesNotContain("secret", r.Error);
    });
    Assert.NotEmpty(_logger.Entries);
    Assert.All(_logger.Entries, entry => Assert.DoesNotContain("secret", entry));
  }

  private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
    new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Redirect(string location)
  {
    var response = new HttpResponseMessage(HttpStatusCode.Found);
    response.Headers.Location = new Uri(location);
    return response;
  }

  private sealed class EndlessStream(long length) : Stream
  {
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
      var read = (int)Math.Min(count, length - BytesRead);
      Array.Fill(buffer, (byte)'a', offset, read);
      BytesRead += read;
      return read;
    }
  }

  private sealed class CapturingLogger : ILogger<PlaylistSourceFetcher>
  {
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      lock (Entries)
      {
        Entries.Add(formatter(state, exception) + " " + exception);
      }
    }
  }
}
