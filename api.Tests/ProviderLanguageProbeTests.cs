using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// The read-only provider language probe: one details request to the playlist's own host, a
/// sanitised summary back, and nothing of the address, host or credentials in answers or logs.
/// </summary>
public sealed class ProviderLanguageProbeTests : IDisposable
{
  // Made-up public IP literals that no other test uses, so the captured logs are this class's own.
  private const string Host = "93.184.215.71";
  private const string OtherHost = "93.184.215.72";
  private const string Password = "probe-pw-x8k3";

  private const string VodWithLanguages = """
    {"info":{"name":"Made Up Film","audio":{"codec_name":"aac","tags":{"language":"eng"}},
     "subtitles":["deu","fra"],"audio_language":"English"},"movie_data":{"stream_id":501}}
    """;

  private readonly StrimAppFactory _factory = new();

  public void Dispose() => _factory.Dispose();

  private static string Link(string user) => $"http://{Host}/get.php?username={user}&password={Password}&type=m3u_plus";

  private static string Details(string user, string action) =>
    $"http://{Host}/player_api.php?username={user}&password={Password}&action={action}";

  private static Func<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
    () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  // The Strim account gets its own name, so log lines about it never echo the provider username.
  private async Task<(ApiClient Client, Guid Id)> OwnPlaylistAsync(string sourceUrl)
  {
    var client = new ApiClient(_factory.CreateClient());
    await client.RegisterAsync($"owner{Guid.NewGuid():N}"[..13]);
    var response = await client.PostAsync("/api/playlists", new { name = "Sofa", sourceUrl, disabledGroups = Array.Empty<string>() });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var created = await response.Content.ReadFromJsonAsync<JsonElement>();
    return (client, created.GetProperty("id").GetGuid());
  }

  private static string ProbePath(Guid id, string kind = "vod", long titleId = 501) =>
    $"/api/playlists/{id}/provider-language-probe?kind={kind}&titleId={titleId}";

  [Fact]
  public async Task VodDetailsWithAudioFields_ListsFieldsAndValues_WithOneRequestToTheOwnHost()
  {
    const string user = "probe-vod";
    _factory.UpstreamResponses[Details(user, "get_vod_info&vod_id=501")] = Json(VodWithLanguages);
    var (client, id) = await OwnPlaylistAsync(Link(user));
    var before = _factory.UpstreamRequests.Count;

    var response = await client.Http.GetAsync(ProbePath(id));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.True(summary.GetProperty("found").GetBoolean());
    var fields = summary.GetProperty("fields").EnumerateArray()
      .ToDictionary(f => f.GetProperty("path").GetString()!, f => f);
    Assert.Equal(new[] { "eng" }, Samples(fields["info.audio.tags.language"]));
    Assert.Equal(new[] { "deu", "fra" }, Samples(fields["info.subtitles"]));
    Assert.Equal(new[] { "English" }, Samples(fields["info.audio_language"]));
    Assert.Equal("object", fields["info.audio"].GetProperty("shapes")[0].GetString());
    Assert.DoesNotContain("info.name", fields.Keys);

    var requests = _factory.UpstreamRequests.Skip(before).ToList();
    Assert.Equal([Details(user, "get_vod_info&vod_id=501")], requests);
  }

  [Fact]
  public async Task SeriesDetails_AskGetSeriesInfo_AndReportEpisodeFieldsAcrossSeasons()
  {
    const string user = "probe-series";
    _factory.UpstreamResponses[Details(user, "get_series_info&series_id=77")] = Json("""
      {"info":{"name":"Made Up Show"},"episodes":{"1":[{"id":"9001","info":{"audio":{"tags":{"language":"ger"}}}}],
       "2":[{"id":"9002","info":{"audio":{"tags":{"language":"eng"}}}}]}}
      """);
    var (client, id) = await OwnPlaylistAsync(Link(user));

    var summary = await client.Http.GetFromJsonAsync<JsonElement>(ProbePath(id, "series", 77));

    var field = summary.GetProperty("fields").EnumerateArray()
      .Single(f => f.GetProperty("path").GetString() == "episodes.*[].info.audio.tags.language");
    Assert.Equal(new[] { "ger", "eng" }, Samples(field));
  }

  [Fact]
  public async Task DetailsWithoutLanguageFields_SayNoneWereFound()
  {
    const string user = "probe-none";
    _factory.UpstreamResponses[Details(user, "get_vod_info&vod_id=501")] =
      Json("""{"info":{"name":"Made Up Film","genre":"Drama"},"movie_data":{"stream_id":501}}""");
    var (client, id) = await OwnPlaylistAsync(Link(user));

    var summary = await client.Http.GetFromJsonAsync<JsonElement>(ProbePath(id));

    Assert.False(summary.GetProperty("found").GetBoolean());
    Assert.Empty(summary.GetProperty("fields").EnumerateArray());
    Assert.Contains("No language fields", summary.GetProperty("message").GetString());
  }

  [Fact]
  public async Task NeitherAnswersNorLogsCarryAddressHostOrCredentials_AndOtherHostsAreNeverContacted()
  {
    const string user = "probe-leak-u5";
    _factory.UpstreamResponses[Details(user, "get_vod_info&vod_id=501")] = Json(
      $$$"""{"info":{"audio_language":"{{{Password}}}","subtitle_url":"http://{{{Host}}}/sub.srt","lang":"{{{user}}}"}}""");
    _factory.UpstreamResponses[Details(user, "get_vod_info&vod_id=502")] = Json("""{"error":"x"}""", HttpStatusCode.InternalServerError);
    _factory.UpstreamResponses[Details(user, "get_vod_info&vod_id=503")] = () =>
    {
      var redirect = new HttpResponseMessage(HttpStatusCode.Found);
      redirect.Headers.Location = new Uri($"http://{OtherHost}/player_api.php?username={user}&password={Password}");
      return redirect;
    };
    _factory.UpstreamResponses[Details(user, "get_vod_info&vod_id=504")] = Json("<html>not json</html>");
    var (client, id) = await OwnPlaylistAsync(Link(user));

    var bodies = new List<string>();
    var success = await client.Http.GetAsync(ProbePath(id, titleId: 501));
    Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    bodies.Add(await success.Content.ReadAsStringAsync());
    foreach (var titleId in new[] { 502L, 503L, 504L })
    {
      var failure = await client.Http.GetAsync(ProbePath(id, titleId: titleId));
      Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
      var body = await failure.Content.ReadAsStringAsync();
      Assert.Contains("The provider did not return usable details for this title.", body);
      bodies.Add(body);
    }

    Assert.DoesNotContain(_factory.UpstreamRequests, r => r.Contains(OtherHost));
    Assert.NotEmpty(_factory.LogEntries);
    foreach (var text in bodies.Concat(_factory.LogEntries))
    {
      Assert.DoesNotContain(Host, text);
      Assert.DoesNotContain(user, text);
      Assert.DoesNotContain(Password, text);
      Assert.DoesNotContain("player_api", text);
    }
  }

  [Theory]
  [InlineData("http://93.184.215.71/playlist.m3u")]
  [InlineData("http://93.184.215.71/get.php?username=only-user")]
  public async Task NonXtreamPlaylist_IsNotSupported_AndNothingIsFetched(string sourceUrl)
  {
    var (client, id) = await OwnPlaylistAsync(sourceUrl);
    var before = _factory.UpstreamRequests.Count;

    var response = await client.Http.GetAsync(ProbePath(id));

    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    Assert.Contains("Not supported", await response.Content.ReadAsStringAsync());
    Assert.Equal(before, _factory.UpstreamRequests.Count);
  }

  [Fact]
  public async Task OtherUsersPlaylist_Is404_AndAnonymousIsRejected()
  {
    const string user = "probe-owner";
    var (_, id) = await OwnPlaylistAsync(Link(user));
    var other = new ApiClient(_factory.CreateClient());
    await other.RegisterAsync($"other{Guid.NewGuid():N}"[..13]);
    var before = _factory.UpstreamRequests.Count;

    Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync(ProbePath(id))).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(ProbePath(id))).StatusCode);
    Assert.Equal(before, _factory.UpstreamRequests.Count);
  }

  private static string[] Samples(JsonElement field) =>
    field.GetProperty("samples").EnumerateArray().Select(s => s.GetString()!).ToArray();
}
