using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Library's My marks list (#299) in library.js, main.js, library.css and DESIGN.md at their source, as
/// <see cref="LibraryMarkControlsTests"/> does (api.Tests has no JS engine). The list order and the undo round trip
/// of the marks API are covered by TitleMarksEndpointTests.
/// </summary>
public class LibraryMyMarksTests
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

  private static string MyMarks() => Between(Read("library.js"), "// My marks (#299)", "// A mark saved under the API's key");

  [Fact]
  public void MyMarks_IsPartOfTheLibrary_AndPagesThroughTheListEndpoint()
  {
    var library = Read("library.js");
    var mount = Between(library, "function mount(ctx)", "function show(sub, query)");
    Assert.Contains("panels.marks.append(myMarks(ctx));", mount);
    Assert.True(mount.IndexOf("myMarks(ctx)", StringComparison.Ordinal) < mount.IndexOf("load(ctx, controller);", StringComparison.Ordinal));

    var section = MyMarks();
    Assert.Contains("const MARKS_PAGE = 50;", section);
    Assert.Contains("requested = Math.max(Math.floor(view.shown.size / MARKS_PAGE) + 1, view.minPage);", section);
    Assert.Contains("page = await ctx.listMarks({ ...filter, page: requested, pageSize: MARKS_PAGE }, ctx.controller.signal);", section);
    Assert.Contains("if (view.shown.has(m.key)) continue;", section);
    // #446: no More button; the next page loads once the sentinel comes within 600px of the screen.
    Assert.DoesNotContain("'More'", section);
    Assert.Contains("new IntersectionObserver((seen) => { if (seen.some((e) => e.isIntersecting)) loadPage(); }, { rootMargin: '600px' });", section);
    Assert.Contains("foot.replaceChildren(sentinel);\n        observer.observe(sentinel);", section.ReplaceLineEndings("\n"));

    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("listMarks: (params, signal) => api(`/marks?${new URLSearchParams(params)}`, { signal }),", show);
  }

  [Fact]
  public void APageThatAddsNoNewRow_MovesOnToTheNextPage()
  {
    // A mark set in the search or a group goes to the top and shifts every position by one, so the page computed
    // from the shown count can repeat only marks already shown; More must not ask for that page forever.
    var load = Between(MyMarks(), "const loadPage = async () => {", "const build = (key) => {");
    Assert.Contains("added += 1;", load);
    Assert.Contains("if (!view.done && added === 0) {\n        view.minPage = requested + 1;\n        loadPage();\n        return;\n      }", load.ReplaceLineEndings("\n"));
    Assert.True(load.IndexOf("view.done = items.length", StringComparison.Ordinal) < load.IndexOf("if (!view.done && added === 0)", StringComparison.Ordinal));
    Assert.Contains("view.minPage = 1;", Between(MyMarks(), "const start = () => {", "start();"));
  }

  [Fact]
  public void ARow_IsALibraryRow_WithTitleYearAndTheMarkControls()
  {
    var row = Between(MyMarks(), "const build = (key) => {", "return li;");
    // #447: a .lib-row with the title and year from the mark (filmName's title path) and the search rows' controls.
    Assert.Contains("title: m.title, year: m.year,", row);
    Assert.Contains("h('span', { class: 'lib-text' }, filmName(ctx, opened, t?.group, ctx.list?.cacheKey), h('span', { class: 'lib-meta' }, meta)),", row);
    Assert.Contains("rowMarks(ctx, e),", row);
    Assert.Contains("paintMarks(li.querySelector('.library-marks'));", row);
    // A row opens a card, never an entry: no link.
    Assert.DoesNotContain("h('a'", row);
    Assert.DoesNotContain("href", MyMarks());
    Assert.DoesNotContain("library-hit", MyMarks());
  }

  [Fact]
  public void Filters_SendSeenOrRating_AndStartAgain()
  {
    var section = MyMarks();
    Assert.Contains("{ value: 'seen', label: 'Seen', params: { seen: true } },", section);
    Assert.Contains("{ value: 'liked', label: 'Liked', params: { rating: 1 } },", section);
    Assert.Contains("{ value: 'disliked', label: 'Disliked', params: { rating: -1 } },", section);
    Assert.Contains("onclick: () => choose(option.value),", section);
    Assert.Contains("view.filter = name;", section);
    Assert.Contains("view.seq += 1;", section);
    Assert.Contains("if (seq !== view.seq || !ctx.isCurrent()) return;", section);
  }

  [Fact]
  public void ARowsControls_SetTheMarkWithCsrf_AndNoRemoveIsLeft()
  {
    var section = MyMarks();
    // #447: the row's controls set the mark through setMark, which sends the PUT; Remove, Undo and removeMark are gone.
    Assert.DoesNotContain("Remove", section);
    Assert.DoesNotContain("undo", section);
    Assert.Contains("const e = { name: m.year ? `${m.title} (${m.year})` : m.title,", section);

    // The PUT goes through main.js's api(), which sends the CSRF token on every mutating call.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.DoesNotContain("removeMark", show);
    Assert.Contains("setMark: (mark) => api('/marks', { method: 'PUT', body: mark }),", show);
    var api = Between(Read("main.js"), "async function api(path", "async function isCsrfRejection(");
    Assert.Contains("const mutating = method !== 'GET' && method !== 'HEAD';", api);
    Assert.Contains("if (token) headers['X-CSRF-TOKEN'] = token;", api);
  }

  [Fact]
  public void NoMarks_ShowsAnEmptyState()
  {
    // The texts themselves are pinned in LibraryMarksTabUiTests (#446).
    var paint = Between(MyMarks(), "const paint = () => {", "    };");
    Assert.Contains("else foot.replaceChildren(status(MARK_EMPTY[view.filter] || 'No marks of this kind.'));", paint);
  }

  [Fact]
  public void MyMarks_HasNoInlineStyle_AndDesignMdDescribesIt()
  {
    Assert.DoesNotContain("style", MyMarks());
    var css = Read("library.css");
    // #447: the rows are .lib-row; the plain-text row's rules are gone.
    Assert.DoesNotContain(".library-hit", css);
    Assert.DoesNotContain(".library-mylist > .card", css);
    Assert.DoesNotContain(".library-mylist .search-hit-name", css);

    var design = Read("DESIGN.md").ReplaceLineEndings("\n");
    var section = Between(design, "### Library (", "\n### ").Replace("\n", " ");
    Assert.Contains("My marks (#299):", section);
    foreach (var phrase in new[] { "newest change first", "no longer in any playlist", "All, Seen, Liked or Disliked",
      "\"Not in this list\"", "opens its film or series card on tap", "(`is-static`)", "The controls are the search rows'",
      "With no marks it says so" })
      Assert.Contains(phrase, section);
    Assert.DoesNotContain("Remove", Between(section, "My marks (#299):", "With no marks"));
    Assert.Contains("rows (name and year, group or Not in this list, marks)", design);
  }
}
