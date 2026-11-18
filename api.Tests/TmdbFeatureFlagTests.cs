using System.Text.Json;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Api.Tests;

/// <summary>GET /api/config reports whether TMDB is enabled without exposing the token.</summary>
public sealed class TmdbFeatureFlagTests
{
  private const string TestToken = "test-tmdb-token-not-real-0123456789";

  private static async Task<string> ConfigBody(string? token)
  {
    using var factory = new StrimAppFactory();
    using var host = token is null
      ? factory
      : factory.WithWebHostBuilder(builder => builder.UseSetting(TmdbClient.TokenVariable, token));
    var response = await host.CreateClient().GetAsync("/api/config");
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
  }

  private static bool TmdbEnabled(string body)
  {
    using var json = JsonDocument.Parse(body);
    return json.RootElement.GetProperty("tmdbEnabled").GetBoolean();
  }

  [Fact]
  public async Task Enabled_WhenTokenSet()
  {
    Assert.True(TmdbEnabled(await ConfigBody(TestToken)));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public async Task Disabled_WhenTokenMissingOrBlank(string? token)
  {
    // "Missing" relies on the test environment not setting the variable itself.
    if (token is null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TmdbClient.TokenVariable))) return;
    Assert.False(TmdbEnabled(await ConfigBody(token)));
  }

  [Fact]
  public async Task Response_DoesNotContainToken()
  {
    var body = await ConfigBody(TestToken);
    Assert.DoesNotContain(TestToken, body);
    Assert.DoesNotContain(TestToken[..12], body);
    Assert.DoesNotContain(TestToken[^12..], body);
  }
}
