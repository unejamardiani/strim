using Api.Services;
using Xunit;

namespace Api.Tests;

public class SourceHostTests
{
  [Fact]
  public void FromAddress_DropsUserInfoPortPathAndQuery()
  {
    Assert.Equal("example.test",
      SourceHost.FromAddress("http://user:pass@example.test:8080/get.php?username=a&password=b&type=m3u"));
  }

  [Fact]
  public void FromAddress_ReturnsLowerCase()
  {
    Assert.Equal("example.test", SourceHost.FromAddress("https://Example.TEST/list.m3u"));
  }

  [Theory]
  [InlineData("http://example.test")]
  [InlineData("https://example.test/")]
  [InlineData("  https://example.test  ")]
  public void FromAddress_HostOnly_ReturnsHost(string address)
  {
    Assert.Equal("example.test", SourceHost.FromAddress(address));
  }

  [Theory]
  [InlineData("http://192.0.2.10/list.m3u", "192.0.2.10")]
  [InlineData("http://user:pass@192.0.2.10:8080/get.php?type=m3u", "192.0.2.10")]
  public void FromAddress_IpHost_ReturnedAsIs(string address, string expected)
  {
    Assert.Equal(expected, SourceHost.FromAddress(address));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("/list.m3u")]
  [InlineData("list.m3u")]
  [InlineData("example.test/list.m3u")]
  [InlineData("file:///x.m3u")]
  [InlineData("ftp://example.test/list.m3u")]
  [InlineData("javascript:alert(1)")]
  public void FromAddress_MissingRelativeOrNonHttp_ReturnsNull(string? address)
  {
    Assert.Null(SourceHost.FromAddress(address));
  }

  [Theory]
  [InlineData("http://")]
  [InlineData("https://")]
  [InlineData("http:///list.m3u")]
  [InlineData("http://exa mple.test/")]
  [InlineData("http://example.test:99999/")]
  [InlineData("http://[::1/")]
  [InlineData("ht!tp://example.test/")]
  [InlineData("http://user@:8080/")]
  [InlineData("\0\u0001\u0002")]
  [InlineData("http://%zz/")]
  public void FromAddress_Malformed_ReturnsNullWithoutThrowing(string address)
  {
    var ex = Record.Exception(() => Assert.Null(SourceHost.FromAddress(address)));
    Assert.Null(ex);
  }

  [Fact]
  public void SourceHost_IsStaticServiceHelper_OutsideSecurityHelpers()
  {
    var type = typeof(SourceHost);
    Assert.True(type.IsAbstract && type.IsSealed); // static class
    Assert.Equal("Api.Services", type.Namespace);
    Assert.NotEqual(typeof(SecurityHelpers), type.DeclaringType);
    Assert.True(type.GetMethod(nameof(SourceHost.FromAddress))!.IsStatic);
  }
}
