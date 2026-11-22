using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the two system contrast settings (#497) at their source in theme.css, app.css, library.css and
/// site.css, as the other UI tests do (api.Tests has no JS engine): more contrast strengthens text, edges and strokes;
/// forced colours keep switches, markers, state and button outlines visible with the system palette.</summary>
public class ContrastPreferenceUiTests
{
  private const string MoreContrastBlock =
    "@media (prefers-contrast: more) {\n" +
    "  :root { --ink-3: var(--ink-2); --edge: var(--ink-2); --card-border: var(--edge); --line: var(--line-strong); }\n" +
    "  .icon.icon.icon { stroke-width: 2.6; }\n" +
    "}";

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  /// <summary>The body of every <c>@media</c> block in <paramref name="css"/> whose query is <paramref name="query"/>.</summary>
  private static List<string> Blocks(string css, string query)
  {
    var blocks = new List<string>();
    var head = $"@media ({query}) {{";
    for (var at = css.IndexOf(head, StringComparison.Ordinal); at >= 0; at = css.IndexOf(head, at + 1, StringComparison.Ordinal))
    {
      var start = at + head.Length;
      var depth = 1;
      var i = start;
      for (; i < css.Length && depth > 0; i++)
      {
        if (css[i] == '{') depth++;
        else if (css[i] == '}') depth--;
      }
      blocks.Add(css[start..(i - 1)]);
    }
    return blocks;
  }

  private static string Forced(string file)
  {
    var blocks = Blocks(Read(file), "forced-colors: active");
    Assert.True(blocks.Count > 0, $"{file} has no forced-colors block.");
    return string.Join("\n", blocks);
  }

  [Fact]
  public void MoreContrast_StrengthensTextEdgesAndStrokes()
  {
    var theme = Read("theme.css");
    var block = theme.IndexOf(MoreContrastBlock, StringComparison.Ordinal);
    Assert.True(block >= 0, "The prefers-contrast: more block is missing from theme.css.");
    var dark = theme.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal);
    Assert.True(dark >= 0 && block > dark, "The more-contrast block must come after the dark tokens.");
  }

  [Fact]
  public void ForcedColours_KeepSwitchesVisible()
  {
    var app = Forced("app.css");
    Assert.Contains(".toggle, .switch { forced-color-adjust: none; background: Canvas; box-shadow: none; border: 1.5px solid ButtonText; }", app);
    Assert.Contains(".toggle::after, .switch::after { top: 1.5px; left: 1.5px; background: ButtonText; }", app);
    Assert.Contains(".toggle:checked, .switch.on { background: Highlight; border-color: Highlight; }", app);
    Assert.Contains(".toggle:checked::after, .switch.on::after { background: HighlightText; }", app);
    // The switch opts out of adjustment, so its focus ring (and the field's, whose shadow forced colours drop) is set here.
    Assert.Contains(".toggle:focus-visible, .switch:focus-visible, .input:focus { outline: 2px solid Highlight; outline-offset: 2px; }", app);

    var site = Forced("site.css");
    Assert.Contains(".switch { forced-color-adjust: none; background: Canvas; box-shadow: none; border: 1.5px solid ButtonText; }", site);
    Assert.Contains(".switch::after { top: 1.5px; left: 1.5px; background: ButtonText; }", site);
    Assert.Contains(".switch.on { background: Highlight; border-color: Highlight; }", site);
    Assert.Contains(".switch.on::after { background: HighlightText; }", site);
  }

  [Fact]
  public void ForcedColours_KeepMarkersAndState()
  {
    Assert.Contains(".dot, [data-type] > .dot, .dot[data-type] { forced-color-adjust: none; background: CanvasText; }", Forced("app.css"));
    Assert.Contains(".toast::before, .toast.error::before { forced-color-adjust: none; background: Highlight; }", Forced("app.css"));
    Assert.Contains(".lib-dot, .lib-dot[data-type] { forced-color-adjust: none; background: CanvasText; }", Forced("library.css"));
    Assert.Contains(".dot, .dot.movies, .dot.series { forced-color-adjust: none; background: CanvasText; }", Forced("site.css"));

    var theme = Forced("theme.css");
    // The wordmark dot opts out of adjustment, so its tangerine pulse has to go as well.
    Assert.Contains(".wm-dot { forced-color-adjust: none; background: Highlight; box-shadow: none; animation: none; }", theme);
    // site.css's .tv-card .wm-dot pulse outranks the theme rule, so the 50x page stops it in its own block.
    var siteCss = Read("site.css");
    var tvPulse = siteCss.IndexOf(".tv-card .wm-dot {", StringComparison.Ordinal);
    Assert.True(tvPulse >= 0, "site.css lost the television's pulse rule.");
    Assert.Contains(".tv-card .wm-dot { animation: none; }", Forced("site.css"));
    Assert.True(siteCss.LastIndexOf("@media (forced-colors: active)", StringComparison.Ordinal) > tvPulse,
      "The forced-colors block in site.css must come after the television's pulse rule.");
    // The selection mark is a border, not an outline: a focused selected pill keeps it next to the focus ring.
    Assert.Contains(
      ".pills button[aria-pressed=\"true\"], .lib-chip[aria-pressed=\"true\"], .lib-tabs a[aria-current=\"page\"] { border: 2px solid Highlight; outline-color: Highlight; }",
      theme);
    Assert.DoesNotContain(":not(:focus-visible)", theme);
    Assert.Contains(
      "[aria-current=\"page\"] > .icon .sig, [aria-pressed=\"true\"] > .icon .sig { fill: Highlight; stroke: none; }",
      theme);
    // The Watchlist bookmark's own .sig rule in library.css loads last and would otherwise win.
    Assert.Contains(".lib-watch[aria-pressed=\"true\"] .sig { fill: Highlight; stroke: none; }", Forced("library.css"));
  }

  [Fact]
  public void ForcedColours_OutlineButtonsBelowFocus()
  {
    Assert.Contains(
      ":where(.btn, .icon-btn, .pills button, .lib-ib, .lib-chip, .lib-tabs) { outline: 1px solid ButtonBorder; outline-offset: -1px; }",
      Forced("theme.css"));
  }

  [Fact]
  public void ForcedColours_ComeAfterTheRulesTheyReplace()
  {
    foreach (var (file, rule) in new[]
             {
               ("app.css", ".toast.error::before {"), ("app.css", ".switch.on::after {"),
               ("site.css", ".switch.on::after {"), ("library.css", ".lib-dot[data-type=\"Series\"] {"),
               ("library.css", ".lib-watch[aria-pressed=\"true\"] .sig { fill: var(--surface); }"),
               ("theme.css", "[aria-current=\"page\"] > .icon .sig, [aria-pressed=\"true\"] > .icon .sig { fill: var(--accent);"),
             })
    {
      var css = Read(file);
      var replaced = css.IndexOf(rule, StringComparison.Ordinal);
      Assert.True(replaced >= 0, $"{file} lost '{rule}'.");
      Assert.True(css.LastIndexOf("@media (forced-colors: active)", StringComparison.Ordinal) > replaced,
        $"The forced-colors block in {file} must come after '{rule}'.");
    }
  }

  [Fact]
  public void NoImportant()
  {
    foreach (var file in new[] { "theme.css", "app.css", "library.css", "site.css" })
    {
      var css = Read(file);
      foreach (var block in Blocks(css, "forced-colors: active").Concat(Blocks(css, "prefers-contrast: more")))
        Assert.DoesNotContain("!important", block);
    }
  }
}
