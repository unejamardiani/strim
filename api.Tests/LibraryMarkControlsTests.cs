using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Library's mark controls and filters (#298) in library.js, main.js, library.css and DESIGN.md. api.Tests has
/// no JS engine (no new dependency), so the frontend is checked at its source, as in <see cref="LibraryRouteTests"/>;
/// the marks API and the mark options are covered by TitleMarksEndpointTests and LibraryMarksTests.
/// </summary>
public class LibraryMarkControlsTests
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

  private static string Marks() => Between(Read("library.js"), "// Marks (#298)", "// Browse (#287)");

  [Fact]
  public void FilmAndSeriesRows_GetSeenAndThumbsControls_ThatUndoOnASecondTap()
  {
    var marks = Marks();
    Assert.Contains("const MARK_KINDS = { Movies: 'film', Series: 'series' };", marks);
    Assert.Contains("if (!entry.markKey || !kind) return null;", marks);
    Assert.Contains("class: 'icon-btn library-mark', type: 'button', 'aria-label': label, title: label, 'aria-pressed': 'false',", marks);
    Assert.Contains("control('Seen', 'check', (m) => ({ ...m, seen: !m.seen })),", marks);
    Assert.Contains("control('Thumbs up', 'up', (m) => ({ ...m, rating: m.rating === 1 ? 0 : 1 })),", marks);
    Assert.Contains("control('Thumbs down', 'down', (m) => ({ ...m, rating: m.rating === -1 ? 0 : -1 })));", marks);
    Assert.Contains("seen.setAttribute('aria-pressed', String(m.seen));", marks);
    Assert.Contains("up.setAttribute('aria-pressed', String(m.rating === 1));", marks);
    Assert.Contains("down.setAttribute('aria-pressed', String(m.rating === -1));", marks);

    // Search results and group entries both carry the controls.
    var library = Read("library.js");
    Assert.Contains("markControls(ctx, m));", Between(library, "function hit(", "window.StrimLibrary"));
    Assert.Contains("h('span', { class: 'search-hit-name', text: e.name }), markControls(ctx, e))", library);
    Assert.Contains("const names = page.entries || [];", library);

    // The icons exist; the tap target is the phone control height.
    var js = Read("main.js");
    Assert.Contains("  up: '<path ", js);
    Assert.Contains("  down: '<path transform=\"rotate(180 12 12)\" ", js);
    var css = Read("library.css");
    Assert.Matches(@"\.library-mark\s*\{\s*width:\s*var\(--control-h\);\s*height:\s*var\(--control-h\);", css);
    Assert.Matches(@"\.library-mark\[aria-pressed=""true""\]\s*\{\s*background:\s*var\(--accent-soft\);", css);
  }

  [Fact]
  public void SettingAMark_SendsTheFullStateThroughMainJsWithCsrf_AndShowsOnEveryVariant()
  {
    var marks = Marks();
    // The rows of one title share their state by mark key and repaint together, without a reload.
    Assert.Contains("const marks = new Map();", marks);
    Assert.Contains("dataset: { key } },", marks);
    Assert.Contains("for (const el of ctx.root.querySelectorAll('.library-marks')) if (el.dataset.key === key) paintMarks(el);", marks);
    Assert.Contains("marks.set(key, next);\n    repaint();\n    try {", marks);
    Assert.Contains("await ctx.setMark({ name, kind, seen: next.seen, rating: next.rating });", marks);

    // library.js has no request of its own; main.js hands in the marks call through api(), which adds the CSRF token.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("setMark: (mark) => api('/marks', { method: 'PUT', body: mark }),", show);
    Assert.Contains("toast,", show);
    var api = Between(Read("main.js"), "async function api(path", "async function isCsrfRejection(");
    Assert.Contains("if (token) headers['X-CSRF-TOKEN'] = token;", api);
  }

  [Fact]
  public void AFailedRequest_PutsTheMarkBack_AndShowsAnError()
  {
    var failure = Between(Marks(), "} catch (err) {", "\n    }\n  }");
    Assert.Contains("if (marks.get(key) !== next) return;", failure);
    Assert.Contains("marks.set(key, before);\n      repaint();", failure);
    Assert.Contains("ctx.toast(`The mark was not changed. ${err.message || ''}`.trim(), { error: true });", failure);
  }

  [Fact]
  public void HideSeenAndLikedOnly_SendTheMarkOptions_AndShowTheCountLeftOut()
  {
    var library = Read("library.js");
    Assert.Contains("let hideSeen = false;\n  let likedOnly = false;", library);
    var section = Between(library, "function searchSection(", "async function runSearch(");
    Assert.Contains("const seenFilter = markFilter('Hide seen', () => hideSeen, (v) => { hideSeen = v; });", section);
    Assert.Contains("const likedFilter = markFilter('Liked only', () => likedOnly, (v) => { likedOnly = v; });", section);
    Assert.Contains("h('div', { class: 'library-filters' }, types, toggle, seenFilter, likedFilter),", section);
    Assert.Contains("run();\n          onMarkFilter();", section);

    var run = Between(library, "async function runSearch(", "// Marks (#298)");
    Assert.Contains("if (hideSeen) body.hideSeen = true;\n    if (likedOnly) body.liked = true;", run);
    Assert.Contains("const seen = hideSeen && Number.isInteger(found.seenHiddenCount) ? found.seenHiddenCount : 0;", run);
    Assert.Contains("seen > 0 ? `${plural(seen, 'seen match', 'seen matches')} hidden` : ''", run);
    Assert.Contains("else if (likedOnly) text = 'Nothing you liked matches your search.';", run);

    // The open group asks with the same options, starts again on a change and shows its seen count.
    var browse = Between(library, "// Browse (#287)", "function hit(");
    Assert.Contains("if (hideSeen) params.hideSeen = true;\n        if (likedOnly) params.liked = true;", browse);
    Assert.Contains("const reload = () => { if (state.open) open(state.open.group, false); };", browse);
    Assert.Contains("plural(page.seenHiddenCount, 'seen entry', 'seen entries')", browse);
    Assert.Contains("searchSection(ctx, result, browse.refresh, browse.reload),", library);

    // The Library asks the endpoints that know the caller's marks.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("api('/library/search', { method: 'POST', body, signal })", show);
    Assert.Contains("api(`/library/group-entries?${new URLSearchParams(params)}`, { signal })", show);
  }

  [Fact]
  public void TheEditor_ShowsNoMarks()
  {
    var js = Read("main.js");
    var outsideLibrary = js.Replace(Between(js, "function showLibrary(playlist)", "function linkCard("), "");
    foreach (var mark in new[] { "/marks", "/library/", "setMark", "library-mark", "markKey", "seenHiddenCount", "Hide seen", "Liked only" })
      Assert.DoesNotContain(mark, outsideLibrary);
    foreach (var file in new[] { "app.html", "app.css", "theme.css" })
      Assert.DoesNotContain("library-mark", Read(file));
  }

  [Fact]
  public void DesignMd_DescribesTheMarkControls()
  {
    var design = Read("DESIGN.md").ReplaceLineEndings("\n");
    var section = Between(design, "### Library (", "\n### ").Replace("\n", " ");
    Assert.Contains("Marks (#298):", section);
    foreach (var phrase in new[] { "Seen (check), Thumbs up and Thumbs down", "`--control-h`", "`aria-pressed`", "tapping it again undoes it",
      "every variant", "puts the mark back", "\"Hide seen\" and \"Liked only\"", "The editor shows no marks." })
      Assert.Contains(phrase, section);
    Assert.Single(Regex.Matches(section, Regex.Escape("Read-only: nothing in the Library changes the playlist")));
  }
}
