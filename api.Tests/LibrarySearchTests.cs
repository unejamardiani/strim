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
    Assert.Contains("const hidden = hideDisabled && !isViewer(ctx) && Number.isInteger(found.hiddenCount) ? found.hiddenCount : 0;", run);
    Assert.Contains("`${plural(hidden, 'match', 'matches')} in switched-off groups`", run);
    Assert.Contains("else if (left) text = `Nothing in ${listWord(ctx)}. ${left}.`;", run);

    // The disabled groups and the cache key are the ones the Library loaded: the stored selection.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("return { groups: ed.groups, disabled: ed.disabled, totalChannels: ed.totalChannels, cacheKey: ed.cacheKey };", show);
    Assert.Contains("searchHint,", show);
    Assert.Contains("search: (body, signal) => api('/library/search', { method: 'POST', body, signal }),", show);
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
    Assert.Contains("m.enabled === false && !isViewer(ctx) ? h('span', { class: 'search-badge off', text: 'Off' }) : null", hit);
  }

  [Fact]
  public void Results_ShowNameGroupAndType_WithoutAnyControl()
  {
    var library = Read("library.js");
    var hit = Between(library, "function hit(", "window.StrimLibrary");
    Assert.Contains("h('li', { class: 'library-hit', dataset: { type } },", hit);
    // A film's name opens the film card (#367) outside hit(); every other name stays text.
    Assert.Contains("filmName(ctx, m, m.group, cacheKey),", hit);
    Assert.Contains("h('span', { class: 'search-hit-meta', text: `${m.group} · ${TYPE_LABELS[type]}` })", hit);
    Assert.Contains("const TYPE_LABELS = { Live: 'Channel', Movies: 'Film', Series: 'Series' };", library);
    foreach (var control in new[] { "'button'", "'a'", "'input'", "onclick", "href", "role: 'switch'" })
      Assert.DoesNotContain(control, hit);
  }

  [Fact]
  public void Results_ShowTheEditorsLanguageLabels_PassedInNotCopied()
  {
    var library = Read("library.js");
    var hit = Between(library, "function hit(", "window.StrimLibrary");
    Assert.Contains("h('span', { class: 'search-hit-line' },", hit);
    Assert.Contains("ctx.searchLanguages(m)),", hit);
    // The labels and their phone fitting are the editor's functions, handed over by main.js (no import, #285).
    foreach (var copied in new[] { "lang-chip", "languageSource === 'title') return", "Audio ", "import " })
      Assert.DoesNotContain(copied, library);
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("    searchLanguages,\n    fitSearchLanguages,\n", show);
    var run = Between(library, "async function runSearch(", "function hit(");
    Assert.Contains("ctx.fitSearchLanguages(results.body);", run);
    Assert.Contains("window.addEventListener('resize', () => ctx.fitSearchLanguages(results.body), { signal: ctx.controller.signal });", library);
  }

  [Fact]
  public void LanguageFilter_IsTheEditors_AndSendsLangWithTypesAndToggle()
  {
    var library = Read("library.js");
    var section = Between(library, "function searchSection(", "async function runSearch(");
    Assert.Contains("const host = { searchLang: [], searchLanguages: [], onLangChoice: () => run() };", section);
    Assert.Contains("const filter = ctx.languageFilter(host);", section);
    Assert.Contains("h('div', { class: 'search-head' }, results.head, filter.bar),", section);
    Assert.Contains("filter.panel,", section);
    foreach (var copied in new[] { "lang-filter", "lang-panel", "lang-option", "type: 'checkbox'" })
      Assert.DoesNotContain(copied, library);

    // One request carries the type, the toggle and the language choice together; the offer comes from the response.
    var run = Between(library, "async function runSearch(", "function hit(");
    Assert.Contains("const lang = state.host.searchLang.join(',');", run);
    Assert.Contains("if (contentTypes.length) body.contentTypes = contentTypes;\n    if (lang) body.lang = lang;", run);
    Assert.Contains("state.host.searchLanguages = found.languages.filter(", run);
    Assert.Contains("state.filter.render();", run);
    Assert.Contains("else if (lang) text = 'Nothing matches your search in the chosen languages.';", run);

    // main.js builds the editor's chip and list on the Library's host; a change there searches the Library again.
    var js = Read("main.js");
    var show = Between(js, "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("host.langFilter = langFilterNodes(host);", show);
    Assert.Contains("render: () => renderLangFilter(host)", show);
    var choice = Between(js, "function setLangChoice(ed, values)", "\n}\n");
    Assert.Contains("if (ed.onLangChoice) ed.onLangChoice();\n  else runSearch(ed);", choice);
    Assert.Contains("const f = ed.langFilter || picker.langFilter;", Between(js, "function renderLangFilter(ed)", "const codes = "));
    // The editor's state never has a host's fields, so its filter keeps working on the picker and runSearch.
    var editorState = Between(js, "function newEditor(playlist)", "\n}\n");
    Assert.DoesNotContain("onLangChoice", editorState);
    Assert.DoesNotContain("langFilter", editorState);
  }

  [Fact]
  public void EditorEverythingSearch_IsUnchanged()
  {
    var js = Read("main.js");
    var run = Between(js, "async function runSearch(ed)", "function renderSearchResults(ed)");
    Assert.Contains("const hideDisabled = ed.filter.show === 'kept';", run);
    Assert.Contains("body: { cacheKey: ed.cacheKey, query, disabledGroups: [...ed.disabled], hideDisabled, lang: lang || null },", run);
    Assert.DoesNotContain("contentTypes", run);
    // main.js calls the editor's search endpoint from the editor only; the Library gets its marks search (#297).
    Assert.Single(Regex.Matches(js, Regex.Escape("api('/playlist/search'")));
    Assert.Single(Regex.Matches(js, Regex.Escape("api('/library/search'")));
    Assert.DoesNotContain("/playlist/search", Read("library.js"));
  }
}
