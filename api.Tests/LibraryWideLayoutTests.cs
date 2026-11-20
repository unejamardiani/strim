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
    // An open group (#423) sits beside the group list, which sticks with its own scroll; the head loses its back.
    Assert.Contains(".lib-groups-cols.has-open { display: grid; grid-template-columns: minmax(300px, 0.4fr) minmax(0, 1fr); gap: 24px; align-items: start; }", body);
    Assert.Contains(".lib-groups-cols.has-open .lib-groups-side { position: sticky; top: calc(var(--topbar-h) + 12px); max-height: calc(100dvh - var(--topbar-h) - 30px); overflow-y: auto; }", body);
    Assert.Contains(".lib-groups-cols.has-open .lib-ghead .lib-ib { display: none; }", body);
    Assert.Contains(".lib-groups-cols.has-open .lib-sticky { margin: 0; padding: 0 0 10px; top: calc(var(--topbar-h) + 12px); background: var(--bg); }", body);
    var css = Read("library.css");
    foreach (var gone in new[] { ".library-open", ".library-entries-head", ".library-group-title", ".library-browse:has", ".library-groups[hidden]" })
      Assert.DoesNotContain(gone, css);
  }

  [Fact]
  public void LibraryCss_BelowBreakpoint_KeepsThePhoneLayout()
  {
    var css = Read("library.css");
    var outside = OutsideMedia(css.Split("/* Film card (#367)")[0]);
    // Below 1100px nothing places columns: an open group replaces the list, which CSS hides.
    Assert.DoesNotContain("grid", outside);
    Assert.DoesNotContain("[hidden]", outside);
    Assert.DoesNotContain("max-width", outside);
    Assert.Matches(@"\.library-browse\s*\{\s*display:\s*flex;\s*flex-direction:\s*column;", outside);
    Assert.Contains("@media (max-width: 1099px) { .lib-groups-cols.has-open .lib-groups-side { display: none; } }", css);

    var js = Read("library.js");
    Assert.Contains("const el = h('section', { class: 'library-browse lib-groups-cols' }, side, openEl);", js);
    Assert.Contains("el.classList.add('has-open');", js);
    Assert.Contains("el.classList.remove('has-open');", js);
    Assert.DoesNotContain("groupsEl.hidden", js);
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
