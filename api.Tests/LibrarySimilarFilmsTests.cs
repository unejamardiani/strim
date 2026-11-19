using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>The film card's similar films, found in the owner's list, against a stubbed TMDB.</summary>
public sealed class LibrarySimilarFilmsTests : IAsyncLifetime
{
  // A made-up value; only its presence enables the card.
  private const string Token = "test-tmdb-token-not-real-7c2f19d4";

  // "Films Old" is switched off on the saved playlist; the adult group is never matched.
  private const string Playlist = """
    #EXTM3U
    #EXTINF:-1 group-title="Films Old",Speed (1994)
    http://line.example/movie/u/p/20.mkv
    #EXTINF:-1 group-title="Films",DE - Heat (1995) 4K
    http://line.example/movie/u/p/21.mkv
    #EXTINF:-1 group-title="Films",Speed (1994) HD
    http://line.example/movie/u/p/22.mkv
    #EXTINF:-1 group-title="XXX | Films",Ronin (1998)
    http://line.example/movie/u/p/23.mkv
    #EXTINF:-1 group-title="Films Old",Collateral (2004)
    http://line.example/movie/u/p/24.mkv
    #EXTINF:-1 group-title="Films",Thief
    http://line.example/movie/u/p/25.mkv
    """;

  private const string Search = """{"results":[{"id":603,"title":"The Matrix","release_date":"1999-03-31"}]}""";

  // TMDB's order: an adult-only match, one only in a switched-off group, one in the list, one not at all, one in both
  // groups, and one without a matching year that a mark's stored TMDB id finds.
  private const string Details = """
    {"id":603,"title":"The Matrix","release_date":"1999-03-31","genres":[],"similar":{"results":[
      {"id":8,"title":"Ronin","release_date":"1998-09-25","poster_path":"/r.jpg"},
      {"id":7,"title":"Collateral","release_date":"2004-08-05","poster_path":"/c.jpg"},
      {"id":5,"title":"Heat","release_date":"1995-12-15","poster_path":"/h.jpg"},
      {"id":4,"title":"Unknown Film","release_date":"2001-01-01","poster_path":"/u.jpg"},
      {"id":3,"title":"Speed","release_date":"1994-06-09","poster_path":"/s.jpg"},
      {"id":11,"title":"Thief","release_date":"1981-03-27","poster_path":"/t.jpg"}]}}
    """;

  private readonly ConcurrentQueue<Uri> _tmdbRequests = new();
  private StrimAppFactory _root = null!;
  private WebApplicationFactory<Program> _app = null!;
  private ApiClient _client = null!;
  private Guid _playlistId;
  private string _cacheKey = "";

  public async Task InitializeAsync()
  {
    _root = new StrimAppFactory();
    _app = _root.WithWebHostBuilder(builder =>
    {
      builder.ConfigureAppConfiguration((_, config) =>
        config.AddInMemoryCollection(new Dictionary<string, string?> { [TmdbClient.TokenVariable] = Token }));
      builder.ConfigureTestServices(services => services.AddHttpClient(TmdbClient.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() => new StubTmdb(_tmdbRequests)));
    });
    _client = new ApiClient(_app.CreateClient());
    await _client.RegisterAsync("similar-owner");
    var created = await _client.PostAsync("/api/playlists", new
    {
      name = "Living room", sourceUrl = StrimAppFactory.UpstreamUrl, sourceName = "provider",
      disabledGroups = new[] { "Films Old" }, totalChannels = 6, groupCount = 3,
    });
    created.EnsureSuccessStatusCode();
    _playlistId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    var analyzed = await _client.PostAsync("/api/playlist/analyze", new { rawText = Playlist });
    analyzed.EnsureSuccessStatusCode();
    _cacheKey = JsonDocument.Parse(await analyzed.Content.ReadAsStringAsync()).RootElement.GetProperty("cacheKey").GetString()!;

    // "Thief" carries no year, so only the TMDB id stored on its mark ties it to film 11.
    await MarkAsync("Thief", seen: false, filmDbId: "11", rating: 1);
  }

  public async Task DisposeAsync()
  {
    await _app.DisposeAsync();
    await _root.DisposeAsync();
  }

  [Fact]
  public async Task Similar_NoExtraRequest()
  {
    var similar = await SimilarAsync();

    Assert.Equal(6, similar.Count);
    Assert.Equal(["/3/search/movie", "/3/movie/603"], _tmdbRequests.Select(u => u.AbsolutePath));
    Assert.Contains("append_to_response=similar", _tmdbRequests.Last().Query);
  }

  [Fact]
  public async Task Similar_InList_HasGroupAndEnabled()
  {
    var similar = await SimilarAsync();

    var heat = similar.Single(s => s.GetProperty("id").GetInt32() == 5);
    Assert.Equal("Heat", heat.GetProperty("title").GetString());
    Assert.Equal(1995, heat.GetProperty("year").GetInt32());
    Assert.Equal("https://image.tmdb.org/t/p/w342/h.jpg", heat.GetProperty("posterUrl").GetString());
    Assert.True(heat.GetProperty("inList").GetBoolean());
    Assert.Equal("DE - Heat (1995) 4K", heat.GetProperty("entry").GetString());
    Assert.Equal("Films", heat.GetProperty("group").GetString());
    Assert.True(heat.GetProperty("enabled").GetBoolean());

    // Speed is in a switched-off group first and in "Films" later: the switched-on entry wins.
    var speed = similar.Single(s => s.GetProperty("id").GetInt32() == 3);
    Assert.Equal("Speed (1994) HD", speed.GetProperty("entry").GetString());
    Assert.True(speed.GetProperty("enabled").GetBoolean());

    var unknown = similar.Single(s => s.GetProperty("id").GetInt32() == 4);
    Assert.False(unknown.GetProperty("inList").GetBoolean());
    Assert.Equal("https://image.tmdb.org/t/p/w342/u.jpg", unknown.GetProperty("posterUrl").GetString());
    Assert.Equal(JsonValueKind.Null, unknown.GetProperty("entry").ValueKind);

    // The full list, only on request: Collateral is found in its switched-off group.
    var full = await SimilarAsync("&includeDisabled=true");
    var collateral = full.Single(s => s.GetProperty("id").GetInt32() == 7);
    Assert.True(collateral.GetProperty("inList").GetBoolean());
    Assert.Equal("Films Old", collateral.GetProperty("group").GetString());
    Assert.False(collateral.GetProperty("enabled").GetBoolean());
  }

  [Fact]
  public async Task Similar_InListFirst()
  {
    // Ronin is only in an adult group and Collateral only in a switched-off one: neither is in the cleaned list.
    Assert.Equal([5, 3, 11, 8, 7, 4], Ids(await SimilarAsync()));
    Assert.Equal([7, 5, 3, 11, 8, 4], Ids(await SimilarAsync("&includeDisabled=true")));

    // Without the list's cacheKey nothing is in the list, and TMDB's order holds.
    var path = $"/api/playlists/{_playlistId}/library/film-card?name=The%20Matrix%20(1999)&group=Films&type=Movies";
    var card = JsonDocument.Parse(await _client.Http.GetStringAsync(path)).RootElement;
    var similar = card.GetProperty("film").GetProperty("similar").EnumerateArray().ToList();
    Assert.Equal([8, 7, 5, 4, 3, 11], Ids(similar));
    Assert.All(similar, s => Assert.False(s.GetProperty("inList").GetBoolean()));
  }

  [Fact]
  public async Task Similar_OnePassOverIndex()
  {
    var pipeline = _app.Services.GetRequiredService<PlaylistPipeline>();
    var index = pipeline.Cache.TryGetAnalysis(pipeline.Cache.TryGetSessionSource(_cacheKey)!)!.SearchIndex!;
    var before = index.PassCount;

    var similar = await SimilarAsync();

    Assert.Equal(3, similar.Count(s => s.GetProperty("inList").GetBoolean()));
    Assert.Equal(before + 1, index.PassCount);
  }

  [Fact]
  public async Task Similar_HideSeen()
  {
    await MarkAsync("Heat (1995)", seen: true, filmDbId: null);

    var similar = await SimilarAsync();
    Assert.True(similar.Single(s => s.GetProperty("id").GetInt32() == 5).GetProperty("seen").GetBoolean());
    Assert.False(similar.Single(s => s.GetProperty("id").GetInt32() == 3).GetProperty("seen").GetBoolean());

    Assert.Equal([3, 11, 8, 7, 4], Ids(await SimilarAsync("&hideSeen=true")));
  }

  private static List<int> Ids(IEnumerable<JsonElement> similar) => similar.Select(s => s.GetProperty("id").GetInt32()).ToList();

  private async Task<List<JsonElement>> SimilarAsync(string options = "")
  {
    var path = $"/api/playlists/{_playlistId}/library/film-card?name=The%20Matrix%20(1999)&group=Films&type=Movies"
      + $"&cacheKey={Uri.EscapeDataString(_cacheKey)}{options}";
    var response = await _client.Http.GetAsync(path);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var card = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    Assert.Equal("matched", card.GetProperty("status").GetString());
    return card.GetProperty("film").GetProperty("similar").EnumerateArray().ToList();
  }

  /// <summary>Marks a film through PUT /api/marks; the TMDB id has no route yet, so it is set in the database.</summary>
  private async Task MarkAsync(string name, bool seen, string? filmDbId, int rating = 0)
  {
    var response = await _client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind = "film", seen, rating });
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    if (filmDbId is null) return;
    using var scope = _app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var key = MarkKey.From(name, MarkKind.Film)!.Key;
    db.TitleMarks.Single(m => m.TitleKey == key).FilmDbId = filmDbId;
    db.SaveChanges();
  }

  private sealed class StubTmdb(ConcurrentQueue<Uri> requests) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      requests.Enqueue(request.RequestUri!);
      var body = request.RequestUri!.AbsolutePath == "/3/search/movie" ? Search : Details;
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
      });
    }
  }
}
