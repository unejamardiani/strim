using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Library search (#286): the editor's Everything search, narrowed by type, by default on the
/// groups that are on, with one toggle for the switched-off groups. Results are text only.
/// The API behaviour (contentTypes, hideDisabled, hiddenCount, enabled) is covered by the search tests of #284.
/// </summary>
public class LibrarySearchTests
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

  [Fact]
  public void TypeControl_SendsContentTypes_AndAllSendsNone()
  {
    var library = Read("library.js");
    var types = Between(library, "const TYPES = [", "];");
    Assert.Contains("{ value: 'all', label: 'All', types: [] },", types);
    Assert.Contains("{ value: 'films', label: 'Films', types: ['Movies'] },", types);
    Assert.Contains("{ value: 'series', label: 'Series', types: ['Series'] },", types);
    Assert.Contains("{ value: 'channels', label: 'Channels', types: ['Live'] },", types);

    var run = Between(library, "async function runSearch(", "function hit(");
    Assert.Contains("const contentTypes = TYPES.find((t) => t.value === state.type).types;", run);
    Assert.Contains("if (contentTypes.length) body.contentTypes = contentTypes;", run);
    Assert.Contains("found = await ctx.search(body, ctx.controller.signal);", run);

    // Picking a type searches again; the pills are the editor's segmented control.
    var section = Between(library, "function searchSection(", "async function runSearch(");
    Assert.Contains("h('div', { class: 'pills', role: 'group', 'aria-label': 'Type' });", section);
    Assert.Contains("state.type = option.value;", section);
  }

  [Fact]
  public void ByDefault_HidesTheStoredDisabledGroups_AndShowsTheHiddenCount()
  {
    var library = Read("library.js");
    Assert.Contains("let includeOff = false;", library);
    Assert.DoesNotContain("localStorage", library);
    Assert.DoesNotContain("sessionStorage", library);

    var run = Between(library, "async function runSearch(", "function hit(");
    Assert.Contains("const hideDisabled = !includeOff;", run);
    Assert.Contains("const body = { cacheKey: list.cacheKey, query: state.query, disabledGroups: [...list.disabled], hideDisabled };", run);
    Assert.Contains("const hidden = hideDisabled && Number.isInteger(found.hiddenCount) ? found.hiddenCount : 0;", run);
    Assert.Contains("`${plural(hidden, 'match', 'matches')} in switched-off groups`", run);
    Assert.Contains("else if (left) text = `Nothing in your list. ${left}.`;", run);

    // The disabled groups and the cache key are the ones the Library loaded: the stored selection.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("return { groups: ed.groups, disabled: ed.disabled, totalChannels: ed.totalChannels, cacheKey: ed.cacheKey };", show);
    Assert.Contains("searchHint,", show);
    Assert.Contains("search: (body, signal) => api('/playlist/search', { method: 'POST', body, signal }),", show);
  }

  [Fact]
  public void OneToggle_IncludesSwitchedOffGroups_AndMarksTheirResultsOff()
  {
    var library = Read("library.js");
    var section = Between(library, "function searchSection(", "async function runSearch(");
    Assert.Single(Regex.Matches(library, Regex.Escape("'Include switched-off groups'")));
    Assert.Contains("class: 'btn btn-sm library-toggle', type: 'button', 'aria-pressed': String(includeOff),", section);
    Assert.Contains("includeOff = !includeOff;", section);

    var hit = library[library.IndexOf("function hit(", StringComparison.Ordinal)..];
    Assert.Contains("m.enabled === false ? h('span', { class: 'search-badge off', text: 'Off' }) : null", hit);
  }

  [Fact]
  public void Results_ShowNameGroupAndType_WithoutAnyControl()
  {
    var library = Read("library.js");
    var hit = Between(library, "function hit(", "window.StrimLibrary");
    Assert.Contains("h('li', { class: 'library-hit', dataset: { type } },", hit);
    Assert.Contains("h('span', { class: 'search-hit-name', text: m.name }),", hit);
    Assert.Contains("h('span', { class: 'search-hit-meta', text: `${m.group} · ${TYPE_LABELS[type]}` })", hit);
    Assert.Contains("const TYPE_LABELS = { Live: 'Channel', Movies: 'Film', Series: 'Series' };", library);
    foreach (var control in new[] { "'button'", "'a'", "'input'", "onclick", "href", "role: 'switch'" })
      Assert.DoesNotContain(control, hit);
  }

  [Fact]
  public void EditorEverythingSearch_IsUnchanged()
  {
    var js = Read("main.js");
    var run = Between(js, "async function runSearch(ed)", "function renderSearchResults(ed)");
    Assert.Contains("const hideDisabled = ed.filter.show === 'kept';", run);
    Assert.Contains("body: { cacheKey: ed.cacheKey, query, disabledGroups: [...ed.disabled], hideDisabled, lang: lang || null },", run);
    Assert.DoesNotContain("contentTypes", run);
    // main.js calls the search endpoint from the editor and hands the Library one read-only function.
    Assert.Equal(2, Regex.Matches(js, Regex.Escape("api('/playlist/search'")).Count);
    Assert.DoesNotContain("/playlist/search", Read("library.js"));
  }
}
