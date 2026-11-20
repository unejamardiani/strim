using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the Library's My marks list (#299, the Marks tab since #424) in library.js and main.js at their source, as
/// <see cref="LibraryMarkControlsTests"/> does (api.Tests has no JS engine). The list order and the undo round trip
/// of the marks API are covered by TitleMarksEndpointTests; the tab itself by <see cref="LibraryMarksTabUiTests"/>.
/// </summary>
public class LibraryMyMarksTests
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

  internal static string MyMarks() => Between(Read("library.js"), "// The Marks tab (#424)", "// A mark saved under another key");

  [Fact]
  public void MyMarks_IsPartOfTheLibrary_AndPagesThroughTheListEndpoint()
  {
    var library = Read("library.js");
    var mount = Between(library, "function mount(ctx)", "function show(sub, query)");
    Assert.Contains("panels.marks.append(myMarks(ctx));", mount);
    Assert.True(mount.IndexOf("myMarks(ctx)", StringComparison.Ordinal) < mount.IndexOf("load(ctx, controller);", StringComparison.Ordinal));

    var section = MyMarks();
    Assert.Contains("const MARKS_PAGE = 50;", section);
    Assert.Contains("requested = Math.max(Math.floor(state.shown.size / MARKS_PAGE) + 1, state.minPage);", section);
    Assert.Contains("page = await ctx.listMarks({ ...filter, page: requested, pageSize: MARKS_PAGE }, ctx.controller.signal);", section);
    Assert.Contains("if (state.shown.has(m.key)) continue;", section);
    // More pages load with a sentinel 600px before the end instead of a More button.
    Assert.Contains("{ rootMargin: '600px' }", section);
    Assert.DoesNotContain("'More'", section);

    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("listMarks: (params, signal) => api(`/marks?${new URLSearchParams(params)}`, { signal }),", show);
  }

  [Fact]
  public void APageThatAddsNoNewRow_MovesOnToTheNextPage()
  {
    // A mark set in the search or a group goes to the top and shifts every position by one, so the page computed
    // from the shown count can repeat only marks already shown; paging must not ask for that page forever.
    var load = Between(MyMarks(), "const loadPage = async () => {", "const lookup = async");
    Assert.Contains("added.push(m.key);", load);
    Assert.Contains("if (!state.done && added.length === 0) {\n        state.minPage = requested + 1;\n        loadPage();\n        return;\n      }", load);
    Assert.True(load.IndexOf("state.done = items.length", StringComparison.Ordinal) < load.IndexOf("if (!state.done && added.length === 0)", StringComparison.Ordinal));
    Assert.Contains("state.minPage = 1;", Between(MyMarks(), "const start = () => {", "start();"));
  }

  [Fact]
  public void ARow_HasTheSharedControls_InsteadOfARemoveButton()
  {
    var section = MyMarks();
    var row = Between(section, "const markRow = (m, t) => {", "return li;");
    Assert.Contains("rowMarks(ctx, entry),", row);
    Assert.Contains("paintMarks(li.querySelector('.library-marks'));", row);
    Assert.DoesNotContain("Remove", section);
    Assert.DoesNotContain("removeMark", section);
    Assert.DoesNotContain("removeMark", Read("main.js"));
    // The controls set the mark with main.js's api(), which sends the CSRF token on every mutating call.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("setMark: (mark) => api('/marks', { method: 'PUT', body: mark }),", show);
    var api = Between(Read("main.js"), "async function api(path", "async function isCsrfRejection(");
    Assert.Contains("const mutating = method !== 'GET' && method !== 'HEAD';", api);
    Assert.Contains("if (token) headers['X-CSRF-TOKEN'] = token;", api);
  }

  [Fact]
  public void MyMarks_HasNoInlineStyle_AndDesignMdDescribesIt()
  {
    Assert.DoesNotContain("style", MyMarks());
    Assert.DoesNotContain(".library-hit", Read("library.css"));
    var design = Read("DESIGN.md");
    var section = Between(design, "### Library (", "\n### ").Replace("\n", " ");
    Assert.Contains("My marks (#299, the Marks tab since #424):", section);
    foreach (var phrase in new[] { "no longer in any playlist", "Watchlist, All, Seen, Liked, Disliked", "`marks?view=…`",
      "\"Not in this list\"", "There is no Remove button", "Undo, which sets the previous mark again", "at most 920px wide" })
      Assert.Contains(phrase, section);
    Assert.DoesNotContain("Remove button at", section);
  }
}
