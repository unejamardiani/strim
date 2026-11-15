using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>
/// Captures every log entry of the host, at every level and in every category (including
/// System.Net.Http.HttpClient.*), and proves that upstream fetches never write the source path,
/// query string or credentials.
/// </summary>
public sealed class FetchLoggingTests : IDisposable
{
  private const string Base = "http://93.184.215.14";
  private const string UserName = "u-k7q2xw";
  private const string Password = "pw-z9m4vt";
  private const string Query = $"username={UserName}&password={Password}";
  private const string PathSegment = "pathmark-r3j8";

  private readonly StrimAppFactory _factory = new();
  private readonly WebApplicationFactory<Program> _host;
  private readonly ConcurrentQueue<string> _entries = new();

  public FetchLoggingTests()
  {
    _host = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
    {
      logging.AddProvider(new AllEntriesProvider(_entries));
      // A provider-specific rule beats the category levels from appsettings, so nothing is filtered.
      logging.AddFilter<AllEntriesProvider>(null, LogLevel.Trace);
    }));
  }

  public void Dispose()
  {
    _host.Dispose();
    _factory.Dispose();
  }

  [Fact]
  public async Task FetchAsync_And_FetchSmallTextAsync_NeverLogPathQueryOrCredentials()
  {
    var ok = $"{Base}/{PathSegment}/ok.php?{Query}";
    var error = $"{Base}/{PathSegment}/error.php?{Query}";
    var redirect = $"{Base}/{PathSegment}/redirect.php?{Query}";
    var target = $"{Base}/{PathSegment}/target.php?{Query}";
    _factory.UpstreamResponses[ok] = () => Text(HttpStatusCode.OK, "#EXTM3U\n");
    _factory.UpstreamResponses[error] = () => Text(HttpStatusCode.InternalServerError, "failed");
    _factory.UpstreamResponses[redirect] = () =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.Found);
      response.Headers.Location = new Uri(target);
      return response;
    };
    _factory.UpstreamResponses[target] = () => Text(HttpStatusCode.OK, "#EXTM3U\n");

    var fetcher = _host.Services.GetRequiredService<PlaylistSourceFetcher>();

    var downloaded = await fetcher.FetchAsync(ok, null, default);
    Assert.NotNull(downloaded.DownloadedFile);
    var redirected = await fetcher.FetchAsync(redirect, null, default);
    Assert.NotNull(redirected.DownloadedFile);
    var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.FetchAsync(error, null, default));
    Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
    // The downloaded temporary files live in the factory's throwaway directory, removed on dispose.
    downloaded.DownloadedFile!.DiskReservation?.Dispose();
    redirected.DownloadedFile!.DiskReservation?.Dispose();

    Assert.True((await fetcher.FetchSmallTextAsync(ok, default)).Success);
    Assert.True((await fetcher.FetchSmallTextAsync(redirect, default)).Success);
    Assert.False((await fetcher.FetchSmallTextAsync(error, default)).Success);

    // The stub was really asked for every address, including the redirect target.
    Assert.Contains(target, _factory.UpstreamRequests);
    Assert.NotEmpty(_entries);
    Assert.All(_entries, entry =>
    {
      Assert.DoesNotContain(UserName, entry);
      Assert.DoesNotContain(Password, entry);
      Assert.DoesNotContain(Query, entry);
      Assert.DoesNotContain(PathSegment, entry);
    });
  }

  private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
    new(status) { Content = new StringContent(body, Encoding.UTF8, "application/x-mpegurl") };

  private sealed class AllEntriesProvider(ConcurrentQueue<string> entries) : ILoggerProvider, ILogger
  {
    public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, entries);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CategoryLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull
      {
        entries.Enqueue($"{category} scope: {state}");
        return null;
      }

      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      {
        // State values are recorded too, in case a structured property carries the URL.
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
          ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
          : string.Empty;
        entries.Enqueue($"{category} [{logLevel}] {formatter(state, exception)} {values} {exception}");
      }
    }
  }
}
