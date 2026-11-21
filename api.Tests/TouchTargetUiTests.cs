using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the invisible 44px phone touch targets of the small controls (#490): drawn sizes stay, ::after adds the target.</summary>
public class TouchTargetUiTests
{
  private const string PhoneQuery = "@media (max-width: 767px) {";

  private static readonly (string Selector, string Inset)[] EditorTargets =
  [
    (".link-box .btn", "-3px 0"),
    (".pills button", "-4px 0"),
    (".bulk .btn", "-6px 0"),
    (".account", "-2px 0"),
    (".playlist-title a", "-7px 0"),
    (".site-links a", "-6px -4px"),
    (".sharing-line", "-12px -8px"),
    (".adult-switch", "-11px 0 -2px"),
  ];

  private static string Read(string file) =>
    File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  /// <summary>The bodies of every phone-only media block (nesting one level of rules deep).</summary>
  private static List<string> PhoneBlocks(string css)
  {
    var blocks = new List<string>();
    var at = 0;
    while ((at = css.IndexOf(PhoneQuery, at, StringComparison.Ordinal)) >= 0)
    {
      var start = at + PhoneQuery.Length;
      var depth = 1;
      var i = start;
      for (; i < css.Length && depth > 0; i++)
      {
        if (css[i] == '{') depth++;
        else if (css[i] == '}') depth--;
      }
      blocks.Add(css[start..(i - 1)]);
      at = i;
    }
    return blocks;
  }

  private static string AfterRule(string selector, string inset) =>
    $"{selector}::after {{ content: \"\"; position: absolute; inset: {inset}; }}";

  private static bool HasPositionRelative(string block, string selector)
  {
    foreach (Match m in Regex.Matches(block, @"([^{}]+)\{([^}]*)\}", RegexOptions.CultureInvariant))
    {
      var selectors = m.Groups[1].Value.Split(',').Select(s => s.Trim());
      if (selectors.Contains(selector) && m.Groups[2].Value.Contains("position: relative;"))
        return true;
    }
    return false;
  }

  [Fact]
  public void SectionLinks_Get44pxOnPhones()
  {
    var blocks = PhoneBlocks(Read("library.css"));
    Assert.Contains(blocks, b =>
      b.Contains(".lib-sec-head a, .lib-sec-head button { position: relative; }") &&
      b.Contains(".lib-sec-head a::after, .lib-sec-head button::after { content: \"\"; position: absolute; inset: -19px -14px -8px; }"));
  }

  [Fact]
  public void SmallButtons_Get44pxOnPhones()
  {
    var blocks = PhoneBlocks(Read("theme.css"));
    Assert.Contains(blocks, b =>
      b.Contains(".btn-sm { position: relative; }") && b.Contains(AfterRule(".btn-sm", "-3px 0")));
  }

  [Fact]
  public void EditorAndCardControls_Get44pxOnPhones()
  {
    var blocks = PhoneBlocks(Read("app.css"));
    var block = blocks.SingleOrDefault(b => b.Contains(AfterRule(".pills button", "-4px 0")));
    Assert.NotNull(block);
    foreach (var (selector, inset) in EditorTargets)
    {
      Assert.Contains(AfterRule(selector, inset), block);
      Assert.True(HasPositionRelative(block, selector), $"'{selector}' has no position: relative in the phone block.");
    }
  }

  [Fact]
  public void Targets_AreNotDrawn()
  {
    var rules = new List<string>();
    foreach (var file in new[] { "library.css", "theme.css", "app.css" })
      foreach (var block in PhoneBlocks(Read(file)))
        rules.AddRange(Regex.Matches(block, @"[^{}]+::after \{[^}]*\}").Select(m => m.Value.Trim()));

    var targets = EditorTargets.Select(t => t.Selector).Append(".btn-sm").Append(".lib-sec-head a").ToArray();
    var newRules = rules.Where(r => targets.Any(t => r.StartsWith(t + "::after", StringComparison.Ordinal))).ToList();
    Assert.Equal(targets.Length, newRules.Count);
    foreach (var rule in newRules)
    {
      Assert.DoesNotContain("background", rule);
      Assert.DoesNotContain("border", rule);
      Assert.DoesNotContain("box-shadow", rule);
    }
  }
}
