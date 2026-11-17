namespace Api.Services;

/// <summary>
/// Reduces a stored playlist source address to a display-safe host name. Provider addresses often
/// carry credentials in the user info, path or query, so only the lower-case host is kept.
/// Pure parsing with <see cref="Uri"/>: no network or DNS calls, and it never throws.
/// </summary>
public static class SourceHost
{
  /// <summary>
  /// Returns the lower-case host of an absolute http(s) address, or <c>null</c> when the address is
  /// missing, relative, uses another scheme or cannot be parsed.
  /// </summary>
  public static string? FromAddress(string? address)
  {
    if (string.IsNullOrWhiteSpace(address))
    {
      return null;
    }

    try
    {
      if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
      {
        return null;
      }

      if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
      {
        return null;
      }

      var host = uri.Host;
      return string.IsNullOrEmpty(host) ? null : host.ToLowerInvariant();
    }
    catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or ArgumentException)
    {
      return null;
    }
  }
}
