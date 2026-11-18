using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Api.Tests.Integration;

public class PlaylistSearchEndpointTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private const string SessionExpired = "The playlist session expired. Analyze the source again.";

  // "Germany" holds live channels and a movie, so the movie is written to "Germany · Movies".
  private const string WithMovie = """
    #EXTM3U
    #EXTINF:-1 tvg-name="ARD" group-title="Germany",ARD
    http://line.example/live/u/p/1.ts
    #EXTINF:-1 group-title="Germany",Das Boot (1981)
    http://line.example/movie/u/p/20.mp4
    #EXTINF:-1 group-title="Kids",KiKA
    http://line.example/live/u/p/3.ts
    """;

  [Fact]
  public async Task Search_ReturnsMatchesWithGroupAndPosition()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, TestPlaylists.Mixed);

    var result = await SearchOkAsync(client, new { cacheKey, query = "zdf" });

    var match = Assert.Single(result.Matches);
    Assert.Equal(new MatchDto("ZDF", "Germany", "Germany · Live", 2, "Live", true), match);
    Assert.False(result.HasMore);
  }

  [Fact]
  public async Task Search_ReturnsSplitGroupTitle()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, WithMovie);

    var result = await SearchOkAsync(client, new { cacheKey, query = "Das Boot" });

    var match = Assert.Single(result.Matches);
    Assert.Equal(new MatchDto("Das Boot (1981)", "Germany · Movies", "Germany · Movies", 1, "Movies", true), match);
  }

  [Fact]
  public async Task Search_LimitsToFifty()
  {
    var playlist = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 60; i++)
    {
      playlist.Append($"#EXTINF:-1 group-title=\"News\",News {i}\nhttp://line.example/live/u/p/{i}.ts\n");
    }
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, playlist.ToString());

    var result = await SearchOkAsync(client, new { cacheKey, query = "news" });

    Assert.Equal(50, result.Matches.Count);
    Assert.True(result.HasMore);
    Assert.Equal(Enumerable.Range(1, 50), result.Matches.Select(m => m.Position));
  }

  [Fact]
  public async Task Search_ReportsDisabledGroup()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, WithMovie);

    var byKey = await SearchOkAsync(client, new { cacheKey, query = "Das Boot", disabledGroups = new[] { "Germany · Movies" } });
    Assert.False(Assert.Single(byKey.Matches).Enabled);

    var byTitle = await SearchOkAsync(client, new { cacheKey, query = "kika", disabledGroups = new[] { "kids" } });
    Assert.False(Assert.Single(byTitle.Matches).Enabled);

    var other = await SearchOkAsync(client, new { cacheKey, query = "ARD", disabledGroups = new[] { "Germany · Movies" } });
    Assert.True(Assert.Single(other.Matches).Enabled);
  }

  [Fact]
  public async Task Search_HideDisabled_LeavesOutAndCountsOffGroups()
  {
    var playlist = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 60; i++)
      playlist.Append($"#EXTINF:-1 group-title=\"Off\",News Off {i}\nhttp://line.example/live/u/p/{i}.ts\n");
    for (var i = 1; i <= 3; i++)
      playlist.Append($"#EXTINF:-1 group-title=\"On\",News On {i}\nhttp://line.example/live/u/p/{100 + i}.ts\n");
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, playlist.ToString());
    var disabledGroups = new[] { "Off" };

    var today = await SearchOkAsync(client, new { cacheKey, query = "news", disabledGroups });
    Assert.Equal(50, today.Matches.Count);
    Assert.True(today.HasMore);
    Assert.All(today.Matches, m => Assert.False(m.Enabled));
    Assert.Equal(0, today.HiddenCount);

    var explicitOff = await SearchOkAsync(client, new { cacheKey, query = "news", disabledGroups, hideDisabled = false });
    Assert.Equal(today.Matches, explicitOff.Matches);
    Assert.Equal((true, 0), (explicitOff.HasMore, explicitOff.HiddenCount));

    var hidden = await SearchOkAsync(client, new { cacheKey, query = "news", disabledGroups, hideDisabled = true });
    Assert.Equal(new[] { "News On 1", "News On 2", "News On 3" }, hidden.Matches.Select(m => m.Name));
    Assert.DoesNotContain(hidden.Matches, m => !m.Enabled);
    Assert.False(hidden.HasMore);
    Assert.Equal(60, hidden.HiddenCount);
  }

  [Fact]
  public async Task Search_HideDisabled_CountsOffMatchesPastTheLimit()
  {
    var playlist = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 51; i++)
      playlist.Append($"#EXTINF:-1 group-title=\"On\",News On {i}\nhttp://line.example/live/u/p/{i}.ts\n");
    for (var i = 1; i <= 60; i++)
      playlist.Append($"#EXTINF:-1 group-title=\"Off\",News Off {i}\nhttp://line.example/live/u/p/{100 + i}.ts\n");
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, playlist.ToString());

    var hidden = await SearchOkAsync(client, new { cacheKey, query = "news", disabledGroups = new[] { "Off" }, hideDisabled = true });

    Assert.Equal(50, hidden.Matches.Count);
    Assert.All(hidden.Matches, m => Assert.True(m.Enabled));
    Assert.True(hidden.HasMore);
    Assert.Equal(60, hidden.HiddenCount);
  }

  [Fact]
  public async Task Search_WithoutHideDisabled_ReturnsZeroHiddenCount()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, WithMovie);

    var result = await SearchOkAsync(client, new { cacheKey, query = "kika", disabledGroups = new[] { "Kids" } });

    // HiddenCount defaults to -1 in the DTO, so 0 means the field is present.
    Assert.Equal(0, result.HiddenCount);
    Assert.False(Assert.Single(result.Matches).Enabled);
  }

  [Fact]
  public async Task Search_RejectsInvalidInput()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, TestPlaylists.Mixed);

    object[] invalid =
    [
      new { cacheKey, query = "" },
      new { cacheKey },
      new { cacheKey, query = "a" },
      new { cacheKey, query = "é !" },
      new { cacheKey, query = new string('a', 101) },
      new { cacheKey, query = "ard", disabledGroups = Enumerable.Repeat("x", 10_001).ToArray() },
      new { cacheKey, query = "ard", disabledGroups = new[] { new string('x', 5_000) } },
      new { cacheKey, query = "ard", disabledGroups = new[] { " " } },
    ];
    foreach (var body in invalid)
    {
      var response = await client.PostAsync("/api/playlist/search", body);
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    var longest = await client.PostAsync("/api/playlist/search", new { cacheKey, query = new string('a', 100) });
    Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
  }

  [Fact]
  public async Task Search_ContentTypes_ReturnsOnlyChosenTypes()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, """
      #EXTM3U
      #EXTINF:-1 group-title="Germany",Boot TV
      http://line.example/live/u/p/1.ts
      #EXTINF:-1 group-title="Germany",Das Boot (1981)
      http://line.example/movie/u/p/20.mp4
      #EXTINF:-1 group-title="Shows",Boot Camp S01E01
      http://line.example/series/u/p/30.mkv
      """);

    var films = await SearchOkAsync(client, new { cacheKey, query = "boot", contentTypes = new[] { "Movies" } });
    var all = await SearchOkAsync(client, new { cacheKey, query = "boot", contentTypes = Array.Empty<string>() });

    Assert.Equal(new MatchDto("Das Boot (1981)", "Germany · Movies", "Germany · Movies", 1, "Movies", true), Assert.Single(films.Matches));
    Assert.Equal(3, all.Matches.Count);
  }

  [Fact]
  public async Task Search_UnknownContentType_Returns400()
  {
    var client = new ApiClient(factory.CreateClient());
    var cacheKey = await AnalyzeAsync(client, WithMovie);

    foreach (var types in new[] { new[] { "Films" }, new[] { "1" }, new[] { "Movies", "" } })
    {
      var response = await client.PostAsync("/api/playlist/search", new { cacheKey, query = "ard", contentTypes = types });
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
      Assert.Equal("Unknown content type. Use Live, Movies, Series.", error!.Error);
    }

    var tooMany = await client.PostAsync(
      "/api/playlist/search", new { cacheKey, query = "ard", contentTypes = new[] { "Live", "Movies", "Series", "Live" } });
    Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    Assert.Equal("Provide at most 3 content types.", (await tooMany.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
  }

  [Fact]
  public async Task Search_ExpiredSession_Returns400()
  {
    var client = new ApiClient(factory.CreateClient());

    var response = await client.PostAsync("/api/playlist/search", new { cacheKey = "pl-unknown", query = "ard" });

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
    Assert.Equal(SessionExpired, error!.Error);
  }

  [Fact]
  public async Task Search_IndexUnavailable_ReturnsError()
  {
    using var own = new StrimAppFactory();
    using var noIndex = own.WithWebHostBuilder(builder => builder.UseSetting("PlaylistCache:MaxSearchIndexBytes", "0"));
    var client = new ApiClient(noIndex.CreateClient());
    var cacheKey = await AnalyzeAsync(client, TestPlaylists.Mixed);

    var response = await client.PostAsync("/api/playlist/search", new { cacheKey, query = "ard" });

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
    Assert.Contains("not available", error!.Error);
  }

  [Fact]
  public async Task UnknownApiRoute_StillReturnsJson404()
  {
    var client = new ApiClient(factory.CreateClient());

    var response = await client.PostAsync("/api/playlist/search/unknown", new { query = "ard" });

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
  }

  private static async Task<string> AnalyzeAsync(ApiClient client, string rawText)
  {
    var response = await client.PostAsync("/api/playlist/analyze", new { rawText });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<AnalysisDto>())!.CacheKey;
  }

  private static async Task<SearchDto> SearchOkAsync(ApiClient client, object body)
  {
    var response = await client.PostAsync("/api/playlist/search", body);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<SearchDto>())!;
  }

  private sealed record AnalysisDto(string CacheKey);
  private sealed record MatchDto(string Name, string Group, string GroupKey, int Position, string ContentType, bool Enabled);
  private sealed record SearchDto(List<MatchDto> Matches, bool HasMore, int HiddenCount = -1);
  private sealed record ErrorDto(string Error);
}
