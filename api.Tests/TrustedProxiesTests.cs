using System.Net;
using Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Tests;

public class TrustedProxiesTests
{
  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public void ParseTrustedProxies_NullOrWhitespace_ReturnsEmpty(string? value)
  {
    var result = Security.ParseTrustedProxies(value);

    Assert.Empty(result.Proxies);
    Assert.Empty(result.Networks);
    Assert.Empty(result.Rejected);
  }

  [Fact]
  public void ParseTrustedProxies_SingleIPv4AndIPv6_ReturnsProxies()
  {
    var result = Security.ParseTrustedProxies("10.0.0.1, ::1");

    Assert.Equal(new[] { IPAddress.Parse("10.0.0.1"), IPAddress.IPv6Loopback }, result.Proxies);
    Assert.Empty(result.Networks);
    Assert.Empty(result.Rejected);
  }

  [Fact]
  public void ParseTrustedProxies_Cidr_ReturnsNetwork()
  {
    var result = Security.ParseTrustedProxies("10.0.0.0/8, fd00::/8");

    Assert.Empty(result.Proxies);
    Assert.Collection(result.Networks,
      network =>
      {
        Assert.Equal(IPAddress.Parse("10.0.0.0"), network.BaseAddress);
        Assert.Equal(8, network.PrefixLength);
      },
      network =>
      {
        Assert.Equal(IPAddress.Parse("fd00::"), network.BaseAddress);
        Assert.Equal(8, network.PrefixLength);
      });
    Assert.Empty(result.Rejected);
  }

  [Fact]
  public void ParseTrustedProxies_CidrWithHostBits_IsAcceptedAsBefore()
  {
    // The HttpOverrides parser accepted host bits; System.Net.IPNetwork.TryParse would not.
    var result = Security.ParseTrustedProxies("10.1.2.3/8");

    var network = Assert.Single(result.Networks);
    Assert.Equal(8, network.PrefixLength);
    Assert.True(network.Contains(IPAddress.Parse("10.200.0.1")));
    Assert.Empty(result.Rejected);
  }

  [Fact]
  public void ParseTrustedProxies_InvalidEntries_AreReportedAndSkipped()
  {
    var result = Security.ParseTrustedProxies("nonsense, 10.0.0.0/99");

    Assert.Empty(result.Proxies);
    Assert.Empty(result.Networks);
    Assert.Equal(new[] { "nonsense", "10.0.0.0/99" }, result.Rejected);
  }

  [Fact]
  public void ParseTrustedProxies_TrimsAndIgnoresEmptyEntries()
  {
    var result = Security.ParseTrustedProxies(" 10.0.0.1 ,, ");

    Assert.Equal(new[] { IPAddress.Parse("10.0.0.1") }, result.Proxies);
    Assert.Empty(result.Networks);
    Assert.Empty(result.Rejected);
  }

  [Fact]
  public void ForwardedHeadersOptions_WithoutTrustedProxies_ClearsListsAndHasNoForwardLimit()
  {
    var options = ResolveOptions(trustedProxies: null);

    Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    Assert.Empty(options.KnownProxies);
    Assert.Empty(options.KnownIPNetworks);
    Assert.Null(options.ForwardLimit);
  }

  [Fact]
  public void ForwardedHeadersOptions_WithTrustedProxies_SetsForwardLimitOne()
  {
    var options = ResolveOptions(trustedProxies: "10.0.0.1, 192.168.0.0/16, nonsense");

    Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    Assert.Equal(1, options.ForwardLimit);
    Assert.Contains(IPAddress.Parse("10.0.0.1"), options.KnownProxies);
    Assert.Contains(options.KnownIPNetworks, network =>
      network.BaseAddress.Equals(IPAddress.Parse("192.168.0.0")) && network.PrefixLength == 16);
  }

  private static ForwardedHeadersOptions ResolveOptions(string? trustedProxies)
  {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["TRUSTED_PROXIES"] = trustedProxies })
      .Build();
    using var provider = new ServiceCollection()
      .AddStrimForwardedHeaders(configuration)
      .BuildServiceProvider();
    return provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
  }
}
