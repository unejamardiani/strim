using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the wide Library (#288): from the editor's 1100px breakpoint library.css places the group list and the
/// open group's entries side by side with a media query alone; below it the phone layout is unchanged, and
/// DESIGN.md describes the Library screen.
/// </summary>
public class LibraryWideLayoutTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private const string MediaBlock = @"@media \(min-width: 1100px\)\s*\{((?:[^{}]*\{[^{}]*\})*)[^{}]*\}";

  private static string OutsideMedia(string css) => Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "");

  [Fact]
  public void LibraryCss_FromEditorBreakpoint_PlacesGroupsAndOpenGroupSideBySide()
  {
    // The editor's wide breakpoint is the one the Library uses.
    Assert.Matches(MediaBlock, Read("app.css"));
    var media = Regex.Match(Read("library.css"), MediaBlock);
    Assert.True(media.Success, "library.css has no @media (min-width: 1100px) block.");
    var body = media.Groups[1].Value;
    Assert.Matches(@"\.page:has\(\.lib-panel\)\s*\{\s*max-width:\s*1440px;", body);
    Assert.Matches(@"\.library-browse:has\(> \.library-open:not\(\[hidden\]\)\)\s*\{[^}]*display:\s*grid;[^}]*grid-template-columns:\s*minmax\(300px, 0\.4fr\) minmax\(0, 1fr\);", body);
    // The group list stays shown next to the open group even though library.js hides it on a phone. theme.css
    // hides every [hidden] element with !important, so a plain display would lose: the override must be
    // !important too, and library.css must load after theme.css for it to win.
    Assert.Matches(@"\[hidden\]\s*\{\s*display:\s*none\s*!important;", Read("theme.css"));
    Assert.Matches(@"\.library-browse > \.library-groups\[hidden\]\s*\{\s*display:\s*flex\s*!important;", body);
    var html = Read("app.html");
    var theme = html.IndexOf("href=\"theme.css\"", StringComparison.Ordinal);
    var library = html.IndexOf("href=\"library.css\"", StringComparison.Ordinal);
    Assert.True(theme >= 0 && library > theme, "app.html must load library.css after theme.css.");
    Assert.Matches(@"\.library-browse > \.library-title\s*\{\s*grid-column:\s*1 / -1;", body);
    Assert.Matches(@"\.library-browse > \.library-open\s*\{[^}]*position:\s*sticky;[^}]*overflow-y:\s*auto;", body);
  }

  [Fact]
  public void LibraryCss_BelowBreakpoint_KeepsThePhoneLayout()
  {
    var outside = OutsideMedia(Read("library.css").Split("/* Film card (#367)")[0]);
    // Below 1100px nothing places columns or overrides the hidden group list: an open group replaces the list.
    Assert.DoesNotContain("grid", outside);
    Assert.DoesNotContain("[hidden]", outside);
    Assert.DoesNotContain(".library-open", outside);
    Assert.DoesNotContain("max-width", outside);
    Assert.Matches(@"\.library-browse\s*\{\s*display:\s*flex;\s*flex-direction:\s*column;", outside);

    var js = Read("library.js");
    Assert.Contains("const entriesEl = h('div', { class: 'library-open', hidden: true });", js);
    Assert.Contains("groupsEl.hidden = true;", js);
    Assert.Contains("groupsEl.hidden = false;", js);
  }

  [Fact]
  public void LibraryStyles_LiveInLibraryCss_WithoutInlineStyles()
  {
    Assert.DoesNotContain(".library-", Read("app.css"));
    Assert.DoesNotContain("style=", Read("app.html"));
    var js = Read("library.js").Split("// Film card (#367)")[0];
    Assert.DoesNotContain("style", js);
    Assert.DoesNotContain("matchMedia", js);
  }

  [Fact]
  public void DesignMd_HasLibrarySection_WithPurposeRulesAndBothLayouts()
  {
    var design = Read("DESIGN.md").ReplaceLineEndings("\n");
    var start = design.IndexOf("### Library (`#/p/<id>/library`: library.js + library.css)", StringComparison.Ordinal);
    Assert.True(start >= 0, "DESIGN.md has no Library section.");
    var end = design.IndexOf("\n### ", start + 4, StringComparison.Ordinal);
    var section = design[start..end].Replace("\n", " ");
    Assert.Contains("Purpose:", section);
    Assert.Contains("Read-only: nothing in the Library changes the playlist or what the player link serves.", section);
    Assert.Contains("My list by default", section);
    Assert.Contains("\"Include switched-off groups\"", section);
    Assert.Contains("Search:", section);
    Assert.Contains("Browse:", section);
    Assert.Contains("Phone layout (below 1100px)", section);
    Assert.Contains("Wide Library (from 1100px, CSS only)", section);
    Assert.Contains("`minmax(300px, 0.4fr) minmax(0, 1fr)`", section);
    Assert.Contains("1440px maximum", section);
  }
}
