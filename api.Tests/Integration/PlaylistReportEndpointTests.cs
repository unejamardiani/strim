using System.Net;
using System.Net.Http.Json;
using System.Text;
using Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
  public async Task Report_ReturnsGroupCounts()
  {
    var response = await NewClient().PostAsync("/api/playlist/report", new { sourceUrl = ReportUpstreamUrl });
    response.EnsureSuccessStatusCode();
    Assert.Contains("\"groupCounts\":", await response.Content.ReadAsStringAsync());
    var report = (await response.Content.ReadFromJsonAsync<ReportDto>())!;

    Assert.Equal(
      new[]
      {
        new GroupCountDto("Germany", 1, 2),
        new GroupCountDto("Germany Backup", 1, 1),
        new GroupCountDto("HD Channels", 0, 1),
        new GroupCountDto("Kids", 1, 0),
        new GroupCountDto("Kids Extra", 1, 0),
      },
      report.GroupCounts);
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
  public async Task Report_RefusesPlaylistOverMemoryBudget()
  {
    // The six fixture entries take roughly 3 KB by the report's estimate; a 1 KB budget is passed within the first few.
    using var own = new StrimAppFactory();
    using var small = own.WithWebHostBuilder(builder => builder.UseSetting("PlaylistCache:MaxReportBytes", "1024"));
    var client = new ApiClient(small.CreateClient());
    var analyzed = await client.PostAsync("/api/playlist/analyze", new { rawText = Fixture });
    analyzed.EnsureSuccessStatusCode();
    var cacheKey = (await analyzed.Content.ReadFromJsonAsync<AnalysisDto>())!.CacheKey;

    var response = await client.PostAsync("/api/playlist/report", new { cacheKey });

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains("too large for a duplicates and variants report", await response.Content.ReadAsStringAsync());
  }

  private const string LargeUpstreamUrl = "http://93.184.215.14/get.php?username=u&password=p&type=huge";
  private const int LargeChannels = 300_000;
  private const int LargePairs = LargeChannels / 1000;

  /// <summary>
  /// 300,300 entries: 300,000 channels with their own tvg-id and address; every 1000th channel also has an SD entry
  /// that shares its tvg-id, so it forms one duplicate set and one variant group.
  /// </summary>
  private static string LargePlaylist(int padding = 0)
  {
    var pad = padding > 0 ? $" x-note=\"{new string('n', padding)}\"" : "";
    var text = new StringBuilder("#EXTM3U\n", 40 * 1024 * 1024);
    for (var i = 0; i < LargeChannels; i++)
    {
      var paired = i % 1000 == 0;
      text.Append($"#EXTINF:-1 tvg-id=\"c{i}\"{pad} group-title=\"Group {i % 40}\",Channel {i}{(paired ? " HD" : "")}\nhttp://line.example/live/u/p/{i}.ts\n");
      if (paired) text.Append($"#EXTINF:-1 tvg-id=\"c{i}\" group-title=\"Backup\",Channel {i} SD\nhttp://line.example/live/u/p/{i}b.ts\n");
    }
    return text.ToString();
  }

  [Fact]
  public async Task Report_LargePlaylist_IsReportedWithDefaultBudget()
  {
    using var own = new StrimAppFactory();
    own.UpstreamPlaylists[LargeUpstreamUrl] = LargePlaylist();
    Assert.Equal(128L * 1024 * 1024, own.Services.GetRequiredService<PlaylistFileCache>().MaxReportBytes);

    var response = await new ApiClient(own.CreateClient()).PostAsync("/api/playlist/report", new { sourceUrl = LargeUpstreamUrl });
    var body = await response.Content.ReadAsStringAsync();

    Assert.True(response.StatusCode == HttpStatusCode.OK, body);
    var report = (await response.Content.ReadFromJsonAsync<ReportDto>())!;
    Assert.Equal(LargeChannels + LargePairs, report.TotalEntries);
    Assert.Equal(LargePairs, report.DuplicateSetCount);
    Assert.Equal(2 * LargePairs, report.DuplicateEntryCount);
    Assert.Equal(LargePairs, report.VariantGroupCount);
    Assert.Equal(2 * LargePairs, report.VariantEntryCount);
    Assert.False(report.Truncated);
    Assert.Equal(LargePairs, report.DuplicateSets.Count);
    Assert.Equal(LargePairs, report.VariantGroups.Count);
    var last = report.DuplicateSets[^1];
    Assert.Equal(new[] { "Channel 299000 HD", "Channel 299000 SD" }, last.Entries.Select(e => e.Name));
    Assert.Equal(new[] { "Group 0", "Backup" }, last.Groups);

    // Neither the entries' addresses nor the source address and its credentials reach the response or the log
    // (the fetcher logs the bare host on purpose).
    Assert.Contains(own.LogEntries, entry => entry.Contains("Downloaded playlist source", StringComparison.Ordinal));
    foreach (var address in new[] { "line.example", LargeUpstreamUrl, "get.php", "password=" })
    {
      Assert.DoesNotContain(address, body, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain(own.LogEntries, entry => entry.Contains(address, StringComparison.OrdinalIgnoreCase));
    }
  }

  [Fact]
  public async Task Report_SecondRequestDuringRunningReport_GetsBusyAnswer()
  {
    using var own = new StrimAppFactory();
    using var app = own.WithWebHostBuilder(builder => builder.UseSetting("PlaylistCache:QueueTimeoutSeconds", "1"));
    // Padded entries make each of the report's passes slow enough to outlast the second request's 1 s queue wait.
    own.UpstreamPlaylists[LargeUpstreamUrl] = LargePlaylist(padding: 300);
    var client = new ApiClient(app.CreateClient());

    var first = client.PostAsync("/api/playlist/report", new { sourceUrl = LargeUpstreamUrl });
    // The upstream is fetched inside the job gate, so once it is asked the first report holds the gate.
    while (!own.UpstreamRequests.Contains(LargeUpstreamUrl) && !first.IsCompleted) await Task.Delay(5);
    var second = await client.PostAsync("/api/playlist/report", new { sourceUrl = ReportUpstreamUrl });

    Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    Assert.Contains("busy", await second.Content.ReadAsStringAsync());
    Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
  }

  [Fact]
  public async Task ReadEntriesFromFile_KeepsEntriesWithinBudget_RefusesOneByteLess()
  {
    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, Fixture);
      var budget = (await PlaylistProcessor.ReadEntriesFromFileAsync(path, long.MaxValue, CancellationToken.None)).Sum(PlaylistProcessor.EstimatedEntryBytes);

      Assert.Equal(6, (await PlaylistProcessor.ReadEntriesFromFileAsync(path, budget, CancellationToken.None)).Count);
      await Assert.ThrowsAsync<PlaylistReportTooLargeException>(
        () => PlaylistProcessor.ReadEntriesFromFileAsync(path, budget - 1, CancellationToken.None));
    }
    finally
    {
      File.Delete(path);
    }
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
    List<VariantGroupDto> VariantGroups,
    List<GroupCountDto>? GroupCounts);
  private sealed record GroupCountDto(string Group, int DuplicateEntryCount, int VariantEntryCount);
}
