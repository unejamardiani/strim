using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins an open group's own address in the Groups tab (#442): the route a row navigates to, the query of the
/// mounted address, Back with the focus on the group's row after the list is drawn again, and the fall-back for a
/// group that is gone. Source pins like the other Library UI tests (no JS engine).</summary>
public class LibraryGroupViewUiTests
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

  private static string Library() => Read("library.js");
  private static string Show() => Between(Library(), "function show(sub, query) {", "// Search and Groups both wait on the list");
  private static string Browse() => Between(Library(), "function browseSection(ctx, list) {", "function hit(");
  private static string Handler() => Between(Browse(), "view.groupAddress = (key, query) => {", "if (view.groupQuery) {");

  private static void InOrder(string source, params string[] parts)
  {
    var at = -1;
    foreach (var part in parts)
    {
      var next = source.IndexOf(part, at + 1, StringComparison.Ordinal);
      Assert.True(next > at, $"'{part}' is missing or out of order.");
      at = next;
    }
  }

  [Fact]
  public void Group_HasItsOwnRoute()
  {
    var browse = Browse();
    // The row navigates to the group's address, its key encoded, as a history entry.
    Assert.Contains("const groupHash = (key) => `${view.base}/groups/${encodeURIComponent(key)}`;", browse);
    Assert.Contains("onclick: () => go(g) },", Between(browse, "const groupRow = (g, t) => {", "h('span', { class: 'lib-dot'"));
    var go = Between(browse, "const go = (g) => {", "};");
    Assert.Contains("location.hash = groupHash(g.key);", go);
    Assert.DoesNotContain("replaceState", go);
    Assert.DoesNotContain("onclick: () => open(g)", browse);

    // show() decodes everything after "groups/", a malformed escape as written, and hands it to the Groups tab.
    var show = Show();
    Assert.Contains("view.groupKey = groupKeyOf(String(sub));", show);
    Assert.Contains("if (tab === 'groups') view.groupAddress?.(view.groupKey, view.groupQuery);", show);
    var decode = Between(show, "function groupKeyOf(sub) {", "\n  }\n");
    Assert.Contains("if (!sub.startsWith('groups/')) return null;", decode);
    Assert.Contains("const raw = sub.slice('groups/'.length);", decode);
    Assert.Contains("return decodeURIComponent(raw);", decode);
    Assert.Contains("} catch {\n      return raw;", decode);
    // main.js already passes "groups/<encoded key>" and the query; it is not changed.
    Assert.Contains("(?:\\/([^?#]*))?(?:\\?(.*))?$/i", Read("main.js"));

    // The address opens the group with the group's type chosen; an open group's address is kept, never the list's.
    var handler = Handler();
    Assert.Contains("if (state.open?.group.key === key) return;", handler);
    InOrder(handler, "groupType = t.value;", "open(g);", "renderGroups();");
    Assert.Contains("if (state.open) return;", Between(browse, "const keepGroupsAddress = () => {", "};"));
    // A list still loading applies the address once it is loaded.
    Assert.Contains("if (view.tab === 'groups' && view.groupKey !== null) view.groupAddress(view.groupKey, view.groupQuery);", browse);
  }

  [Fact]
  public void MountedAddress_KeepsItsQuery()
  {
    var library = Library();
    var mount = Between(library, "function mount(ctx) {", "// Opens a tab of the Library");
    Assert.Contains("show(ctx.sub, ctx.query);", mount);
    Assert.DoesNotContain("show(ctx.sub);", mount);
    // show() takes the query as given for the Groups tab and never blanks one it was given.
    var show = Show();
    Assert.Contains("view.groupQuery = query || '';", show);
    Assert.DoesNotContain("view.groupQuery = '';", show);
    // The Groups address handler gets that query: the type of "…/groups?type=series" after a reload, and an open
    // group keeps the query it came with on the tab's link.
    Assert.Contains("view.groupAddress?.(view.groupKey, view.groupQuery);", show);
    var handler = Handler();
    Assert.Contains("const value = new URLSearchParams(query).get('type');", handler);
    Assert.Contains("view.nav.querySelector('[data-tab=\"groups\"]').href = `${groupHash(key)}${query ? `?${query}` : ''}`;", handler);
    var browse = Browse();
    Assert.Contains("const value = new URLSearchParams(view.groupQuery).get('type');", browse);
  }

  [Fact]
  public void Back_FocusesTheGroupsRowAfterTheListIsRedrawn()
  {
    var browse = Browse();
    // close() itself focuses nothing: renderGroups() replaces the rows afterwards.
    var close = Between(browse, "const close = () => {", "};");
    Assert.DoesNotContain("focus", close);

    // Back (the list's address) in the handler: close, renderGroups(), then the row's focus.
    var list = Between(Handler(), "if (key === null) {", "return;\n      }");
    InOrder(list, "const was = state.open?.group.key;", "close();", "renderGroups();", "if (was !== undefined) focusRow(was);");

    // The shared toggle closing a switched-off group: the same order.
    var refresh = Between(browse, "const refresh = () => {", "};");
    InOrder(refresh, "if (gone) close();", "renderGroups();", "if (gone) focusRow(gone.key);");

    // "All groups" keeps its look and goes to the list's address, a history entry.
    Assert.Contains("h('button', { class: 'btn btn-sm', type: 'button', onclick: back }, icon('back'), 'All groups'),", browse);
    Assert.Contains("const back = () => { location.hash = listHash(); };", browse);
    Assert.Contains("const focusRow = (key) => {\n      for (const b of groupsEl.querySelectorAll('.is-group')) if (b.dataset.key === key) b.focus();", browse);
  }

  [Fact]
  public void UnknownGroup_FallsBackToTheListWithAToast()
  {
    var handler = Handler();
    // Only a group the list shows opens: not in the list, or switched off while those are left out, falls back.
    Assert.Contains("const g = browseGroups(list, includeOff).sorted.find((x) => x.key === key);", handler);
    var unknown = Between(handler, "if (!g || !t) {", "return;");
    InOrder(unknown, "close();", "renderGroups();", "ctx.toast('That group is not in this list any more.', { error: true });");
    // renderGroups() with no group open replaces the address by the list's, so a reload does not repeat the toast.
    var keep = Between(Browse(), "const keepGroupsAddress = () => {", "};");
    InOrder(keep, "if (state.open) return;", "const hash = listHash();",
      "if (view.tab === 'groups' && location.hash !== hash) history.replaceState(history.state, '', hash);");
    // show() sets the tab before the handler runs, so the replacement applies on the first visit too.
    InOrder(Show(), "view.tab = tab;", "if (tab === 'groups') view.groupAddress?.(view.groupKey, view.groupQuery);");
  }
}
