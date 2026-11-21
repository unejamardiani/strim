using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Watchlist button on the card and on laptop rows (#420) at its source, as the other UI tests do.</summary>
public class LibraryWatchlistUiTests
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

  private static string Rule(string css, string selector)
  {
    var match = Regex.Match(css, $@"(?m)^\s*{Regex.Escape(selector)}\s*\{{([^}}]*)\}}");
    Assert.True(match.Success, $"No rule for '{selector}'.");
    return match.Groups[1].Value;
  }

  private static string Marks() => Between(Read("library.js"), "// Marks (#298)", "// Browse (#287)");

  [Fact]
  public void MarkState_CarriesWatchlist()
  {
    var marks = Marks();
    Assert.Contains("const markOf = (m) => ({ seen: m.seen === true, rating: m.rating || 0, watchlist: m.watchlist === true });", marks);
    Assert.Contains("await ctx.setMark({ name, kind, seen: next.seen, rating: next.rating, watchlist: next.watchlist });", marks);
    // The server's answer has the last word on the watchlist.
    Assert.Contains("if (saved && marks.get(key) === next && saved.watchlist !== next.watchlist) {\n        marks.set(key, { ...next, watchlist: saved.watchlist === true });", marks);

    // The card reads the answer's marks.watchlist.
    var card = Between(Read("library.js"), "function cardMarks(", "const cardToast");
    Assert.Contains("watchlist: answer.marks.watchlist === true }", card);
    Assert.Contains("given.watchlist !== now.watchlist", card);

    // main.js forwards the body unchanged, with the CSRF token of api().
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("setMark: (mark) => api('/marks', { method: 'PUT', body: mark }),", show);
    Assert.Contains("  bookmark: '<path d=\"M7 4h10a1 1 0 0 1 1 1v15l-6-4-6 4V5a1 1 0 0 1 1-1z\"/>',", Read("main.js"));
  }

  [Fact]
  public void Seen_ClearsWatchlistLocallyBeforeTheRequest()
  {
    var marks = Marks();
    Assert.Contains("const toggleSeen = (m) => (m.seen ? { ...m, seen: false } : { ...m, seen: true, watchlist: false });", marks);
    // Both Seen controls, the capsule's and the phone row's, use it.
    Assert.Contains("control('Seen', 'check', toggleSeen),", marks);
    Assert.Contains("key, toggleSeen),", Between(marks, "function rowMarks(", "function watchButton("));
    // The local state and every control of the title change before the request goes out.
    Assert.Contains("marks.set(key, next);\n    repaint();\n    try {", Between(marks, "async function setMark(", "} catch (err) {"));
    // A repaint of the capsule repaints the Watchlist button beside it.
    var paint = Between(marks, "function paintMarks(el)", "function rowMarks(");
    Assert.Contains("const watch = el.parentElement?.querySelector(':scope > .lib-watch');\n    if (watch) paintWatch(watch, m);", paint);
  }

  [Fact]
  public void Card_HasWatchlistButtonWithBothLabels()
  {
    var library = Read("library.js");
    var marks = Marks();
    var button = Between(marks, "function watchButton(", "// Shown at once on every row");
    Assert.Contains("class: 'lib-watch', type: 'button', dataset: { key },", button);
    Assert.Contains("(m) => ({ ...m, watchlist: !m.watchlist })", button);
    Assert.Contains("ctx.icon('bookmark')", button);
    Assert.Contains("const label = m.watchlist === true ? 'On your watchlist' : 'Add to watchlist';", button);
    Assert.Contains("el.setAttribute('aria-pressed', String(m.watchlist === true));", button);
    Assert.Contains("el.setAttribute('aria-label', label);", button);
    Assert.Contains("el.title = label;", button);

    // Capsule, thin line, button; the card's context carries the in-card error note.
    var card = Between(library, "function cardMarks(", "const cardToast");
    Assert.Contains("const cardCtx = { ...ctx, toast: cardToast(ctx) };", card);
    Assert.Contains("return [el, ctx.h('span', { class: 'film-sep', 'aria-hidden': 'true' }), watchButton(cardCtx, markEntry)];", card);

    // Loading, "choose" and the matched card all put the marks in .film-actions.
    var render = Between(library, "function renderFilm(ctx, answer)", "function filmBackdrop(");
    Assert.Contains("const marksEl = cardMarks(ctx, entry, answer);", render);
    Assert.Equal(3, Regex.Matches(render, Regex.Escape("h('div', { class: 'film-actions' }")).Count);
    Assert.Contains("h('div', { class: 'film-actions' }, marksEl),", render);
    Assert.Contains("h('div', { class: 'film-actions' }, marksEl));", render);
    Assert.Contains("h('span', { class: 'film-sep', 'aria-hidden': 'true' })] : null, marksEl));", render);

    var css = Read("library.css");
    var actions = Rule(css, ".film-actions");
    Assert.Contains("flex-wrap: wrap;", actions);
    Assert.Contains("justify-content: center;", actions);
    Assert.Contains("row-gap: 10px;", actions);
    var watch = Rule(css, ".lib-watch");
    Assert.Contains("width: 42px; height: 42px;", watch);
    Assert.Contains("border-radius: 50%;", watch);
    Assert.Contains("background: color-mix(in srgb, var(--surface-2) 80%, transparent);", watch);
    Assert.Contains("background: var(--accent-soft); color: var(--accent-ink);", Rule(css, ".lib-watch[aria-pressed=\"true\"]"));
    Assert.Contains("fill: currentColor;", Rule(css, ".lib-watch[aria-pressed=\"true\"] svg"));

    var design = Read("DESIGN.md");
    Assert.Contains("\"Add to watchlist\" when off, \"On your watchlist\"", design);
    Assert.Contains("Seen wins: marking a title seen takes it\noff the watchlist", design);
  }

  [Fact]
  public void Rows_ShowTheButtonOnlyFrom768()
  {
    var library = Read("library.js");
    Assert.Contains("return [h('span', { class: 'lib-quick', role: 'group' }, like, seen), watchButton(ctx, entry), capsule];",
      Between(library, "function rowMarks(ctx, entry)", "function watchButton("));
    var css = Read("library.css");
    Assert.Contains("display: none;", Rule(css, ".lib-row .lib-watch"));
    Assert.Contains("@media (min-width: 768px) { .lib-row .lib-watch { display: grid; } }", css);
    // Above the row's open overlay, like the capsule.
    Assert.Contains("position: relative; z-index: 1;", Rule(css, ".lib-row .lib-seen, .lib-row .library-marks, .lib-row .lib-watch"));
  }

  [Fact]
  public void Failure_RestoresAllThreeValues()
  {
    var marks = Marks();
    var setMark = marks[marks.IndexOf("async function setMark(", StringComparison.Ordinal)..];
    // before is the whole state (seen, rating, watchlist), so putting it back restores all three and repaints.
    Assert.Contains("const before = marks.get(key);", setMark);
    var failure = Between(setMark, "} catch (err) {", "\n    }\n  }");
    Assert.Contains("if (marks.get(key) !== next) return;", failure);
    Assert.Contains("marks.set(key, before);\n      repaint();", failure);
    Assert.Contains("ctx.toast(`The mark was not changed. ${err.message || ''}`.trim(), { error: true });", failure);
    // Every source of the state carries watchlist, so before always holds the third value.
    Assert.Contains("const now = marks.get(key) || { seen: false, rating: 0, watchlist: false };", Read("library.js"));
  }
}
