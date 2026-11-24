using System.Text.RegularExpressions;
using Api.Services;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the content security policies (#307): app.html lets posters load from TMDB's image host and from nowhere
/// else; the other pages keep their policy.
/// </summary>
public sealed class ContentSecurityPolicyTests : IDisposable
{
  private const string AppPolicy =
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https://image.tmdb.org; connect-src 'self' http://localhost:5000 http://127.0.0.1:5000; form-action 'self'; base-uri 'self';";

  private const string PagePolicy =
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; form-action 'self'; base-uri 'self';";

  private readonly StrimAppFactory _factory = new() { WebRoot = StrimAppFactory.RepositoryRoot() };

  public void Dispose() => _factory.Dispose();

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string MetaPolicy(string html)
  {
    var matches = Regex.Matches(html, "<meta http-equiv=\"Content-Security-Policy\" content=\"([^\"]*)\" />");
    Assert.Single(matches);
    Assert.Single(Regex.Matches(html, "Content-Security-Policy"));
    return matches[0].Groups[1].Value;
  }

  private static Dictionary<string, string> Directives(string policy) =>
    policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(d => d.Split(' ', 2))
      .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

  [Fact]
  public void AppHtml_ImgSrc_AllowsOnlyTmdbImageHost()
  {
    var policy = MetaPolicy(Read("app.html"));
    Assert.Equal(AppPolicy, policy);

    var directives = Directives(policy);
    var sources = directives["img-src"].Split(' ');
    Assert.Equal(new[] { "'self'", "data:", $"https://{TmdbPosterUrl.Host}" }, sources);
    Assert.DoesNotContain(sources, s => s.Contains('*'));

    // Every other directive is the one the pages without posters use, apart from the editor's connect-src.
    var page = Directives(PagePolicy);
    foreach (var (name, value) in directives.Where(d => d.Key is not "img-src" and not "connect-src"))
      Assert.Equal(page[name], value);
    Assert.Equal(page.Keys.OrderBy(k => k), directives.Keys.OrderBy(k => k));
  }

  [Fact]
  public void AppHtml_ConnectSrc_AllowsOnlyItsOwnOriginAndTheLocalApi()
  {
    // #540: no https: source; the editor fetches only its own API (posters load through img-src).
    var directives = Directives(MetaPolicy(Read("app.html")));
    Assert.Equal("'self' http://localhost:5000 http://127.0.0.1:5000", directives["connect-src"]);
  }

  [Theory]
  [InlineData("index.html")]
  [InlineData("how-to-use.html")]
  [InlineData("features.html")]
  [InlineData("comparison.html")]
  [InlineData("404.html")]
  [InlineData("500.html")]
  [InlineData("50x.html")]
  public void OtherPages_KeepTheirPolicy(string file)
  {
    var policy = MetaPolicy(Read(file));
    Assert.Equal(PagePolicy, policy);
    Assert.DoesNotContain("tmdb", policy);
  }

  [Theory]
  [InlineData("/app.html", AppPolicy)]
  [InlineData("/", PagePolicy)]
  public async Task ResponseHeader_WhenSet_MatchesThePagePolicy(string path, string expected)
  {
    var response = await _factory.CreateClient().GetAsync(path);
    response.EnsureSuccessStatusCode();

    Assert.Equal(expected, MetaPolicy(await response.Content.ReadAsStringAsync()));
    if (response.Headers.TryGetValues("Content-Security-Policy", out var header))
      Assert.Equal(expected, Assert.Single(header));
  }
}
