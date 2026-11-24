using System.Net;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>
/// The client address that rate limits partition by comes from proxy headers only when the
/// immediate peer is a trusted proxy. Each test gets its own host, so the partitions start empty.
/// The auth routes allow 20 requests per minute and queue 2 more.
/// </summary>
public class ClientAddressTests
{
  private const string AuthRoute = "/api/auth/providers";
  private const string CsrfRoute = "/api/auth/csrf-token";
  private static readonly IPAddress Proxy = IPAddress.Parse("10.0.0.5");
  private static readonly IPAddress Stranger = IPAddress.Parse("192.0.2.50");

  [Fact]
  public async Task WithoutTrustedProxies_ForwardedFor_DoesNotChoosePartition()
  {
    using var host = new StrimAppFactory { PeerAddress = Stranger };
    var client = host.CreateClient();

    var requests = Enumerable.Range(1, 25)
      .Select(i => Send(client, AuthRoute, ("X-Forwarded-For", $"203.0.113.{i}")))
      .ToList();

    Assert.True(await AnyRejected(requests));
  }

  [Fact]
  public async Task TrustedProxy_CloudflareClient_ChoosesPartition()
  {
    using var host = new StrimAppFactory { TrustedProxies = "10.0.0.0/24", PeerAddress = Proxy };
    var client = host.CreateClient();
    // A request that waits in the limiter's queue means both clients share one partition.
    client.Timeout = TimeSpan.FromSeconds(10);

    for (var i = 0; i < 20; i++)
    {
      Assert.Equal(HttpStatusCode.OK, (await Send(client, AuthRoute, ("CF-Connecting-IP", "198.51.100.1"))).StatusCode);
    }
    for (var i = 0; i < 20; i++)
    {
      Assert.Equal(HttpStatusCode.OK, (await Send(client, AuthRoute, ("CF-Connecting-IP", "198.51.100.2"))).StatusCode);
    }

    // The first client's partition is used up.
    var sameClient = Enumerable.Range(0, 3)
      .Select(_ => Send(client, AuthRoute, ("CF-Connecting-IP", "198.51.100.1")))
      .ToList();
    Assert.True(await AnyRejected(sameClient));
  }

  [Fact]
  public async Task UntrustedPeer_CloudflareClient_IsIgnored()
  {
    using var host = new StrimAppFactory { TrustedProxies = "10.0.0.5", PeerAddress = Stranger };
    var client = host.CreateClient();

    var requests = Enumerable.Range(1, 25)
      .Select(i => Send(client, AuthRoute, ("CF-Connecting-IP", $"198.51.100.{i}")))
      .ToList();

    Assert.True(await AnyRejected(requests));
  }

  [Fact]
  public async Task ArrSsl_FromUntrustedPeer_KeepsScheme()
  {
    using var host = new StrimAppFactory { TrustedProxies = "10.0.0.5", PeerAddress = Stranger };

    // In Development the CSRF cookie is Secure only when the request scheme is HTTPS.
    using var response = await Send(host.CreateClient(), CsrfRoute, ("X-ARR-SSL", "on"));

    Assert.DoesNotContain("secure", CsrfCookie(response), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ArrSsl_FromTrustedProxy_SetsHttps()
  {
    using var host = new StrimAppFactory { TrustedProxies = "10.0.0.5", PeerAddress = Proxy };

    using var response = await Send(host.CreateClient(), CsrfRoute, ("X-ARR-SSL", "on"));

    Assert.Contains("secure", CsrfCookie(response), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Waits for the requests in completion order until one is rejected. Requests that wait in the
  /// limiter's queue are never awaited once a rejection is seen; disposing the host ends them.
  /// </summary>
  private static async Task<bool> AnyRejected(List<Task<HttpResponseMessage>> requests)
  {
    while (requests.Count > 0)
    {
      var done = await Task.WhenAny(requests);
      requests.Remove(done);
      if ((await done).StatusCode == HttpStatusCode.TooManyRequests) return true;
    }
    return false;
  }

  private static async Task<HttpResponseMessage> Send(HttpClient client, string path, (string Name, string Value) header)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Add(header.Name, header.Value);
    return await client.SendAsync(request);
  }

  private static string CsrfCookie(HttpResponseMessage response)
  {
    Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
    return Assert.Single(cookies, c => c.StartsWith("strim.csrf=", StringComparison.Ordinal));
  }
}
