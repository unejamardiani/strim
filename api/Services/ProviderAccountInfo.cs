using System.Globalization;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Account facts from a provider's <c>player_api.php</c> answer. Never carries the username or
/// password. <see cref="IsKnown"/> is false when the answer could not be used.
/// <see cref="NeverExpires"/> is true when the answer states <c>exp_date: null</c>; callers then
/// show no expiry instead of falling back to another source.
/// </summary>
public sealed record ProviderAccountResult(
  bool IsKnown,
  string? Status,
  DateTimeOffset? ExpiresAt,
  int? MaxConnections,
  int? ActiveConnections,
  bool NeverExpires = false)
{
  public static readonly ProviderAccountResult Unknown = new(false, null, null, null, null);
}

/// <summary>
/// Asks a provider's <c>player_api.php</c> (only for <c>get.php</c> links) through the fetcher's
/// address and redirect checks.
/// </summary>
public static class ProviderAccountLookup
{
  /// <summary>Null when there is no account endpoint or the answer is unusable.</summary>
  public static async Task<ProviderAccountResult?> LookUpAsync(
    PlaylistSourceFetcher fetcher, string? sourceUrl, CancellationToken cancellationToken)
  {
    if (ProviderAccountInfo.BuildEndpoint(sourceUrl) is not { } endpoint) return null;

    var answer = await fetcher.FetchSmallTextAsync(endpoint.AbsoluteUri, cancellationToken);
    var account = answer.Success ? ProviderAccountInfo.Parse(answer.Text) : ProviderAccountResult.Unknown;
    return account.IsKnown ? account : null;
  }
}

/// <summary>
/// Pure helpers for provider account info: builds the account endpoint from a playlist link and
/// parses the account answer defensively. No I/O.
/// </summary>
public static class ProviderAccountInfo
{
  // 9999-12-31T23:59:59Z, the largest value DateTimeOffset accepts.
  private const long MaxUnixSeconds = 253402300799;

  /// <summary>
  /// Turns <c>…/get.php?username=u&amp;password=p…</c> into <c>…/player_api.php?username=u&amp;password=p</c>
  /// on the same scheme, host, port and directory. Returns null for any other link.
  /// </summary>
  public static Uri? BuildEndpoint(string? playlistLink)
  {
    if (string.IsNullOrWhiteSpace(playlistLink)
      || !Uri.TryCreate(playlistLink.Trim(), UriKind.Absolute, out var uri)
      || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
      return null;
    }

    var path = uri.AbsolutePath;
    var slash = path.LastIndexOf('/');
    if (!string.Equals(path[(slash + 1)..], "get.php", StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    string? username = null;
    string? password = null;
    foreach (var pair in uri.Query.TrimStart('?').Split('&'))
    {
      var equals = pair.IndexOf('=');
      if (equals <= 0)
      {
        continue;
      }

      var name = pair[..equals];
      var value = pair[(equals + 1)..];
      if (name == "username")
      {
        username ??= value;
      }
      else if (name == "password")
      {
        password ??= value;
      }
    }

    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
    {
      return null;
    }

    var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path[..(slash + 1)] + "player_api.php")
    {
      Query = $"username={username}&password={password}",
    };
    return builder.Uri;
  }

  /// <summary>
  /// Reads <c>user_info</c> from a <c>player_api.php</c> answer. Never throws; anything unusable
  /// (empty, HTML, invalid JSON, no <c>user_info</c> object, <c>auth: 0</c>) gives
  /// <see cref="ProviderAccountResult.Unknown"/>.
  /// </summary>
  public static ProviderAccountResult Parse(string? body)
  {
    if (string.IsNullOrWhiteSpace(body))
    {
      return ProviderAccountResult.Unknown;
    }

    try
    {
      using var document = JsonDocument.Parse(body);
      if (document.RootElement.ValueKind != JsonValueKind.Object
        || !document.RootElement.TryGetProperty("user_info", out var info)
        || info.ValueKind != JsonValueKind.Object
        || (info.TryGetProperty("auth", out var auth) && ReadLong(auth) == 0))
      {
        return ProviderAccountResult.Unknown;
      }

      return new ProviderAccountResult(
        IsKnown: true,
        Status: ReadText(info, "status"),
        ExpiresAt: ReadUnixTime(info, "exp_date"),
        MaxConnections: ReadInt(info, "max_connections"),
        ActiveConnections: ReadInt(info, "active_cons"),
        NeverExpires: info.TryGetProperty("exp_date", out var expiry) && expiry.ValueKind == JsonValueKind.Null);
    }
    catch (JsonException)
    {
      return ProviderAccountResult.Unknown;
    }
    catch (InvalidOperationException)
    {
      // Property lookups throw when a property name holds an escape that is not valid UTF-16.
      return ProviderAccountResult.Unknown;
    }
    catch (ArgumentException)
    {
      // JsonDocument.Parse throws when the text itself holds a lone surrogate character.
      return ProviderAccountResult.Unknown;
    }
  }

  private static string? ReadText(JsonElement parent, string name)
  {
    if (!parent.TryGetProperty(name, out var value))
    {
      return null;
    }

    var text = value.ValueKind switch
    {
      JsonValueKind.String => ReadString(value),
      JsonValueKind.Number => value.GetRawText(),
      _ => null,
    };
    return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
  }

  private static int? ReadInt(JsonElement parent, string name)
  {
    var number = parent.TryGetProperty(name, out var value) ? ReadLong(value) : null;
    return number is >= int.MinValue and <= int.MaxValue ? (int)number.Value : null;
  }

  private static DateTimeOffset? ReadUnixTime(JsonElement parent, string name)
  {
    var seconds = parent.TryGetProperty(name, out var value) ? ReadLong(value) : null;
    return seconds is >= 0 and <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
  }

  private static long? ReadLong(JsonElement value)
  {
    return value.ValueKind switch
    {
      JsonValueKind.Number when value.TryGetInt64(out var number) => number,
      JsonValueKind.String when long.TryParse(
        ReadString(value)?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
      _ => null,
    };
  }

  // GetString throws InvalidOperationException when an escape does not decode to valid UTF-16,
  // such as an unpaired surrogate ("\uD800"); such a field counts as unreadable.
  private static string? ReadString(JsonElement value)
  {
    try
    {
      return value.GetString();
    }
    catch (InvalidOperationException)
    {
      return null;
    }
  }
}
