using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>
/// Builds the poster URL the browser loads straight from TMDB's image host. Strim never downloads, stores, proxies
/// or changes images; it only hands out this address for a TMDB file path.
/// </summary>
public static partial class TmdbPosterUrl
{
  public const string Host = "image.tmdb.org";
  public const string Size = "w342";
  public const string BaseUrl = "https://" + Host + "/t/p/" + Size + "/";

  /// <summary>The poster URL for a TMDB <c>poster_path</c> such as <c>/abc123.jpg</c>; null for anything else.</summary>
  public static string? From(string? posterPath) =>
    posterPath is not null && PlainFilePath().IsMatch(posterPath) ? BaseUrl + posterPath[1..] : null;

  // One slash, then a plain file name: letters, digits, "_" or "-", and an image extension. This rules out "..",
  // "//", "?", "#", ":", backslashes and anything else that could leave the fixed host and size.
  [GeneratedRegex(@"\A/[A-Za-z0-9_-]{1,128}\.(?:jpg|jpeg|png|webp)\z", RegexOptions.CultureInvariant)]
  private static partial Regex PlainFilePath();
}
