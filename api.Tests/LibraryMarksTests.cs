using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Data;
using Api.Services;
using Api.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

/// <summary>The Library's search and group entries carry the caller's marks and filter by seen and liked (#297).</summary>
public class LibraryMarksTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private const string NoKeyName = "[Matrix] 4K";

  private const string Playlist = $"""
    #EXTM3U
    #EXTINF:-1 group-title="Films",DE - The Matrix (1999) 4K
    http://line.example/movie/u/p/1.mkv
    #EXTINF:-1 group-title="Films",EN| The Matrix (1999) [FHD]
    http://line.example/movie/u/p/2.mkv
    #EXTINF:-1 group-title="Films",The Matrix Reloaded (2003)
    http://line.example/movie/u/p/3.mkv
    #EXTINF:-1 group-title="Films",{NoKeyName}
    http://line.example/movie/u/p/4.mkv
    #EXTINF:-1 group-title="Series",Matrix Stories S01E02
    http://line.example/series/u/p/5.mkv
    #EXTINF:-1 group-title="Channels",The Matrix (1999)
    http://line.example/live/u/p/6.ts
    """;

  private async Task<(ApiClient Client, string CacheKey)> SignedInWithMarksAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    await MarkAsync(client, "The Matrix (1999)", "film", seen: true, rating: 1);
    await MarkAsync(client, "Matrix Stories S03E01", "series", seen: false, rating: 1);
    await MarkAsync(client, "The Matrix Reloaded (2003)", "film", seen: true, rating: -1);
    return (client, await AnalyzeAsync(client, Playlist));
  }

  /// <summary>A new user with "DE - The Matrix (1999) 4K" on the watchlist and the Reloaded seen, and the list analysed.</summary>
  internal static async Task<(ApiClient Client, string CacheKey)> WatchlistedAsync(StrimAppFactory app, string userName)
  {
    var client = new ApiClient(app.CreateClient());
    await client.RegisterAsync(userName);
    (await client.SendAsync(HttpMethod.Put, "/api/marks", new { name = "The Matrix (1999)", kind = "film", watchlist = true }))
      .EnsureSuccessStatusCode();
    await MarkAsync(client, "The Matrix Reloaded (2003)", "film", seen: true, rating: 0);
    return (client, await AnalyzeAsync(client, Playlist));
  }

  /// <summary>Each entry's watchlist field as raw JSON ("true", "false" or "null") by name.</summary>
  internal static Dictionary<string, string> WatchlistFlags(JsonElement items) =>
    items.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("watchlist").GetRawText());

  [Fact]
  public void Lookup_ReturnsTheWatchlistFlag()
  {
    var marks = new LibraryMarks(new() { ["film:1999:the matrix"] = (false, 0, true), ["film:2003:the matrix reloaded"] = (true, 1, false) });

    Assert.Equal(("film:1999:the matrix", false, 0, true), marks.Lookup("The Matrix (1999)", PlaylistContentType.Movies));
    Assert.Equal(("film:2003:the matrix reloaded", true, 1, false), marks.Lookup("The Matrix Reloaded (2003)", PlaylistContentType.Movies));
    Assert.Null(marks.Lookup("Heat (1995)", PlaylistContentType.Movies).Watchlist);
  }

  private static async Task MarkAsync(ApiClient client, string name, string kind, bool seen, int rating) =>
    (await client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind, seen, rating })).EnsureSuccessStatusCode();

  private static async Task<string> AnalyzeAsync(ApiClient client, string rawText) =>
    (await (await client.PostAsync("/api/playlist/analyze", new { rawText })).EnsureSuccessStatusCode()
      .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cacheKey").GetString()!;

  private static async Task<SearchDto> SearchAsync(ApiClient client, string cacheKey, string query = "matrix", bool? hideSeen = null, bool? liked = null)
  {
    var response = await client.Http.PostAsJsonAsync("/api/library/search", new { cacheKey, query, hideSeen, liked });
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<SearchDto>())!;
  }

  private static async Task<EntriesDto> EntriesAsync(ApiClient client, string cacheKey, string group, string options = "")
  {
    var response = await client.Http.GetAsync(
      $"/api/library/group-entries?cacheKey={Uri.EscapeDataString(cacheKey)}&group={Uri.EscapeDataString(group)}{options}");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<EntriesDto>())!;
  }

  [Fact]
  public async Task SearchAndGroupEntries_CarryTheMark_TheSameForEveryVariant()
  {
    var (client, cacheKey) = await SignedInWithMarksAsync("library-marks");

    var found = await SearchAsync(client, cacheKey);
    var byName = found.Matches.ToDictionary(m => m.Name);
    Assert.Equal(6, found.Total);
    Assert.Equal(new MarkDto("film:1999:the matrix", true, 1), byName["DE - The Matrix (1999) 4K"].Mark);
    Assert.Equal(new MarkDto("film:1999:the matrix", true, 1), byName["EN| The Matrix (1999) [FHD]"].Mark);
    Assert.Equal(new MarkDto("film:2003:the matrix reloaded", true, -1), byName["The Matrix Reloaded (2003)"].Mark);
    Assert.Equal(new MarkDto("series::matrix stories", false, 1), byName["Matrix Stories S01E02"].Mark);
    Assert.Null(MarkKey.From(NoKeyName, MarkKind.Film));
    Assert.Equal(new MarkDto(null, null, null), byName[NoKeyName].Mark);
    Assert.Equal(new MarkDto(null, null, null), byName["The Matrix (1999)"].Mark);

    var films = await EntriesAsync(client, cacheKey, "Films");
    Assert.Equal(films.Names, films.Entries.Select(e => e.Name));
    Assert.Equal(
      [new("film:1999:the matrix", true, 1), new("film:1999:the matrix", true, 1), new("film:2003:the matrix reloaded", true, -1), new(null, null, null)],
      films.Entries.Select(e => e.Mark).ToArray());
    Assert.Equal(new MarkDto("series::matrix stories", false, 1), Assert.Single((await EntriesAsync(client, cacheKey, "Series")).Entries).Mark);
    var channel = Assert.Single((await EntriesAsync(client, cacheKey, "Channels")).Entries);
    Assert.Equal(("Live", new MarkDto(null, null, null)), (channel.ContentType, channel.Mark));
  }

  [Fact]
  public async Task HideSeen_LeavesOutSeenTitles_ButNeverChannelsOrEntriesWithoutKey()
  {
    var (client, cacheKey) = await SignedInWithMarksAsync("library-hide-seen");

    var found = await SearchAsync(client, cacheKey, hideSeen: true);
    Assert.Equal([NoKeyName, "Matrix Stories S01E02", "The Matrix (1999)"], found.Matches.Select(m => m.Name));
    Assert.Equal(3, found.SeenHiddenCount);
    Assert.Equal(3, found.Total);
    Assert.Equal("Live", found.Matches[^1].ContentType);

    var films = await EntriesAsync(client, cacheKey, "Films", "&hideSeen=true");
    Assert.Equal([NoKeyName], films.Names);
    Assert.Equal((1, 3), (films.Total, films.SeenHiddenCount));
    Assert.Equal(0, (await SearchAsync(client, cacheKey)).SeenHiddenCount);
  }

  [Fact]
  public async Task Liked_ReturnsOnlyThumbsUp()
  {
    var (client, cacheKey) = await SignedInWithMarksAsync("library-liked");

    var found = await SearchAsync(client, cacheKey, liked: true);
    Assert.Equal(["DE - The Matrix (1999) 4K", "EN| The Matrix (1999) [FHD]", "Matrix Stories S01E02"], found.Matches.Select(m => m.Name));
    Assert.Equal(3, found.Total);

    var both = await SearchAsync(client, cacheKey, hideSeen: true, liked: true);
    Assert.Equal(["Matrix Stories S01E02"], both.Matches.Select(m => m.Name));
    Assert.Equal(2, both.SeenHiddenCount);

    Assert.Equal(["DE - The Matrix (1999) 4K", "EN| The Matrix (1999) [FHD]"], (await EntriesAsync(client, cacheKey, "Films", "&liked=true")).Names);
    Assert.Empty((await EntriesAsync(client, cacheKey, "Channels", "&liked=true")).Names);
  }

  [Fact]
  public async Task AnotherUsersMarks_NeverShow()
  {
    var (owner, cacheKey) = await SignedInWithMarksAsync("library-owner");
    var other = new ApiClient(factory.CreateClient());
    await other.RegisterAsync("library-other");

    var found = await SearchAsync(other, cacheKey);
    Assert.All(found.Matches, m => Assert.Null(m.Mark.Seen));
    Assert.All((await EntriesAsync(other, cacheKey, "Films")).Entries, e => Assert.Null(e.Mark.Rating));
    Assert.Empty((await SearchAsync(other, cacheKey, liked: true)).Matches);
    Assert.Equal(0, (await SearchAsync(other, cacheKey, hideSeen: true)).SeenHiddenCount);
    Assert.Equal(true, (await SearchAsync(owner, cacheKey)).Matches[0].Mark.Seen);

    var anonymous = await factory.CreateClient().PostAsJsonAsync("/api/library/search", new { cacheKey, query = "matrix" });
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
  }

  [Fact]
  public async Task EditorSearchAndGroupEntries_KeepTheirShape()
  {
    var (client, cacheKey) = await SignedInWithMarksAsync("library-editor-shape");

    var search = await client.Http.PostAsJsonAsync("/api/playlist/search", new { cacheKey, query = "matrix", hideSeen = true, liked = true });
    var body = await search.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(["matches", "hasMore", "hiddenCount", "total", "languages"], body.EnumerateObject().Select(p => p.Name));
    Assert.Equal(6, body.GetProperty("matches").GetArrayLength());
    Assert.Equal(
      ["name", "group", "groupKey", "position", "contentType", "enabled", "audioLanguages", "subtitleLanguages", "markers", "prefixTags", "languageSource"],
      body.GetProperty("matches")[0].EnumerateObject().Select(p => p.Name));

    var entries = await client.Http.GetFromJsonAsync<JsonElement>(
      $"/api/playlist/group-entries?cacheKey={Uri.EscapeDataString(cacheKey)}&group=Films&hideSeen=true");
    Assert.Equal(["groupKey", "offset", "limit", "names", "total"], entries.EnumerateObject().Select(p => p.Name));
    Assert.Equal(4, entries.GetProperty("total").GetInt32());
  }

  [Fact]
  public async Task LargePlaylist_ReadsTheMarksWithOneQueryPerRequest()
  {
    const int films = 3000;
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync("library-large");
    SeedSeenEveryOtherFilm("library-large", films);
    var cacheKey = await AnalyzeAsync(client, "#EXTM3U\n" + string.Concat(Enumerable.Range(1, films).Select(i =>
      $"#EXTINF:-1 group-title=\"Films\",Film {i} (2001)\nhttp://line.example/movie/u/p/{i}.mkv\n")));

    var before = MarkQueries();
    var found = await SearchAsync(client, cacheKey, "film", hideSeen: true);
    Assert.Equal(1, MarkQueries() - before);
    Assert.Equal((films / 2, films / 2), (found.Total, found.SeenHiddenCount));
    Assert.Equal(50, found.Matches.Count);
    Assert.All(found.Matches, m => Assert.Null(m.Mark.Seen));

    before = MarkQueries();
    var page = await EntriesAsync(client, cacheKey, "Films", "&offset=50");
    Assert.Equal(1, MarkQueries() - before);
    Assert.Equal(films, page.Total);
    Assert.Equal(25, page.Entries.Count(e => e.Mark.Seen == true));

    before = MarkQueries();
    page = await EntriesAsync(client, cacheKey, "Films", "&hideSeen=true&offset=50");
    Assert.Equal(1, MarkQueries() - before);
    Assert.Equal("Film 101 (2001)", page.Names[0]);
  }

  // Counts the SQL commands EF logged against the marks table.
  private int MarkQueries() =>
    factory.LogEntries.Count(e => e.Contains("Executed DbCommand", StringComparison.Ordinal) && e.Contains("titlemarks", StringComparison.Ordinal));

  // Marks "Film 2 (2001)", "Film 4 (2001)", ... seen with one SQL statement.
  private void SeedSeenEveryOtherFilm(string userName, int films)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.ExecuteSqlRaw(
      """
      WITH RECURSIVE n(i) AS (SELECT 2 UNION ALL SELECT i + 2 FROM n WHERE i + 2 <= {1})
      INSERT INTO titlemarks (id, userid, kind, titlekey, year, displaytitle, seen, rating, filmdbid, watchlist, createdutc, updatedutc)
      SELECT printf('00000000-0000-0000-0001-%012d', i), {0}, 'film', 'film:2001:film ' || i, 2001, 'Film ' || i,
             1, 0, NULL, 0, strftime('%Y-%m-%d %H:%M:%S+00:00', 'now'), strftime('%Y-%m-%d %H:%M:%S+00:00', 'now')
      FROM n
      """,
      db.Users.Single(u => u.UserName == userName).Id, films);
  }

  private sealed record MarkDto(string? MarkKey, bool? Seen, int? Rating);

  private sealed record MatchDto(string Name, string ContentType, string? MarkKey, bool? Seen, int? Rating)
  {
    public MarkDto Mark => new(MarkKey, Seen, Rating);
  }
  private sealed record SearchDto(List<MatchDto> Matches, int SeenHiddenCount, int Total);
  private sealed record EntriesDto(List<string> Names, List<MatchDto> Entries, int Total, int SeenHiddenCount);
}
