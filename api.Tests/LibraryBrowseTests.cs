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
    Assert.Contains("const BROWSE_HEADINGS = { Movies: 'Films', Series: 'Series', Live: 'Channels' };", browse);
    Assert.Contains("const sorted = [...shown].sort((a, b) => BROWSE_ORDER.indexOf(a.type) - BROWSE_ORDER.indexOf(b.type) || a.index - b.index);", browse);
    Assert.Contains("h('span', { class: 'search-hit-meta', text: plural(g.count, 'entry', 'entries') })", browse);
    Assert.Contains("h('h3', { class: 'field-label', text: BROWSE_HEADINGS[g.type] })", browse);

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
    Assert.Contains("const offBadge = (g) => (list.disabled.has(g.key) ? h('span', { class: 'search-badge off', text: 'Off' }) : null);", browse);

    var refresh = Between(browse, "const refresh = () => {", "};");
    Assert.Contains("renderGroups();", refresh);
    Assert.Contains("if (state.open && !includeOff && list.disabled.has(state.open.group.key)) close();", refresh);
  }

  private const string PinnedOpenAndLoadPage = """
        const open = (g, scroll = true) => {
          const view = {
            group: g, offset: 0,
            count: h('p', { class: 'search-hit-meta', text: plural(g.count, 'entry', 'entries') }),
            list: h('ol', { class: 'search-list library-entries' }),
            foot: h('div', { class: 'library-foot' }),
          };
          state.open = view;
          entriesEl.replaceChildren(
            h('div', { class: 'library-entries-head' },
              h('button', { class: 'btn btn-sm', type: 'button', onclick: close }, icon('back'), 'All groups'),
              h('div', { class: 'grow' }, h('h3', { class: 'library-group-title', text: g.title || 'Untitled group' }), view.count),
              offBadge(g)),
            h('div', { class: 'card' }, view.list, view.foot));
          groupsEl.hidden = true;
          entriesEl.hidden = false;
          if (scroll) el.scrollIntoView({ block: 'start' });
          loadPage(view);
        };

        // A failed page shows its error below the entries already shown, which stay.
        const loadPage = async (view) => {
          view.foot.replaceChildren(status(view.offset ? 'Loading more…' : 'Loading entries…'));
          let page;
          try {
            const params = { cacheKey: list.cacheKey, group: view.group.key, offset: view.offset, limit: PAGE_SIZE };
            if (hideSeen) params.hideSeen = true;
            if (likedOnly) params.liked = true;
            page = await ctx.groupEntries(params, ctx.controller.signal);
          } catch (err) {
            if (view !== state.open || ctx.controller.signal.aborted || accessGone(ctx, err)) return;
            view.foot.replaceChildren(h('div', { class: 'notice error', role: 'alert' },
              h('p', { text: err.message || 'The entries could not be loaded.' }),
              h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadPage(view) }, 'Try again')));
            return;
          }
          if (view !== state.open || !ctx.isCurrent()) return;
          const names = page.entries || [];
          view.list.append(...names.map((e) => row(ctx, e, view.group.title, list.cacheKey, null, null)));
          view.offset += names.length;
          view.count.textContent = plural(page.total, 'entry', 'entries');
          const done = names.length === 0 || view.offset >= page.total;
          if (!done) view.foot.replaceChildren(h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadPage(view) }, 'More'));
          else {
            const seen = hideSeen && page.seenHiddenCount > 0 ? ` · ${plural(page.seenHiddenCount, 'seen entry', 'seen entries')} hidden` : '';
            view.foot.replaceChildren(status((page.total === 0 ? 'No entries in this group.' : `All ${fmt(page.total)} shown`) + seen));
          }
        };
    """;

  [Fact]
  public void OpeningAGroup_PagesWithOffsetAndLimit_UntilTotal_AndAFailedPageKeepsTheEntries()
  {
    var browse = Browse();
    // Opening (first page at once), each request with offset/limit, 'More' until total, and a failure that
    // only replaces the footer are pinned to the exact source: clearing the list, skipping the first page or
    // another offset step fails here. api.Tests has no JS engine (no new dependency); the test below walks
    // the real endpoint with the same steps.
    Assert.Equal(PinnedOpenAndLoadPage.ReplaceLineEndings("\n"), Between(browse, "    const open = (g, scroll = true) => {", "\n\n    // The shared toggle"));
    // The entries list is only placed in the view and appended to; nothing else clears or replaces it.
    Assert.Equal(2, Regex.Matches(browse, Regex.Escape("view.list")).Count);
    Assert.DoesNotContain("innerHTML", Read("library.js"));

    // The page size is the server's limit.
    Assert.Contains("const PAGE_SIZE = 50;", browse);
    Assert.Contains("public const int MaxSearchResults = 50;", Read("api/Endpoints/EndpointSupport.cs"));

    // main.js hands in the read-only group-entries request with the parameters as the query string.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("groupEntries: (params, signal) => api(`/library/group-entries?${new URLSearchParams(params)}`, { signal }),", show);
    Assert.DoesNotContain("/playlist/group-entries", Read("library.js"));

    // 'All groups' (pinned in open) goes back to the list.
    var close = Between(browse, "const close = () => {", "const open = (g, scroll = true) => {");
    Assert.Contains("groupsEl.hidden = false;", close);
  }

  /// <summary>
  /// Walks a 120-entry group through the real endpoint with the steps of the loadPage pinned above: the first page on opening, then
  /// 'More' with offset += names until total. A failed page leaves the shown names and 'Try again' resumes.
  /// </summary>
  [Fact]
  public async Task PagingAGroup_ThroughTheRealEndpoint_RequestsSuccessivePagesUntilTotal()
  {
    var browse = Browse();
    var pageSize = int.Parse(Regex.Match(browse, @"const PAGE_SIZE = (\d+);").Groups[1].Value);

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
    Assert.Equal(new HashSet<string> { "More", "Try again" }, labels);
  }
}
