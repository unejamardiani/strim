using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>The read-only, paged list of one group's entries (story #215).</summary>
public class GroupEntriesEndpointTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private const string SessionExpired = "The playlist session expired. Analyze the source again.";

  private const string WithCredentials = """
    #EXTM3U
    #EXTINF:-1 tvg-name="ARD" group-title="Germany",ARD HD
    http://example.invalid/user/pass/1.ts
    #EXTINF:-1 group-title="Kids",KiKA
    http://example.invalid/user/pass/2.ts
    #EXTINF:-1 tvg-id="zdf.de" group-title="Germany",ZDF HD
    http://example.invalid/user/pass/3.ts
    #EXTINF:-1 group-title="Germany",Das Boot (1981)
    http://example.invalid/user/pass/4.mp4
    """;

  [Fact]
  public async Task ListsDisplayNamesInPlaylistOrderWithTotal()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, TestPlaylists.Mixed);

    var result = await EntriesOkAsync(client, cacheKey, "Germany · Live");

    Assert.Equal(["ARD", "ZDF"], result.Names);
    Assert.Equal(2, result.Total);
    Assert.Equal(0, result.Offset);
    Assert.Equal(50, result.Limit);
  }

  [Fact]
  public async Task ReturnsTheRequestedPageAndClampsTheLimitToTheSearchPageSize()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, Generated("News", 120));

    var second = await EntriesOkAsync(client, cacheKey, "News", "&offset=50&limit=50");
    Assert.Equal(Enumerable.Range(51, 50).Select(i => $"News {i}"), second.Names);
    Assert.Equal(120, second.Total);

    var last = await EntriesOkAsync(client, cacheKey, "News", "&offset=100&limit=50");
    Assert.Equal(Enumerable.Range(101, 20).Select(i => $"News {i}"), last.Names);

    var clamped = await EntriesOkAsync(client, cacheKey, "News", "&limit=1000");
    Assert.Equal(50, clamped.Limit);
    Assert.Equal(50, clamped.Names.Count);

    var past = await EntriesOkAsync(client, cacheKey, "News", "&offset=500");
    Assert.Empty(past.Names);
    Assert.Equal(120, past.Total);
  }

  [Fact]
  public async Task LargeGroup_ReturnsOnlyTheFirstPage()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, Generated("Sports", 5_000));

    var result = await EntriesOkAsync(client, cacheKey, "Sports", "&limit=20");

    Assert.Equal(Enumerable.Range(1, 20).Select(i => $"Sports {i}"), result.Names);
    Assert.Equal(5_000, result.Total);
  }

  [Fact]
  public async Task UnknownGroup_ReturnsEmptyListLikeASearchWithoutMatches()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, TestPlaylists.Mixed);

    var result = await EntriesOkAsync(client, cacheKey, "Nowhere");

    Assert.Empty(result.Names);
    Assert.Equal(0, result.Total);
  }

  [Fact]
  public async Task UnknownOrForeignSession_GetsTheSameStatusAsTheSearch()
  {
    var owner = new ApiClient(factory.CreateClient());
    await owner.RegisterAsync("groupentries-owner");
    var cacheKey = await AnalyzeAsync(owner, TestPlaylists.Mixed);
    var stranger = new ApiClient(factory.CreateClient());

    foreach (var key in new[] { "pl-unknown", cacheKey })
    {
      var search = await stranger.PostAsync("/api/playlist/search", new { cacheKey = key, query = "ard" });
      var entries = await stranger.Http.GetAsync(EntriesPath(key, "Germany · Live"));
      Assert.Equal(search.StatusCode, entries.StatusCode);
    }

    var expired = await stranger.Http.GetAsync(EntriesPath("pl-unknown", "Germany · Live"));
    Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
    Assert.Equal(SessionExpired, (await expired.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
  }

  [Fact]
  public async Task RejectsInvalidInput()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, TestPlaylists.Mixed);

    string[] invalid =
    [
      "/api/playlist/group-entries?group=Germany",
      EntriesPath(cacheKey, " "),
      EntriesPath(cacheKey, new string('x', 5_000)),
      EntriesPath(cacheKey, "Germany") + "&offset=-1",
      EntriesPath(cacheKey, "Germany") + "&limit=0",
    ];
    foreach (var path in invalid)
    {
      var response = await client.Http.GetAsync(path);
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
  }

  [Fact]
  public async Task IndexUnavailable_ReturnsTheSearchError()
  {
    using var own = new StrimAppFactory();
    using var noIndex = own.WithWebHostBuilder(builder => builder.UseSetting("PlaylistCache:MaxSearchIndexBytes", "0"));
    var client = new ApiClient(noIndex.CreateClient());
    var cacheKey = await AnalyzeAsync(client, TestPlaylists.Mixed);

    var response = await client.Http.GetAsync(EntriesPath(cacheKey, "Germany · Live"));

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains("not available", (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
  }

  [Fact]
  public async Task NeverReturnsOrLogsStreamUrlsOrCredentials()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, WithCredentials);
    var logsBefore = factory.LogEntries.Count;

    // A plain GET without a CSRF token: the endpoint only reads.
    var response = await client.Http.GetAsync(EntriesPath(cacheKey, "Germany"));
    var body = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var result = (await response.Content.ReadFromJsonAsync<EntriesDto>())!;
    Assert.Equal(["ARD HD", "ZDF HD", "Das Boot (1981)"], result.Names);
    foreach (var secret in new[] { "example.invalid", "http", ".ts", "user", "pass" })
    {
      Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
    }
    var logs = factory.LogEntries.Skip(logsBefore).ToList();
    Assert.DoesNotContain(logs, entry => entry.Contains("example.invalid", StringComparison.OrdinalIgnoreCase)
      || entry.Contains("/user/pass", StringComparison.OrdinalIgnoreCase));
  }

  private static string Generated(string group, int count)
  {
    var playlist = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= count; i++)
    {
      playlist.Append($"#EXTINF:-1 group-title=\"{group}\",{group} {i}\nhttp://line.example/live/u/p/{i}.ts\n");
      playlist.Append($"#EXTINF:-1 group-title=\"Other\",Other {i}\nhttp://line.example/live/u/p/o{i}.ts\n");
    }
    return playlist.ToString();
  }

  private static string EntriesPath(string cacheKey, string group) =>
    $"/api/playlist/group-entries?cacheKey={Uri.EscapeDataString(cacheKey)}&group={Uri.EscapeDataString(group)}";

  private static async Task<string> AnalyzeAsync(ApiClient client, string rawText)
  {
    var response = await client.PostAsync("/api/playlist/analyze", new { rawText });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<AnalysisDto>())!.CacheKey;
  }

  private static async Task<EntriesDto> EntriesOkAsync(ApiClient client, string cacheKey, string group, string query = "")
  {
    var response = await client.Http.GetAsync(EntriesPath(cacheKey, group) + query);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<EntriesDto>())!;
  }

  private sealed record AnalysisDto(string CacheKey);
  private sealed record EntriesDto(string GroupKey, int Offset, int Limit, List<string> Names, int Total);
  private sealed record ErrorDto(string Error);
}
