namespace Api.Services;

/// <summary>
/// Cache policy for the frontend files. Without explicit headers Cloudflare and browsers guess a
/// lifetime, which can pair a fresh index.html with a stale main.js after a deploy.
/// </summary>
public static class StaticAssetCaching
{
  public const string Revalidate = "no-cache";
  public const string Immutable = "public, max-age=31536000, immutable";
  public const string Fonts = "public, max-age=2592000";

  /// <param name="path">Request path of the static file.</param>
  /// <param name="versioned">True when the URL carries a content version (<c>?v=</c>), stamped at image build time.</param>
  public static string GetCacheControl(string path, bool versioned)
  {
    if (versioned) return Immutable;
    return path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ? Fonts : Revalidate;
  }
}
