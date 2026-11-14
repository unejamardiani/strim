using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>
/// Pins HTTP behaviour that a framework upgrade could change silently: cookie auth answering API
/// calls with 401 instead of a login redirect, forwarded headers without TRUSTED_PROXIES, the
/// Swagger document and the routing order between static files and endpoints.
/// </summary>
public class UpgradeBehaviourTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  [Fact]
  public async Task Unauthenticated_ProtectedApiCall_Returns401WithoutLocationHeader()
  {
    var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var response = await client.GetAsync("/api/playlists");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    Assert.Null(response.Headers.Location);
  }

  [Fact]
  public async Task ForwardedProto_Https_IsAppliedToRequestScheme()
  {
    // In Development the CSRF cookie is Secure only when the request scheme is HTTPS. Fresh
    // clients, so each request gets a new cookie.
    using var plain = await factory.CreateClient().GetAsync("/api/auth/csrf-token");
    Assert.DoesNotContain("secure", CsrfCookie(plain), StringComparison.OrdinalIgnoreCase);

    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf-token");
    request.Headers.Add("X-Forwarded-Proto", "https");
    using var forwarded = await factory.CreateClient().SendAsync(request);
    Assert.Contains("secure", CsrfCookie(forwarded), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ForwardedFor_SetsRemoteIp()
  {
    // A dedicated host, so the rate-limit partitions start empty. The share route is limited per
    // client IP (30 per minute, queue 1); a wrong link is a cheap 404.
    using var host = new StrimAppFactory();
    var client = host.CreateClient();
    var path = $"/api/playlists/{Guid.NewGuid()}/share/nope";

    for (var i = 0; i < 30; i++)
    {
      Assert.Equal(HttpStatusCode.NotFound, (await Send(client, path, "203.0.113.10")).StatusCode);
    }
    // Of the next two, one waits in the queue for the next window and the other is rejected.
    // The waiting one is never awaited; disposing the host ends it.
    var overflow = new[] { Send(client, path, "203.0.113.10"), Send(client, path, "203.0.113.10") };
    var rejected = await await Task.WhenAny(overflow);
    Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

    // Another forwarded client IP is its own partition and is not limited.
    Assert.Equal(HttpStatusCode.NotFound, (await Send(client, path, "203.0.113.20")).StatusCode);
  }

  [Fact]
  public async Task SwaggerDocument_IsServed()
  {
    var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert.Equal("Strim API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
  }

  [Fact]
  public async Task RoutingOrder_LandingAtRoot_AppAtApp_StillHolds()
  {
    // The test host has no wwwroot of its own; serve the repository's frontend as in the image.
    var client = factory
      .WithWebHostBuilder(builder => builder.UseWebRoot(RepositoryRoot()))
      .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // Identify the pages by stable markers, not by marketing copy: the landing page is a Strim
    // page that links to /app; the editor is the page that loads app.css and main.js.
    var landing = await client.GetAsync("/");
    Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
    var landingHtml = await landing.Content.ReadAsStringAsync();
    Assert.Matches(@"<title>[^<]*\bStrim\b[^<]*</title>", landingHtml);
    Assert.Contains("href=\"/app\"", landingHtml);
    Assert.DoesNotContain("src=\"main.js\"", landingHtml);

    var editor = await client.GetAsync("/app");
    Assert.Equal(HttpStatusCode.OK, editor.StatusCode);
    var editorHtml = await editor.Content.ReadAsStringAsync();
    Assert.Contains("href=\"app.css\"", editorHtml);
    Assert.Contains("src=\"main.js\"", editorHtml);

    var unknownApi = await client.GetAsync("/api/does-not-exist");
    Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
    Assert.Equal("application/json", unknownApi.Content.Headers.ContentType?.MediaType);
  }

  private static string CsrfCookie(HttpResponseMessage response)
  {
    Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
    return Assert.Single(cookies, c => c.StartsWith("strim.csrf=", StringComparison.Ordinal));
  }

  private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string forwardedFor)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Add("X-Forwarded-For", forwardedFor);
    return await client.SendAsync(request);
  }

  private static string RepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "strim.sln")))
    {
      directory = directory.Parent;
    }
    return directory?.FullName ?? throw new InvalidOperationException("strim.sln not found above the test output.");
  }
}
