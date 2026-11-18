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

    // The groups are the ones the Library loaded once; the browse view sits next to the search.
    var load = Between(library, "async function load(", "const TYPES = [");
    Assert.Contains("const browse = browseSection(ctx, result);", load);
    Assert.Contains("searchSection(ctx, result, browse.refresh),\n    browse.el);", load);
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
    Assert.Contains("includeOff = !includeOff;", section);
    Assert.Contains("run();\n        onToggle();", section);

    Assert.Contains("const shown = list.groups.filter((g) => withOff || !list.disabled.has(g.key));", browse);
    Assert.Contains("return { sorted, hidden: list.groups.length - shown.length };", browse);
    Assert.Contains("const { sorted, hidden } = browseGroups(list, includeOff);", browse);
    Assert.Contains("if (hidden > 0) nodes.push(status(`${plural(hidden, 'switched-off group')} hidden`));", browse);
    Assert.Contains("const offBadge = (g) => (list.disabled.has(g.key) ? h('span', { class: 'search-badge off', text: 'Off' }) : null);", browse);

    var refresh = Between(browse, "const refresh = () => {", "};");
    Assert.Contains("renderGroups();", refresh);
    Assert.Contains("if (state.open && !includeOff && list.disabled.has(state.open.group.key)) close();", refresh);
  }

  [Fact]
  public void OpeningAGroup_PagesWithOffsetAndLimit_UntilTotal_AndAFailedPageKeepsTheEntries()
  {
    var browse = Browse();
    var load = Between(browse, "const loadPage = async (view) => {", "const refresh = () => {");
    Assert.Contains("page = await ctx.groupEntries({ cacheKey: list.cacheKey, group: view.group.key, offset: view.offset, limit: PAGE_SIZE }, ctx.controller.signal);", load);
    Assert.Contains("view.offset += names.length;", load);
    Assert.Contains("const done = names.length === 0 || view.offset >= page.total;", load);
    Assert.Contains("if (!done) view.foot.replaceChildren(h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadPage(view) }, 'More'));", load);
    // Names are text; new names are appended, so earlier pages stay.
    Assert.Contains("view.list.append(...names.map((name) => h('li', { class: 'library-hit' }, h('span', { class: 'search-hit-name', text: name }))));", load);
    Assert.DoesNotContain("innerHTML", Read("library.js"));

    // A failure replaces only the footer with the error and a retry; the list is not touched.
    var failure = Between(load, "} catch (err) {", "return;\n      }");
    Assert.Contains("view.foot.replaceChildren(h('div', { class: 'notice error', role: 'alert' },", failure);
    Assert.Contains("'Try again'", failure);
    Assert.DoesNotContain("view.list", failure);
    Assert.Contains("if (view !== state.open || ctx.controller.signal.aborted) return;", failure);

    // A page of a group no longer open is dropped; the page size is the server's limit.
    Assert.Contains("if (view !== state.open || !ctx.isCurrent()) return;", load);
    Assert.Contains("const PAGE_SIZE = 50;", browse);
    Assert.Contains("public const int MaxSearchResults = 50;", Read("api/Endpoints/EndpointSupport.cs"));

    // main.js hands in the read-only group-entries request with the parameters as the query string.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("groupEntries: (params, signal) => api(`/playlist/group-entries?${new URLSearchParams(params)}`, { signal }),", show);
    Assert.DoesNotContain("/playlist/group-entries", Read("library.js"));

    // 'All groups' goes back to the list.
    var open = Between(browse, "const open = (g) => {", "const loadPage =");
    Assert.Contains("h('button', { class: 'btn btn-sm', type: 'button', onclick: close }, icon('back'), 'All groups'),", open);
    var close = Between(browse, "const close = () => {", "const open = (g) => {");
    Assert.Contains("groupsEl.hidden = false;", close);
  }

  // Runs library.js in Node against a minimal DOM: opens a group of 120 entries and pages through it, with the
  // third page failing once. Prints one JSON object with what the view showed after each step.
  private const string PagingHarness = """
    const fs = require('fs');
    class El {
      constructor(tag) { this.tag = tag; this.children = []; this.attrs = {}; this.dataset = {}; this.hidden = false; this.text = ''; this.classes = ''; }
      set textContent(v) { this.text = String(v); this.children = []; }
      get textContent() { return this.text + this.children.map((c) => c.textContent).join(''); }
      append(...nodes) { for (const n of nodes) if (n != null) this.children.push(typeof n === 'string' ? Object.assign(new El('#text'), { text: n }) : n); }
      replaceChildren(...nodes) { this.children = []; this.append(...nodes); }
      setAttribute(k, v) { this.attrs[k] = String(v); }
      addEventListener() {}
      focus() {}
      scrollIntoView() {}
      all(pred, out = []) { for (const c of this.children) { if (pred(c)) out.push(c); c.all(pred, out); } return out; }
      querySelectorAll(sel) { const cls = sel.slice(1); return this.all((e) => e.classes.split(' ').includes(cls)); }
    }
    const h = (tag, attrs, ...kids) => {
      const el = new El(tag);
      for (const [k, v] of Object.entries(attrs || {})) {
        if (k === 'class') el.classes = v; else if (k === 'text') el.text = String(v);
        else if (k === 'dataset') Object.assign(el.dataset, v); else if (k === 'hidden') el.hidden = v;
        else if (k.startsWith('on')) el[k] = v; else el.setAttribute(k, v);
      }
      el.append(...kids.flat());
      return el;
    };
    const visible = (el, root) => { const path = []; const walk = (n) => { if (n === el) return true; for (const c of n.children) if (walk(c)) { path.push(n); return true; } return false; }; walk(root); return !el.hidden && path.every((p) => !p.hidden); };
    const requests = [];
    let failNext = false;
    const total = 120;
    const groupEntries = async (params) => {
      requests.push({ ...params });
      if (failNext) { failNext = false; throw new Error('The provider did not answer.'); }
      const names = [];
      for (let i = params.offset; i < Math.min(total, params.offset + params.limit); i++) names.push(`Film ${i + 1}`);
      return { names, total };
    };
    const root = h('div');
    const ctx = {
      root, h, icon: () => h('svg'), controller: new AbortController(), fmt: String,
      plural: (n, one, many) => `${n} ${n === 1 ? one : (many || one + 's')}`,
      playlist: { id: 'p1', name: 'Test', sourceUrl: 'http://example.invalid/list.m3u' },
      loadGroups: async () => ({ cacheKey: 'ck', totalChannels: 121, disabled: new Set(),
        groups: [{ key: 'news', title: 'News', type: 'Live', count: 1, index: 0 }, { key: 'films', title: 'Films A', type: 'Movies', count: 120, index: 1 }] }),
      isCurrent: () => true, errorMessage: (e) => e.message, searchHint: () => 'Type to search.', search: async () => ({ matches: [] }), groupEntries,
    };
    global.window = {};
    eval(fs.readFileSync(process.argv[2], 'utf8'));
    const flush = () => new Promise((r) => setImmediate(r));
    const button = (label) => root.all((e) => e.tag === 'button' && e.textContent.includes(label) && visible(e, root))[0];
    const steps = [];
    const snap = (name, extra = '') => {
      const e = root.all((n) => n.tag === 'ol')[0]?.children.map((c) => c.textContent) ?? [];
      const error = root.all((n) => n.attrs.role === 'alert' && visible(n, root)).map((n) => n.textContent).join('');
      const groups = !root.all((n) => n.classes === 'library-groups')[0].hidden;
      steps.push(`${name} ${e.length} ${e[0] ?? ''}..${e.at(-1) ?? ''} more=${!!button('More')} retry=${!!button('Try again')} groups=${groups} error=${error}${extra}`);
    };
    (async () => {
      window.StrimLibrary.mount(ctx);
      await flush(); await flush();
      steps.push(`groups ${root.all((e) => e.classes.split(' ').includes('library-group')).map((e) => e.dataset.key)}`);
      button('Films A').onclick(); await flush(); snap('first');
      button('More').onclick(); await flush(); snap('second');
      failNext = true;
      button('More').onclick(); await flush(); snap('failed');
      button('Try again').onclick(); await flush();
      snap('last', ` foot=${root.all((e) => e.classes === 'library-foot')[0].textContent}`);
      button('All groups').onclick(); snap('back');
      console.log(JSON.stringify({ requests: requests.map((r) => `${r.group} ${r.offset}/${r.limit}`), steps }));
    })().catch((e) => { console.error(e); process.exit(1); });
    """;

  [Fact]
  public async Task OpeningAGroup_InTheBrowser_RequestsPagesUntilTotal_AndAFailedPageKeepsTheEntries()
  {
    var script = Path.Combine(Path.GetTempPath(), $"strim-library-{Guid.NewGuid():N}.js");
    File.WriteAllText(script, PagingHarness);
    var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(script);
    start.ArgumentList.Add(Path.Combine(StrimAppFactory.RepositoryRoot(), "library.js"));
    using var node = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
    var error = node.StandardError.ReadToEndAsync();
    var output = await node.StandardOutput.ReadToEndAsync();
    await node.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
    File.Delete(script);
    Assert.True(node.ExitCode == 0, $"The harness failed: {await error}");

    using var json = System.Text.Json.JsonDocument.Parse(output);
    // Opening requests the first page; More asks for the next offset; the retry repeats the failed page.
    Assert.Equal(new[] { "films 0/50", "films 50/50", "films 100/50", "films 100/50" },
      json.RootElement.GetProperty("requests").EnumerateArray().Select(r => r.GetString()));
    Assert.Equal(string.Join('\n', new[]
    {
      "groups films,news",
      "first 50 Film 1..Film 50 more=true retry=false groups=false error=",
      "second 100 Film 1..Film 100 more=true retry=false groups=false error=",
      // The failed page shows its error and a retry; the 100 entries already shown stay.
      "failed 100 Film 1..Film 100 more=false retry=true groups=false error=The provider did not answer.Try again",
      // At total, More is gone.
      "last 120 Film 1..Film 120 more=false retry=false groups=false error= foot=All 120 shown",
      // 'All groups' returns to the group list.
      "back 0 .. more=false retry=false groups=true error=",
    }), string.Join('\n', json.RootElement.GetProperty("steps").EnumerateArray().Select(s => s.GetString())));
  }

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
