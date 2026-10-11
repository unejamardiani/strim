using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

/// <summary>
/// Hosts the real app against a throwaway SQLite database and cache directory. Upstream playlist
/// downloads go to <see cref="UpstreamPlaylists"/> instead of the network.
/// </summary>
public sealed class StrimAppFactory : WebApplicationFactory<Program>
{
  // A public IP literal passes the SSRF pre-check without a DNS lookup; the stub answers it.
  public const string UpstreamUrl = "http://93.184.215.14/get.php?username=u&password=p";

  private readonly string _root = Path.Combine(Path.GetTempPath(), $"strim-it-{Guid.NewGuid():N}");

  public Dictionary<string, string> UpstreamPlaylists { get; } = new()
  {
    [UpstreamUrl] = TestPlaylists.Mixed,
  };

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseEnvironment("Development");
    builder.UseSetting("SQLITE_PATH", Path.Combine(_root, "strim.db"));
    builder.UseSetting("PlaylistCache:Directory", Path.Combine(_root, "cache"));
    builder.ConfigureTestServices(services =>
    {
      services.AddHttpClient("fetcher").ConfigurePrimaryHttpMessageHandler(() => new StubUpstream(UpstreamPlaylists));
    });
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

  private sealed class StubUpstream(Dictionary<string, string> playlists) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var key = request.RequestUri!.ToString();
      var response = playlists.TryGetValue(key, out var body)
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-mpegurl") }
        : new HttpResponseMessage(HttpStatusCode.NotFound);
      return Task.FromResult(response);
    }
  }
}

public static class TestPlaylists
{
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
