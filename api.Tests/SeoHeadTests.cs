using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the head of the public pages for search engines and link previews, and the favicon file on every
/// page Strim serves (#623).</summary>
public class SeoHeadTests
{
  private const string Origin = "https://strim.plis.dev/";

  private static readonly string[] Public = ["index.html", "features.html", "how-to-use.html", "comparison.html"];

  public static TheoryData<string> PublicPages => Rows(Public);

  public static TheoryData<string> AllPages => Rows([.. Public, "app.html", "404.html", "500.html", "50x.html"]);

  private static TheoryData<string> Rows(string[] pages)
  {
    var rows = new TheoryData<string>();
    foreach (var page in pages) rows.Add(page);
    return rows;
  }

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string Head(string file)
  {
    var html = Read(file);
    return html[..html.IndexOf("</head>", StringComparison.Ordinal)];
  }

  private static string Title(string file) => Regex.Match(Head(file), "<title>([^<]*)</title>").Groups[1].Value;

  private static string Meta(string file, string attribute, string key)
  {
    var match = Regex.Match(Head(file), $"<meta {attribute}=\"{Regex.Escape(key)}\" content=\"([^\"]*)\"");
    Assert.True(match.Success, $"{file} has no <meta {attribute}=\"{key}\">.");
    return match.Groups[1].Value;
  }

  [Theory]
  [MemberData(nameof(PublicPages))]
  public void Title_IsPresentAndAtMost60Characters(string page)
  {
    var title = Title(page);
    Assert.False(string.IsNullOrWhiteSpace(title), $"{page} has no title.");
    Assert.True(title.Length <= 60, $"{page} title has {title.Length} characters.");
  }

  [Theory]
  [MemberData(nameof(PublicPages))]
  public void Description_Has70To160CharactersAndNamesIptvOrM3u(string page)
  {
    var description = Meta(page, "name", "description");
    Assert.InRange(description.Length, 70, 160);
    Assert.True(description.Contains("IPTV") || description.Contains("M3U"), $"{page} description names neither IPTV nor M3U.");
  }

  [Fact]
  public void TitlesAndDescriptions_AreUniqueAcrossThePublicPages()
  {
    Assert.Equal(Public.Length, Public.Select(Title).Distinct().Count());
    Assert.Equal(Public.Length, Public.Select(p => Meta(p, "name", "description")).Distinct().Count());
  }

  [Theory]
  [MemberData(nameof(PublicPages))]
  public void Canonical_IsThePagesOwnUrl_AndOgUrlEqualsIt(string page)
  {
    var expected = page == "index.html" ? Origin : Origin + page;
    Assert.Contains($"<link rel=\"canonical\" href=\"{expected}\" />", Head(page));
    Assert.Equal(expected, Meta(page, "property", "og:url"));
  }

  [Theory]
  [MemberData(nameof(PublicPages))]
  public void OpenGraphAndTwitterTags_ArePresentAndNotEmpty(string page)
  {
    Assert.Equal("website", Meta(page, "property", "og:type"));
    Assert.Equal("Strim", Meta(page, "property", "og:site_name"));
    Assert.NotEmpty(Meta(page, "property", "og:title"));
    Assert.NotEmpty(Meta(page, "property", "og:description"));
    Assert.Equal("summary", Meta(page, "name", "twitter:card"));
    Assert.NotEmpty(Meta(page, "name", "twitter:title"));
    Assert.NotEmpty(Meta(page, "name", "twitter:description"));
    Assert.DoesNotContain("property=\"twitter:", Read(page));
  }

  [Theory]
  [MemberData(nameof(PublicPages))]
  public void Page_HasOneH1_AndEnglishLanguage(string page)
  {
    var html = Read(page);
    Assert.Single(Regex.Matches(html, "<h1[\\s>]"));
    Assert.Contains("<html lang=\"en\"", html);
  }

  [Theory]
  [MemberData(nameof(AllPages))]
  public void Icon_IsTheLogoFile_NotADataUri(string page)
  {
    var head = Head(page);
    Assert.DoesNotMatch(new Regex("rel=\"icon\"[^>]*href=\"data:"), head);
    Assert.Contains("<link rel=\"icon\" href=\"/logo-icon.svg\" type=\"image/svg+xml\" />", head);
  }

  [Fact]
  public void Image_ShipsTheLogoFile()
  {
    Assert.True(File.Exists(Path.Combine(StrimAppFactory.RepositoryRoot(), "logo-icon.svg")));
    Assert.Matches(new Regex(@"cp [^\n]*logo-icon\.svg[^\n]* api/wwwroot/"), Read("Dockerfile"));
  }
}
