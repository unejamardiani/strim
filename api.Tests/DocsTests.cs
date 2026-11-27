using System.Text.RegularExpressions;
using Api.Services;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Keeps docs/DATABASE_SCHEMA.md and docs/SECURITY.md in step with the code (#606): every documented column of
/// deploy/sanitise/columns.txt, the controls the code has, and the code paths the documents name.
/// </summary>
public class DocsTests
{
  private const string SchemaDoc = "docs/DATABASE_SCHEMA.md";
  private const string SecurityDoc = "docs/SECURITY.md";

  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  public static TheoryData<string> DocumentedColumns()
  {
    var profile = new[] { "AspNetUsers.avatar", "AspNetUsers.display_name", "AspNetUsers.theme" };
    var data = new TheoryData<string>();
    foreach (var line in Read("deploy/sanitise/columns.txt").Split('\n'))
    {
      var entry = line.Trim().Split(' ')[0];
      if (entry.StartsWith("playlists.") || entry.StartsWith("titlemarks.") || entry.StartsWith("playlistshares.")
          || profile.Contains(entry))
      {
        data.Add(entry);
      }
    }
    return data;
  }

  [Theory]
  [MemberData(nameof(DocumentedColumns))]
  public void SchemaDocumentNamesEveryColumn(string entry)
  {
    var column = entry[(entry.IndexOf('.') + 1)..];
    Assert.Contains($"`{column}`", Read(SchemaDoc));
  }

  [Theory]
  [InlineData("Nothing reads the value yet")]
  [InlineData("dotnet ef")]
  [InlineData("Recommended: EF Core Migrations")]
  public void SchemaDocumentDropsOutdatedStatements(string text) =>
    Assert.DoesNotContain(text, Read(SchemaDoc));

  [Theory]
  [InlineData("nosniff")]
  [InlineData("Referrer-Policy")]
  [InlineData("frame-ancestors")]
  [InlineData("no-store")]
  [InlineData("SHA-256")]
  [InlineData(TmdbClient.TokenVariable)]
  public void SecurityDocumentNamesTheControls(string text) =>
    Assert.Contains(text, Read(SecurityDoc));

  [Fact]
  public void SecurityDocumentDescribesThePublicInstanceAsItIs()
  {
    var doc = Regex.Replace(Read(SecurityDoc), @"\s+", " ");
    Assert.DoesNotContain("sit behind Cloudflare Access except the share route", doc);
    Assert.Contains("only player links are reachable without it", doc);
  }

  [Theory]
  [InlineData(SchemaDoc)]
  [InlineData(SecurityDoc)]
  public void NamedCodePathsExist(string doc)
  {
    var paths = Regex.Matches(Read(doc), @"\bapi(?:\.Tests)?/[A-Za-z0-9_/.]+?\.cs\b")
      .Select(m => m.Value)
      .Distinct()
      .ToList();
    Assert.NotEmpty(paths);
    var root = StrimAppFactory.RepositoryRoot();
    Assert.All(paths, path => Assert.True(File.Exists(Path.Combine(root, path)), $"{doc} names {path}, which does not exist."));
  }
}
