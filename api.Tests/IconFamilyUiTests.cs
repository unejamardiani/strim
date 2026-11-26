using System.Text;
using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Stem and dot icon family (#493) at its source in main.js, theme.css and library.css (api.Tests
/// has no JS engine): the 31 names stay (24 of #493, six of #495, edit of #581), every object glyph carries one filled signal dot, and no glyph brings its
/// own colour.</summary>
public class IconFamilyUiTests
{
  private static readonly string[] Names =
  [
    "back", "folder", "more", "copy", "download", "list", "refresh", "trash", "link", "clock", "plus", "check",
    "bookmark", "close", "sort", "up", "down", "chevron", "star", "home", "book", "people", "search", "grid",
    "marks", "trend", "signal", "spark", "tv", "eye", "edit",
  ];

  private static readonly string[] ObjectGlyphs =
  [
    "home", "search", "grid", "book", "bookmark", "folder", "people", "clock", "link", "copy", "trash", "download",
    "refresh", "list", "up", "down", "back", "marks", "trend", "signal", "spark", "tv", "eye", "edit",
  ];

  // The six of #495, as drawn in the approved mockup; the marks check is its body (.b) for the on state (#494).
  private static readonly Dictionary<string, string> NewGlyphs = new()
  {
    ["marks"] = "<path class=\"b\" d=\"M3.8 13.2 8.6 18l7.2-8.6\"/><circle class=\"sig\" cx=\"19.3\" cy=\"5.6\" r=\"1.7\"/>",
    ["trend"] = "<path d=\"M3.5 17.2 9 11.7l3.5 3.5 4.6-5.7\"/><circle class=\"sig\" cx=\"19.6\" cy=\"6.3\" r=\"1.7\"/>",
    ["signal"] = "<path d=\"M6 11.5a6.5 6.5 0 0 1 6.5 6.5M6 5a13 13 0 0 1 13 13\"/><circle class=\"sig\" cx=\"6.2\" cy=\"17.8\" r=\"1.9\"/>",
    ["spark"] = "<path class=\"b\" d=\"M10.5 4c.7 4.3 2.2 5.8 6.5 6.5-4.3.7-5.8 2.2-6.5 6.5-.7-4.3-2.2-5.8-6.5-6.5 4.3-.7 5.8-2.2 6.5-6.5z\"/><circle class=\"sig\" cx=\"18.6\" cy=\"18\" r=\"1.7\"/>",
    ["tv"] = "<rect class=\"b\" x=\"3.5\" y=\"7.5\" width=\"17\" height=\"12.5\" rx=\"2.6\"/><path d=\"M8.5 3.5 12 7.5l3.5-4\"/><circle class=\"sig\" cx=\"16.3\" cy=\"15.8\" r=\"1.7\"/>",
    ["eye"] = "<path class=\"b\" d=\"M2.8 12c2.2-3.9 5.3-5.8 9.2-5.8s7 1.9 9.2 5.8c-2.2 3.9-5.3 5.8-9.2 5.8S5 15.9 2.8 12z\"/><circle class=\"sig\" cx=\"12\" cy=\"12\" r=\"2.1\"/>",
  };

  private static readonly string[] BareGlyphs = ["chevron", "close", "plus", "check", "sort", "star"];

  private static readonly Dictionary<string, string> Approved = new()
  {
    ["home"] = "<path class=\"b\" d=\"M3.6 11.8 10.6 5.6l7 6.2v6.7a2 2 0 0 1-2 2h-10a2 2 0 0 1-2-2z\"/><path d=\"M10.6 20.5v-4.6\"/><circle class=\"sig\" cx=\"19.4\" cy=\"5.2\" r=\"1.7\"/>",
    ["search"] = "<circle class=\"b\" cx=\"10\" cy=\"10\" r=\"6\"/><path d=\"M14.4 14.4l2.2 2.2\"/><circle class=\"sig\" cx=\"19.5\" cy=\"19.5\" r=\"1.7\"/>",
    ["grid"] = "<rect class=\"b\" x=\"4\" y=\"4\" width=\"6.6\" height=\"6.6\" rx=\"1.9\"/><rect class=\"b\" x=\"4\" y=\"13.4\" width=\"6.6\" height=\"6.6\" rx=\"1.9\"/><rect class=\"b\" x=\"13.4\" y=\"13.4\" width=\"6.6\" height=\"6.6\" rx=\"1.9\"/><circle class=\"sig\" cx=\"16.7\" cy=\"7.3\" r=\"2.2\"/>",
    ["book"] = "<path d=\"M5.5 4.5v15M11 10v9.5M15.4 8.6l3.9 10.6\"/><circle class=\"sig\" cx=\"11\" cy=\"5.6\" r=\"1.7\"/>",
    ["bookmark"] = "<path class=\"b\" d=\"M7.8 4h8.4A1.8 1.8 0 0 1 18 5.8V20l-6-4.3L6 20V5.8A1.8 1.8 0 0 1 7.8 4z\"/><circle class=\"sig\" cx=\"12\" cy=\"9.3\" r=\"1.7\"/>",
  };

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string MapSource()
  {
    var js = Read("main.js");
    var start = js.IndexOf("const ICONS = {", StringComparison.Ordinal);
    Assert.True(start >= 0, "main.js has no 'const ICONS = {'.");
    var end = js.IndexOf("\n};", start, StringComparison.Ordinal);
    Assert.True(end > start, "The ICONS map has no closing '};'.");
    return js[start..(end + 3)];
  }

  private static Dictionary<string, string> Map()
  {
    var source = MapSource();
    var entries = Regex.Matches(source, @"^  (\w+): '([^']*)',$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    var body = source.Split('\n').Length - 2;
    Assert.Equal(body, entries.Count);
    return entries.ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
  }

  private static int Count(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

  [Fact]
  public void Family_HasThe31Names()
  {
    Assert.Equal(Names.Order(), Map().Keys.Order());
  }

  [Fact]
  public void ObjectGlyphs_CarryExactlyOneSignalDot()
  {
    var map = Map();
    foreach (var name in ObjectGlyphs)
      Assert.True(Count(map[name], "class=\"sig\"") == 1, $"'{name}' must carry exactly one signal dot.");
    Assert.Equal(3, Count(map["more"], "class=\"sig\""));
    foreach (var name in BareGlyphs)
      Assert.True(Count(map[name], "class=\"sig\"") == 0, $"'{name}' must carry no signal dot.");
  }

  [Fact]
  public void Glyphs_AreColourless()
  {
    foreach (var (name, glyph) in Map())
      foreach (var banned in new[] { "style=", "fill=", "stroke=", "#" })
        Assert.False(glyph.Contains(banned, StringComparison.Ordinal), $"'{name}' contains '{banned}'.");
  }

  [Fact]
  public void Glyphs_MatchTheApprovedDrawing()
  {
    var map = Map();
    foreach (var (name, glyph) in Approved)
      Assert.Equal(glyph, map[name]);
  }

  [Fact]
  public void NewGlyphs_MatchTheApprovedDrawing()
  {
    var map = Map();
    foreach (var (name, glyph) in NewGlyphs)
      Assert.Equal(glyph, map[name]);
  }

  [Fact]
  public void Family_StaysSmall()
  {
    var bytes = Encoding.UTF8.GetByteCount(MapSource());
    Assert.True(bytes < 4400, $"The ICONS map is {bytes} bytes; it must stay under 4,400.");
  }

  [Fact]
  public void TabGlyphs_KeepTheDotClearOfTheBody()
  {
    var map = Map();
    Assert.Contains("cx=\"19.4\" cy=\"5.2\"", map["home"]);
    Assert.Contains("cx=\"19.5\" cy=\"19.5\"", map["search"]);
    Assert.Equal(Approved["home"], map["home"]);
    Assert.Equal(Approved["search"], map["search"]);
  }

  [Fact]
  public void SignalDot_IsFilledNotStroked()
  {
    var theme = Read("theme.css");
    var icon = theme.IndexOf(".icon {", StringComparison.Ordinal);
    var sig = theme.IndexOf(".icon .sig { fill: currentColor; stroke: none; }", StringComparison.Ordinal);
    Assert.True(icon >= 0, "theme.css has no '.icon {' rule.");
    Assert.True(sig > icon, "'.icon .sig { fill: currentColor; stroke: none; }' must follow the .icon rule.");

    var library = Read("library.css");
    Assert.Contains(".lib-art-watch .sig { fill: var(--accent); }", library);
    Assert.Contains(".lib-watch[aria-pressed=\"true\"] .sig { fill: var(--surface); }", library);
  }

  // The field glyphs are background images in app.css (#498): they repeat ICONS.search and the chevron turned down.
  [Fact]
  public void FieldGlyphs_FollowTheFamily()
  {
    var app = Read("app.css");
    var lens = Regex.Match(app, @"\.input-search \{[^}]*\}", RegexOptions.CultureInvariant);
    Assert.True(lens.Success, "app.css has no .input-search rule.");
    Assert.Contains("cx='10' cy='10' r='6'", lens.Value);
    Assert.Contains("d='M14.4 14.4l2.2 2.2'", lens.Value);
    Assert.Contains("cx='19.5' cy='19.5' r='1.7' fill='%23857a6d' stroke='none'", lens.Value);

    var arrow = Regex.Match(app, @"select\.input \{[^}]*\}", RegexOptions.CultureInvariant);
    Assert.True(arrow.Success, "app.css has no select.input rule.");
    Assert.Contains("d='M6 9.5l6 6 6-6'", arrow.Value);
    Assert.Contains("stroke-linejoin='round'", arrow.Value);

    Assert.DoesNotContain("r='7'", app);
    Assert.DoesNotContain("M20 20l-3.5-3.5", app);
  }
}
