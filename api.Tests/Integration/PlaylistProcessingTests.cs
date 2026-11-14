using System.Net;
using System.Net.Http.Json;
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

  private const string AdultMix = """
    #EXTM3U
    #EXTINF:-1 tvg-name="Daily Brief" group-title="News",Daily Brief
    https://example.com/news.m3u8
    #EXTINF:-1 tvg-name="Late Show A" group-title="XXX",Late Show A
    https://example.com/late-a.m3u8
    #EXTINF:-1 tvg-name="Late Show B" group-title="For Adults",Late Show B
    https://example.com/late-b.m3u8
    """;

  [Fact]
  public async Task Analyze_ReturnsIsAdultPerGroup()
  {
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { rawText = AdultMix });
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

    var flags = json.GetProperty("groups").EnumerateArray()
      .ToDictionary(g => g.GetProperty("name").GetString()!, g => g.GetProperty("isAdult").GetBoolean());

    Assert.Equal(3, flags.Count);
    Assert.False(flags["News"]);
    Assert.True(flags["XXX"]);
    Assert.True(flags["For Adults"]);
  }

  [Fact]
  public async Task Generate_IgnoresAdultFlag()
  {
    var client = NewClient();
    var response = await client.PostAsync("/api/playlist/analyze", new { rawText = AdultMix });
    response.EnsureSuccessStatusCode();
    var analysis = (await response.Content.ReadFromJsonAsync<AnalysisDto>())!;

    var none = await GenerateAsync(client, analysis.CacheKey);
    Assert.Equal(3, none.KeptChannels);
    var noneText = await client.Http.GetStringAsync($"/api/playlist/output/{none.OutputKey}");
    Assert.Contains("Late Show A", noneText);
    Assert.Contains("Late Show B", noneText);

    var news = await GenerateAsync(client, analysis.CacheKey, "News");
    Assert.Equal(2, news.KeptChannels);
    var newsText = await client.Http.GetStringAsync($"/api/playlist/output/{news.OutputKey}");
    Assert.DoesNotContain("Daily Brief", newsText);
    Assert.Contains("Late Show A", newsText);
    Assert.Contains("Late Show B", newsText);

    var xxx = await GenerateAsync(client, analysis.CacheKey, "XXX");
    Assert.Equal(2, xxx.KeptChannels);
    var xxxText = await client.Http.GetStringAsync($"/api/playlist/output/{xxx.OutputKey}");
    Assert.Contains("Daily Brief", xxxText);
    Assert.DoesNotContain("Late Show A", xxxText);
    Assert.Contains("Late Show B", xxxText);
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

  [Fact]
  public async Task Generate_KeepsSourceHeaderLine()
  {
    var client = NewClient();
    var analysis = await AnalyzeRawAsync(client, TestPlaylists.WithGuideHeader);
    var generated = await GenerateAsync(client, analysis.CacheKey, "Kids");

    var text = await client.Http.GetStringAsync($"/api/playlist/output/{generated.OutputKey}");
    var lines = text.Replace("\r\n", "\n").Split('\n');
    Assert.Equal(TestPlaylists.GuideHeader, lines[0]);
    Assert.Equal("# Created with Strim (https://strim.plis.dev)", lines[1]);
    Assert.Equal(1, generated.KeptChannels);
  }

  private static async Task<AnalysisDto> AnalyzeRawAsync(ApiClient client, string rawText = TestPlaylists.Mixed)
  {
    var response = await client.PostAsync("/api/playlist/analyze", new { rawText });
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
