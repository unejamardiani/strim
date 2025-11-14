using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Strim.Tests;

public class SampleHealthCheckTests : IClassFixture<PlaylistApplicationFactory>
{
    private readonly PlaylistApplicationFactory _factory;

    public SampleHealthCheckTests(PlaylistApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsHealthyPayload()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.NotNull(payload);
        Assert.Equal("healthy", payload!.Status);
    }

    private sealed record HealthResponse(string Status, DateTimeOffset Timestamp);
}
