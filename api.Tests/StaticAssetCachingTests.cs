using Api.Services;
using Xunit;

namespace Api.Tests;

public class StaticAssetCachingTests
{
  [Theory]
  [InlineData("/index.html")]
  [InlineData("/main.js")]
  [InlineData("/app.css")]
  [InlineData("/blog/index.html")]
  public void UnversionedPagesAndCode_AreRevalidated(string path)
  {
    Assert.Equal("no-cache", StaticAssetCaching.GetCacheControl(path, versioned: false));
  }

  [Theory]
  [InlineData("/main.js")]
  [InlineData("/app.css")]
  public void VersionedAssets_AreImmutable(string path)
  {
    Assert.Equal(StaticAssetCaching.Immutable, StaticAssetCaching.GetCacheControl(path, versioned: true));
  }

  [Fact]
  public void Fonts_AreCachedForThirtyDays()
  {
    Assert.Equal(StaticAssetCaching.Fonts, StaticAssetCaching.GetCacheControl("/fonts/figtree-latin-wght-normal.woff2", versioned: false));
  }
}
