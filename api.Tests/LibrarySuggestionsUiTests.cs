using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins Home's "Trending this week" (#478) and "Because you liked …" (#479) rails in library.js and main.js at their source, as the other
/// Library UI tests do (api.Tests has no JS engine).</summary>
public class LibrarySuggestionsUiTests
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

  private static string Home() => Between(Read("library.js"), "function homeSection(ctx) {", "// My marks (#299)");

  private static string Section() => Between(Home(), "const suggested = (source, title) => {", "// New in <group> (#427)");

  [Fact]
  public void Suggestions_AreAskedThroughBothContexts()
  {
    var main = Read("main.js");
    var own = Between(main, "function showLibrary(", "function showSharedLibrary(id)");
    Assert.Contains("suggestions: (params, signal) => api(`/playlists/${playlist.id}/library/suggestions?${new URLSearchParams(params)}`, { signal }),", own);
    var viewer = Between(main, "function showSharedLibrary(id)", "function linkCard(");
    Assert.Contains("suggestions: (params, signal) => api(`${base}/suggestions?${new URLSearchParams(scoped(params))}`, { signal }),", viewer);
  }

  [Fact]
  public void Trending_IsASectionBetweenTheListsAndNewIn_OnlyWithTmdbOn()
  {
    var home = Home();
    // Built only under rails (TMDB on); a part without a link.
    Assert.Contains("const trending = rails ? suggested('trending', 'Trending this week') : null;", home);
    Assert.Contains("const p = part(title || '');", Section());
    Assert.Contains("h('div', { class: 'lib-home-lists' }, watch.p.el, follow.p.el), trending?.p.el, liked?.p.el, newIn.el, fresh.el, types.el);", home);
    // Asked only through the section, so with TMDB off (trending null) the route is never asked.
    Assert.Contains("view.homeSuggest = (fresh) => [trending, liked].forEach((s) => s?.load(fresh));", home);
    Assert.Single(home.Split("ctx.suggestions(")[1..]);
    Assert.Contains("ctx.suggestions(", Section());
    Assert.Contains("p.body.replaceChildren(rail(p, label,", Section());
  }

  [Fact]
  public void Trending_LoadsOnceTheListIsThere_AndIsKeptWhileTheLibraryIsOpen()
  {
    var library = Read("library.js");
    var home = Home();
    var section = Section();
    // Asked from view.homeList, which runs as the list arrives (and fails, or has no source).
    var homeList = Between(home, "view.homeList = (again) => {", "// Marks set anywhere (#448)");
    Assert.Contains("view.homeFresh();\n      view.homeSuggest();\n", homeList);
    // Not while loading, without a source or after a failure: hidden.
    Assert.Contains("if (!ctx.list || ctx.listState !== 'ready') return void (p.el.hidden = true);", section);
    Assert.Contains("answer = await ctx.suggestions({ source, cacheKey: ctx.list.cacheKey, includeDisabled: includeOff }, ctx.controller.signal);", section);
    Assert.Contains("busy(p);", section);
    // Kept per source and includeOff for the mounted Library (homeSection runs once per mount).
    Assert.Contains("const keptSuggestions = new Map();", home);
    Assert.Contains("kept = `${source}\\n${includeOff}`;", section);
    Assert.Contains("if (keptSuggestions.has(kept)) return paint(keptSuggestions.get(kept));", section);
    Assert.Contains("keptSuggestions.set(kept, shown);", section);
    Assert.DoesNotContain("keptSuggestions", Between(library, "function show(sub, query)", "function inPanels("));
    // "Include switched-off groups" asks again in both directions: the toggle drops every kept answer before the
    // lookup, so off → on → off does not restore the first answer. Home and list repaints call without `fresh`.
    Assert.Contains("ctx.lookupMarks?.();\n      view.homeSuggest?.(true);\n    };", Between(library, "ctx.onIncludeOff = () => {", "const markFilter"));
    var load = Between(section, "s.load = async (fresh) => {", "busy(p);");
    var clear = load.IndexOf("if (fresh) keptSuggestions.clear();", StringComparison.Ordinal);
    Assert.True(clear >= 0 && clear < load.IndexOf("if (keptSuggestions.has(kept))", StringComparison.Ordinal),
      "The toggle must drop the kept answers before they are reused.");
    Assert.DoesNotContain("view.homeSuggest(true)", home);
    // An answer for a Library left since or an older request changes nothing.
    Assert.Contains("if (seq !== s.seq || !live()) return;", section);
  }

  [Fact]
  public void Trending_BelowThreeOrFailed_IsHidden_WithoutANotice()
  {
    var section = Section();
    Assert.Contains("p.el.hidden = titles.length < 3 || !label;", section);
    Assert.Contains("p.n.textContent = fmt(titles.length);", section);
    Assert.Contains(".slice(0, 12);", section);
    var caught = Between(section, "} catch (err) {", "return;");
    Assert.Contains("if (seq === s.seq && live() && !accessGone(ctx, err)) p.el.hidden = true;", caught);
    Assert.DoesNotContain("failed(", section);
    Assert.DoesNotContain("Try again", section);
    Assert.DoesNotContain("lib-empty", section);
    // The marks of the answer are applied first, so badges show.
    Assert.Contains("if (t.markKey) applyMark(ctx, t.markKey, t, at);", section);
  }

  [Fact]
  public void Tile_TakesItsPosterFromTheAnswer_AndAsksNoPostersRoute()
  {
    var library = Read("library.js");
    Assert.Contains("function posterTile(ctx, { key, entry, title, year, group, type, meta, bar, poster = true, posterUrl }) {", library);
    var tile = Between(library, "function posterTile(ctx, {", "function paintTile(");
    Assert.Contains("    if (posterUrl !== undefined) showPoster(ctx, tile, posterUrl);\n    else if (poster) view.posters?.request(", tile);
    var section = Section();
    Assert.Contains("posterUrl: posterUrl || null }", section);
    Assert.Contains("meta: t.contentType === 'Series' ? seasonsLine(ctx, t) : t.year ? String(t.year) : '',", section);
    Assert.Contains("key: t.markKey, entry: t, title: t.title || t.name, year: t.year, group: t.group, type: t.contentType,", section);
    Assert.DoesNotContain("view.posters?.request", section);
  }

  [Fact]
  public void Liked_IsTheSecondSuggestionSection_AfterTrending()
  {
    var home = Home();
    // Built through the shared helper, only under rails, with the heading left to the answer.
    Assert.Contains("const liked = rails ? suggested('liked', null) : null;", home);
    Assert.True(home.IndexOf("const trending = rails ?", StringComparison.Ordinal) < home.IndexOf("const liked = rails ?", StringComparison.Ordinal));
    // Asked with source 'liked' by the one call of the helper; both sections load independently.
    Assert.Single(home.Split("ctx.suggestions(")[1..]);
    Assert.Contains("[trending, liked].forEach((s) => s?.load(fresh))", home);
    // Home's children: directly after Trending, before New in <group>.
    Assert.Contains("trending?.p.el, liked?.p.el, newIn.el,", home);
  }

  [Fact]
  public void Liked_HeadingNamesTheSeed_AsText()
  {
    var section = Section();
    Assert.Contains("const [heading] = p.head.children;", section);
    Assert.Contains("if (!title) heading.className = 'lib-sec-one';", section);
    Assert.Contains("const label = title || (seed?.title ? `Because you liked ${seed.title}` : '');", section);
    // Text and title of the h2, and the same text as the rail's label.
    Assert.Contains("if (!title) heading.textContent = heading.title = label;", section);
    Assert.Contains("p.body.replaceChildren(rail(p, label,", section);
    Assert.Contains("const shown = { titles, seed: answer?.seed || null };", section);
    Assert.DoesNotContain("innerHTML", section);
    // The one-line rule already exists (cut with an ellipsis, the count and arrows keep their width).
    var css = Read("library.css");
    Assert.Contains(".lib-sec-head .lib-sec-one { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }", css);
    Assert.Contains(".lib-sec-head > :not(.lib-sec-one) { flex-shrink: 0; }", css);
  }

  [Fact]
  public void Liked_WithoutASeedOrBelowThree_IsHidden_AndShowsNoLoadingLine()
  {
    var section = Section();
    Assert.Contains("p.el.hidden = titles.length < 3 || !label;", section);
    // While asking: Trending shows "Loading…", the liked section (no fixed title) stays hidden.
    Assert.Contains("if (title) busy(p);\n        else p.el.hidden = true;\n", section);
    Assert.Contains("if (seq === s.seq && live() && !accessGone(ctx, err)) p.el.hidden = true;", section);
    Assert.Contains("const p = part(title || '');", section);
  }

  [Fact]
  public void Liked_IsNotAskedAgainWhenAMarkChanges()
  {
    var home = Home();
    var changed = Between(home, "view.marksChanged = (key, mark, how) => {", "watch.load();");
    Assert.DoesNotContain("homeSuggest", changed);
    Assert.DoesNotContain("liked", changed);
    Assert.DoesNotContain("suggestions", changed);
    Assert.DoesNotContain(".load(fresh)", changed);
  }
}
