using System.Net;
using System.Text.Json;
using Xunit;
using static Api.Tests.LibraryFilmCardTests;

namespace Api.Tests;

/// <summary>GET /api/library/providers against a stubbed TMDB; the names and ids are invented.</summary>
public sealed class LibraryProvidersEndpointTests
{
  private const string Path = "/api/library/providers";

  private static string Provider(int id, string? name, int? ch = null) =>
    JsonSerializer.Serialize(new
    {
      provider_id = id, provider_name = name, logo_path = "/logo.png",
      display_priorities = ch is int p ? new Dictionary<string, int> { ["CH"] = p, ["DE"] = 0 } : new Dictionary<string, int> { ["DE"] = 0 },
    });

  private static HttpResponseMessage List(params string[] providers) => Json($$"""{"results":[{{string.Join(",", providers)}}]}""");

  private static Func<Uri, HttpResponseMessage> Tmdb(Func<HttpResponseMessage> movies, Func<HttpResponseMessage> tv) =>
    uri => uri.AbsolutePath == "/3/watch/providers/movie" ? movies() : tv();

  private static async Task<JsonElement> ProvidersAsync(TestHost host)
  {
    var response = await host.Client.Http.GetAsync(Path);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
  }

  private static List<(int, string?)> All(JsonElement answer) => answer.GetProperty("providers").EnumerateArray()
    .Select(p => (p.GetProperty("id").GetInt32(), p.GetProperty("name").GetString())).ToList();

  [Fact]
  public async Task Providers_MergeFilmAndSeriesLists_ByChPriorityThenName()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb(
      () => List(Provider(8, "Film Flix", 5), Provider(20, "Zeta Stream", 2), Provider(30, "No Rank"), Provider(40, "Beta Stream", 2)),
      () => List(Provider(8, "Series Flix", 1), Provider(50, "Alpha Stream", 2), Provider(60, "Also Unranked"))));

    var answer = await ProvidersAsync(host);
    Assert.Equal("CH", answer.GetProperty("region").GetString());
    Assert.True(answer.GetProperty("tmdbData").GetBoolean());
    Assert.Equal([(8, "Film Flix"), (50, "Alpha Stream"), (40, "Beta Stream"), (20, "Zeta Stream"), (60, "Also Unranked"), (30, "No Rank")],
      All(answer));
    Assert.DoesNotContain("logo", answer.GetRawText(), StringComparison.OrdinalIgnoreCase);
    Assert.Equal(["/3/watch/providers/movie?watch_region=CH", "/3/watch/providers/tv?watch_region=CH"],
      host.TmdbRequests.Select(u => u.PathAndQuery).Order());
  }

  [Fact]
  public async Task Providers_DropEntriesWithoutNameOrId_AndStopAtForty()
  {
    var many = Enumerable.Range(1, 45).Select(i => Provider(i, $"Service {i:D2}", i)).ToList();
    await using var host = await TestHost.StartAsync(Token, Tmdb(
      () => List([Provider(0, "Zero", 0), Provider(-3, "Negative", 0), Provider(100, null, 0), Provider(101, " ", 0), .. many]),
      () => List()));

    var providers = All(await ProvidersAsync(host));
    Assert.Equal(40, providers.Count);
    Assert.Equal(Enumerable.Range(1, 40).Select(i => (i, (string?)$"Service {i:D2}")), providers);
  }

  [Fact]
  public async Task Providers_WhenOneListFails_AnswerTheOther()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb(
      () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
      () => List(Provider(9, "Series Only", 1))));

    var answer = await ProvidersAsync(host);
    Assert.Equal([(9, "Series Only")], All(answer));
    Assert.True(answer.GetProperty("tmdbData").GetBoolean());
    Assert.Equal(2, host.TmdbRequests.Count);
  }

  [Fact]
  public async Task Providers_WhenBothFail_AreEmpty()
  {
    await using var host = await TestHost.StartAsync(Token, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

    var answer = await ProvidersAsync(host);
    Assert.Empty(All(answer));
    Assert.False(answer.GetProperty("tmdbData").GetBoolean());
    // No retry: one call per list.
    Assert.Equal(2, host.TmdbRequests.Count);
  }

  [Fact]
  public async Task Providers_SecondCall_IsServedFromTheCache()
  {
    await using var host = await TestHost.StartAsync(Token, Tmdb(() => List(Provider(8, "Film Flix", 1)), () => List()));

    var first = All(await ProvidersAsync(host));
    var second = All(await ProvidersAsync(host));
    Assert.Equal(first, second);
    Assert.Equal(2, host.TmdbRequests.Count);
  }

  [Fact]
  public async Task Providers_WithoutSession_Gets401()
  {
    await using var host = await TestHost.StartAsync(Token, _ => List());
    Assert.Equal(HttpStatusCode.Unauthorized, (await host.Factory.CreateClient().GetAsync(Path)).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }

  [Fact]
  public async Task Providers_WithoutTmdbToken_Gets404()
  {
    await using var host = await TestHost.StartAsync(token: null, _ => List());
    Assert.Equal(HttpStatusCode.NotFound, (await host.Client.Http.GetAsync(Path)).StatusCode);
    Assert.Empty(host.TmdbRequests);
  }
}
