using Api.Services;
using Xunit;

namespace Api.Tests;

public class ErrorPagesTests
{
  // The heading of 404.html; no other status may borrow it.
  private const string NotFoundHeading = "This channel isn’t in your playlist.";

  [Fact]
  public void For_404_UsesThe404PageWithItsOwnText()
  {
    var page = ErrorPages.For(404);

    Assert.Equal("404.html", page.File);
    Assert.Null(page.Text);
  }

  [Theory]
  [InlineData(500, "500.html")]
  [InlineData(502, "50x.html")]
  [InlineData(503, "50x.html")]
  [InlineData(599, "50x.html")]
  public void For_500_And_Other5xx_KeepTheirPages(int statusCode, string file)
  {
    var page = ErrorPages.For(statusCode);

    Assert.Equal(file, page.File);
    Assert.Null(page.Text);
  }

  [Fact]
  public void For_429_UsesThe50xPageWithTooManyRequestsText()
  {
    var page = ErrorPages.For(429);

    Assert.Equal("50x.html", page.File);
    Assert.NotNull(page.Text);
    Assert.Equal("429", page.Text.Code);
    Assert.Contains("try again", page.Text.Lead, StringComparison.OrdinalIgnoreCase);

    var all = AllText(page.Text);
    Assert.DoesNotContain("Nothing here", all, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("restarting", all, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("channel", all, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData(400)]
  [InlineData(403)]
  [InlineData(405)]
  [InlineData(410)]
  [InlineData(302)]
  [InlineData(0)]
  [InlineData(600)]
  public void For_OtherClientErrors_UsesANeutralMessageWithTheRealCode(int statusCode)
  {
    var page = ErrorPages.For(statusCode);

    Assert.NotNull(page.Text);
    Assert.Equal(statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture), page.Text.Code);

    var all = AllText(page.Text);
    Assert.DoesNotContain(NotFoundHeading, all);
    Assert.DoesNotContain("Nothing here", all, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("not found", all, StringComparison.OrdinalIgnoreCase);
  }

  private static string AllText(ErrorPageText text) => $"{text.Title}\n{text.Heading}\n{text.Lead}";
}
