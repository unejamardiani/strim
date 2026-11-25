using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

public class ProfileEndpointTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static Task<HttpResponseMessage> PutAsync(ApiClient client, object body, bool withCsrf = true) =>
    client.SendAsync(HttpMethod.Put, "/api/profile", body, withCsrf);

  private static async Task<JsonElement> MeAsync(ApiClient client) =>
    await client.Http.GetFromJsonAsync<JsonElement>("/api/auth/me");

  [Fact]
  public async Task Me_WithoutProfile_ShowsTheUserName_AndDefaults()
  {
    var client = await SignedInAsync("profile-defaults");

    var me = await MeAsync(client);

    Assert.Equal(JsonValueKind.Null, me.GetProperty("displayName").ValueKind);
    Assert.Equal("profile-defaults", me.GetProperty("name").GetString());
    Assert.Equal(JsonValueKind.Null, me.GetProperty("avatar").ValueKind);
    Assert.Equal("system", me.GetProperty("theme").GetString());
    Assert.Equal("password", me.GetProperty("signIn").GetProperty("method").GetString());
    Assert.Equal("profile-defaults", me.GetProperty("signIn").GetProperty("identity").GetString());
  }

  [Fact]
  public async Task Put_WithoutCsrfToken_IsRejected_AndNothingChanges()
  {
    var client = await SignedInAsync("profile-nocsrf");

    var response = await PutAsync(client, new { displayName = "Sofa" }, withCsrf: false);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(JsonValueKind.Null, (await MeAsync(client)).GetProperty("displayName").ValueKind);
  }

  [Fact]
  public async Task Put_SignedOut_Answers401()
  {
    var client = new ApiClient(factory.CreateClient());

    Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(client, new { displayName = "Sofa" })).StatusCode);
  }

  public static TheoryData<string, string> InvalidBodies => new()
  {
    { "displayName", $$"""{"displayName":"{{new string('a', 41)}}"}""" },
    { "displayName", """{"displayName":"Sofa\u0007King"}""" },
    { "displayName", """{"displayName":"   "}""" },
    { "displayName", """{"displayName":7}""" },
    { "avatar", """{"avatar":"purple:tv"}""" },
    { "avatar", """{"avatar":"violet:rocket"}""" },
    { "avatar", """{"avatar":"violet"}""" },
    { "avatar", """{"avatar":"violet:tv:star"}""" },
    { "avatar", """{"avatar":"Violet:TV"}""" },
    { "theme", """{"theme":"sepia"}""" },
    { "theme", """{"theme":null}""" },
  };

  [Theory]
  [MemberData(nameof(InvalidBodies))]
  public async Task Put_WithAnInvalidValue_Answers400WithTheField(string field, string json)
  {
    var client = await SignedInAsync($"profile-invalid-{Guid.NewGuid():N}");

    var response = await PutAsync(client, JsonDocument.Parse(json).RootElement);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    var me = await MeAsync(client);
    Assert.Equal(JsonValueKind.Null, me.GetProperty("displayName").ValueKind);
    Assert.Equal(JsonValueKind.Null, me.GetProperty("avatar").ValueKind);
    Assert.Equal("system", me.GetProperty("theme").GetString());
  }

  [Fact]
  public async Task Put_AValidUpdate_IsReturned_AndShownByMe()
  {
    var client = await SignedInAsync("profile-valid");

    var response = await PutAsync(client, new { displayName = "  Sofa Captain  ", avatar = "tangerine:tv", theme = "dark" });

    response.EnsureSuccessStatusCode();
    foreach (var profile in new[] { await response.Content.ReadFromJsonAsync<JsonElement>(), await MeAsync(client) })
    {
      Assert.Equal("Sofa Captain", profile.GetProperty("displayName").GetString());
      Assert.Equal("Sofa Captain", profile.GetProperty("name").GetString());
      Assert.Equal("tangerine:tv", profile.GetProperty("avatar").GetString());
      Assert.Equal("dark", profile.GetProperty("theme").GetString());
    }
  }

  [Fact]
  public async Task Put_APartialUpdate_KeepsTheOtherFields()
  {
    var client = await SignedInAsync("profile-partial");
    (await PutAsync(client, new { displayName = "Sofa", avatar = "gold:star", theme = "light" })).EnsureSuccessStatusCode();

    (await PutAsync(client, new { theme = "dark" })).EnsureSuccessStatusCode();

    var me = await MeAsync(client);
    Assert.Equal("Sofa", me.GetProperty("displayName").GetString());
    Assert.Equal("gold:star", me.GetProperty("avatar").GetString());
    Assert.Equal("dark", me.GetProperty("theme").GetString());
  }

  [Fact]
  public async Task Put_NullOrEmpty_ClearsDisplayNameAndAvatar()
  {
    var client = await SignedInAsync("profile-clear");
    (await PutAsync(client, new { displayName = "Sofa", avatar = "ink:eye" })).EnsureSuccessStatusCode();

    (await PutAsync(client, new { displayName = "", avatar = (string?)null })).EnsureSuccessStatusCode();

    var me = await MeAsync(client);
    Assert.Equal(JsonValueKind.Null, me.GetProperty("displayName").ValueKind);
    Assert.Equal("profile-clear", me.GetProperty("name").GetString());
    Assert.Equal(JsonValueKind.Null, me.GetProperty("avatar").ValueKind);
  }

  [Fact]
  public async Task Put_ChangesOnlyTheSignedInUsersProfile()
  {
    var first = await SignedInAsync("profile-first");
    var second = await SignedInAsync("profile-second");

    (await PutAsync(first, new { displayName = "First", avatar = "violet:home", theme = "dark" })).EnsureSuccessStatusCode();

    var other = await MeAsync(second);
    Assert.Equal(JsonValueKind.Null, other.GetProperty("displayName").ValueKind);
    Assert.Equal("profile-second", other.GetProperty("name").GetString());
    Assert.Equal(JsonValueKind.Null, other.GetProperty("avatar").ValueKind);
    Assert.Equal("system", other.GetProperty("theme").GetString());
  }

  [Fact]
  public async Task Me_AfterAnExternalSignIn_NamesTheProvider()
  {
    var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
      services.AddSingleton<IStartupFilter, ExternalSignInTests.ExternalSignInStartupFilter>()));
    var client = await ExternalSignInAsync(app, "key-profile", "profile.external@example.test");

    var me = await MeAsync(client);

    Assert.Equal("profile.external", me.GetProperty("name").GetString());
    Assert.Equal("testprovider", me.GetProperty("signIn").GetProperty("method").GetString());
    Assert.Equal("profile.external@example.test", me.GetProperty("signIn").GetProperty("identity").GetString());
  }

  [Fact]
  public async Task Me_AfterTheSecurityStampRefresh_StillNamesTheProvider()
  {
    // A zero interval makes Identity rebuild the session's principal on every request, as it does after 30 minutes.
    var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
      services.AddSingleton<IStartupFilter, ExternalSignInTests.ExternalSignInStartupFilter>();
      services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
    }));
    var client = await ExternalSignInAsync(app, "key-refresh", "profile.refresh@example.test");

    await MeAsync(client);
    var me = await MeAsync(client);

    Assert.Equal("testprovider", me.GetProperty("signIn").GetProperty("method").GetString());
  }

  private static async Task<ApiClient> ExternalSignInAsync(WebApplicationFactory<Program> app, string key, string email)
  {
    var client = new ApiClient(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
    (await client.Http.GetAsync($"{ExternalSignInTests.SignInPath}?key={key}&email={email}")).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.Redirect, (await client.Http.GetAsync("/api/auth/external-callback?returnUrl=/app")).StatusCode);
    return client;
  }

  [Theory]
  [InlineData("Sofa Captain", "viewer@example.test", "Sofa Captain")]
  [InlineData(null, "viewer", "viewer")]
  [InlineData("", "viewer", "viewer")]
  [InlineData(null, "viewer@example.test", "viewer")]
  public void ShownName_PrefersDisplayName_ThenUserName_ThenEmailLocalPart(string? displayName, string userName, string expected)
  {
    Assert.Equal(expected, ProfileEndpoints.ShownName(displayName, userName));
  }
}
