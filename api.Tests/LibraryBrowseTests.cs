using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Library browse view (#287): the analysed groups by type and then in playlist order, the shared
/// 'Include switched-off groups' toggle, and paging through a group with /playlist/group-entries.
/// The endpoint itself is covered by the group-entries tests of #215.
/// </summary>
public class LibraryBrowseTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"'{start}' not found.");
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    Assert.True(to > from, $"'{end}' not found after '{start}'.");
    return source[from..to];
  }

  private static string Browse() => Between(Read("library.js"), "// Browse (#287)", "function hit(");

  [Fact]
  public void Groups_ComeFromTheAnalysis_SortedByTypeThenPlaylistOrder_WithTheirCount()
  {
    var library = Read("library.js");
    var browse = Browse();
    Assert.Contains("const BROWSE_ORDER = ['Movies', 'Series', 'Live'];", browse);
    Assert.Contains("const sorted = [...shown].sort((a, b) => BROWSE_ORDER.indexOf(a.type) - BROWSE_ORDER.indexOf(b.type) || a.index - b.index);", browse);
    // The chips and rows (#422) are pinned in LibraryGroupsUiTests.
    Assert.Contains("h('span', { class: 'lib-meta', text: plural(g.count, t.one, t.many) })", browse);

    // The groups are the ones the Library loaded once; the browse view has the Groups tab, the search its own (#418).
    var load = Between(library, "async function load(", "const TYPES = [");
    Assert.Contains("const browse = browseSection(ctx, result);", load);
    Assert.Contains("view.panels.search.replaceChildren(searchSection(ctx, result, browse.refresh, browse.reload));\n    view.panels.groups.replaceChildren(browse.el);", load);
    Assert.Single(Regex.Matches(library, Regex.Escape("ctx.loadGroups(")));

    // index and type are the playlist position and the content type main.js derives from the analysis.
    var apply = Between(Read("main.js"), "function applyAnalysis(ed, analysis, { sample })", "const perTitle = new Map();");
    Assert.Contains("return { key: g.name, title, type, count: g.count || 0, index,", apply);
  }

  [Fact]
  public void SharedToggle_ListsOnlySwitchedOnGroups_ShowsTheHiddenCount_AndMarksOffGroups()
  {
    var library = Read("library.js");
    var browse = Browse();
    // One toggle for search and browse.
    Assert.Single(Regex.Matches(library, Regex.Escape("'Include switched-off groups'")));
    var section = Between(library, "function searchSection(", "async function runSearch(");
    Assert.Contains("run();\n      onToggle();", section);
    Assert.Contains("includeOff = e.target.checked; ctx.onIncludeOff?.();", Between(library, "function libraryMenu(ctx)", "async function load("));

    Assert.Contains("const shown = list.groups.filter((g) => withOff || !list.disabled.has(g.key));", browse);
    Assert.Contains("return { sorted, hidden: list.groups.length - shown.length };", browse);
    Assert.Contains("const { sorted, hidden } = browseGroups(list, includeOff);", browse);
    Assert.Contains("if (hidden > 0) nodes.push(status(`${plural(hidden, 'switched-off group')} hidden`));", browse);
    Assert.Contains("list.disabled.has(g.key) ? h('span', { class: 'lib-off', text: 'Off' }) : null),", browse);

    var refresh = Between(browse, "const refresh = () => {", "};");
    Assert.Contains("renderGroups();", refresh);
    Assert.Contains("const gone = state.open && !includeOff && list.disabled.has(state.open.group.key) ? state.open.group : null;", refresh);
    Assert.Contains("if (gone) close();", refresh);
  }

  /// <summary>
  /// Walks a 120-entry group through the real group-entries endpoint, which the editor keeps using (the Library's open
  /// group asks the titles route since #443): pages with offset += names until total. A failed page leaves the shown
  /// names and a retry resumes at the same offset.
  /// </summary>
  [Fact]
  public async Task PagingAGroup_ThroughTheRealEndpoint_RequestsSuccessivePagesUntilTotal()
  {
    const int pageSize = 50;
    Assert.Contains("public const int MaxSearchResults = 50;", Read("api/Endpoints/EndpointSupport.cs"));
    Assert.DoesNotContain("innerHTML", Read("library.js"));

    using var app = new StrimAppFactory();
    var client = new ApiClient(app.CreateClient());
    var playlist = new System.Text.StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 120; i++) playlist.Append($"#EXTINF:-1 group-title=\"News\",News {i}\nhttp://line.example/live/{i}.ts\n");
    var analyzed = await client.PostAsync("/api/playlist/analyze", new { rawText = playlist.ToString() });
    analyzed.EnsureSuccessStatusCode();
    var cacheKey = (await analyzed.Content.ReadFromJsonAsync<AnalysisDto>())!.CacheKey;

    var shown = new List<string>();
    var offset = 0;
    var offsets = new List<int>();
    var mores = 0;
    var failed = false;
    while (true)
    {
      // The second page fails once (an expired session), then 'Try again' asks for the same offset.
      var key = offsets.Count == 1 && !failed ? "pl-expired" : cacheKey;
      offsets.Add(offset);
      var response = await client.Http.GetAsync($"/api/playlist/group-entries?cacheKey={Uri.EscapeDataString(key)}&group=News&offset={offset}&limit={pageSize}");
      if (!response.IsSuccessStatusCode)
      {
        failed = true;
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.Content.ReadFromJsonAsync<ErrorDto>())!.Error));
        Assert.Equal(pageSize, shown.Count);
        continue;
      }
      var page = (await response.Content.ReadFromJsonAsync<EntriesDto>())!;
      shown.AddRange(page.Names);
      offset += page.Names.Count;
      if (page.Names.Count == 0 || offset >= page.Total) break;
      mores++;
    }

    Assert.True(failed);
    Assert.Equal([0, 50, 50, 100], offsets);
    Assert.Equal(2, mores);
    Assert.Equal(Enumerable.Range(1, 120).Select(i => $"News {i}"), shown);
  }

  [Fact]
  public async Task GroupEntries_CarryTheWatchlistFlag()
  {
    using var app = new StrimAppFactory();
    var (client, cacheKey) = await LibraryMarksTests.WatchlistedAsync(app, "browse-watch");

    var body = await client.Http.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"/api/library/group-entries?cacheKey={Uri.EscapeDataString(cacheKey)}&group=Films");
    var flags = LibraryMarksTests.WatchlistFlags(body.GetProperty("entries"));

    Assert.Equal(("true", "true", "false", "null"), (flags["DE - The Matrix (1999) 4K"], flags["EN| The Matrix (1999) [FHD]"],
      flags["The Matrix Reloaded (2003)"], flags["[Matrix] 4K"]));
  }

  private sealed record AnalysisDto(string CacheKey);
  private sealed record EntriesDto(List<string> Names, int Total);
  private sealed record ErrorDto(string Error);

  [Fact]
  public void BrowseView_HasNoSwitchBulkActionOrSave()
  {
    var browse = Browse();
    Assert.DoesNotMatch(@"\bswitch\b", browse);
    foreach (var forbidden in new[] { "checkbox", "class: 'toggle'", "library-toggle", "bulk", "Bulk", "Save", "Switch", "Turn off", "Turn on", "disabled.add", "disabled.delete", "POST" })
      Assert.DoesNotContain(forbidden, browse);
    // The only buttons open a group, page, retry or go back.
    var labels = Regex.Matches(browse, @"\}, '([^']+)'\)").Select(m => m.Groups[1].Value).ToHashSet();
    Assert.Equal(new HashSet<string> { "Try again" }, labels);
  }
}
