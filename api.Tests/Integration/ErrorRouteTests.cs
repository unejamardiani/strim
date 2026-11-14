using System.Net;
using Api.Services;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>The error route against the real pages in the repository root.</summary>
public sealed class ErrorRouteTests : IDisposable
{
  // The headings of 404.html and 50x.html as they ship.
  private const string NotFoundHeading = "This channel isn’t in your playlist.";
  private const string UnavailableHeading = "We’ll be right back.";

  private readonly StrimAppFactory _factory = new() { WebRoot = StrimAppFactory.RepositoryRoot() };

  public void Dispose() => _factory.Dispose();

  [Fact]
  public async Task Error404_ServesThe404Page()
  {
    var (response, body) = await GetAsync("/error/404");

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    AssertHtml(response);
    Assert.Contains(NotFoundHeading, body);
  }

  [Fact]
  public async Task Error429_ServesTooManyRequestsPage()
  {
    var (response, body) = await GetAsync("/error/429");
    var text = ErrorPages.For(429).Text!;

    Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    AssertHtml(response);
    Assert.Contains($"<h1>{text.Heading}</h1>", body);
    Assert.Contains("<p class=\"eyebrow\">Error 429</p>", body);
    Assert.Contains($"<title>{text.Title} | Strim</title>", body);
    Assert.DoesNotContain(NotFoundHeading, body);
    Assert.DoesNotContain(UnavailableHeading, body);
    Assert.DoesNotContain("restarting", body);
    // Header and footer of the page stay.
    Assert.Contains("class=\"site-top\"", body);
    Assert.Contains("class=\"site-footer\"", body);
  }

  [Fact]
  public async Task Error403_ServesNeutralPage()
  {
    var (response, body) = await GetAsync("/error/403");
    var text = ErrorPages.For(403).Text!;

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    AssertHtml(response);
    Assert.Contains($"<h1>{text.Heading}</h1>", body);
    Assert.Contains("<p class=\"eyebrow\">Error 403</p>", body);
    Assert.DoesNotContain(NotFoundHeading, body);
    Assert.DoesNotContain("404", body);
  }

  [Fact]
  public async Task Error503_StillServesThe50xPage()
  {
    var (response, body) = await GetAsync("/error/503");

    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    Assert.Contains(UnavailableHeading, body);
  }

  [Fact]
  public async Task RenderedErrorPage_HasNoInlineScriptOrStyle()
  {
    var (_, body) = await GetAsync("/error/429");

    Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("style=", body, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Content-Security-Policy", body);
  }

  [Fact]
  public async Task UnknownPage_StillGetsThe404Page()
  {
    var (response, body) = await GetAsync("/no-such-page");

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Contains(NotFoundHeading, body);
  }

  private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string path)
  {
    var response = await _factory.CreateClient().GetAsync(path);
    return (response, await response.Content.ReadAsStringAsync());
  }

  private static void AssertHtml(HttpResponseMessage response) =>
    Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
}
