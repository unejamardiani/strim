using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit;

namespace Api.Tests.Integration;

public class PlaylistReportEndpointTests : IClassFixture<StrimAppFactory>
{
  private const string ReportUpstreamUrl = "http://93.184.215.14/get.php?username=u&password=p&type=report";

  // Duplicates: ARD HD/ARD SD share a tvg-id, KiKA/KiKA Copy share an address.
  // Variants: ARD HD/ARD SD and ZDF/ZDF HD.
  private const string Fixture = """
    #EXTM3U
    #EXTINF:-1 tvg-id="ard.de" group-title="Germany",ARD HD
    http://line.example/live/u/p/1.ts
    #EXTINF:-1 tvg-id="ard.de" group-title="Germany Backup",ARD SD
    http://line.example/live/u/p/2.ts
    #EXTINF:-1 group-title="Germany",ZDF
    http://line.example/live/u/p/3.ts
    #EXTINF:-1 group-title="HD Channels",ZDF HD
    http://line.example/live/u/p/4.ts
    #EXTINF:-1 group-title="Kids",KiKA
    http://line.example/live/u/p/5.ts
    #EXTINF:-1 group-title="Kids Extra",KiKA Copy
    http://line.example/live/u/p/5.ts
    """;

  private readonly StrimAppFactory _factory;

  public PlaylistReportEndpointTests(StrimAppFactory factory)
  {
    _factory = factory;
    _factory.UpstreamPlaylists[ReportUpstreamUrl] = Fixture;
  }

  private ApiClient NewClient() => new(_factory.CreateClient());

  private static async Task<ReportDto> ReportAsync(ApiClient client, object body)
  {
    var response = await client.PostAsync("/api/playlist/report", body);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<ReportDto>())!;
  }

  [Fact]
  public async Task Report_ReturnsDuplicateAndVariantCounts()
  {
    var report = await ReportAsync(NewClient(), new { sourceUrl = ReportUpstreamUrl });

    Assert.Equal(6, report.TotalEntries);
    Assert.Equal(2, report.DuplicateSetCount);
    Assert.Equal(4, report.DuplicateEntryCount);
    Assert.Equal(2, report.VariantGroupCount);
    Assert.Equal(4, report.VariantEntryCount);
    Assert.False(report.Truncated);
    Assert.Equal(2, report.DuplicateSets.Count);
    Assert.Equal(2, report.VariantGroups.Count);
  }

  [Fact]
  public async Task Report_ListsGroupsPerSet()
  {
    var client = NewClient();
    var analyzed = await client.PostAsync("/api/playlist/analyze", new { rawText = Fixture });
    analyzed.EnsureSuccessStatusCode();
    var cacheKey = (await analyzed.Content.ReadFromJsonAsync<AnalysisDto>())!.CacheKey;

    var report = await ReportAsync(client, new { cacheKey });

    var ard = report.DuplicateSets[0];
    Assert.Equal(new[] { "Germany", "Germany Backup" }, ard.Groups);
    Assert.Equal(new[] { "ARD HD", "ARD SD" }, ard.Entries.Select(e => e.Name));
    Assert.Equal(new[] { "HD", "SD" }, ard.Entries.Select(e => Assert.Single(e.QualityWords)));
    Assert.Equal(new[] { "Kids", "Kids Extra" }, report.DuplicateSets[1].Groups);

    var zdf = Assert.Single(report.VariantGroups, g => g.BaseName == "ZDF");
    Assert.Equal(new[] { "Germany", "HD Channels" }, zdf.Groups);
    Assert.Equal(new[] { "Germany", "HD Channels" }, zdf.Entries.Select(e => e.Group));
    Assert.Empty(zdf.Entries[0].QualityWords);
    Assert.Equal(new[] { "HD" }, zdf.Entries[1].QualityWords);
    Assert.Equal(new[] { "Germany", "Germany Backup" }, Assert.Single(report.VariantGroups, g => g.BaseName == "ARD").Groups);
  }

  [Fact]
  public async Task Report_CapsListedSets_KeepsFullCounts_SetsTruncated()
  {
    // 501 channels with an HD and an SD entry sharing a tvg-id: 501 duplicate sets and 501 variant groups.
    var text = new StringBuilder("#EXTM3U\n");
    for (var i = 0; i < 501; i++)
    {
      text.Append($"#EXTINF:-1 tvg-id=\"c{i}\" group-title=\"Group {i % 7}\",Channel {i} HD\nhttp://line.example/live/u/p/{i}a.ts\n");
      text.Append($"#EXTINF:-1 tvg-id=\"c{i}\" group-title=\"Group {i % 5}\",Channel {i} SD\nhttp://line.example/live/u/p/{i}b.ts\n");
    }
    const string url = "http://93.184.215.14/get.php?username=u&password=p&type=large";
    _factory.UpstreamPlaylists[url] = text.ToString();

    var report = await ReportAsync(NewClient(), new { sourceUrl = url });

    Assert.Equal(1002, report.TotalEntries);
    Assert.Equal(501, report.DuplicateSetCount);
    Assert.Equal(1002, report.DuplicateEntryCount);
    Assert.Equal(501, report.VariantGroupCount);
    Assert.Equal(1002, report.VariantEntryCount);
    Assert.True(report.Truncated);
    Assert.Equal(500, report.DuplicateSets.Count);
    Assert.Equal(500, report.VariantGroups.Count);
  }

  [Fact]
  public async Task Report_DoesNotChangeStoredPlaylistOrSelections()
  {
    var owner = NewClient();
    await owner.RegisterAsync("report-owner");
    var created = await owner.PostAsync("/api/playlists", new
    {
      name = "Living room",
      sourceUrl = ReportUpstreamUrl,
      disabledGroups = new[] { "Kids Extra", "HD Channels" },
    });
    created.EnsureSuccessStatusCode();
    var playlist = (await created.Content.ReadFromJsonAsync<PlaylistDto>())!;
    var sharePath = $"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}";

    var playlistBefore = await PlaylistStateAsync(owner, playlist.Id);
    var downloadBefore = await _factory.CreateClient().GetByteArrayAsync(sharePath);

    await ReportAsync(owner, new { sourceUrl = ReportUpstreamUrl });

    var playlistAfter = await PlaylistStateAsync(owner, playlist.Id);
    var downloadAfter = await _factory.CreateClient().GetByteArrayAsync(sharePath);
    Assert.Equal(playlistBefore, playlistAfter);
    Assert.Equal(downloadBefore, downloadAfter);
    Assert.DoesNotContain("KiKA Copy", Encoding.UTF8.GetString(downloadAfter));
  }

  [Fact]
  public async Task Report_RejectsRequestWithoutCsrfToken()
  {
    var response = await NewClient().PostAsync("/api/playlist/report", new { sourceUrl = ReportUpstreamUrl }, withCsrf: false);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains("CSRF", await response.Content.ReadAsStringAsync());
  }

  /// <summary>The stored playlist without its view counters, which the share download itself updates.</summary>
  private static async Task<string> PlaylistStateAsync(ApiClient client, Guid id)
  {
    var stored = (await client.Http.GetFromJsonAsync<PlaylistDto>($"/api/playlists/{id}"))!;
    return string.Join("|", stored.Name, stored.SourceUrl, stored.ShareCode, stored.IsActive, stored.AutoRefreshEnabled,string.Join(",", stored.DisabledGroups));
  }

  private sealed record AnalysisDto(string CacheKey);
  private sealed record EntryDto(string Name, string Group, List<string> QualityWords);
  private sealed record DuplicateSetDto(List<string> Groups, List<EntryDto> Entries);
  private sealed record VariantGroupDto(string BaseName, List<string> Groups, List<EntryDto> Entries);
  private sealed record ReportDto(
    int TotalEntries,
    int DuplicateSetCount,
    int DuplicateEntryCount,
    int VariantGroupCount,
    int VariantEntryCount,
    bool Truncated,
    List<DuplicateSetDto> DuplicateSets,
    List<VariantGroupDto> VariantGroups);
}
