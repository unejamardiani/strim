using System.Text.RegularExpressions;
using Api.Services;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the README to what Strim does today (#592): its links and architecture paths exist, it names every
/// configuration variable, "What it does" covers the Library, sharing, the player link and the Watchlist group,
/// and the public instance is described as signed-in only.
/// </summary>
public class ReadmeTests
{
  private static readonly string Root = StrimAppFactory.RepositoryRoot();

  private static string Readme() => File.ReadAllText(Path.Combine(Root, "README.md")).ReplaceLineEndings("\n");

  /// <summary>The text of the section that starts with <paramref name="heading"/>, up to the next level-2 heading.</summary>
  private static string Section(string readme, string heading)
  {
    var match = Regex.Match(readme, $@"^## {Regex.Escape(heading)}\n(.*?)(?=^## |\z)", RegexOptions.Singleline | RegexOptions.Multiline);
    Assert.True(match.Success, $"section \"{heading}\" missing");
    return match.Groups[1].Value;
  }

  private static bool Exists(string relativePath)
  {
    var path = relativePath.TrimEnd('/');
    if (path.Contains('*'))
    {
      var directory = Path.Combine(Root, Path.GetDirectoryName(path) ?? "");
      return Directory.Exists(directory) && Directory.GetFiles(directory, Path.GetFileName(path)).Length > 0;
    }
    var full = Path.Combine(Root, path);
    return File.Exists(full) || Directory.Exists(full);
  }

  [Fact]
  public void RelativeLinks_PointToFilesInTheRepository()
  {
    var targets = Regex.Matches(Readme(), @"\]\(([^)\s#]+)[^)]*\)|(?:src|href)=""([^""#]+)""")
      .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
      .Where(t => !t.Contains("://") && !t.StartsWith("mailto:", StringComparison.Ordinal))
      .ToList();

    Assert.NotEmpty(targets);
    Assert.All(targets, t => Assert.True(Exists(t), $"link target {t} does not exist"));
  }

  [Fact]
  public void ArchitectureTable_NamesOnlyExistingPaths()
  {
    var paths = Regex.Matches(Section(Readme(), "Architecture"), @"^\| ([^|]+) \|", RegexOptions.Multiline)
      .SelectMany(row => Regex.Matches(row.Groups[1].Value, "`([^`]+)`").Select(m => m.Groups[1].Value))
      .ToList();

    Assert.Contains("api/Models/", paths);
    Assert.Contains("deploy/sanitise/", paths);
    Assert.Contains("library.js", paths);
    Assert.All(paths, p => Assert.True(Exists(p), $"architecture path {p} does not exist"));
  }

  [Theory]
  [InlineData("DB_PROVIDER")]
  [InlineData("SQLITE_PATH")]
  [InlineData("POSTGRES_CONNECTION")]
  [InlineData("PUBLIC_SHARE_BASE_URL")]
  [InlineData("ALLOWED_ORIGINS")]
  [InlineData("TRUSTED_PROXIES")]
  [InlineData("GOOGLE_CLIENT_ID")]
  [InlineData("MICROSOFT_CLIENT_ID")]
  [InlineData("GITHUB_CLIENT_ID")]
  [InlineData("CF_ACCESS_TEAM_DOMAIN")]
  [InlineData("CF_ACCESS_AUD")]
  [InlineData("DATA_PROTECTION_KEYS_PATH")]
  [InlineData(TmdbClient.TokenVariable)]
  public void Configuration_NamesEachVariable(string variable)
  {
    Assert.Contains($"`{variable}`", Section(Readme(), "Configuration"));
  }

  [Fact]
  public void WhatItDoes_NamesLibrarySharingPlayerLinkAndWatchlistGroup()
  {
    var section = Section(Readme(), "What it does");

    Assert.Contains("**Library**", section);
    Assert.Contains("**Sharing**", section);
    Assert.Contains("**Player link**", section);
    Assert.Contains("Watchlist group", section);
    Assert.Matches(new Regex(@"TMDB key[^.]*cards[^.]*posters"), section);
  }

  [Fact]
  public void PublicInstance_IsNotDescribedAsHavingPublicPages()
  {
    var readme = Readme();

    Assert.DoesNotMatch(new Regex(@"landing\s+page\s+and\s+guides\s+are\s+public"), readme);
    Assert.Matches(new Regex(@"Cloudflare\s+Access\s+sign-in\s+on\s+every\s+page"), Section(readme, "Deployment"));
  }

  [Fact]
  public void MigrationTool_NoteNamesTablesItDoesNotCopyAndWarns()
  {
    var note = Regex.Match(Readme(), @"^\*\*Migration tool:\*\*(.*?)(?=\n\n)", RegexOptions.Singleline | RegexOptions.Multiline);
    Assert.True(note.Success, "migration tool note missing");
    var text = Regex.Replace(note.Groups[1].Value, @"\s+", " ");

    Assert.Matches(new Regex(@"does not copy the tables `titlemarks`[^.]*`playlistshares`"), text);
    Assert.Contains("Do not use it for a database that holds marks, shares or profiles", text);
  }

  [Theory]
  [InlineData("titlemarks")]
  [InlineData("playlistshares")]
  public void MigrationTool_StillDoesNotCopyTable(string table)
  {
    var tool = File.ReadAllText(Path.Combine(Root, "tools", "Strim.DbMigrator", "Program.cs"));

    Assert.False(
      tool.Contains(table, StringComparison.OrdinalIgnoreCase),
      $"tools/Strim.DbMigrator now names {table}: the README's migration tool note is due for a change.");
  }

  [Fact]
  public void RepositoryAddresses_PointToPlisLabs()
  {
    var addresses = Regex.Matches(Readme(), @"github\.com/[^/\s)""]+/strim\b", RegexOptions.IgnoreCase).Select(m => m.Value);

    Assert.All(addresses, a => Assert.Equal("github.com/plis-labs/strim", a));
  }
}
