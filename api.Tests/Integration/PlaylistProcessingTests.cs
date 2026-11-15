using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

  [Theory]
  [InlineData("#EXTINF:-1 group-title=\"Ungrouped\",Named Channel\nhttp://provider.example/live/2001.ts\n")]
  [InlineData("#EXTINF:-1 tvg-name=\"Loose\",Loose Channel\nhttp://provider.example/live/2002.ts\n" +
              "#EXTINF:-1 group-title=\"Ungrouped\",Named Channel\nhttp://provider.example/live/2001.ts\n")]
  public async Task Analyze_GroupExplicitlyTitledUngrouped_ReturnsItsSearchWords(string entries)
  {
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { rawText = "#EXTM3U\n" + entries });
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    var group = Assert.Single(json.RootElement.GetProperty("groups").EnumerateArray().ToList());
    Assert.Equal("Ungrouped", group.GetProperty("groupTitle").GetString());
    Assert.Equal(new[] { "ungrouped" }, group.GetProperty("words").EnumerateArray().Select(w => w.GetString()!).ToArray());
  }

  [Fact]
  public async Task Analyze_ExplicitUngroupedOfOtherContentType_KeepsUntitledGroupWordsEmpty()
  {
    const string playlist = "#EXTM3U\n" +
      "#EXTINF:-1 tvg-name=\"Loose\",Loose Channel\nhttp://provider.example/live/4001.ts\n" +
      "#EXTINF:-1 group-title=\"Ungrouped\",Named Show\nhttp://provider.example/series/4002.mkv\n";
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { rawText = playlist });
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var groups = json.RootElement.GetProperty("groups").EnumerateArray().ToList();

    Assert.Equal(2, groups.Count);
    var live = Assert.Single(groups, g => g.GetProperty("contentType").GetString() == "Live");
    var series = Assert.Single(groups, g => g.GetProperty("contentType").GetString() == "Series");
    Assert.Equal(JsonValueKind.Array, live.GetProperty("words").ValueKind);
    Assert.Empty(live.GetProperty("words").EnumerateArray());
    Assert.Equal(new[] { "ungrouped" }, series.GetProperty("words").EnumerateArray().Select(w => w.GetString()!).ToArray());
  }

  [Fact]
  public async Task Analyze_EntriesWithoutOrWithEmptyTitle_ReturnEmptyWords()
  {
    const string playlist = "#EXTM3U\n" +
      "#EXTINF:-1 tvg-name=\"Loose\",Loose Channel\nhttp://provider.example/live/3001.ts\n" +
      "#EXTINF:-1 group-title=\"\",Empty Channel\nhttp://provider.example/live/3002.ts\n";
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { rawText = playlist });
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    var group = Assert.Single(json.RootElement.GetProperty("groups").EnumerateArray().ToList());
    Assert.Equal("Ungrouped", group.GetProperty("groupTitle").GetString());
    Assert.Equal(2, group.GetProperty("count").GetInt32());
    Assert.Equal(JsonValueKind.Array, group.GetProperty("words").ValueKind);
    Assert.Empty(group.GetProperty("words").EnumerateArray());
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

  private const string AccountHost = "http://93.184.215.14";
  private const string AccountPassword = "acct-pw-7Hq2";

  // Registers a get.php playlist and an optional player_api.php answer under a unique username.
  private string StubAccount(string name, Func<HttpResponseMessage>? accountAnswer)
  {
    var link = $"{AccountHost}/get.php?username={name}&password={AccountPassword}&type=m3u_plus";
    factory.UpstreamPlaylists[link] = TestPlaylists.Mixed;
    if (accountAnswer is not null)
    {
      factory.UpstreamResponses[$"{AccountHost}/player_api.php?username={name}&password={AccountPassword}"] = accountAnswer;
    }

    return link;
  }

  private static Func<HttpResponseMessage> Answer(HttpStatusCode status, string body, string mediaType = "application/json") =>
    () => new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) };

  private async Task<System.Text.Json.JsonElement> AnalyzeUrlAsync(string sourceUrl)
  {
    var response = await NewClient().PostAsync("/api/playlist/analyze", new { sourceUrl });
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
  }

  [Theory]
  [InlineData("number", "1767225600")]
  [InlineData("string", "\"1767225600\"")]
  public async Task Analyze_WithAccountEndpoint_ReturnsAccountInfo(string name, string expDate)
  {
    var link = StubAccount($"exp-{name}", Answer(HttpStatusCode.OK,
      $$$"""{"user_info":{"auth":1,"status":"Active","exp_date":{{{expDate}}},"max_connections":"2","active_cons":1}}"""));
    var before = DateTimeOffset.UtcNow;

    var json = await AnalyzeUrlAsync(link);

    Assert.Equal(4, json.GetProperty("totalChannels").GetInt32());
    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), json.GetProperty("expirationUtc").GetDateTimeOffset());
    Assert.Equal("Active", json.GetProperty("accountStatus").GetString());
    Assert.Equal(2, json.GetProperty("maxConnections").GetInt32());
    Assert.Equal(1, json.GetProperty("activeConnections").GetInt32());
    Assert.InRange(json.GetProperty("accountCheckedUtc").GetDateTimeOffset(), before, DateTimeOffset.UtcNow);
  }

  [Fact]
  public async Task Analyze_AccountExpiryReplacesUrlParameter()
  {
    var link = StubAccount("exp-url", Answer(HttpStatusCode.OK,
      """{"user_info":{"auth":1,"status":"Active","exp_date":1767225600}}""")) + "&exp=1700000000";
    factory.UpstreamPlaylists[link] = TestPlaylists.Mixed;

    var json = await AnalyzeUrlAsync(link);

    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), json.GetProperty("expirationUtc").GetDateTimeOffset());
  }

  [Fact]
  public async Task Analyze_WithNullExpiry_ReturnsStatusWithoutExpiration()
  {
    var link = StubAccount("exp-null", Answer(HttpStatusCode.OK,
      """{"user_info":{"auth":1,"status":"Active","exp_date":null,"max_connections":"1","active_cons":"0"}}""")) + "&exp=1700000000";
    factory.UpstreamPlaylists[link] = TestPlaylists.Mixed;

    var json = await AnalyzeUrlAsync(link);

    Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("expirationUtc").ValueKind);
    Assert.Equal("Active", json.GetProperty("accountStatus").GetString());
    Assert.Equal(1, json.GetProperty("maxConnections").GetInt32());
    Assert.Equal(0, json.GetProperty("activeConnections").GetInt32());
  }

  [Theory]
  [InlineData("fallback-missing", null)]
  [InlineData("fallback-bad", "not json")]
  [InlineData("fallback-noexp", """{"user_info":{"auth":1,"status":"Active"}}""")]
  public async Task Analyze_WithoutAccountExpiry_FallsBackToUrlExpiry(string name, string? body)
  {
    var link = StubAccount(name, body is null ? null : Answer(HttpStatusCode.OK, body)) + "&exp=1700000000";
    factory.UpstreamPlaylists[link] = TestPlaylists.Mixed;

    var json = await AnalyzeUrlAsync(link);

    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), json.GetProperty("expirationUtc").GetDateTimeOffset());
  }

  public static TheoryData<string, HttpStatusCode, string, string> FailingAccountAnswers => new()
  {
    { "not-found", HttpStatusCode.NotFound, "", "text/plain" },
    { "html", HttpStatusCode.OK, "<html><body>Error</body></html>", "text/html" },
    { "bad-json", HttpStatusCode.OK, "{\"user_info\":{\"status\":", "application/json" },
  };

  [Theory]
  [MemberData(nameof(FailingAccountAnswers))]
  public async Task Analyze_WhenAccountLookupFails_StillReturnsGroups(string name, HttpStatusCode status, string body, string mediaType)
  {
    var link = StubAccount($"fail-{name}", Answer(status, body, mediaType));
    factory.LogEntries.Clear();

    var json = await AnalyzeUrlAsync(link);

    Assert.Equal(3, json.GetProperty("groups").GetArrayLength());
    foreach (var field in new[] { "expirationUtc", "accountStatus", "maxConnections", "activeConnections", "accountCheckedUtc" })
    {
      Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty(field).ValueKind);
    }

    Assert.Contains(factory.UpstreamRequests, r => r.Contains($"player_api.php?username=fail-{name}&"));
    Assert.NotEmpty(factory.LogEntries);
    Assert.DoesNotContain(factory.LogEntries, entry => entry.Contains(AccountPassword));
  }

  [Fact]
  public async Task Analyze_WithoutGetPhp_AsksNoAccountEndpoint()
  {
    var link = $"{AccountHost}/lists/no-account.m3u?username=noacct&password={AccountPassword}";
    factory.UpstreamPlaylists[link] = TestPlaylists.Mixed;

    var json = await AnalyzeUrlAsync(link);

    Assert.Equal(4, json.GetProperty("totalChannels").GetInt32());
    Assert.Contains(factory.UpstreamRequests, r => r == link);
    Assert.DoesNotContain(factory.UpstreamRequests, r => r.Contains("player_api.php") && r.Contains("noacct"));
    Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("accountCheckedUtc").ValueKind);
  }

  [Fact]
  public async Task Pipeline_AfterRestart_ReusesFreshSource()
  {
    using var app = new StrimAppFactory();
    var first = await app.Services.GetRequiredService<PlaylistPipeline>()
      .GetSourceAsync(StrimAppFactory.UpstreamUrl, CancellationToken.None);

    // A second host on the same cache directory, as after a restart or deploy.
    var restarted = app.WithWebHostBuilder(_ => { });
    app.UpstreamRequests.Clear();
    var reused = await restarted.Services.GetRequiredService<PlaylistPipeline>()
      .GetSourceAsync(StrimAppFactory.UpstreamUrl, CancellationToken.None);

    Assert.Empty(app.UpstreamRequests);
    Assert.NotSame(first, reused);
    Assert.Equal(first.FilePath, reused.FilePath);
    Assert.Equal(first.ContentHash, reused.ContentHash);
  }

  [Fact]
  public async Task Pipeline_AfterRestart_RevalidatesWithStoredETag()
  {
    using var app = new StrimAppFactory();
    var lastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    app.UpstreamResponses[StrimAppFactory.UpstreamUrl] = () =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(TestPlaylists.Mixed, System.Text.Encoding.UTF8, "application/x-mpegurl"),
      };
      response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
      response.Content.Headers.LastModified = lastModified;
      return response;
    };
    var first = await app.Services.GetRequiredService<PlaylistPipeline>()
      .GetSourceAsync(StrimAppFactory.UpstreamUrl, CancellationToken.None);

    var restarted = app.WithWebHostBuilder(builder => builder.UseSetting("PlaylistCache:RevalidationIntervalMinutes", "0"));
    app.UpstreamResponses[StrimAppFactory.UpstreamUrl] = () => new HttpResponseMessage(HttpStatusCode.NotModified);
    app.UpstreamValidators.Clear();
    var revalidated = await restarted.Services.GetRequiredService<PlaylistPipeline>()
      .GetSourceAsync(StrimAppFactory.UpstreamUrl, CancellationToken.None);

    var request = Assert.Single(app.UpstreamValidators);
    Assert.Equal(StrimAppFactory.UpstreamUrl, request.Url);
    Assert.Equal("\"v1\"", request.IfNoneMatch);
    Assert.Equal(lastModified, request.IfModifiedSince);
    Assert.Equal(first.FilePath, revalidated.FilePath);
    Assert.True(File.Exists(revalidated.FilePath));
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
