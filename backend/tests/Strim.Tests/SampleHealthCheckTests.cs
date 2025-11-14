using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Strim.Tests;

public class SampleHealthCheckTests
{
    [Fact]
    public async Task HealthEndpoint_ReturnsHealthyPayload()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.NotNull(payload);
        Assert.Equal("healthy", payload!.Status);
    }

    private sealed record HealthResponse(string Status, DateTimeOffset Timestamp);
}
