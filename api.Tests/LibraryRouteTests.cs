using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the read-only Library route (#285): #/p/&lt;id&gt;/library opens library.js, which main.js mounts
/// and feeds with its own loaders; the Library changes nothing and the page keeps its CSP.
/// </summary>
public class LibraryRouteTests
{
  private const string Csp =
    "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https://image.tmdb.org; connect-src 'self' https: http://localhost:5000 http://127.0.0.1:5000; form-action 'self'; base-uri 'self';\" />";

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
  public void Route_SendsLibraryHashToTheLibrary_AndKeepsTheEditorRoute()
  {
    var js = Read("main.js");
    var parse = Between(js, "function parseRoute()", "function navigate(");
    Assert.Contains("const library = /^\\/p\\/([0-9a-f-]{36})\\/library(?:\\/([^?#]*))?(?:\\?(.*))?$/i.exec(path);", parse);
    Assert.Contains("const match = /^\\/p\\/([0-9a-f-]{36})(\\/sharing)?$/i.exec(path);", parse);
    Assert.Contains("if (match) return { name: match[2] ? 'sharing' : 'playlist', id: match[1].toLowerCase() };", parse);

    var route = Between(js, "function route()", "function closeSheets()");
    Assert.Contains("if (target.name === 'library') return showLibrary(playlist);", route);
    Assert.True(route.IndexOf("showLibrary(playlist)", StringComparison.Ordinal) < route.LastIndexOf("showEditor(playlist);", StringComparison.Ordinal));

    var show = Between(js, "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("window.StrimLibrary.mount({", show);
  }

  [Fact]
  public void CardAndMenu_OfferTheLibrary_NextToEditGroups()
  {
    var js = Read("main.js");
    var card = Between(js, "function playlistCard(playlist)", "function menuIcon(");
    var actions = card[card.IndexOf("h('div', { class: 'row-actions' },", StringComparison.Ordinal)..];
    Assert.Contains("h('a', { class: 'btn btn-sm', href: `#/p/${playlist.id}` }, icon('list'), 'Edit groups'),\n      h('a', { class: 'btn btn-sm', href: `#/p/${playlist.id}/library` }, icon('book'), 'Library')", actions);

    var menu = Between(js, "function openPlaylistMenu(id)", "function replacePlaylist(");
    Assert.Contains("inLibrary ? null : item('book', 'Library', () => navigate(`#/p/${playlist.id}/library`)),", menu);
  }

  [Fact]
  public void LibraryAndEditor_LinkToEachOther_Once()
  {
    var library = Read("library.js");
    Assert.Single(Regex.Matches(library, Regex.Escape("href: `#/p/${playlist.id}`")));
    // The Library links to itself only through its tabs (#418).
    Assert.Single(Regex.Matches(library, Regex.Escape("/library`")));
    Assert.Contains("const base = isViewer(ctx) ? `#/shared/${playlist.id}` : `#/p/${playlist.id}/library`;", library);

    var head = Between(Read("main.js"), "function editorHead(ed, playlist)", "function linkCard(");
    Assert.Single(Regex.Matches(head, Regex.Escape("href: `#/p/${playlist.id}/library`")));
  }

  [Fact]
  public void Library_MakesNoStateChangingCall_AndRendersNoEditingControls()
  {
    var full = Read("library.js");
    // The ⋯ menu (#418) holds the one switch, "Include switched-off groups", which only changes what the Library shows.
    var menu = Between(full, "function libraryMenu(ctx)", "async function load(");
    Assert.Single(Regex.Matches(menu, Regex.Escape("type: 'checkbox', role: 'switch'")));
    Assert.Contains("onchange: (e) => { includeOff = e.target.checked; ctx.onIncludeOff?.(); },", menu);
    var library = full.Replace(menu, "");
    Assert.DoesNotMatch(@"\b(PUT|PATCH|DELETE|POST)\b", library);
    Assert.DoesNotMatch(@"\b(fetch|XMLHttpRequest|sendBeacon|api)\s*\(", library);
    foreach (var forbidden in new[] { "/api/playlists", "/playlists", "/playlist/generate", "regenerate-code", "toggle-active", "auto-refresh" })
      Assert.DoesNotContain(forbidden, library);
    // "switch" as a word or role; the copy may name "switched-off groups" (#286).
    Assert.DoesNotMatch(@"\bswitch\b", library);
    foreach (var forbidden in new[] { "checkbox", "bulk", "Bulk", "Save", "dock", "action-bar", "setActionBar" })
      Assert.DoesNotContain(forbidden, library);
    // The only stored values are this device's group counts for the "+N new" badge (#422), its group view (#428) and
    // its Marks view (#429), never the playlist.
    Assert.Equal(2, Regex.Matches(library, Regex.Escape("localStorage.") + @"\w+Item\(countsKey\(ctx\)").Count);
    Assert.Equal(2, Regex.Matches(library, Regex.Escape("localStorage.") + @"\w+Item\(GROUP_VIEW_KEY").Count);
    Assert.Equal(2, Regex.Matches(library, Regex.Escape("localStorage.") + @"\w+Item\(MARKS_VIEW_KEY").Count);
    Assert.Equal(6, Regex.Matches(library, "localStorage").Count);

    // main.js hides the dock and the picker before the Library renders.
    var show = Between(Read("main.js"), "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("teardownPicker();", show);
    Assert.Contains("setActionBar(false);", show);
  }

  [Fact]
  public void Library_ReusesTheEditorsLoaders_PassedInFromMainJs()
  {
    var js = Read("main.js");
    var show = Between(js, "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("const ed = newEditor(playlist);", show);
    Assert.Contains("applyAnalysis(ed, await fetchAnalysis(playlist.sourceUrl, { signal }), { sample: false });", show);
    Assert.Contains("errorMessage: loadErrorMessage,", show);

    // The editor loads through the same function; the analyze call exists once.
    var load = Between(js, "async function loadGroups(ed, url", "async function fetchAnalysis(");
    Assert.Contains("await fetchAnalysis(url, { sample, signal: controller.signal });", load);
    Assert.Single(Regex.Matches(js, Regex.Escape("'/playlist/analyze'")));

    var library = Read("library.js");
    Assert.Contains("await ctx.loadGroups(own.signal);", library);
    Assert.DoesNotContain("analyze", library);
    Assert.DoesNotContain("restoreFilters", library);
    // The only global the Library sets at load time.
    Assert.Single(Regex.Matches(library, @"window\.\w+\s*="));
    Assert.Contains("window.StrimLibrary = { mount, show };", library);
  }

  [Fact]
  public void SwitchingBeforeTheAnalysisFinished_SharesOneAnalyzeRequest_InsteadOfAHttp429()
  {
    var js = Read("main.js");
    var fetch = Between(js, "async function fetchAnalysis(", "function loadErrorMessage(");

    // Both routes go through one shared request per source; another source waits for the previous one.
    Assert.Contains("if (!sample) return sharedAnalysis(url, { sourceUrl: url }, signal);", fetch);
    Assert.Contains("let entry = pendingAnalyses.get(key);", fetch);
    Assert.Contains("entry.promise = lastAnalysis.then(() => {", fetch);
    Assert.Contains("return api('/playlist/analyze', { method: 'POST', body });", fetch);
    Assert.Contains("lastAnalysis = entry.promise.catch(() => {});", fetch);

    // A queued analysis whose callers all left before its turn is skipped instead of sent, so an abandoned
    // playlist cannot delay the current one; the check runs at its turn, before the request.
    var turn = Between(fetch, "entry.promise = lastAnalysis.then(() => {", "lastAnalysis = entry.promise.catch(");
    var skip = turn.IndexOf("if (queued.waiting === 0) throw new DOMException('The analysis was cancelled.', 'AbortError');", StringComparison.Ordinal);
    Assert.True(skip >= 0 && skip < turn.IndexOf("api('/playlist/analyze'", StringComparison.Ordinal));

    // An aborted fetch settles before the server releases its gate, so the next analysis would start too early
    // and get a 429. The analyze request therefore carries no signal and is never aborted; the queue advances
    // only once the server answered, and a caller that leaves only stops waiting.
    var queue = fetch[fetch.IndexOf("const pendingAnalyses", StringComparison.Ordinal)..];
    Assert.DoesNotContain("signal: controller.signal", queue);
    Assert.DoesNotContain("AbortController", queue);
    Assert.DoesNotContain(".abort()", queue);
    var join = fetch[fetch.IndexOf("function joinAnalysis(", StringComparison.Ordinal)..];
    Assert.Contains("const leave = () => reject(new DOMException('The analysis was cancelled.', 'AbortError'));", join);
    // Every caller counts while it waits and stops counting once, on leaving or when the analysis settled, so a
    // caller that stays keeps the shared request alive.
    Assert.Contains("entry.waiting += 1;", join);
    Assert.Contains("const quit = () => { if (joined) { joined = false; entry.waiting -= 1; } };", join);
    Assert.Contains("const onAbort = () => { quit(); leave(); };", join);
    Assert.Contains("signal?.addEventListener('abort', onAbort, { once: true });", join);
    Assert.True(join.IndexOf("if (signal?.aborted) return leave();", StringComparison.Ordinal) < join.IndexOf("entry.waiting += 1;", StringComparison.Ordinal));

    // Leaving the Library cancels its wait before the next route joins; the Library uses main.js's controller.
    var route = Between(js, "function route()", "function closeSheets()");
    var abort = route.IndexOf("libraryController?.abort();", StringComparison.Ordinal);
    Assert.True(abort > 0);
    foreach (var next in new[] { "showLibrary(playlist)", "showSharedLibrary(target.id)", "showEditor(", "showHome()" })
      Assert.True(abort < route.IndexOf(next, StringComparison.Ordinal), next);
    var show = Between(js, "function showLibrary(playlist)", "function linkCard(");
    Assert.Contains("libraryController = new AbortController();", show);
    Assert.Contains("controller: libraryController,", show);
    var library = Read("library.js").Split("// Film card (#367)")[0];
    Assert.Contains("const { root, playlist, h, icon, controller } = ctx;", library);
    Assert.DoesNotContain("new AbortController", library);
  }

  [Fact]
  public void Scripts_HaveNoLocalImports()
  {
    var import = new Regex(@"(\bfrom|\bimport)\s*\(?\s*['""`]\.\.?/");
    foreach (var file in Directory.GetFiles(StrimAppFactory.RepositoryRoot(), "*.js"))
      Assert.False(import.IsMatch(File.ReadAllText(file)), $"{Path.GetFileName(file)} imports another script.");
    Assert.DoesNotMatch(import, Read("main.js"));
    Assert.DoesNotMatch(import, Read("library.js"));
  }

  [Fact]
  public void AppHtml_LoadsLibraryFilesByTag_WithoutInlineCode_AndTheSameCsp()
  {
    var html = Read("app.html");
    Assert.Single(Regex.Matches(html, Regex.Escape(Csp)));
    Assert.Single(Regex.Matches(html, "Content-Security-Policy"));

    var library = html.IndexOf("<script type=\"module\" src=\"library.js\"></script>", StringComparison.Ordinal);
    var main = html.IndexOf("<script type=\"module\" src=\"main.js\"></script>", StringComparison.Ordinal);
    Assert.True(library >= 0 && library < main, "library.js must load before main.js.");
    var appCss = html.IndexOf("<link rel=\"stylesheet\" href=\"app.css\" />", StringComparison.Ordinal);
    var libraryCss = html.IndexOf("<link rel=\"stylesheet\" href=\"library.css\" />", StringComparison.Ordinal);
    Assert.True(appCss >= 0 && libraryCss > appCss, "library.css must load after app.css.");

    // Every script has a src and is empty; no style element or attribute.
    Assert.Equal(Regex.Matches(html, "<script").Count, Regex.Matches(html, "<script type=\"module\" src=\"[a-z]+\\.js\"></script>").Count);
    Assert.DoesNotContain("<style", html);
    Assert.DoesNotContain("style=", html);
  }

  [Fact]
  public async Task TitlesRoute_WithoutSession_Answers401()
  {
    using var app = new StrimAppFactory();
    var response = await app.CreateClient().GetAsync($"/api/playlists/{Guid.NewGuid()}/library/titles?cacheKey=any");
    Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
  }
}
