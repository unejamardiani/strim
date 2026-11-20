using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Groups tab's type chips, group rows and per-device "+N new" badge (#422) at their source, as the
/// other UI tests do (no JS engine).</summary>
public class LibraryGroupsUiTests
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

  private static string Browse() => Between(Read("library.js"), "// Browse (#287)", "function hit(");

  [Fact]
  public void TypeChips_ReplaceTheThreeCards()
  {
    var library = Read("library.js");
    var browse = Browse();
    Assert.DoesNotContain("library-type", library);
    Assert.DoesNotContain("BROWSE_HEADINGS", library);
    // One chip per type that has groups, with its number of groups; the first one when nothing is chosen.
    Assert.Contains("const types = GROUP_TYPES.map((t) => ({ ...t, groups: sorted.filter((g) => g.type === t.type) })).filter((t) => t.groups.length);", browse);
    Assert.Contains("const chosen = types.find((t) => t.value === groupType) || types[0];", browse);
    Assert.Contains("class: 'lib-chip', type: 'button', 'aria-pressed': String(t === chosen), dataset: { value: t.value },", browse);
    Assert.Contains("}, t.label, h('span', { class: 'n', text: fmt(t.groups.length) })));", browse);
    Assert.Contains("h('div', { class: 'lib-chips', role: 'group', 'aria-label': 'Group type' }, chips)", browse);
    Assert.Contains("h('div', { class: 'lib-list', dataset: { type: chosen.type } }, chosen.groups.map((g) => groupRow(g, chosen)))", browse);
    // The choice lives for the page session and in the address, replaced.
    Assert.Contains("let groupType = null;", library);
    Assert.Contains("const hash = `${view.base}/groups?type=${groupType}`;", browse);
    Assert.Contains("if (view.tab === 'groups' && !state.open && location.hash !== hash) history.replaceState(history.state, '', hash);", browse);
    Assert.Contains("if (tab === 'groups') {", Between(library, "function show(sub, query)", "function inPanels("));
    // Owner copy as before.
    Assert.Contains("if (hidden > 0) nodes.push(status(`${plural(hidden, 'switched-off group')} hidden`));", browse);
    Assert.Contains("'No group is on. Include switched-off groups to browse them.'", browse);

    var css = Read("library.css");
    Assert.DoesNotContain(".library-type", css);
    Assert.Contains(".lib-row.is-group { width: 100%; border-left: 0; border-right: 0; border-bottom: 0; background: none; text-align: left; font: inherit; color: inherit; }", css);
    Assert.Contains(".lib-row.is-group:hover { background: color-mix(in srgb, var(--surface-2) 55%, transparent); }", css);
    Assert.Contains("@media (min-width: 1100px) { .lib-row.is-group.is-open {", css);
    Assert.Contains(".lib-off { flex-shrink: 0; padding: 2px 8px; border-radius: 999px; background: var(--surface-2); color: var(--ink-2); font-size: 11.5px; font-weight: 700; }", css);
  }

  [Fact]
  public void GroupRow_UsesTheTypeWordForItsCount()
  {
    var browse = Browse();
    Assert.Contains("{ value: 'films', type: 'Movies', label: 'Films', one: 'film', many: 'films' },", browse);
    Assert.Contains("{ value: 'series', type: 'Series', label: 'Series', one: 'episode', many: 'episodes' },", browse);
    Assert.Contains("{ value: 'channels', type: 'Live', label: 'Channels', one: 'channel', many: 'channels' },", browse);
    var row = Between(browse, "const groupRow = (g, t) => {", "};");
    Assert.Contains("h('button', { class: 'lib-row is-group', type: 'button', dataset: { key: g.key, type: g.type }, onclick: () => { location.hash = groupHash(g.key, markParams()); } },", row);
    Assert.Contains("h('span', { class: 'lib-dot', 'aria-hidden': 'true', dataset: { type: g.type } }),", row);
    Assert.Contains("h('span', { class: 'lib-title', text: g.title || 'Untitled group' }),", row);
    Assert.Contains("h('span', { class: 'lib-meta', text: plural(g.count, t.one, t.many) })", row);
    Assert.Contains("list.disabled.has(g.key) ? h('span', { class: 'lib-off', text: 'Off' }) : null,", row);
    Assert.Contains("h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, icon('chevron'))", row);
    Assert.DoesNotContain("'entry', 'entries'", row);
  }

  [Fact]
  public void Viewer_GetsTheSameListInsteadOfTiles()
  {
    var library = Read("library.js");
    Assert.DoesNotContain("groupTiles", library);
    Assert.DoesNotContain("group-tile", library);
    Assert.DoesNotContain("type-head", library);
    var render = Between(Browse(), "const renderGroups = () => {", "const keepGroupsAddress");
    Assert.DoesNotContain("if (isViewer(ctx)) return", render);
    var css = Read("library.css");
    foreach (var gone in new[] { ".group-tiles", ".group-tile", ".type-head" })
      Assert.DoesNotContain(gone, css);
  }

  [Fact]
  public void NewBadge_ComparesWithTheCountStoredOnThisDevice()
  {
    var browse = Browse();
    Assert.Contains("const countsKey = (ctx) => `strim.library.counts.${ctx.playlist.id}`;", browse);
    Assert.Contains("const newSince = (seen, g) => (Number.isFinite(seen[g.key]) && g.count > seen[g.key] ? g.count - seen[g.key] : 0);", browse);
    Assert.Contains("fresh > 0 ? h('span', { class: 'lib-new', text: `+${fmt(fresh)} new` }) : null,", browse);
    // Opening a group stores its count; groups that no longer exist are dropped on write.
    var open = Between(browse, "const open = (g, p) => {", "const unseen");
    Assert.Contains("state.seen = { ...state.seen, [g.key]: g.count };", open);
    Assert.Contains("writeCounts(ctx, list, state.seen);", open);
    var write = Between(browse, "function writeCounts(ctx, list, counts) {", "function seenCounts(");
    Assert.Contains("for (const g of list.groups) if (Number.isFinite(counts[g.key])) kept[g.key] = counts[g.key];", write);
    // Nothing goes to the server.
    Assert.DoesNotContain("strim.library.counts", Read("main.js"));

    var css = Read("library.css");
    Assert.Contains(".lib-new { flex-shrink: 0; padding: 2px 8px; border-radius: 999px; background: var(--accent-soft); color: var(--accent-ink); font-size: 11.5px; font-weight: 800; }", css);
  }

  [Fact]
  public void NewBadge_StorageAccessIsGuarded()
  {
    var library = Read("library.js");
    var uses = Regex.Matches(library, @"localStorage\.");
    Assert.NotEmpty(uses);
    foreach (Match use in uses)
    {
      var tryAt = library.LastIndexOf("try {", use.Index, StringComparison.Ordinal);
      Assert.True(tryAt >= 0, $"localStorage at {use.Index} has no try before it.");
      var catchAt = library.IndexOf("} catch", tryAt, StringComparison.Ordinal);
      Assert.True(catchAt > use.Index, $"localStorage at {use.Index} is not inside a try block.");
    }
    // Invalid JSON or no storage: nothing stored, so nothing is new and nothing throws.
    var read = Between(library, "function readCounts(ctx) {", "function writeCounts(");
    Assert.Contains("const stored = JSON.parse(localStorage.getItem(countsKey(ctx)));", read);
    Assert.Contains("return stored && typeof stored === 'object' && !Array.isArray(stored) ? stored : null;", read);
    Assert.Contains("} catch {\n      return null;", read);
  }

  [Fact]
  public void FirstVisit_StoresCountsAndMarksNothing()
  {
    var browse = Browse();
    var first = Between(browse, "function seenCounts(ctx, list) {", "const newSince");
    Assert.Contains("if (stored) return stored;", first);
    Assert.Contains("const counts = Object.fromEntries(list.groups.map((g) => [g.key, g.count]));", first);
    Assert.Contains("writeCounts(ctx, list, counts);", first);
    // The stored counts equal the current ones, so newSince is 0 for every group.
    Assert.Contains("return counts;", first);
    Assert.Contains("const state = { open: null, seen: seenCounts(ctx, list) };", browse);
  }
}
