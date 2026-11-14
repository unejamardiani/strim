using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Api.Tests.Integration;

/// <summary>
/// Hosts the real app against a throwaway SQLite database and cache directory. Upstream playlist
/// downloads go to <see cref="UpstreamPlaylists"/> instead of the network.
/// </summary>
public sealed class StrimAppFactory : WebApplicationFactory<Program>
{
  // A public IP literal passes the SSRF pre-check without a DNS lookup; the stub answers it.
  public const string UpstreamUrl = "http://93.184.215.14/get.php?username=u&password=p";
  public const string GuideHeaderUpstreamUrl = "http://93.184.215.14/get.php?username=u&password=p&type=guide";

  private readonly string _root = Path.Combine(Path.GetTempPath(), $"strim-it-{Guid.NewGuid():N}");

  public Dictionary<string, string> UpstreamPlaylists { get; } = new()
  {
    [UpstreamUrl] = TestPlaylists.Mixed,
    [GuideHeaderUpstreamUrl] = TestPlaylists.WithGuideHeader,
  };

  /// <summary>Scripted upstream answers (status codes, redirects, bodies); checked before <see cref="UpstreamPlaylists"/>.</summary>
  public Dictionary<string, Func<HttpResponseMessage>> UpstreamResponses { get; } = new();

  /// <summary>Every address the stubbed upstream was asked for, in order.</summary>
  public ConcurrentQueue<string> UpstreamRequests { get; } = new();

  /// <summary>Formatted log entries (message and exception) written by the app.</summary>
  public ConcurrentQueue<string> LogEntries { get; } = new();

  /// <summary>Web root for the app; null keeps the default. Set it to <see cref="RepositoryRoot"/> to serve the real pages.</summary>
  public string? WebRoot { get; init; }

  /// <summary>The repository root (where strim.sln, 404.html and 50x.html live), found from the test binaries.</summary>
  public static string RepositoryRoot()
  {
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
      if (File.Exists(Path.Combine(dir.FullName, "strim.sln"))) return dir.FullName;
    }
    throw new InvalidOperationException("Repository root with strim.sln not found.");
  }

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseEnvironment("Development");
    if (WebRoot is not null) builder.UseWebRoot(WebRoot);
    builder.UseSetting("SQLITE_PATH", Path.Combine(_root, "strim.db"));
    builder.UseSetting("PlaylistCache:Directory", Path.Combine(_root, "cache"));
    builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(LogEntries)));
    builder.ConfigureTestServices(services =>
    {
      services.AddHttpClient("fetcher").ConfigurePrimaryHttpMessageHandler(
        () => new StubUpstream(UpstreamPlaylists, UpstreamResponses, UpstreamRequests));
    });
  }

  private sealed class CapturingLoggerProvider(ConcurrentQueue<string> entries) : ILoggerProvider, ILogger
  {
    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      entries.Enqueue(formatter(state, exception) + " " + exception);

    public void Dispose()
    {
    }
  }

  protected override void Dispose(bool disposing)
  {
    base.Dispose(disposing);
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
      // SQLite may still hold the file briefly on some platforms; temp space is reclaimed anyway.
    }
  }

  private sealed class StubUpstream(
    Dictionary<string, string> playlists,
    Dictionary<string, Func<HttpResponseMessage>> responses,
    ConcurrentQueue<string> requests) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var key = request.RequestUri!.ToString();
      requests.Enqueue(key);
      if (responses.TryGetValue(key, out var scripted))
      {
        return Task.FromResult(scripted());
      }

      var response = playlists.TryGetValue(key, out var body)
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-mpegurl") }
        : new HttpResponseMessage(HttpStatusCode.NotFound);
      return Task.FromResult(response);
    }
  }
}

public static class TestPlaylists
{
  public const string GuideHeader =
    "#EXTM3U url-tvg=\"http://epg.example/guide.xml\" x-tvg-url=\"http://epg.example/a.xml,http://epg.example/b.xml\" tvg-url=\"http://epg.example/guide.xml\" tvg-shift=\"+1\"";

  public const string WithGuideHeader = GuideHeader + "\n" + """
    #EXTINF:-1 tvg-name="ARD" group-title="Germany",ARD
    http://line.example/live/u/p/1.ts
    #EXTINF:-1 group-title="Kids",KiKA
    http://line.example/live/u/p/3.ts
    """;

  // "Germany" mixes live channels and series episodes (Xtream Codes style), "Kids" is live only.
  public const string Mixed = """
    #EXTM3U
    #EXTINF:-1 tvg-name="ARD" group-title="Germany",ARD
    http://line.example/live/u/p/1.ts
    #EXTINF:-1 tvg-name="ZDF" group-title="Germany",ZDF
    http://line.example/live/u/p/2.ts
    #EXTINF:-1 group-title="Germany",Tatort S01E01
    http://line.example/series/u/p/10.mkv
    #EXTINF:-1 group-title="Kids",KiKA
    http://line.example/live/u/p/3.ts
    """;
}

/// <summary>Cookie-aware client that sends the antiforgery header on state-changing requests.</summary>
public sealed class ApiClient(HttpClient http)
{
  public HttpClient Http { get; } = http;

  public async Task<string> CsrfTokenAsync()
  {
    var response = await Http.GetFromJsonAsync<TokenResponse>("/api/auth/csrf-token");
    return response!.Token;
  }

  public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool withCsrf = true)
  {
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = JsonContent.Create(body);
    if (withCsrf) request.Headers.Add("X-CSRF-TOKEN", await CsrfTokenAsync());
    return await Http.SendAsync(request);
  }

  public Task<HttpResponseMessage> PostAsync(string path, object? body = null, bool withCsrf = true) =>
    SendAsync(HttpMethod.Post, path, body, withCsrf);

  public async Task<HttpResponseMessage> RegisterAsync(string userName, string password = "Correct-Horse-1")
  {
    var response = await PostAsync("/api/auth/register", new { userName, password });
    response.EnsureSuccessStatusCode();
    return response;
  }

  private sealed record TokenResponse(string Token);
}
