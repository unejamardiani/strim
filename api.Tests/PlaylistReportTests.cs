using System.Text.Json;
using Api.Models;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistReportTests
{
  [Fact]
  public async Task GroupCounts_CountAllFindingsEvenWhenListTruncated()
  {
    // Three duplicate sets (two entries each, shared tvg-id) and three variant groups (HD and plain) in "News".
    var entries = new List<PlaylistEntry>();
    for (var i = 0; i < 3; i++)
    {
      Add(entries, $"News {i} HD", "News", tvgId: $"news{i}");
      Add(entries, $"News {i}", "News", tvgId: $"news{i}");
    }

    var report = await BuildBothAsync(entries, maxListed: 1);

    Assert.True(report.Truncated);
    Assert.Single(report.DuplicateSets);
    Assert.Single(report.VariantGroups);
    var news = Assert.Single(report.GroupCounts);
    Assert.Equal(new ReportGroupCount("News", 6, 6), news);
  }

  [Fact]
  public async Task GroupCounts_OmitGroupsWithoutFindings()
  {
    var entries = new List<PlaylistEntry>();
    Add(entries, "Sport", "Sport", tvgId: "sport");
    Add(entries, "Arena", "Sport", tvgId: "sport");
    Add(entries, "Weather", "Other");
    // "Mixed" holds live channels and a film, so the analysis splits it by content type.
    Add(entries, "Music", "Mixed", tvgId: "music");
    Add(entries, "Jukebox", "Mixed", tvgId: "music");
    Add(entries, "A Film", "Mixed", path: "/movie/u/p/1.mkv");

    var report = await BuildBothAsync(entries);

    Assert.Equal(
      new[] { new ReportGroupCount("Sport", 2, 0), new ReportGroupCount("Mixed · Live", 2, 0) },
      report.GroupCounts);
  }

  [Fact]
  public async Task GroupCounts_CountCrossGroupDuplicatesPerGroup()
  {
    var entries = new List<PlaylistEntry>();
    Add(entries, "Cinema", "Germany", tvgId: "cinema");
    Add(entries, "Kino", "Germany", tvgId: "cinema");
    Add(entries, "Film Palace", "Germany Backup", tvgId: "cinema");

    var report = await BuildBothAsync(entries);

    var set = Assert.Single(report.DuplicateSets);
    Assert.Equal(new[] { "Germany", "Germany Backup" }, set.Groups);
    Assert.Equal(
      new[] { new ReportGroupCount("Germany", 2, 0), new ReportGroupCount("Germany Backup", 1, 0) },
      report.GroupCounts);
  }

  private static void Add(List<PlaylistEntry> entries, string name, string group, string? tvgId = null, string? path = null) =>
    entries.Add(new PlaylistEntry(
      entries.Count, name, group, tvgId, $"http://example.invalid{path ?? $"/live/u/p/{entries.Count}.ts"}"));

  /// <summary>Builds the report in memory and through the multi-pass builder and checks both agree.</summary>
  private static async Task<PlaylistReportResponse> BuildBothAsync(List<PlaylistEntry> entries, int maxListed = 500)
  {
    var inMemory = PlaylistReport.Build(entries, maxListed);
    var streamed = await PlaylistReport.BuildStreamingAsync(
      _ => Read(entries), long.MaxValue, maxListed, CancellationToken.None);
    Assert.Equal(JsonSerializer.Serialize(inMemory), JsonSerializer.Serialize(streamed));
    return streamed;
  }

  private static async IAsyncEnumerable<PlaylistEntry> Read(List<PlaylistEntry> entries)
  {
    foreach (var entry in entries)
    {
      await Task.Yield();
      yield return entry;
    }
  }
}
