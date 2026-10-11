using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Api.Tests.Integration;

public class AuthEndpointTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private ApiClient NewClient() => new(factory.CreateClient());

  [Fact]
  public async Task Register_WithoutCsrfToken_IsRejected()
  {
    var client = NewClient();
    var response = await client.PostAsync("/api/auth/register", new { userName = "nocsrf", password = "Correct-Horse-1" }, withCsrf: false);
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  }

  [Fact]
  public async Task Register_WithWeakPassword_IsRejected()
  {
    var client = NewClient();
    var response = await client.PostAsync("/api/auth/register", new { userName = "weak", password = "short" });
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  }

  [Fact]
  public async Task Register_Me_Logout_Login_RoundTrip()
  {
    var client = NewClient();
    await client.RegisterAsync("roundtrip");

    var me = await client.Http.GetFromJsonAsync<UserResponse>("/api/auth/me");
    Assert.Equal("roundtrip", me!.UserName);

    (await client.PostAsync("/api/auth/logout")).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.Http.GetAsync("/api/auth/me")).StatusCode);

    var wrong = await client.PostAsync("/api/auth/login", new { userName = "roundtrip", password = "Wrong-Horse-1" });
    Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

    (await client.PostAsync("/api/auth/login", new { userName = "roundtrip", password = "Correct-Horse-1" })).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.OK, (await client.Http.GetAsync("/api/auth/me")).StatusCode);
  }

  [Fact]
  public async Task Providers_AreEmptyWithoutConfiguration()
  {
    var providers = await NewClient().Http.GetFromJsonAsync<List<object>>("/api/auth/providers");
    Assert.Empty(providers!);
  }

  [Fact]
  public async Task ExternalLogin_ForUnconfiguredProvider_IsNotFound()
  {
    var response = await NewClient().Http.GetAsync("/api/auth/external/github?returnUrl=/app");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  private sealed record UserResponse(string UserName, string? Email);
}
