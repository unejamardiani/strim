using System.Text.RegularExpressions;
using Api.Configuration;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// The staging check fails when the migrated database has a column that deploy/sanitise/columns.txt does not list
/// (#575: refreshfailurecount from #570). Every playlist column the PostgreSQL patch adds must be listed there.
/// </summary>
public class SanitiseColumnsCoverageTests
{
  [Fact]
  public void EveryPatchedPostgresPlaylistColumn_IsListedInSanitiseColumns()
  {
    var listed = File.ReadAllLines(Path.Combine(StrimAppFactory.RepositoryRoot(), "deploy", "sanitise", "columns.txt"))
      .Select(line => line.Trim())
      .Where(line => line.Length > 0 && !line.StartsWith('#'))
      .Select(line => line.Split(' ')[0])
      .ToHashSet();

    var added = Regex.Matches(Database.PostgresPlaylistColumnsSql, @"ALTER TABLE playlists ADD COLUMN (\w+)")
      .Select(m => "playlists." + m.Groups[1].Value)
      .ToList();

    Assert.NotEmpty(added);
    Assert.Empty(added.Except(listed));
  }
}
