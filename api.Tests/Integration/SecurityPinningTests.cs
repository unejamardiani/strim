using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Configuration;
using Api.Endpoints;
using Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>
/// Pins security controls that already hold (#549), so a later change cannot drop one silently.
/// </summary>
public sealed class SecurityPinningTests : IDisposable
{
  private const string Base = "http://93.184.215.14";
  private readonly StrimAppFactory _factory = new();

  public void Dispose() => _factory.Dispose();

  // ---- The fetcher's production primary handler, not the test stub ----

  private static SocketsHttpHandler ProductionFetcherHandler()
  {
    var services = new ServiceCollection()
      .AddLogging()
      .AddStrimPlaylistServices(new ConfigurationBuilder().Build())
      .BuildServiceProvider();
    HttpMessageHandler handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(PlaylistServices.FetcherClient);
    while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
    return Assert.IsType<SocketsHttpHandler>(handler);
  }

  [Fact]
  public void FetcherHandler_UsesNoProxy_FollowsNoRedirect_AndDoesNotDecompress()
  {
    var handler = ProductionFetcherHandler();

    Assert.False(handler.UseProxy);
    Assert.False(handler.AllowAutoRedirect);
    Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    Assert.NotNull(handler.ConnectCallback);
  }

  [Theory]
  [InlineData("http://127.0.0.1:9/")]
  [InlineData("http://[::1]:9/")]
  public async Task FetcherHandler_RefusesLoopbackAtConnectTime(string target)
  {
    // Straight to the handler: no URL pre-check of PlaylistSourceFetcher runs before the connect.
    using var client = new HttpClient(ProductionFetcherHandler());

    var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(target));

    var messages = string.Join(" | ", Chain(error).Select(e => e.Message));
    Assert.Contains("internal or private network addresses is not allowed", messages);
  }

  private static IEnumerable<Exception> Chain(Exception? error)
  {
    for (; error is not null; error = error.InnerException) yield return error;
  }

  // ---- The main playlist download ----

  [Theory]
  [InlineData("http://127.0.0.1/get.php")]
  [InlineData("http://10.0.0.5/get.php")]
  [InlineData("http://192.168.1.1/get.php")]
  public async Task FetchAsync_RefusesRedirectToPrivateAddress(string target)
  {
    var fetcher = _factory.Services.GetRequiredService<PlaylistSourceFetcher>();
    _factory.UpstreamResponses[$"{Base}/start"] = () =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.Found);
      response.Headers.Location = new Uri(target);
      return response;
    };
    var reached = false;
    _factory.UpstreamResponses[target] = () =>
    {
      reached = true;
      return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TestPlaylists.Mixed, Encoding.UTF8) };
    };

    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.FetchAsync($"{Base}/start", null, default));

    Assert.Contains("internal or private", error.Message);
    Assert.False(reached);
  }

  // ---- CSRF inventory ----

  /// <summary>
  /// The only non-GET /api routes without the CSRF filter. Each changes no stored state: anonymous,
  /// stateless processing or a read-only search or lookup. A new route belongs here only for that reason.
  /// </summary>
  private static readonly HashSet<string> CsrfExempt = new(StringComparer.Ordinal)
  {
    "POST /api/playlist/analyze",
    "POST /api/playlist/generate",
    "POST /api/playlist/search",
    "POST /api/library/search",
    "POST /api/playlists/{id:guid}/library/search",
    "POST /api/playlists/{id:guid}/library/posters",
    "POST /api/playlists/{id:guid}/library/titles/lookup",
    // The JSON 404 for unknown /api routes.
    "* /api/{**path}",
  };

  [Fact]
  public async Task EveryNonGetApiRoute_RequiresCsrfToken_OrIsListedAsExempt()
  {
    var client = new ApiClient(_factory.CreateClient());
    await client.RegisterAsync("csrf-inventory");

    var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
      .OfType<RouteEndpoint>()
      .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
      .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
        .Where(method => method is not ("GET" or "HEAD" or "OPTIONS"))
        .Select(method => (Method: method, Pattern: e.RoutePattern.RawText!)))
      .ToList();
    Assert.Contains(routes, r => r is { Method: "PATCH", Pattern: "/api/playlists/{id:guid}/toggle-active" });

    var unprotected = new List<string>();
    foreach (var (method, pattern) in routes.Where(r => !CsrfExempt.Contains($"{r.Method} {r.Pattern}")))
    {
      if (method == "*")
      {
        unprotected.Add($"{method} {pattern} (no method restriction)");
        continue;
      }
      // Signed in but without X-CSRF-TOKEN: the filter must refuse before the handler runs.
      using var request = new HttpRequestMessage(new HttpMethod(method), Concrete(pattern))
      {
        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
      };
      var response = await client.Http.SendAsync(request);
      var body = await response.Content.ReadAsStringAsync();
      if (response.StatusCode != HttpStatusCode.BadRequest || !body.Contains(EndpointFilters.CsrfError))
      {
        unprotected.Add($"{method} {pattern} -> {(int)response.StatusCode}");
      }
    }

    Assert.Empty(unprotected);
  }

  private static string Concrete(string pattern) =>
    Regex.Replace(pattern, @"\{[^}]+\}", match => match.Value.Contains(":guid") ? Guid.NewGuid().ToString() : "x");

  // ---- Frontend HTML injection ----

  [Fact]
  public void InnerHtml_IsUsedOnlyInTheIconHelper_WithTheConstantIconMap()
  {
    var root = StrimAppFactory.RepositoryRoot();
    var main = File.ReadAllText(Path.Combine(root, "main.js"));
    var library = File.ReadAllText(Path.Combine(root, "library.js"));

    Assert.DoesNotContain("innerHTML", library);
    Assert.Single(Regex.Matches(main, "innerHTML"));
    Assert.Contains(
      "function icon(name) {\n  const tpl = document.createElement('template');\n" +
      "  tpl.innerHTML = `<svg class=\"icon\" viewBox=\"0 0 24 24\" aria-hidden=\"true\">${ICONS[name]}</svg>`;",
      main.Replace("\r\n", "\n"));
    Assert.Single(Regex.Matches(main, @"^const ICONS = \{", RegexOptions.Multiline));
    Assert.DoesNotMatch(@"ICONS(\[[^\]]+\]|\.\w+)\s*=[^=]", main);
  }

  // ---- Production cookies and CORS ----

  private WebApplicationFactory<Program> Production() =>
    _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

  [Fact]
  public async Task Production_AuthAndCsrfCookies_AreHostPrefixed_HttpOnly_Secure()
  {
    using var app = Production();
    var client = new ApiClient(app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }));

    var token = await client.Http.GetAsync("/api/auth/csrf-token");
    var register = await client.RegisterAsync("prod-cookies");

    AssertCookie(token, "__Host-strim.csrf");
    AssertCookie(register, "__Host-strim.auth");
  }

  private static void AssertCookie(HttpResponseMessage response, string name)
  {
    Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies), name);
    var cookie = Assert.Single(cookies, c => c.StartsWith(name + "=", StringComparison.Ordinal)).ToLowerInvariant();
    Assert.Contains("; httponly", cookie);
    Assert.Contains("; secure", cookie);
    Assert.Contains("; path=/", cookie);
    Assert.DoesNotContain("; domain=", cookie);
  }

  [Fact]
  public async Task Production_WithoutAllowedOrigins_CorsAllowsNoOrigin()
  {
    using var app = Production();
    var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/playlists");
    preflight.Headers.Add("Origin", "https://other.example");
    preflight.Headers.Add("Access-Control-Request-Method", "POST");
    using var simple = new HttpRequestMessage(HttpMethod.Get, "/api/config");
    simple.Headers.Add("Origin", "http://localhost:8000");

    foreach (var request in new[] { preflight, simple })
    {
      var response = await client.SendAsync(request);
      Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"), request.RequestUri!.ToString());
      Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"), request.RequestUri!.ToString());
    }
  }

  // ---- Share route and /api/config ----

  [Fact]
  public async Task ShareRoute_AnswersM3u_PrivateNoStore_AsAttachment()
  {
    var owner = new ApiClient(_factory.CreateClient());
    await owner.RegisterAsync("share-headers");
    var created = await owner.PostAsync("/api/playlists", new
    {
      name = "Sofa", sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups = Array.Empty<string>(),
    });
    var playlist = (await created.Content.ReadFromJsonAsync<PlaylistDto>())!;

    var response = await _factory.CreateClient().GetAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("application/x-mpegurl", response.Content.Headers.ContentType?.MediaType);
    Assert.True(response.Headers.CacheControl?.Private);
    Assert.True(response.Headers.CacheControl?.NoStore);
    Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
  }

  [Fact]
  public async Task Config_ExposesExactlyThreeFields()
  {
    var config = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/config");

    Assert.Equal(
      new[] { "publicShareBaseUrl", "shareAccessNote", "tmdbEnabled" },
      config.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
  }
}
