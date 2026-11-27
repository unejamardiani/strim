using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the "File structure" block of DESIGN.md to the front-end files that exist (#607): it names every page,
/// stylesheet and script of the repository root, everything it names exists, and the landing page is not called public.
/// </summary>
public class DesignDocTests
{
  private static readonly string Root = StrimAppFactory.RepositoryRoot();

  /// <summary>The code block under "## File structure".</summary>
  private static string FileStructureBlock()
  {
    var design = File.ReadAllText(Path.Combine(Root, "DESIGN.md")).ReplaceLineEndings("\n");
    var match = Regex.Match(design, @"^## File structure\n+```\n(.*?)^```", RegexOptions.Singleline | RegexOptions.Multiline);
    Assert.True(match.Success, "\"File structure\" block missing");
    return match.Groups[1].Value;
  }

  /// <summary>The names in the block's first column: everything before the description, split at commas.</summary>
  private static List<string> NamedPaths() =>
    FileStructureBlock()
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => Regex.Split(line, @"\s{2,}")[0])
      .SelectMany(names => names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      .ToList();

  [Fact]
  public void FileStructure_NamesEveryFrontEndFileOfTheRepositoryRoot()
  {
    var named = NamedPaths();
    var missing = Directory.GetFiles(Root)
      .Select(Path.GetFileName)
      .Where(name => name!.EndsWith(".html") || name.EndsWith(".css") || name.EndsWith(".js"))
      .Where(name => !named.Contains(name!))
      .ToList();
    Assert.True(missing.Count == 0, $"not listed in \"File structure\": {string.Join(", ", missing)}");
  }

  [Fact]
  public void FileStructure_NamesOnlyFilesAndFoldersThatExist()
  {
    var absent = NamedPaths()
      .Where(path => !File.Exists(Path.Combine(Root, path)) && !Directory.Exists(Path.Combine(Root, path.TrimEnd('/'))))
      .ToList();
    Assert.True(absent.Count == 0, $"named in \"File structure\" but missing: {string.Join(", ", absent)}");
  }

  [Fact]
  public void FileStructure_DoesNotCallTheLandingPagePublic()
  {
    Assert.DoesNotContain("(public)", FileStructureBlock());
  }
}
