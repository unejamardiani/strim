using Api.Services;
using Xunit;

namespace Api.Tests;

/// <summary>Poster URLs on TMDB's image host, built only from a plain file path.</summary>
public sealed class TmdbPosterUrlTests
{
  private const string Prefix = "https://image.tmdb.org/t/p/w342/";

  [Fact]
  public void ValidPath_BuildsW342Url()
  {
    Assert.Equal("https://image.tmdb.org/t/p/w342/abc123.jpg", TmdbPosterUrl.From("/abc123.jpg"));
    Assert.Equal("https://image.tmdb.org/t/p/w342/A-b_9.png", TmdbPosterUrl.From("/A-b_9.png"));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("/")]
  [InlineData("/../secret.jpg")]
  [InlineData("/a..jpg")]
  [InlineData("//evil.example/a.jpg")]
  [InlineData("/a.jpg?size=original")]
  [InlineData("/a.jpg#x")]
  [InlineData("/https:a.jpg")]
  [InlineData("https://evil.example/a.jpg")]
  [InlineData("/a\\b.jpg")]
  [InlineData("/dir/a.jpg")]
  [InlineData("abc123.jpg")]
  [InlineData("/abc 123.jpg")]
  [InlineData("/abc123")]
  [InlineData("/abc123.exe")]
  [InlineData("/abc%2F123.jpg")]
  [InlineData("/abc123.jpg\n")]
  public void UnsafePath_ReturnsNull(string? path)
  {
    Assert.Null(TmdbPosterUrl.From(path));
  }

  [Fact]
  public void Url_AlwaysOnFixedHostAndSize()
  {
    Assert.Equal("image.tmdb.org", TmdbPosterUrl.Host);
    Assert.Equal("w342", TmdbPosterUrl.Size);
    Assert.Equal(Prefix, TmdbPosterUrl.BaseUrl);

    foreach (var path in new[] { "/abc123.jpg", "/x.webp", "/../x.jpg", "//other/x.jpg", "/x.jpg?w=1", "/q:x.jpg" })
    {
      var url = TmdbPosterUrl.From(path);
      if (url is null) continue;
      Assert.StartsWith(Prefix, url, StringComparison.Ordinal);
      var uri = new Uri(url);
      Assert.Equal("https", uri.Scheme);
      Assert.Equal("image.tmdb.org", uri.Host);
      Assert.Equal("/t/p/w342/" + path[1..], uri.AbsolutePath);
      Assert.Empty(uri.Query);
    }
  }
}
