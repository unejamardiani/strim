using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Watchlist group switch in the playlist's ⋯ sheet and the player link card's hint (#524) at
/// their source in main.js, as the other UI tests do (api.Tests has no JS engine).</summary>
public class WatchlistGroupSwitchUiTests
{
  private const string OnHint =
    "Add this link to your IPTV player. It always serves the groups you saved here, with the watchlist as the first group: your titles and those of people you shared this playlist with.";
  private const string OffHint = "Add this link to your IPTV player. It always serves the groups you saved here.'";

  private static string Js() =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), "main.js")).ReplaceLineEndings("\n");

  private static string Function(string js, string signature)
  {
    var start = js.IndexOf(signature, StringComparison.Ordinal);
    Assert.True(start >= 0, $"'{signature}' is missing from main.js.");
    var end = js.IndexOf("\n}\n", start, StringComparison.Ordinal);
    Assert.True(end > start, $"The end of '{signature}' was not found.");
    return js[start..end];
  }

  [Fact]
  public void Menu_HasTheWatchlistGroupItem_BetweenRefreshAndTurnLinkOff()
  {
    var menu = Function(Js(), "function openPlaylistMenu(id) {");
    var refresh = menu.IndexOf("'Refresh every 6 hours'", StringComparison.Ordinal);
    var item = menu.IndexOf("item('bookmark', 'Watchlist group', () => toggleWatchlistGroup(playlist)", StringComparison.Ordinal);
    var turnLink = menu.IndexOf("'Turn link", StringComparison.Ordinal);
    Assert.True(refresh >= 0, "The refresh item is missing.");
    Assert.True(item > refresh, "The Watchlist group item must come after 'Refresh every 6 hours'.");
    Assert.True(turnLink > item, "The Watchlist group item must come before 'Turn link'.");

    var block = menu[item..turnLink];
    Assert.Contains("'Your watchlist and that of people you shared with, as the first group in your player'", block);
    Assert.Contains("'Players show changes when they reload the list'", block);
    Assert.Contains("playlist.watchlistGroup ? 'Players show changes when they reload the list'", block);
    Assert.Contains("switch${playlist.watchlistGroup ? ' on' : ''}", block);
    Assert.Contains("'aria-hidden': 'true'", block);
  }

  [Fact]
  public void Switch_CallsTheRouteWithEnabled_AndRepaints()
  {
    var toggle = Function(Js(), "async function toggleWatchlistGroup(playlist) {");
    Assert.Contains("const enabled = !playlist.watchlistGroup;", toggle);
    Assert.Contains("api(`/playlists/${playlist.id}/watchlist-group`, { method: 'PATCH', body: { enabled } })", toggle);
    Assert.Contains("replacePlaylist({ id: playlist.id, watchlistGroup: result.watchlistGroup })", toggle);
    Assert.Contains("refreshViews(updated)", toggle);
    Assert.Contains("toast(err.message, { error: true })", toggle);
    Assert.DoesNotContain("confirm(", toggle);
  }

  [Fact]
  public void LinkCard_SaysWhoseTitlesTheLinkCarries()
  {
    var card = Function(Js(), "function linkCard(playlist) {");
    Assert.Contains("playlist.watchlistGroup", card);
    Assert.Contains("your titles and those of people you shared this playlist with", card);
    var on = card.IndexOf(OnHint, StringComparison.Ordinal);
    var off = card.IndexOf(OffHint, StringComparison.Ordinal);
    var choice = card.IndexOf("text: playlist.watchlistGroup", StringComparison.Ordinal);
    Assert.True(choice >= 0, "The hint must be chosen by playlist.watchlistGroup.");
    Assert.True(on > choice, "The on hint must follow the choice.");
    Assert.True(off > on, "The off hint (today's) must follow the on hint.");
  }
}
