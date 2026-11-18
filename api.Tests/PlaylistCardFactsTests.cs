using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the stored facts on the playlist cards (#234): main.js reads them from the overview, leaves
/// out null facts, shows only the source host, and app.css hides the line on phones.
/// The data contract itself is covered by <see cref="PlaylistOverviewTests"/>.
/// </summary>
public class PlaylistCardFactsTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string FactsFunction()
  {
    var js = Read("main.js");
    var match = Regex.Match(js, @"function playlistFacts\(id\) \{.*?\n\}", RegexOptions.Singleline);
    Assert.True(match.Success, "main.js has no playlistFacts function.");
    return match.Value;
  }

  [Fact]
  public void ListRoute_LoadsTheOverview_AndDropsCachedFactsWhenItFails()
  {
    var js = Read("main.js");
    Assert.Contains("api('/playlists/overview')", js);
    Assert.Contains("playlistFacts(playlist.id)", js);
    var load = Regex.Match(js, @"async function loadOverview\(\) \{.*?\n\}", RegexOptions.Singleline).Value;
    Assert.False(string.IsNullOrEmpty(load), "main.js has no loadOverview function.");

    // A failed call or a non-array body no longer returns early and keeps the earlier cache (#254).
    Assert.DoesNotMatch(@"catch \{\s*return;\s*\}", load);
    Assert.DoesNotContain("if (!Array.isArray(data)) return;", load);
    Assert.Matches(@"catch \{\s*data = null;\s*\}", load);
    // The cache is rebuilt from the response, or emptied when the response is unusable.
    Assert.Contains("state.overview = Array.isArray(data) ? new Map(data.map((item) => [item.id, item])) : new Map();", load);

    // Every rendered facts line is redrawn after either outcome, so empty ones replace old counts and dates.
    var reset = load.IndexOf("state.overview = Array.isArray(data)", StringComparison.Ordinal);
    var redraw = load.IndexOf("node.replaceWith(playlistFacts(node.dataset.id))", StringComparison.Ordinal);
    Assert.True(reset >= 0 && redraw > reset, "loadOverview redraws the facts lines after setting the cache.");
  }

  [Fact]
  public void Facts_AreEmpty_WhenThePlaylistIsNotInTheOverview()
  {
    var facts = FactsFunction();
    // Without an overview entry no fact is pushed: the line renders as an empty .facts node.
    Assert.Contains("const item = state.overview.get(id);", facts);
    Assert.Matches(@"if \(item\) \{", facts);
    Assert.Contains("return h('div', { class: 'facts', dataset: { id } }, parts);", facts);
  }

  [Fact]
  public void Facts_UseTheAgreedWording_AndLeaveOutNullValues()
  {
    var facts = FactsFunction();
    Assert.Contains("` of ${plural(item.groupCount, 'group')} on`", facts);
    Assert.Contains("' entries'", facts);
    Assert.DoesNotContain("channels on", facts);
    Assert.Contains("`Changed ${relativeTime(item.updatedAt)}`", facts);
    Assert.Contains("`Player fetched ${relativeTime(item.lastViewedUtc)}`", facts);
    Assert.Contains("item.groupCount != null && item.groupsOn != null", facts);
    Assert.Contains("item.totalChannels != null", facts);
    Assert.Contains("if (item.lastViewedUtc)", facts);
    Assert.Contains("if (item.sourceHost)", facts);
    // No placeholder for a missing fact and never the source address.
    Assert.DoesNotContain("—", facts);
    Assert.DoesNotContain("sourceUrl", facts);
    Assert.DoesNotContain("sourceHost(", facts);
  }

  [Fact]
  public void AppCss_HidesFactsOnPhones_AndUsesQuietInkWithTabularFigures()
  {
    var css = Read("app.css");
    var outsideMedia = Regex.Replace(css, @"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "");
    Assert.Matches(@"\.facts\s*\{\s*display:\s*none;\s*\}", outsideMedia);

    var shown = Regex.Match(css, @"@media \(min-width: 768px\)\s*\{\s*\.facts:not\(:empty\)\s*\{([^}]*)\}((?:[^{}]*\{[^{}]*\})*)[^{}]*\}");
    Assert.True(shown.Success, "app.css shows the facts line only from 768px.");
    Assert.Contains("display: flex;", shown.Groups[1].Value);
    Assert.Contains("color: var(--ink-3);", shown.Groups[1].Value);
    Assert.Contains("font-variant-numeric: tabular-nums;", shown.Groups[1].Value);
    Assert.Matches(@"\.facts \.num\s*\{\s*color:\s*var\(--ink-2\);", shown.Groups[2].Value);
  }
}
