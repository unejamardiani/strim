using System.Net;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class SecurityHelpersTests
{
  [Fact]
  public void GenerateSecureShareCode_ReturnsNonEmptyString()
  {
    var code = SecurityHelpers.GenerateSecureShareCode();

    Assert.NotNull(code);
    Assert.NotEmpty(code);
    Assert.True(code.Length > 20, "Share code should be sufficiently long");
  }

  [Fact]
  public void GenerateSecureShareCode_ReturnsUrlSafeCharacters()
  {
    var code = SecurityHelpers.GenerateSecureShareCode();

    Assert.DoesNotContain("+", code);
    Assert.DoesNotContain("/", code);
    Assert.DoesNotContain("=", code);
  }

  [Fact]
  public void GenerateSecureShareCode_GeneratesUniqueValues()
  {
    var codes = new HashSet<string>();
    for (int i = 0; i < 100; i++)
    {
      codes.Add(SecurityHelpers.GenerateSecureShareCode());
    }

    Assert.Equal(100, codes.Count);
  }

  [Theory]
  [InlineData("ftp://example.com/file.m3u")]
  [InlineData("file:///etc/passwd")]
  [InlineData("javascript:alert(1)")]
  public async Task IsBlockedUrl_BlocksNonHttpSchemes(string url)
  {
    var uri = new Uri(url);
    Assert.True(await SecurityHelpers.IsBlockedUrlAsync(uri, CancellationToken.None));
  }

  [Theory]
  [InlineData("http://localhost/test")]
  [InlineData("http://127.0.0.1/test")]
  [InlineData("http://0.0.0.0/test")]
  [InlineData("http://metadata.google.internal/")]
  public async Task IsBlockedUrl_BlocksInternalHostnames(string url)
  {
    var uri = new Uri(url);
    Assert.True(await SecurityHelpers.IsBlockedUrlAsync(uri, CancellationToken.None));
  }

  [Theory]
  [InlineData("http://2130706433/")]
  [InlineData("http://0x7f000001/")]
  [InlineData("http://0177.0.0.1/")]
  [InlineData("http://127.1/")]
  [InlineData("http://[::]/")]
  [InlineData("http://[::ffff:127.0.0.1]/")]
  [InlineData("http://[64:ff9b::a9fe:a9fe]/")]
  public async Task ValidateSourceUri_RefusesInternalAddressesWithTheNormalMessage(string url)
  {
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      PlaylistSourceFetcher.ValidateSourceUriAsync(url, CancellationToken.None));

    Assert.Equal("Access to internal or private network addresses is not allowed", error.Message);
  }

  [Theory]
  [InlineData("198.18.0.1")]
  [InlineData("198.19.255.254")]
  [InlineData("192.0.0.1")]
  [InlineData("192.0.0.255")]
  [InlineData("224.0.0.1")]
  [InlineData("239.255.255.250")]
  [InlineData("240.0.0.1")]
  [InlineData("255.255.255.255")]
  [InlineData("::")]
  [InlineData("::7f00:1")]
  [InlineData("::a00:1")]
  [InlineData("64:ff9b::7f00:1")]
  [InlineData("64:ff9b::a9fe:a9fe")]
  [InlineData("64:ff9b:1::a00:1")]
  [InlineData("64:ff9b:1:ffff::c0a8:1")]
  [InlineData("2002:7f00:1::1")]
  [InlineData("2002:a00:1::")]
  [InlineData("2001::1")]
  [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
  [InlineData("fec0::1")]
  [InlineData("feff::1")]
  [InlineData("ff02::1")]
  [InlineData("ff0e::1")]
  public void IsPrivateOrReservedIP_BlocksSpecialPurposeRanges(string address)
  {
    Assert.True(SecurityHelpers.IsPrivateOrReservedIP(IPAddress.Parse(address)));
  }

  [Theory]
  [InlineData("198.17.255.255")]
  [InlineData("198.20.0.1")]
  [InlineData("192.0.1.1")]
  [InlineData("223.255.255.254")]
  [InlineData("::808:808")]
  [InlineData("64:ff9b::808:808")]
  [InlineData("64:ff9b:1::808:808")]
  [InlineData("2002:808:808::1")]
  [InlineData("2001:4860::1")]
  [InlineData("2a00:1450::1")]
  public void IsPrivateOrReservedIP_AllowsPublicNeighboursOfSpecialPurposeRanges(string address)
  {
    Assert.False(SecurityHelpers.IsPrivateOrReservedIP(IPAddress.Parse(address)));
  }

  [Theory]
  [InlineData("127.0.0.1")]
  [InlineData("10.0.0.1")]
  [InlineData("10.255.255.255")]
  [InlineData("172.16.0.1")]
  [InlineData("172.31.255.255")]
  [InlineData("192.168.0.1")]
  [InlineData("192.168.255.255")]
  [InlineData("169.254.169.254")]
  [InlineData("0.0.0.0")]
  public void IsPrivateOrReservedIP_IdentifiesPrivateIPv4(string ip)
  {
    var addr = IPAddress.Parse(ip);
    Assert.True(SecurityHelpers.IsPrivateOrReservedIP(addr));
  }

  [Theory]
  [InlineData("8.8.8.8")]
  [InlineData("1.1.1.1")]
  [InlineData("208.67.222.222")]
  public void IsPrivateOrReservedIP_AllowsPublicIPv4(string ip)
  {
    var addr = IPAddress.Parse(ip);
    Assert.False(SecurityHelpers.IsPrivateOrReservedIP(addr));
  }

  [Fact]
  public void IsPrivateOrReservedIP_BlocksIPv6Loopback()
  {
    var addr = IPAddress.Parse("::1");
    Assert.True(SecurityHelpers.IsPrivateOrReservedIP(addr));
  }

  [Fact]
  public void IsPrivateOrReservedIP_BlocksIPv6LinkLocal()
  {
    var addr = IPAddress.Parse("fe80::1");
    Assert.True(SecurityHelpers.IsPrivateOrReservedIP(addr));
  }

  [Fact]
  public void IsPrivateOrReservedIP_BlocksIPv6UniqueLocal()
  {
    var addr = IPAddress.Parse("fc00::1");
    Assert.True(SecurityHelpers.IsPrivateOrReservedIP(addr));
  }

  [Theory]
  [InlineData("::ffff:10.0.0.1")]
  [InlineData("::ffff:127.0.0.1")]
  [InlineData("100.64.0.1")]
  [InlineData("100.127.255.254")]
  public void IsPrivateOrReservedIP_BlocksMappedAndSharedAddresses(string address)
  {
    Assert.True(SecurityHelpers.IsPrivateOrReservedIP(IPAddress.Parse(address)));
  }

  [Theory]
  [InlineData("100.63.255.255")]
  [InlineData("100.128.0.1")]
  [InlineData("::ffff:8.8.8.8")]
  public void IsPrivateOrReservedIP_AllowsPublicNeighbours(string address)
  {
    Assert.False(SecurityHelpers.IsPrivateOrReservedIP(IPAddress.Parse(address)));
  }

  [Theory]
  [InlineData("127.0.0.1")]
  [InlineData("10.1.2.3")]
  [InlineData("localhost")]
  public async Task ConnectToPublicAddress_RefusesPrivateTargets(string host)
  {
    await Assert.ThrowsAsync<HttpRequestException>(async () =>
      await SecurityHelpers.ConnectToPublicAddressAsync(new DnsEndPoint(host, 80), CancellationToken.None));
  }
}
