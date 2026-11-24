using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>
/// The external sign-in callback. A test-only route stands in for the provider: it writes the external
/// cookie the way Google, Microsoft or GitHub would after a successful challenge.
/// </summary>
public class ExternalSignInTests : IClassFixture<StrimAppFactory>
{
  private const string Provider = "TestProvider";
  private const string SignInPath = "/test/external-sign-in";

  private readonly WebApplicationFactory<Program> _app;

  public ExternalSignInTests(StrimAppFactory factory)
  {
    _app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
      services.AddSingleton<IStartupFilter, ExternalSignInStartupFilter>()));
  }

  [Fact]
  public async Task FirstExternalSignIn_WithTheEmailOfALocalAccount_CreatesADifferentAccount()
  {
    var local = NewClient();
    await RegisterAsync(local, "local-owner", "a@example.test");
    var created = await local.PostAsync("/api/playlists", new { name = "Local list", sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups = Array.Empty<string>() });
    created.EnsureSuccessStatusCode();

    var external = NewClient();
    var landing = await ExternalSignInAsync(external, "key-a", "a@example.test");

    Assert.Equal("/app", landing);
    var localUser = await FindUserAsync("local-owner");
    var externalUser = await FindByLoginAsync("key-a");
    Assert.NotNull(externalUser);
    Assert.NotEqual(localUser!.Id, externalUser!.Id);
    var playlists = await external.Http.GetFromJsonAsync<List<object>>("/api/playlists");
    Assert.Empty(playlists!);
  }

  [Fact]
  public async Task ExternalSignIn_WhileSignedIn_AddsTheLoginToTheCurrentAccount()
  {
    var client = NewClient();
    await RegisterAsync(client, "linker", "linker@example.test");

    var landing = await ExternalSignInAsync(client, "key-link", "other@example.test");

    Assert.Equal("/app", landing);
    var linked = await FindByLoginAsync("key-link");
    Assert.Equal((await FindUserAsync("linker"))!.Id, linked!.Id);
    var me = await client.Http.GetFromJsonAsync<UserResponse>("/api/auth/me");
    Assert.Equal("linker", me!.UserName);
  }

  [Fact]
  public async Task FirstExternalSignIn_WithAnEmailSharedByTwoLocalAccounts_SignsIntoANewAccount()
  {
    await RegisterAsync(NewClient(), "twin-one", "twin@example.test");
    await RegisterAsync(NewClient(), "twin-two", "twin@example.test");

    var external = NewClient();
    var landing = await ExternalSignInAsync(external, "key-twin", "twin@example.test");

    Assert.Equal("/app", landing);
    var user = await FindByLoginAsync("key-twin");
    Assert.Equal("twin@example.test", user!.UserName);
    Assert.Equal(HttpStatusCode.OK, (await external.Http.GetAsync("/api/auth/me")).StatusCode);
  }

  [Fact]
  public async Task FirstExternalSignIn_WhenTheEmailIsTakenAsUserName_CreatesAnAccountWithTheProviderKey()
  {
    await RegisterAsync(NewClient(), "taken@example.test", null);

    var external = NewClient();
    var landing = await ExternalSignInAsync(external, "key-taken", "taken@example.test");

    Assert.Equal("/app", landing);
    var me = await external.Http.GetFromJsonAsync<UserResponse>("/api/auth/me");
    Assert.Equal($"{Provider}-key-taken", me!.UserName);
    Assert.Equal("taken@example.test", me.Email);
  }

  [Fact]
  public async Task ExternalSignIn_ToALockedOutAccount_IsRefused()
  {
    using (var scope = _app.Services.CreateScope())
    {
      var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
      var user = new IdentityUser { UserName = "locked-out", Email = "locked@example.test" };
      Assert.True((await users.CreateAsync(user)).Succeeded);
      Assert.True((await users.AddLoginAsync(user, new UserLoginInfo(Provider, "key-locked", Provider))).Succeeded);
      Assert.True((await users.SetLockoutEnabledAsync(user, true)).Succeeded);
      Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
    }

    var client = NewClient();
    var landing = await ExternalSignInAsync(client, "key-locked", "locked@example.test");

    Assert.Equal("/app?auth=failed", landing);
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.Http.GetAsync("/api/auth/me")).StatusCode);
    Assert.Equal("key-locked", Assert.Single(await LoginsOfAsync("locked-out")));
  }

  private ApiClient NewClient() =>
    new(_app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

  private static async Task RegisterAsync(ApiClient client, string userName, string? email)
  {
    var response = await client.PostAsync("/api/auth/register", new { userName, password = "Correct-Horse-1", email });
    response.EnsureSuccessStatusCode();
  }

  /// <summary>Completes a provider round trip and returns where the callback redirects.</summary>
  private static async Task<string> ExternalSignInAsync(ApiClient client, string providerKey, string email)
  {
    var query = $"?key={Uri.EscapeDataString(providerKey)}&email={Uri.EscapeDataString(email)}";
    (await client.Http.GetAsync(SignInPath + query)).EnsureSuccessStatusCode();
    var callback = await client.Http.GetAsync("/api/auth/external-callback?returnUrl=/app");
    Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
    return callback.Headers.Location!.OriginalString;
  }

  private async Task<IdentityUser?> FindUserAsync(string userName)
  {
    using var scope = _app.Services.CreateScope();
    return await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByNameAsync(userName);
  }

  private async Task<IdentityUser?> FindByLoginAsync(string providerKey)
  {
    using var scope = _app.Services.CreateScope();
    return await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByLoginAsync(Provider, providerKey);
  }

  private async Task<List<string>> LoginsOfAsync(string userName)
  {
    using var scope = _app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var user = await users.FindByNameAsync(userName);
    return (await users.GetLoginsAsync(user!)).Select(l => l.ProviderKey).ToList();
  }

  private sealed record UserResponse(string UserName, string? Email);

  /// <summary>Answers <see cref="SignInPath"/> by writing the external cookie for the given key and e-mail.</summary>
  private sealed class ExternalSignInStartupFilter : IStartupFilter
  {
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
      app.Use(async (context, nextMiddleware) =>
      {
        if (context.Request.Path != SignInPath)
        {
          await nextMiddleware(context);
          return;
        }

        var identity = new ClaimsIdentity(
          new[]
          {
            new Claim(ClaimTypes.NameIdentifier, context.Request.Query["key"].ToString()),
            new Claim(ClaimTypes.Email, context.Request.Query["email"].ToString()),
          },
          Provider);
        var properties = new AuthenticationProperties();
        properties.Items["LoginProvider"] = Provider;
        await context.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(identity), properties);
      });
      next(app);
    };
  }
}
