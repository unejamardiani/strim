using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Api.Services;

/// <summary>
/// Security utilities for SSRF protection and secure token generation.
/// </summary>
public static class SecurityHelpers
{
  private static readonly string[] BlockedHostnames =
  {
    "localhost", "127.0.0.1", "::1", "0.0.0.0",
    "metadata", "metadata.google.internal"
  };

  /// <summary>
  /// Generates a cryptographically secure share code.
  /// </summary>
  public static string GenerateSecureShareCode()
  {
    var bytes = new byte[32];
    RandomNumberGenerator.Fill(bytes);
    return Convert.ToBase64String(bytes)
      .Replace("+", "-")
      .Replace("/", "_")
      .TrimEnd('=');
  }

  /// <summary>
  /// Validates a URL for SSRF protection by blocking internal/private IP addresses.
  /// </summary>
  public static async Task<bool> IsBlockedUrlAsync(Uri uri, CancellationToken cancellationToken)
  {
    // Block non-HTTP(S) schemes
    if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
    {
      return true;
    }

    var host = uri.DnsSafeHost;

    // Block common internal hostnames
    if (BlockedHostnames.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)))
    {
      return true;
    }

    try
    {
      var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
      return addresses.Any(IsPrivateOrReservedIP);
    }
    catch (Exception ex) when (ex is SocketException or ArgumentException)
    {
      // Resolution failed, or the host is an unspecified address such as [::]: block to be safe.
      return true;
    }
  }

  /// <summary>
  /// Checks if an IP address is private, reserved or otherwise not globally reachable.
  /// </summary>
  public static bool IsPrivateOrReservedIP(IPAddress addr)
  {
    var bytes = addr.GetAddressBytes();

    if (addr.AddressFamily == AddressFamily.InterNetworkV6)
    {
      // An address that carries an IPv4 address reaches that IPv4 host, so judge it as IPv4.
      var embedded = EmbeddedIPv4(addr, bytes);
      if (embedded is not null)
      {
        return IsPrivateOrReservedIP(embedded);
      }

      // Teredo 2001::/32
      if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0) return true;

      // Link-local fe80::/10 and deprecated site-local fec0::/10
      if (bytes[0] == 0xfe && (bytes[1] & 0x80) == 0x80) return true;

      // Unique local fc00::/7
      if ((bytes[0] & 0xfe) == 0xfc) return true;

      // Multicast ff00::/8
      return bytes[0] == 0xff;
    }

    // Current network: 0.0.0.0/8
    if (bytes[0] == 0) return true;

    // Private: 10.0.0.0/8
    if (bytes[0] == 10) return true;

    // Shared address space / CGNAT: 100.64.0.0/10 (also used by overlay VPNs such as Tailscale)
    if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;

    // Loopback: 127.0.0.0/8
    if (bytes[0] == 127) return true;

    // Link-local: 169.254.0.0/16 (AWS metadata, Azure IMDS, etc.)
    if (bytes[0] == 169 && bytes[1] == 254) return true;

    // Private: 172.16.0.0/12
    if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

    // IETF protocol assignments: 192.0.0.0/24
    if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) return true;

    // Private: 192.168.0.0/16
    if (bytes[0] == 192 && bytes[1] == 168) return true;

    // Benchmarking: 198.18.0.0/15
    if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;

    // Documentation/TEST-NET ranges
    if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return true; // 192.0.2.0/24
    if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true; // 198.51.100.0/24
    if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true; // 203.0.113.0/24

    // Multicast 224.0.0.0/4 and reserved 240.0.0.0/4 (incl. broadcast 255.255.255.255)
    return bytes[0] >= 224;
  }

  /// <summary>
  /// Returns the IPv4 address an IPv6 address hands traffic to: IPv4-mapped ::ffff:0:0/96,
  /// IPv4-compatible ::/96 (also covers :: and ::1), NAT64 64:ff9b::/96 and 64:ff9b:1::/48
  /// (IPv4 in the last 32 bits, the usual /96 layout) and 6to4 2002::/16. Null for any other address.
  /// </summary>
  private static IPAddress? EmbeddedIPv4(IPAddress addr, byte[] bytes)
  {
    if (addr.IsIPv4MappedToIPv6)
    {
      return addr.MapToIPv4();
    }

    var isCompatible = bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0;
    var isNat64 = bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b &&
      (bytes.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0 || (bytes[4] == 0x00 && bytes[5] == 0x01));
    if (isCompatible || isNat64)
    {
      return new IPAddress(bytes.AsSpan(12, 4));
    }

    // 6to4 2002::/16 carries the IPv4 address in bits 16 to 47.
    return bytes[0] == 0x20 && bytes[1] == 0x02 ? new IPAddress(bytes.AsSpan(2, 4)) : null;
  }

  /// <summary>
  /// Opens the TCP connection for an outbound playlist request, resolving the host here and
  /// connecting only to public addresses. Checking the URL beforehand is not enough on its own:
  /// the HTTP stack would resolve the name again, which a DNS-rebinding host can exploit.
  /// </summary>
  public static async ValueTask<Stream> ConnectToPublicAddressAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
  {
    var addresses = IPAddress.TryParse(endPoint.Host, out var literal)
      ? new[] { literal }
      : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);
    var allowed = addresses.Where(address => !IsPrivateOrReservedIP(address)).ToArray();
    if (allowed.Length == 0)
    {
      throw new HttpRequestException("Access to internal or private network addresses is not allowed");
    }

    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    try
    {
      await socket.ConnectAsync(allowed, endPoint.Port, cancellationToken);
      return new NetworkStream(socket, ownsSocket: true);
    }
    catch
    {
      socket.Dispose();
      throw;
    }
  }
}
