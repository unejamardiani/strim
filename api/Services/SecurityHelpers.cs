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
  public static bool IsBlockedUrl(Uri uri)
  {
    // Block non-HTTP(S) schemes
    if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
    {
      return true;
    }

    // Resolve hostname to IP addresses and check each one
    try
    {
      var host = uri.DnsSafeHost;

      // Block common internal hostnames
      if (BlockedHostnames.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)))
      {
        return true;
      }

      // Resolve DNS and check IP addresses
      var addresses = Dns.GetHostAddresses(host);
      foreach (var addr in addresses)
      {
        if (IsPrivateOrReservedIP(addr))
        {
          return true;
        }
      }
    }
    catch (SocketException)
    {
      // DNS resolution failed - block to be safe
      return true;
    }

    return false;
  }

  /// <summary>
  /// Checks if an IP address is private or reserved.
  /// </summary>
  public static bool IsPrivateOrReservedIP(IPAddress addr)
  {
    var bytes = addr.GetAddressBytes();

    // IPv4 checks
    if (addr.AddressFamily == AddressFamily.InterNetwork && bytes.Length == 4)
    {
      // Loopback: 127.0.0.0/8
      if (bytes[0] == 127) return true;

      // Private: 10.0.0.0/8
      if (bytes[0] == 10) return true;

      // Private: 172.16.0.0/12
      if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

      // Private: 192.168.0.0/16
      if (bytes[0] == 192 && bytes[1] == 168) return true;

      // Link-local: 169.254.0.0/16 (AWS metadata, Azure IMDS, etc.)
      if (bytes[0] == 169 && bytes[1] == 254) return true;

      // Current network: 0.0.0.0/8
      if (bytes[0] == 0) return true;

      // Broadcast: 255.255.255.255
      if (bytes.All(b => b == 255)) return true;

      // Documentation/TEST-NET ranges
      if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return true; // 192.0.2.0/24
      if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true; // 198.51.100.0/24
      if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true; // 203.0.113.0/24
    }

    // IPv6 checks
    if (addr.AddressFamily == AddressFamily.InterNetworkV6)
    {
      // Loopback ::1
      if (IPAddress.IsLoopback(addr)) return true;

      // Link-local fe80::/10
      if (bytes.Length >= 2 && bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true;

      // Unique local fc00::/7
      if (bytes.Length >= 1 && (bytes[0] & 0xfe) == 0xfc) return true;

      // Unspecified ::
      if (addr.Equals(IPAddress.IPv6None) || addr.Equals(IPAddress.IPv6Any)) return true;
    }

    return false;
  }
}
