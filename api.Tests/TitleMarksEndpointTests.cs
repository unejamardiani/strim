using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Endpoints;
using Api.Models;
using Api.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public class TitleMarksEndpointTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private const string MatrixKey = "film:1999:the matrix";

  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static Task<HttpResponseMessage> SetAsync(ApiClient client, string name, bool? seen = null, int? rating = null, string kind = "film", bool withCsrf = true) =>
    client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind, seen, rating }, withCsrf);

  private static Task<HttpResponseMessage> DeleteAsync(ApiClient client, string key, bool withCsrf = true) =>
    client.SendAsync(HttpMethod.Delete, "/api/marks/" + Uri.EscapeDataString(key), null, withCsrf);

  private static async Task<MarkPage> ListAsync(ApiClient client, string query = "") =>
    (await client.Http.GetFromJsonAsync<MarkPage>("/api/marks" + query))!;

  private T WithDb<T>(Func<AppDbContext, T> action)
  {
    using var scope = factory.Services.CreateScope();
    return action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
  }

  private int RowCount(string userName) =>
    WithDb(db => db.TitleMarks.Count(m => db.Users.Any(u => u.Id == m.UserId && u.UserName == userName)));

  [Fact]
  public async Task SeenThenThumbsUp_StoresOneMark_AndAnotherVariantUpdatesIt()
  {
    var client = await SignedInAsync("marks-variant");

    Assert.Equal(HttpStatusCode.OK, (await SetAsync(client, "DE - The Matrix (1999) 4K", seen: true)).StatusCode);
    var up = await SetAsync(client, "DE - The Matrix (1999) 4K", rating: 1);
    var mark = (await up.Content.ReadFromJsonAsync<MarkDto>())!;
    Assert.Equal(new MarkDto(MatrixKey, "film", "The Matrix", 1999, true, 1), mark);

    await SetAsync(client, "EN| The Matrix (1999) [FHD]", rating: -1);

    var page = await ListAsync(client);
    Assert.Equal(1, page.Total);
    Assert.Equal(new MarkDto(MatrixKey, "film", "The Matrix", 1999, true, -1), Assert.Single(page.Items));
    Assert.Equal(1, RowCount("marks-variant"));
  }

  [Fact]
  public async Task UndoOrDelete_LeavesNoRow()
  {
    var client = await SignedInAsync("marks-undo");

    await SetAsync(client, "The Matrix (1999)", seen: true, rating: 1);
    Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(client, "The Matrix (1999)", seen: false, rating: 0)).StatusCode);
    Assert.Equal(0, RowCount("marks-undo"));

    await SetAsync(client, "The Matrix (1999)", seen: true);
    Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, MatrixKey)).StatusCode);
    Assert.Equal(0, RowCount("marks-undo"));
    Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(client, MatrixKey)).StatusCode);

    // Setting an empty mark that never existed stores nothing.
    Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(client, "Heat (1995)", seen: false)).StatusCode);
    Assert.Equal(0, RowCount("marks-undo"));
  }

  [Fact]
  public async Task SameTitle_OtherYear_StaysTwoMarks()
  {
    var client = await SignedInAsync("marks-dune");

    await SetAsync(client, "Dune (1984)", seen: true);
    await SetAsync(client, "Dune (2021)", rating: 1);

    var page = await ListAsync(client);
    Assert.Equal(new[] { 1984, 2021 }, page.Items.Select(m => m.Year!.Value).Order());
  }

  [Fact]
  public async Task StateChangingCalls_WithoutCsrf_AreRejected()
  {
    var client = await SignedInAsync("marks-csrf");
    await SetAsync(client, "The Matrix (1999)", seen: true);

    var put = await SetAsync(client, "Heat (1995)", seen: true, withCsrf: false);
    var delete = await DeleteAsync(client, MatrixKey, withCsrf: false);

    Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    Assert.Contains(EndpointFilters.CsrfError, await put.Content.ReadAsStringAsync());
    Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    Assert.Contains(EndpointFilters.CsrfError, await delete.Content.ReadAsStringAsync());
    Assert.Equal(new[] { MatrixKey }, (await ListAsync(client)).Items.Select(m => m.Key));
  }

  [Fact]
  public async Task Anonymous_IsRejected_AndUsersSeeOnlyTheirOwnMarks()
  {
    var anonymous = new ApiClient(factory.CreateClient());
    Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/marks")).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await SetAsync(anonymous, "The Matrix (1999)", seen: true)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await DeleteAsync(anonymous, MatrixKey)).StatusCode);

    var b = await SignedInAsync("marks-owner-b");
    await SetAsync(b, "The Matrix (1999)", seen: true, rating: 1);
    var a = await SignedInAsync("marks-owner-a");

    Assert.Empty((await ListAsync(a)).Items);
    Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(a, MatrixKey)).StatusCode);
    await SetAsync(a, "The Matrix (1999)", seen: false, rating: 0);

    Assert.Equal(new MarkDto(MatrixKey, "film", "The Matrix", 1999, true, 1), Assert.Single((await ListAsync(b)).Items));
    Assert.Equal(0, RowCount("marks-owner-a"));
  }

  [Fact]
  public async Task List_ReturnsMarksWithoutAnyPlaylist_AndFiltersAndPages()
  {
    var client = await SignedInAsync("marks-list");
    await SetAsync(client, "Alien (1979)", seen: true);
    await SetAsync(client, "Brazil (1985)", rating: 1);
    await SetAsync(client, "Dark S01E01", rating: -1, kind: "series");

    Assert.Equal(3, (await ListAsync(client)).Total);
    Assert.Equal(new[] { "Alien" }, (await ListAsync(client, "?seen=true")).Items.Select(m => m.Title));
    Assert.Equal(new[] { "Brazil" }, (await ListAsync(client, "?rating=1")).Items.Select(m => m.Title));
    Assert.Equal(new[] { "series::dark" }, (await ListAsync(client, "?rating=-1")).Items.Select(m => m.Key));

    var second = await ListAsync(client, "?page=2&pageSize=2");
    Assert.Equal(3, second.Total);
    Assert.Single(second.Items);
    Assert.Equal(HttpStatusCode.BadRequest, (await client.Http.GetAsync($"/api/marks?pageSize={MarkEndpoints.MaxPageSize + 1}")).StatusCode);
  }

  [Fact]
  public async Task PastTheCap_NewMarkIsRefused_ExistingMarksStayEditable()
  {
    var client = await SignedInAsync("marks-cap");
    await SetAsync(client, "The Matrix (1999)", seen: true);
    WithDb(db =>
    {
      var userId = db.Users.Single(u => u.UserName == "marks-cap").Id;
      // One SQL statement instead of thousands of tracked entities: the bulk insert stays cheap, so it does not
      // slow down the timing-sensitive tests that run in parallel.
      return db.Database.ExecuteSqlRaw(
        """
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {1})
        INSERT INTO titlemarks (id, userid, kind, titlekey, year, displaytitle, seen, rating, filmdbid, createdutc, updatedutc)
        SELECT printf('00000000-0000-0000-0000-%012d', i), {0}, 'film', 'film::filler ' || i, NULL, 'Filler ' || i,
               1, 0, NULL, strftime('%Y-%m-%d %H:%M:%S+00:00', 'now'), strftime('%Y-%m-%d %H:%M:%S+00:00', 'now')
        FROM n
        """,
        userId, MarkEndpoints.MaxMarksPerUser - 1);
    });

    var refused = await SetAsync(client, "Heat (1995)", seen: true);
    Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    Assert.Contains("limit", await refused.Content.ReadAsStringAsync());
    Assert.Equal(MarkEndpoints.MaxMarksPerUser, RowCount("marks-cap"));

    Assert.Equal(HttpStatusCode.OK, (await SetAsync(client, "The Matrix (1999)", rating: 1)).StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, MatrixKey)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await SetAsync(client, "Heat (1995)", seen: true)).StatusCode);
  }

  [Theory]
  [InlineData("film", 501, null)]
  [InlineData("documentary", 10, null)]
  [InlineData("film", 10, 2)]
  [InlineData("film", 0, null)]
  public async Task InvalidInput_IsRejected(string kind, int nameLength, int? rating)
  {
    var client = await SignedInAsync($"marks-input-{kind}-{nameLength}-{rating}");

    var response = await SetAsync(client, new string('a', nameLength), seen: true, rating: rating, kind: kind);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Empty((await ListAsync(client)).Items);
  }

  [Fact]
  public async Task List_ShowsTheNewestChangeFirst_AcrossPages()
  {
    var client = await SignedInAsync("marks-newest");
    await SetAsync(client, "Alien (1979)", seen: true);
    await SetAsync(client, "Brazil (1985)", rating: 1);
    await SetAsync(client, "Dark S01E01", rating: -1, kind: "series");
    // Fixed times, so the order does not hang on the clock between requests: Brazil oldest, then Dark, Alien newest.
    WithDb(db =>
    {
      var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
      foreach (var (key, minutes) in new[] { ("film:1985:brazil", 0), ("series::dark", 1), ("film:1979:alien", 2) })
        db.TitleMarks.Where(m => m.TitleKey == key).ExecuteUpdate(s => s.SetProperty(m => m.UpdatedUtc, start.AddMinutes(minutes)));
      return 0;
    });

    Assert.Equal(new[] { "Alien", "Dark", "Brazil" }, (await ListAsync(client)).Items.Select(m => m.Title));
    Assert.Equal(new[] { "Alien", "Dark" }, (await ListAsync(client, "?pageSize=2")).Items.Select(m => m.Title));
    Assert.Equal(new[] { "Brazil" }, (await ListAsync(client, "?page=2&pageSize=2")).Items.Select(m => m.Title));

    // A new change moves the mark to the top.
    await SetAsync(client, "Brazil (1985)", seen: true);
    Assert.Equal(new[] { "Brazil", "Alien", "Dark" }, (await ListAsync(client)).Items.Select(m => m.Title));
  }

  [Fact]
  public async Task UndoAfterRemove_SetsTheSameMarkAgain_FromItsTitleAndYear()
  {
    var client = await SignedInAsync("marks-remove-undo");
    await SetAsync(client, "DE - The Matrix (1999) 4K", seen: true, rating: 1);
    await SetAsync(client, "Dark S01E01", rating: -1, kind: "series");
    var before = (await ListAsync(client)).Items.OrderBy(m => m.Key).ToList();

    // What the Library's My marks list sends: DELETE by key, then the set call with "title (year)" or the title.
    foreach (var mark in before)
    {
      Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, mark.Key)).StatusCode);
      var name = mark.Year is { } year ? $"{mark.Title} ({year})" : mark.Title;
      var restored = await client.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind = mark.Kind, seen = mark.Seen, rating = mark.Rating });
      Assert.Equal(mark, await restored.Content.ReadFromJsonAsync<MarkDto>());
    }

    Assert.Equal(before, (await ListAsync(client)).Items.OrderBy(m => m.Key));
    Assert.Equal(2, RowCount("marks-remove-undo"));
    Assert.Equal(HttpStatusCode.BadRequest, (await DeleteAsync(client, MatrixKey, withCsrf: false)).StatusCode);
  }

  [Fact]
  public async Task Marks_NeverReachPlayerLinkOrDownload()
  {
    const string Source = "http://93.184.215.14/get.php?username=marks&password=p";
    const string Playlist = """
      #EXTM3U
      #EXTINF:-1 group-title="Films",DE - The Matrix (1999) 4K
      http://line.example/movie/u/p/1.mkv
      #EXTINF:-1 group-title="Films",Dune (2021)
      http://line.example/movie/u/p/2.mkv
      """;
    factory.UpstreamPlaylists[Source] = Playlist;
    var client = await SignedInAsync("marks-output");
    var created = await client.PostAsync("/api/playlists", new { name = "Films", sourceUrl = Source, disabledGroups = Array.Empty<string>() });
    created.EnsureSuccessStatusCode();
    var playlist = (await created.Content.ReadFromJsonAsync<PlaylistDto>())!;
    var sharePath = $"/api/playlists/{playlist.Id}/share/{playlist.ShareCode}";

    var shareBefore = await factory.CreateClient().GetStringAsync(sharePath);
    var downloadBefore = await DownloadAsync(client, Playlist);

    await SetAsync(client, "DE - The Matrix (1999) 4K", seen: true, rating: 1);
    await SetAsync(client, "Dune (2021)", rating: -1);

    var shareAfter = await factory.CreateClient().GetStringAsync(sharePath);
    var downloadAfter = await DownloadAsync(client, Playlist);
    Assert.Equal(shareBefore, shareAfter);
    Assert.Equal(downloadBefore, downloadAfter);
    foreach (var text in new[] { shareAfter, downloadAfter })
    {
      Assert.DoesNotContain(MatrixKey, text);
      Assert.DoesNotContain("seen", text, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("rating", text, StringComparison.OrdinalIgnoreCase);
    }
  }

  private static async Task<string> DownloadAsync(ApiClient client, string rawText)
  {
    var analyzed = await client.PostAsync("/api/playlist/analyze", new { rawText });
    analyzed.EnsureSuccessStatusCode();
    var cacheKey = (await analyzed.Content.ReadFromJsonAsync<CacheKeyDto>())!.CacheKey;
    var generated = await client.PostAsync("/api/playlist/generate", new { cacheKey, disabledGroups = Array.Empty<string>() });
    generated.EnsureSuccessStatusCode();
    var outputKey = (await generated.Content.ReadFromJsonAsync<OutputKeyDto>())!.OutputKey;
    return await client.Http.GetStringAsync($"/api/playlist/output/{outputKey}");
  }

  private sealed record MarkDto(string Key, string Kind, string Title, int? Year, bool Seen, int Rating);
  private sealed record MarkPage(List<MarkDto> Items, int Total, int Page, int PageSize);
  private sealed record CacheKeyDto(string CacheKey);
  private sealed record OutputKeyDto(string OutputKey);
}
