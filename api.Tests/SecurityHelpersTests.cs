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
  public void IsBlockedUrl_BlocksNonHttpSchemes(string url)
  {
    var uri = new Uri(url);
    Assert.True(SecurityHelpers.IsBlockedUrl(uri));
  }

  [Theory]
  [InlineData("http://localhost/test")]
  [InlineData("http://127.0.0.1/test")]
  [InlineData("http://0.0.0.0/test")]
  [InlineData("http://metadata.google.internal/")]
  public void IsBlockedUrl_BlocksInternalHostnames(string url)
  {
    var uri = new Uri(url);
    Assert.True(SecurityHelpers.IsBlockedUrl(uri));
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
}
