using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Library's chip row and compact rows (#419) at their source, as the other UI tests do (no JS engine).</summary>
public class LibraryRowsUiTests
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

  [Fact]
  public void Chips_ReplacePillsAndToggleButtons()
  {
    var library = Read("library.js");
    var section = Between(library, "function searchSection(", "async function runSearch(");
    Assert.Single(Regex.Matches(section, Regex.Escape("class: 'lib-chips', role: 'group'")));
    Assert.Contains("types, h('span', { class: 'lib-chip-sep', 'aria-hidden': 'true' }), seenFilter, likedFilter)", section);
    // Four type chips from TYPES, a single choice; two toggles.
    Assert.Contains("const types = TYPES.map((option) => h('button', {\n      class: 'lib-chip', type: 'button', 'aria-pressed': String(option.value === state.type)", section);
    Assert.Contains("for (const b of types) b.setAttribute('aria-pressed', String(b.dataset.value === option.value));", section);
    Assert.Contains("class: 'lib-chip is-toggle', type: 'button', 'aria-pressed': String(get()),", section);
    Assert.Contains("b.setAttribute('aria-pressed', String(get()));", section);
    var types = Between(library, "const TYPES = [", "];");
    foreach (var label in new[] { "label: 'All'", "label: 'Films'", "label: 'Series'", "label: 'Channels'" })
      Assert.Contains(label, types);
    Assert.Contains("markFilter('Unseen', () => hideSeen, (v) => { hideSeen = v; });", section);
    Assert.Contains("markFilter('Liked', () => likedOnly, (v) => { likedOnly = v; });", section);
    foreach (var gone in new[] { "Hide seen", "Liked only", "library-filters", "library-toggle", "class: 'pills', role: 'group', 'aria-label': 'Type'" })
      Assert.DoesNotContain(gone, section);
    Assert.DoesNotContain("Hide seen", library);
    Assert.DoesNotContain("Liked only", library);

    var css = Read("library.css");
    var chips = Rule(css, ".lib-chips");
    Assert.Contains("overflow-x: auto;", chips);
    Assert.Contains("scrollbar-width: none;", chips);
    Assert.DoesNotContain("wrap", chips);
    Assert.Contains("display: none;", Rule(css, ".lib-chips::-webkit-scrollbar"));
    Assert.Contains("white-space: nowrap;", Rule(css, ".lib-chip"));
    Assert.Contains("background: var(--ink);", Rule(css, ".lib-chip[aria-pressed=\"true\"]"));
    Assert.Contains("background: var(--accent-soft);", Rule(css, ".lib-chip.is-toggle[aria-pressed=\"true\"]"));
  }

  [Fact]
  public void Row_PhoneHasOneSeenButtonAndGlyph_WideHasCapsule()
  {
    var library = Read("library.js");
    var rowMarks = Between(library, "function rowMarks(ctx, entry)", "// Shown at once on every row");
    Assert.Contains("const capsule = markControls(ctx, entry);", rowMarks);
    Assert.Contains("class: 'lib-seen', type: 'button', 'aria-label': 'Seen', 'aria-pressed': 'false',", rowMarks);
    Assert.Contains("setMark(ctx, entry.name, MARK_KINDS[entry.contentType], key, { ...marks.get(key), seen: !marks.get(key).seen })", rowMarks);
    Assert.Contains("h('span', { class: 'lib-like', 'aria-hidden': 'true' }, icon('up'), icon('down'))", rowMarks);
    Assert.Contains("return [h('span', { class: 'lib-quick', role: 'group' }, like, seen), capsule];", rowMarks);
    // Search results and group entries share the row, so both carry the controls.
    var row = Between(library, "function row(ctx, e, group, cacheKey, meta, badge)", "// mount() runs");
    Assert.Contains("rowMarks(ctx, e),", row);
    Assert.Contains("return row(ctx, m, m.group, cacheKey,", Between(library, "function hit(", "function row("));
    Assert.Contains("view.list.append(...names.map((e) => row(ctx, e, view.group.title, list.cacheKey, null, null)));", library);
    // The glyph's state is in the group label.
    var paint = Between(library, "function paintMarks(el)", "function rowMarks(");
    Assert.Contains("like.dataset.rating = String(m.rating);", paint);
    Assert.Contains("quick.setAttribute('aria-label',", paint);
    Assert.Contains("seenButton.setAttribute('aria-pressed', String(m.seen));", paint);

    var css = Read("library.css");
    Assert.Contains("display: none;", Rule(css, ".lib-row .library-marks"));
    Assert.Contains("@media (min-width: 768px) {\n  .lib-row .lib-seen, .lib-row .lib-like { display: none; }\n  .lib-row .library-marks { display: inline-flex; }\n}", css);
    // The phone rule comes after the capsule's own display, so it wins.
    Assert.True(css.IndexOf(".lib-row .library-marks { display: none; }", StringComparison.Ordinal)
      > css.IndexOf(".film-marks.library-marks { display: inline-flex;", StringComparison.Ordinal));
    var seen = Rule(css, ".lib-seen");
    Assert.Contains("width: 36px; height: 36px;", seen);
    Assert.Contains("inset: -4px;", Rule(css, ".lib-seen::after"));
    Assert.Contains("color: var(--accent);", Rule(css, ".lib-like"));
    Assert.Contains("color: var(--ink-3);", Rule(css, ".lib-like[data-rating=\"-1\"]"));
    Assert.Contains("width: 15px; height: 15px;", Rule(css, ".lib-like svg"));
  }

  [Fact]
  public void Row_OpensTheCardFromAnywhere()
  {
    var css = Read("library.css");
    var after = Rule(css, ".lib-row .library-film-open::after");
    Assert.Contains("position: absolute;", after);
    Assert.Contains("inset: 0;", after);
    Assert.Contains("position: relative; z-index: 1;", Rule(css, ".lib-row .lib-seen, .lib-row .library-marks, .lib-row .lib-watch"));
    Assert.Contains("position: relative;", Rule(css, ".lib-row"));
    Assert.Contains("box-shadow: inset 3px 0 0 var(--accent);", Rule(css, ".lib-row.is-open"));
    Assert.Contains("background:", Rule(css, ".lib-row:has(.library-film-open):hover"));
    // A 16px chevron at every width, not only from 768px.
    Assert.Contains("width: 16px; height: 16px;", Rule(css, ".lib-chev svg"));
    Assert.DoesNotContain("library-film-chevron", css);

    var library = Read("library.js");
    Assert.Contains("ctx.h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, ctx.icon('chevron'))", library);
    Assert.Contains("b.closest('.lib-row')?.classList.toggle('is-open', b === button);", library);
    // Title line: name without a trailing (YYYY), then the year; the full name stays the button's name.
    var name = Between(library, "function filmName(", "const filmChevron");
    Assert.Contains(@"/^(.+?)\s*\((\d{4})\)$/", name);
    Assert.Contains("ctx.h('span', { class: 'y', text: year[2] })", name);
    Assert.Contains("class: 'lib-title library-film-open', type: 'button',", name);
    Assert.Contains("'aria-label': e.name,", name);
    Assert.Contains("color: var(--ink-3);", Rule(css, ".lib-title .y"));
    // Type marker and meta line: group and type for a search result, nothing for a group's entries.
    var row = Between(library, "function row(", "// mount() runs");
    Assert.Contains("h('span', { class: 'lib-dot', 'aria-hidden': 'true', dataset: { type } })", row);
    Assert.Contains("h('span', { class: 'lib-meta' }, meta)", row);
    Assert.Contains("display: none;", Rule(css, ".lib-meta:empty"));
    Assert.Contains("background: var(--movies);", Rule(css, ".lib-dot[data-type=\"Movies\"]"));
    Assert.Contains("background: var(--series);", Rule(css, ".lib-dot[data-type=\"Series\"]"));
  }

  [Fact]
  public void Row_SeenTitleIsDimmed()
  {
    var library = Read("library.js");
    var paint = Between(library, "function paintMarks(el)", "function rowMarks(");
    Assert.Contains("const row = el.closest('.lib-row');", paint);
    Assert.Contains("row.classList.toggle('is-seen', m.seen);", paint);
    // A new row is painted once it holds its controls; later changes repaint through the capsule.
    var row = Between(library, "function row(", "// mount() runs");
    Assert.Contains("if (capsule) paintMarks(capsule);", row);
    var css = Read("library.css");
    var seen = Rule(css, ".lib-row.is-seen .lib-title");
    Assert.Contains("color: var(--ink-2);", seen);
    Assert.Contains("font-weight: 550;", seen);
  }

  [Fact]
  public void SearchHead_StacksStatusAndLanguageBelow600()
  {
    var css = Read("library.css");
    Assert.Contains("@media (max-width: 599px) { .library-search .search-head { flex-direction: column; align-items: stretch; gap: 8px; } }", css);
    // The status stays first in the head, the language chip after it.
    Assert.Contains("h('div', { class: 'search-head' }, results.head, filter.bar),", Read("library.js"));
  }
}
