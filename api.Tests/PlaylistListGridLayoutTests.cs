using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the wide playlist list (#233): DESIGN.md states it, app.css implements it with a 1024px media
/// query alone, the other routes keep the 760px page and below the breakpoint nothing changes.
/// </summary>
public class PlaylistListGridLayoutTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string MediaBody(string css, string query)
  {
    var media = Regex.Match(css, @"@media \(min-width: " + Regex.Escape(query) + @"\)\s*\{((?:[^{}]*\{[^{}]*\})*)[^{}]*\}");
    Assert.True(media.Success, $"app.css has no @media (min-width: {query}) block.");
    return media.Groups[1].Value;
  }

  [Fact]
  public void DesignMd_DescribesWideListPage_AndGrid()
  {
    var flat = Read("DESIGN.md").Replace("\r\n", "\n").Replace("\n", " ");
    Assert.Contains("Wide playlist list (from 1024px", flat);
    Assert.Contains("1200px maximum", flat);
    Assert.Contains("repeat(auto-fill, minmax(340px, 1fr))", flat);
    Assert.Contains("Below 1024px the list is the single 760px column", flat);
    // The editor width stays #216's rule.
    Assert.Contains("Wide editor (from 1100px", flat);
  }

  [Fact]
  public void AppCss_FromBreakpoint_WidensOnlyTheListPage_AndLaysOutAGrid()
  {
    var body = MediaBody(Read("app.css"), "1024px");
    Assert.Matches(@"\.page:has\(> \.playlist-list\)\s*\{\s*max-width:\s*1200px;", body);
    // auto-fill keeps empty tracks, so one or two cards are not stretched across the page.
    Assert.Matches(@"\.playlist-list\s*\{[^}]*display:\s*grid;[^}]*grid-template-columns:\s*repeat\(auto-fill, minmax\(340px, 1fr\)\);", body);
    // No reordering, so tab order follows DOM and reading order.
    Assert.DoesNotContain("order:", body);
    Assert.DoesNotContain("grid-auto-flow: dense", body);
    Assert.DoesNotContain("editor-split", body);
  }

  [Fact]
  public void AppCss_BelowBreakpoint_PageStays760_AndListIsAColumn()
  {
    var css = Read("app.css");
    var outsideMedia = Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "");
    Assert.Matches(@"\.page\s*\{[^}]*max-width:\s*760px;", outsideMedia);
    Assert.DoesNotContain(".page:has(> .playlist-list)", outsideMedia);
    Assert.DoesNotMatch(@"\.playlist-list\s*\{[^}]*display:\s*grid;", outsideMedia);
  }

  [Fact]
  public void ListRoute_RendersCardsInsideThePlaylistList_WithoutInlineStyle()
  {
    Assert.Contains("h('div', { class: 'playlist-list' }, playlists.map(playlistCard))", Read("main.js"));
    Assert.DoesNotContain("style=", Read("app.html"));
  }
}
