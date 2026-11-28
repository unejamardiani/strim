using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// The landing page names the site and the app to search engines as microdata (#624): a WebSite and a free
/// SoftwareApplication. Microdata, not JSON-LD, because the page carries no <c>&lt;script&gt;</c>.
/// </summary>
public class StructuredDataTests
{
  private const string Schema = "https://schema.org/";
  private const string SiteUrl = "https://strim.plis.dev/";

  private static string Landing() =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), "index.html")).ReplaceLineEndings("\n");

  /// <summary>The inner markup of the single element whose <c>itemtype</c> is <paramref name="type"/>.</summary>
  private static string Item(string html, string type)
  {
    var opening = Assert.Single(Regex.Matches(html, $@"<(\w+)[^>]*\bitemtype=""{Regex.Escape(Schema + type)}""[^>]*>"));
    Assert.Contains("itemscope", opening.Value);
    return html[(opening.Index + opening.Length)..Closing(html, opening)];
  }

  /// <summary>Where the closing tag of the element that <paramref name="opening"/> opens starts.</summary>
  private static int Closing(string html, Match opening)
  {
    var tag = opening.Groups[1].Value;
    var start = opening.Index + opening.Length;
    var depth = 1;
    foreach (Match match in Regex.Matches(html[start..], $@"<(/?){tag}\b[^>]*>"))
    {
      depth += match.Groups[1].Value == "/" ? -1 : 1;
      if (depth == 0)
        return start + match.Index;
    }
    throw new Xunit.Sdk.XunitException($"<{tag}> at {opening.Index} is never closed");
  }

  /// <summary>
  /// The item's own markup: nested items are cut out, because their properties belong to them and not to the
  /// enclosing item.
  /// </summary>
  private static string Own(string item)
  {
    const string NestedItem = @"<(\w+)[^>]*\bitemscope\b[^>]*>";
    for (var nested = Regex.Match(item, NestedItem); nested.Success; nested = Regex.Match(item, NestedItem))
    {
      var end = item.IndexOf('>', Closing(item, nested)) + 1;
      item = item[..nested.Index] + item[end..];
    }
    return item;
  }

  private static void AssertMeta(string item, string property, string content) =>
    Assert.Contains($@"<meta itemprop=""{property}"" content=""{content}"" />", item);

  private static void AssertUrl(string item) =>
    Assert.Contains($@"<link itemprop=""url"" href=""{SiteUrl}"" />", item);

  [Fact]
  public void WebSite_IsNamedStrim_WithTheSiteUrl()
  {
    var site = Own(Item(Landing(), "WebSite"));

    AssertMeta(site, "name", "Strim");
    AssertUrl(site);
  }

  [Fact]
  public void SoftwareApplication_IsAFreeWebApp_WithTheMetaDescription()
  {
    var html = Landing();
    var app = Item(html, "SoftwareApplication");
    var own = Own(app);
    var description = Regex.Match(html, @"<meta name=""description"" content=""([^""]+)"" />").Groups[1].Value;

    Assert.Contains(@"<span itemprop=""name"">Strim</span>", own);
    Assert.Single(Regex.Matches(own, @"itemprop=""name"""));
    AssertUrl(own);
    AssertMeta(own, "applicationCategory", "MultimediaApplication");
    AssertMeta(own, "operatingSystem", "Web");
    Assert.NotEmpty(description);
    AssertMeta(own, "description", description);
    AssertMeta(own, "isAccessibleForFree", "true");

    Assert.Contains($@"itemprop=""offers"" itemscope itemtype=""{Schema}Offer""", app);
    AssertMeta(Own(Item(app, "Offer")), "price", "0");
  }

  [Fact]
  public void LandingPage_HasNoRatingOrReview_AndStillNoScriptOrInlineStyle()
  {
    var html = Landing();

    Assert.DoesNotContain("aggregateRating", html, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotMatch(new Regex(@"item(prop|type)=""[^""]*review", RegexOptions.IgnoreCase), html);
    Assert.DoesNotContain("priceCurrency", html);
    Assert.DoesNotContain("<script", html);
    Assert.DoesNotContain("style=\"", html);
  }
}
