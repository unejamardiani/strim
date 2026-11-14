using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Api.Tests.Integration;

public class PlaylistProcessingTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private ApiClient NewClient() => new(factory.CreateClient());

  private static int Count(string text, string pattern) => Regex.Matches(text, Regex.Escape(pattern)).Count;

  [Fact]
  public async Task Analyze_SplitsTitlesThatMixLiveAndSeries()
  {
    var analysis = await AnalyzeRawAsync(NewClient());

    Assert.Equal(4, analysis.TotalChannels);
    Assert.Contains(analysis.Groups, g => g is { Name: "Germany · Live", GroupTitle: "Germany", ContentType: "Live", Count: 2 });
    Assert.Contains(analysis.Groups, g => g is { Name: "Germany · Series", GroupTitle: "Germany", ContentType: "Series", Count: 1 });
    Assert.Contains(analysis.Groups, g => g is { Name: "Kids", ContentType: "Live", Count: 1 });
  }

  [Fact]
  public async Task Generate_HidesSplitGroupAndKeepsLiveTitle()
  {
    var client = NewClient();
    var analysis = await AnalyzeRawAsync(client);

    var generated = await GenerateAsync(client, analysis.CacheKey, "Germany · Series");
    Assert.Equal(3, generated.KeptChannels);

    var text = await client.Http.GetStringAsync($"/api/playlist/output/{generated.OutputKey}");
    Assert.Equal(2, Count(text, "group-title=\"Germany\""));
    Assert.DoesNotContain("Tatort", text);
    Assert.Contains("KiKA", text);
  }

  [Fact]
  public async Task Generate_WritesSeriesOfSplitTitleIntoOwnGroup()
  {
    var client = NewClient();
    var analysis = await AnalyzeRawAsync(client);
    var generated = await GenerateAsync(client, analysis.CacheKey);

    var text = await client.Http.GetStringAsync($"/api/playlist/output/{generated.OutputKey}");
    Assert.Equal(1, Count(text, "group-title=\"Germany · Series\""));
    Assert.Equal(2, Count(text, "group-title=\"Germany\""));
  }

  [Fact]
  public async Task Generate_WithOldTitleFilter_HidesWholeTitle()
  {
    var client = NewClient();
    var analysis = await AnalyzeRawAsync(client);
    var generated = await GenerateAsync(client, analysis.CacheKey, "Germany");
    Assert.Equal(1, generated.KeptChannels);
  }

  private const string SearchWordsPlaylist = """
    #EXTM3U
    #EXTINF:-1 tvg-name="Sport One" group-title="DE | Sport",Sport One
    http://provider.example/live/1001.ts
    #EXTINF:-1 tvg-name="Laugh Channel" group-title="Comedy",Laugh Channel
    http://provider.example/live/1002.ts
    #EXTINF:-1 tvg-name="Show Channel" group-title="Series",Show Channel
    http://provider.example/live/1003.ts
    #EXTINF:-1 tvg-name="Loose Channel",Loose Channel
    http://provider.example/live/1004.ts
    """;

  [Fact]
  public async Task Analyze_ReturnsSearchWordsPerGroup_AndKeepsExistingFields()
  {
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { rawText = SearchWordsPlaylist });
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var groups = json.RootElement.GetProperty("groups").EnumerateArray().ToList();

    Assert.Equal(4, json.RootElement.GetProperty("totalChannels").GetInt32());
    Assert.Equal(4, json.RootElement.GetProperty("groupCount").GetInt32());
    Assert.Equal(4, groups.Count);

    void AssertGroup(string name, string[] words)
    {
      var group = Assert.Single(groups, g => g.GetProperty("name").GetString() == name);
      Assert.Equal(name, group.GetProperty("groupTitle").GetString());
      Assert.Equal(1, group.GetProperty("count").GetInt32());
      Assert.Equal("Live", group.GetProperty("contentType").GetString());
      var wordsElement = group.GetProperty("words");
      Assert.Equal(JsonValueKind.Array, wordsElement.ValueKind);
      Assert.Equal(words, wordsElement.EnumerateArray().Select(w => w.GetString()!).ToArray());
    }

    AssertGroup("DE | Sport", ["de", "sport"]);
    AssertGroup("Comedy", ["comedy"]);
    AssertGroup("Series", ["series"]);
    AssertGroup("Ungrouped", []);
  }

  [Fact]
  public async Task Analyze_FromUrl_UsesTheUpstream()
  {
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { sourceUrl = StrimAppFactory.UpstreamUrl });
    var analysis = await response.Content.ReadFromJsonAsync<AnalysisDto>();
    Assert.Equal(4, analysis!.TotalChannels);
  }

  [Fact]
  public async Task Analyze_RejectsPrivateAddresses()
  {
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { sourceUrl = "http://127.0.0.1/list.m3u" });
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  }

  [Fact]
  public async Task Output_WithUnknownKey_IsNotFound()
  {
    var response = await NewClient().Http.GetAsync("/api/playlist/output/out-00000000000000000000000000000000");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task Sample_ReturnsAPlaylist()
  {
    var sample = await NewClient().Http.GetFromJsonAsync<SampleDto>("/api/playlists/sample");
    Assert.StartsWith("#EXTM3U", sample!.Text);
  }

  private static async Task<AnalysisDto> AnalyzeRawAsync(ApiClient client)
  {
    var response = await client.PostAsync("/api/playlist/analyze", new { rawText = TestPlaylists.Mixed });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<AnalysisDto>())!;
  }

  private static async Task<GeneratedDto> GenerateAsync(ApiClient client, string cacheKey, params string[] disabled)
  {
    var response = await client.PostAsync("/api/playlist/generate", new { cacheKey, disabledGroups = disabled });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<GeneratedDto>())!;
  }

  private sealed record GroupDto(string Name, int Count, string GroupTitle, string ContentType);
  private sealed record AnalysisDto(string CacheKey, int TotalChannels, List<GroupDto> Groups);
  private sealed record GeneratedDto(string OutputKey, int TotalChannels, int KeptChannels);
  private sealed record SampleDto(string Text);
}
