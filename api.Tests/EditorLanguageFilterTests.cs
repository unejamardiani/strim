using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the editor's language filter (#266): one Language chip in the Everything results that opens a short list
/// of the languages and prefix tags the search response reports, sends the choice as lang= and clears in one tap.
/// The API side (lang, languages) is covered by the search language tests of S3. Layout is checked by hand.
/// </summary>
public class EditorLanguageFilterTests
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
  public void Search_SendsTheChoiceAsLang_AndKeepsTheReportedLanguages()
  {
    var js = Read("main.js");
    var run = Between(js, "async function runSearch(ed)", "function renderSearchResults(ed)");
    Assert.Contains("const lang = ed.searchLang.join(',');", run);
    Assert.Contains("hideDisabled, lang: lang || null }", run);
    // A cached result only counts for the same choice, so a change or a clear always searches again.
    Assert.Contains("ed.search.hideDisabled === hideDisabled && ed.search.lang === lang", run);
    Assert.Contains("ed.searchLanguages = result.languages.filter(", run);

    // Every change goes through one place that searches again; the search has no pages, so it starts at the top.
    var choice = Between(js, "function setLangChoice(ed, values)", "\n}\n");
    Assert.Contains("ed.searchLang = values;", choice);
    Assert.Contains("runSearch(ed);", choice);

    // A new analysis forgets the old offer; the choice is not stored anywhere.
    Assert.Contains("ed.search = null;\n  ed.searchLanguages = [];", js);
    Assert.DoesNotMatch(new Regex(@"localStorage[^\n]*searchLang"), js);
  }

  [Fact]
  public void Chip_ShowsTheChoice_ClearsInOneTap_AndIsUsableByKeyboard()
  {
    var js = Read("main.js");
    var nodes = Between(js, "function langFilterNodes(ed)", "function setLangChoice(");
    Assert.Contains("class: 'lang-filter-toggle', type: 'button', 'aria-expanded': 'false', 'aria-controls': 'search-lang-panel',", nodes);
    Assert.Contains("title: 'Filters on tags from the title',", nodes);
    Assert.Contains("'aria-label': 'Filter on tags from the title'", nodes);
    Assert.Contains("'aria-label': 'Clear the language filter'", nodes);
    Assert.Contains("setLangChoice(ed, []);", nodes);
    Assert.Contains("if (e.key !== 'Escape') return;", nodes);

    var render = Between(js, "function renderLangFilter(ed)", "const codes = ");
    Assert.Contains("f.label.textContent = active.length ? `: ${active.join(', ')}` : '';", render);
    Assert.Contains("f.clear.hidden = !active.length;", render);
    // Native checkboxes inside labels, so Tab and Space work; languages and prefix tags under their own headings.
    Assert.Contains("h('input', { type: 'checkbox', value: l.value })", render);
    Assert.Contains("section('Languages from titles', known.filter((l) => l.kind !== 'tag'))", render);
    Assert.Contains("section('Tags from titles', known.filter((l) => l.kind === 'tag'))", render);
    Assert.Contains("'Filters on tags read from the title, not on provider details.'", render);
    // Values and tags are inserted as text, never as markup.
    Assert.DoesNotContain("innerHTML", render);
  }

  [Fact]
  public void Styles_UseTokens_AndKeepTheOpenListShort()
  {
    var css = Read("app.css");
    var block = Between(css, ".lang-panel {", "}");
    Assert.Contains("max-height: 132px;", block);
    Assert.Contains("overflow-y: auto;", block);
    var active = Between(css, ".lang-filter-toggle.is-active {", "}");
    Assert.Contains("var(--accent)", active);
    Assert.DoesNotContain("style=\"", Read("app.html"));
  }
}
