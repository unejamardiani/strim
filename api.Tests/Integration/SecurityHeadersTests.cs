using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>Security headers on pages, API answers and the 404 page; no-store on API answers.</summary>
public sealed class SecurityHeadersTests : IDisposable
{
  private readonly StrimAppFactory _factory = new() { WebRoot = StrimAppFactory.RepositoryRoot() };

  public void Dispose() => _factory.Dispose();

  private static void AssertAlwaysOnHeaders(HttpResponseMessage response)
  {
    var path = response.RequestMessage!.RequestUri!.AbsolutePath;
    Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff), path);
    Assert.Equal("nosniff", Assert.Single(nosniff));
    Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var referrer), path);
    Assert.Equal("strict-origin-when-cross-origin", Assert.Single(referrer));
    Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frame), path);
    Assert.Equal("DENY", Assert.Single(frame));
    Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var csp), path);
    Assert.Equal("frame-ancestors 'none'", Assert.Single(csp));
  }

  private async Task<(ApiClient Client, PlaylistDto Playlist)> CreatePlaylistAsync(string userName)
  {
    var client = new ApiClient(_factory.CreateClient());
    await client.RegisterAsync(userName);
    var response = await client.PostAsync("/api/playlists", new { name = "Headers", sourceUrl = StrimAppFactory.UpstreamUrl });
    response.EnsureSuccessStatusCode();
    return (client, (await response.Content.ReadFromJsonAsync<PlaylistDto>())!);
  }

  [Fact]
  public async Task EveryResponse_CarriesTheAlwaysOnHeaders()
  {
    var (client, _) = await CreatePlaylistAsync("headers-everywhere");

    foreach (var path in new[] { "/", "/app", "/app.html", "/api/auth/me", "/api/playlists", "/no-such-page" })
    {
      var response = await client.Http.GetAsync(path);
      Assert.Equal(path == "/no-such-page" ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
      AssertAlwaysOnHeaders(response);
    }
  }

  [Fact]
  public async Task ApiAnswers_AreNoStore_AndTheShareRouteKeepsItsOwnPolicy()
  {
    var (client, playlist) = await CreatePlaylistAsync("headers-no-store");

    foreach (var path in new[] { "/api/playlists", $"/api/playlists/{playlist.Id}" })
    {
      var response = await client.Http.GetAsync(path);
      response.EnsureSuccessStatusCode();
      Assert.True(response.Headers.CacheControl!.NoStore, path);
      Assert.Equal("no-store", response.Headers.CacheControl.ToString());
    }

    var share = await _factory.CreateClient().GetAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}");
    share.EnsureSuccessStatusCode();
    var cacheControl = share.Headers.CacheControl!;
    Assert.True(cacheControl.Private);
    Assert.True(cacheControl.NoStore);
    AssertAlwaysOnHeaders(share);
  }

  [Fact]
  public async Task Hsts_IsSentOverHttpsInProduction()
  {
    using var production = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
    var client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    var response = await client.GetAsync("/");

    Assert.True(response.Headers.TryGetValues("Strict-Transport-Security", out var hsts));
    Assert.Equal("max-age=31536000", Assert.Single(hsts));
  }

  [Fact]
  public async Task Hsts_IsAbsentInDevelopment()
  {
    var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    var response = await client.GetAsync("/");

    Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    AssertAlwaysOnHeaders(response);
  }
}
