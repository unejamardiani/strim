using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the lines that tell an invited person and the owner that a viewer's watchlist shows in the player while the
/// Watchlist group is on (#526), at their source, as the other UI tests do.
/// </summary>
public class WatchlistDisclosureUiTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"'{start}' not found.");
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    Assert.True(to > from, $"'{end}' not found after '{start}'.");
    return source[from..to];
  }

  [Fact]
  public void ViewerLibrary_SaysTheWatchlistShowsInTheOwnersPlayer_OnlyForViewersAndOnlyWhenOn()
  {
    var library = Read("library.js");
    var line = Between(library, "const watchlistShared = (ctx) => {", "\n  };");
    // Behind the viewer check and the list's flag from library/open.
    Assert.Contains("if (!isViewer(ctx) || ctx.list?.watchlistGroup !== true) return null;", line);
    // Built from the owner's name as the cover shows it; without one, "the owner's", never "Someone's".
    Assert.Contains("const owner = ctx.list.ownerName ? `${ctx.list.ownerName}'s` : \"the owner's\";", line);
    Assert.Contains("ctx.h('p', { class: 'lib-status', text: `Titles here also show in ${owner} player. No names are shown.` })", line);
    Assert.DoesNotContain("Someone", line);
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(library, "No names are shown"));

    // Home: directly under the body of "Your watchlist", whichever of rail, rows or empty text it shows.
    var home = Between(library, "const watch = marked(part('Your watchlist'", "// Series you follow");
    Assert.Contains("'Nothing on your watchlist yet. Add a film or series with the bookmark in its card.' }),\n          watchlistShared(ctx) || '');", home);

    // Marks: once, above the list or grid, only in the Watchlist view; painted again when the list arrives.
    var marks = Between(library, "function myMarks(ctx) {", "function syncRows(");
    Assert.Contains("ctx.paintShared = () => shared.replaceChildren((view.filter === 'watchlist' && watchlistShared(ctx)) || '');", marks);
    Assert.Contains("const paint = () => {\n      ctx.paintShared();", marks);
    Assert.Contains("chips),\n      shared,\n      h('div', { class: 'lib-list' }, list, foot));", marks);
    Assert.Contains("ctx.lookupMarks?.();\n    ctx.paintShared?.();", Between(library, "async function load(", "const TYPES = ["));

    // Nowhere else: the two uses above.
    Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(library, @"watchlistShared\(").Count);
  }

  [Fact]
  public void SharedLibrary_PassesWatchlistGroupToTheLibrary()
  {
    var shared = Between(Read("main.js"), "function showSharedLibrary(id) {", "search: (body, signal)");
    var load = Between(shared, "loadGroups: async (signal) => {", "\n    },");
    Assert.Contains("ownerName: open.ownerName,\n        watchlistGroup: open.watchlistGroup === true };", load);
  }

  [Fact]
  public void SharingPage_ListsTheWatchlistItem_OnlyWhenTheGroupIsOn()
  {
    var gets = Between(Read("main.js"), "function sharingGets() {", "async function createShare(");
    const string item ="Their watchlist shows in your player's Watchlist group, without names";
    Assert.Contains("const playlist = state.playlists.find((p) => p.id === sharing.playlist.id);", gets);
    Assert.Contains($"if (playlist?.watchlistGroup) checks.push(\"{item}\");", gets);

    // After the marks item, before the "No player link" item, with the check icon of the checks.
    var marksAt = gets.IndexOf("'Keep their own seen and thumbs marks'];", StringComparison.Ordinal);
    var itemAt = gets.IndexOf(item, StringComparison.Ordinal);
    var noAt = gets.IndexOf("'No player link, no source, no changes to the list'", StringComparison.Ordinal);
    Assert.True(marksAt >= 0 && marksAt < itemAt && itemAt < noAt);
    Assert.Contains("checks.map((text) => h('li', null, icon('check'), text))", gets);
  }
}
