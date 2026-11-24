using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Api.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>Sign-in through a signed Cloudflare Access token, verified against a stubbed certs endpoint.</summary>
public class CloudflareAccessSignInTests : IClassFixture<StrimAppFactory>
{
  private const string TeamDomain = "team.example.test";
  private const string Audience = "test-aud";
  private const string CertsUrl = $"https://{TeamDomain}/cdn-cgi/access/certs";

  private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };
  private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "test-key" };

  private readonly StrimAppFactory _factory;
  private readonly WebApplicationFactory<Program> _app;

  public CloudflareAccessSignInTests(StrimAppFactory factory)
  {
    _factory = factory;
    _app = factory.WithWebHostBuilder(builder =>
    {
      builder.UseSetting("CF_ACCESS_TEAM_DOMAIN", TeamDomain);
      builder.UseSetting("CF_ACCESS_AUD", Audience);
      builder.ConfigureTestServices(services => services.AddHttpClient(CloudflareAccess.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() => new CertsStub()));
    });
  }

  [Fact]
  public async Task NotConfigured_AValidLookingToken_SignsNobodyIn()
  {
    await LinkedUserAsync("not-configured", "sub-off");
    var client = Client(_factory, Token("sub-off"));
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.Http.GetAsync("/api/auth/me")).StatusCode);
  }

  [Fact]
  public async Task ValidToken_OfALinkedSubject_SignsThatAccountIn()
  {
    await LinkedUserAsync("linked", "sub-linked");
    var client = Client(_app, Token("sub-linked"));

    var me = await client.Http.GetAsync("/api/auth/me");

    Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    Assert.Equal("linked", (await me.Content.ReadFromJsonAsync<UserResponse>())!.UserName);
    Assert.Contains(me.Headers.GetValues("Set-Cookie"), c => c.StartsWith("strim.auth="));
  }

  public static TheoryData<string> InvalidTokens => new() { "signature", "audience", "issuer", "expired", "no-email" };

  [Theory]
  [MemberData(nameof(InvalidTokens))]
  public async Task InvalidToken_SignsNobodyIn(string fault)
  {
    await LinkedUserAsync($"invalid-{fault}", $"sub-{fault}");
    var token = fault switch
    {
      "signature" => Token($"sub-{fault}", key: OtherKey),
      "audience" => Token($"sub-{fault}", audience: "other-aud"),
      "issuer" => Token($"sub-{fault}", issuer: "https://other.example.test"),
      "expired" => Token($"sub-{fault}", expires: DateTime.UtcNow.AddMinutes(-5)),
      _ => Token($"sub-{fault}", email: null),
    };
    var client = Client(_app, token);

    var me = await client.Http.GetAsync("/api/auth/me");

    Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    Assert.False(me.Headers.Contains("Set-Cookie"));
  }

  [Fact]
  public async Task PlainEmailHeader_WithoutToken_SignsNobodyIn()
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
    request.Headers.Add("Cf-Access-Authenticated-User-Email", "person@example.test");

    Assert.Equal(HttpStatusCode.Unauthorized, (await Client(_app, null).Http.SendAsync(request)).StatusCode);
  }

  [Fact]
  public async Task UnlinkedSubject_IsLinkedByAPasswordLogin()
  {
    await Client(_app, null).RegisterAsync("to-link");
    var client = Client(_app, Token("sub-new"));
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.Http.GetAsync("/api/auth/me")).StatusCode);

    (await client.PostAsync("/api/auth/login", new { userName = "to-link", password = "Correct-Horse-1" })).EnsureSuccessStatusCode();

    var me = await Client(_app, Token("sub-new")).Http.GetFromJsonAsync<UserResponse>("/api/auth/me");
    Assert.Equal("to-link", me!.UserName);
  }

  [Fact]
  public async Task SessionOfAnotherAccount_IsReplacedByTheLinkedAccount()
  {
    await LinkedUserAsync("user-b", "sub-b");
    var client = Client(_app, null);
    await client.RegisterAsync("user-a");
    client.Http.DefaultRequestHeaders.Add(CloudflareAccess.TokenHeader, Token("sub-b"));

    var me = await client.Http.GetFromJsonAsync<UserResponse>("/api/auth/me");

    Assert.Equal("user-b", me!.UserName);
  }

  [Fact]
  public async Task ShareRoute_WorksWithoutAnyToken()
  {
    var owner = Client(_app, null);
    await owner.RegisterAsync("access-sharer");
    var created = await owner.PostAsync("/api/playlists", new { name = "Shared", sourceUrl = StrimAppFactory.UpstreamUrl, disabledGroups = Array.Empty<string>() });
    var playlist = (await created.Content.ReadFromJsonAsync<PlaylistDto>())!;

    var text = await _app.CreateClient().GetStringAsync($"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}");

    Assert.Contains("ARD", text);
  }

  [Fact]
  public async Task ConnectHint_OnlyForAValidUnlinkedUserToken()
  {
    await LinkedUserAsync("hint-linked", "sub-hint-linked");

    var pending = await AccessAsync(Client(_app, Token("sub-hint", email: "anna@example.test")));
    Assert.True(pending.Active);
    Assert.True(pending.AccessLinkPending);
    Assert.Equal("anna@example.test", pending.AccessEmail);

    foreach (var client in new[]
    {
      Client(_factory, Token("sub-hint")),
      Client(_app, Token("sub-hint", audience: "other-aud")),
      Client(_app, Token("sub-hint-linked")),
    })
    {
      var state = await AccessAsync(client);
      Assert.False(state.AccessLinkPending);
      Assert.Null(state.AccessEmail);
    }
    Assert.False((await AccessAsync(Client(_factory, null))).Active);
  }

  private static ApiClient Client(WebApplicationFactory<Program> app, string? token)
  {
    var client = new ApiClient(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
    if (token is not null) client.Http.DefaultRequestHeaders.Add(CloudflareAccess.TokenHeader, token);
    return client;
  }

  private static async Task<AccessResponse> AccessAsync(ApiClient client) =>
    (await client.Http.GetFromJsonAsync<AccessResponse>("/api/auth/access"))!;

  private async Task LinkedUserAsync(string userName, string subject)
  {
    using var scope = _app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var user = new IdentityUser { UserName = userName };
    Assert.True((await users.CreateAsync(user)).Succeeded);
    Assert.True((await users.AddLoginAsync(user, new UserLoginInfo(CloudflareAccess.LoginProvider, subject, "Cloudflare Access"))).Succeeded);
  }

  private static string Token(
    string subject,
    string? email = "person@example.test",
    string audience = Audience,
    string issuer = $"https://{TeamDomain}",
    DateTime? expires = null,
    SecurityKey? key = null)
  {
    var claims = new Dictionary<string, object> { ["sub"] = subject };
    if (email is not null) claims["email"] = email;
    var end = expires ?? DateTime.UtcNow.AddMinutes(10);
    return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
      Issuer = issuer,
      Audience = audience,
      Claims = claims,
      IssuedAt = end.AddMinutes(-20),
      NotBefore = end.AddMinutes(-20),
      Expires = end,
      SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
    });
  }

  private static string CertsJson()
  {
    var parameters = SigningKey.Rsa.ExportParameters(includePrivateParameters: false);
    return JsonSerializer.Serialize(new
    {
      keys = new[]
      {
        new
        {
          kid = SigningKey.KeyId,
          kty = "RSA",
          alg = "RS256",
          use = "sig",
          n = Base64UrlEncoder.Encode(parameters.Modulus),
          e = Base64UrlEncoder.Encode(parameters.Exponent),
        },
      },
    });
  }

  /// <summary>Answers the certs request with the test key; any other address is not found.</summary>
  private sealed class CertsStub : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      Task.FromResult(request.RequestUri!.ToString() == CertsUrl
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CertsJson()) }
        : new HttpResponseMessage(HttpStatusCode.NotFound));
  }

  private sealed record UserResponse(string UserName, string? Email);

  private sealed record AccessResponse(bool Active, bool AccessLinkPending, string? AccessEmail);
}
