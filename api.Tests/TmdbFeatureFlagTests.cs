using System.Text.Json;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Api.Tests;

/// <summary>GET /api/config reports whether TMDB is enabled without exposing the token.</summary>
public sealed class TmdbFeatureFlagTests
{
  private const string TestToken = "test-tmdb-token-not-real-0123456789";

  /// <param name="token">The token the test host sees; null means unset.</param>
  /// <param name="machineToken">Stands in for a TMDB_READ_ACCESS_TOKEN set on the machine running the tests.</param>
  private static async Task<string> ConfigBody(string? token, string? machineToken = null)
  {
    using var factory = new StrimAppFactory();
    using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
    {
      if (machineToken is not null)
        config.AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = machineToken });
      // Added after the default sources (environment variables included), so this value wins, null included.
      config.AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = token });
    }));
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
    Assert.False(TmdbEnabled(await ConfigBody(token)));
  }

  [Fact]
  public async Task Disabled_WhenTokenUnset_EvenIfMachineSetsOne()
  {
    Assert.False(TmdbEnabled(await ConfigBody(null, machineToken: TestToken)));
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
