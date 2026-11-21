using System.Text;
using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the Stem and dot icon family (#493) at its source in main.js, theme.css and library.css (api.Tests
/// has no JS engine): the 24 names stay, every object glyph carries one filled signal dot, and no glyph brings its
/// own colour.</summary>
public class IconFamilyUiTests
{
  private static readonly string[] Names =
  [
    "back", "folder", "more", "copy", "download", "list", "refresh", "trash", "link", "clock", "plus", "check",
    "bookmark", "close", "sort", "up", "down", "chevron", "star", "home", "book", "people", "search", "grid",
  ];

  private static readonly string[] ObjectGlyphs =
  [
    "home", "search", "grid", "book", "bookmark", "folder", "people", "clock", "link", "copy", "trash", "download",
    "refresh", "list", "up", "down", "back",
  ];

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
  public void Family_KeepsThe24Names()
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
  public void Family_StaysSmall()
  {
    var bytes = Encoding.UTF8.GetByteCount(MapSource());
    Assert.True(bytes < 3500, $"The ICONS map is {bytes} bytes; it must stay under 3,500.");
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
}
