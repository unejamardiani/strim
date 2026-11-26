using System.Text.RegularExpressions;
using Api.Configuration;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// The staging check fails when the migrated database has a column that deploy/sanitise/columns.txt does not list
/// (#575: refreshfailurecount from #570; #589: the AspNetUsers profile columns from #579). Every column that a
/// PostgreSQL patch in Database.cs adds, for every patched table, must be listed there.
/// </summary>
public class SanitiseColumnsCoverageTests
{
  /// <summary>Quoted or unquoted identifiers, with or without IF NOT EXISTS; the exact case is kept.</summary>
  private static readonly Regex AddColumn = new(
    @"ALTER TABLE ""?(\w+)""? ADD COLUMN (?:IF NOT EXISTS )?""?(\w+)""?", RegexOptions.IgnoreCase);

  public static TheoryData<string, string> PatchedTables => new()
  {
    { "playlists", Database.PostgresPlaylistColumnsSql },
    { "AspNetUsers", Database.PostgresUserProfileColumnsSql },
    { "titlemarks", Database.PostgresTitleMarksSql },
  };

  [Theory]
  [MemberData(nameof(PatchedTables))]
  public void EveryPatchedPostgresColumn_IsListedInSanitiseColumns(string table, string patchSql)
  {
    var listed = File.ReadAllLines(Path.Combine(StrimAppFactory.RepositoryRoot(), "deploy", "sanitise", "columns.txt"))
      .Select(line => line.Trim())
      .Where(line => line.Length > 0 && !line.StartsWith('#'))
      .Select(line => line.Split(' ')[0])
      .ToHashSet();

    var added = AddColumn.Matches(patchSql)
      .Select(m => m.Groups[1].Value + "." + m.Groups[2].Value)
      .ToList();

    Assert.Contains(added, column => column.StartsWith(table + ".", StringComparison.Ordinal));
    Assert.Empty(added.Except(listed));
  }
}
