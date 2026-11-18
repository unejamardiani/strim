using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public class PlaylistOverviewTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  // Placeholder host and credentials only; never a real provider.
  private const string CredentialedSource = "http://Provider.Example:8080/get.php?username=sofa-user&password=sofa-secret&type=m3u_plus";

  private async Task<ApiClient> SignedInAsync(string userName)
  {
    var client = new ApiClient(factory.CreateClient());
    await client.RegisterAsync(userName);
    return client;
  }

  private static async Task<Guid> CreateAsync(ApiClient client, object body)
  {
    var response = await client.PostAsync("/api/playlists", body);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<PlaylistDto>())!.Id;
  }

  private static async Task<List<OverviewDto>> OverviewAsync(ApiClient client) =>
    (await client.Http.GetFromJsonAsync<List<OverviewDto>>("/api/playlists/overview"))!;

  [Fact]
  public async Task Anonymous_GetsUnauthorized()
  {
    var response = await factory.CreateClient().GetAsync("/api/playlists/overview");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task ReturnsOnlyTheCallersPlaylists()
  {
    var owner = await SignedInAsync("overview-owner");
    var mine = await CreateAsync(owner, new { name = "Mine", sourceUrl = CredentialedSource });

    var other = await SignedInAsync("overview-other");
    var theirs = await CreateAsync(other, new { name = "Theirs", sourceUrl = CredentialedSource });

    Assert.Equal(new[] { mine }, (await OverviewAsync(owner)).Select(p => p.Id));
    Assert.Equal(new[] { theirs }, (await OverviewAsync(other)).Select(p => p.Id));
  }

  [Fact]
  public async Task Order_MatchesThePlaylistList()
  {
    var client = await SignedInAsync("overview-order");
    var first = await CreateAsync(client, new { name = "First" });
    await CreateAsync(client, new { name = "Second" });
    await CreateAsync(client, new { name = "Third" });
    // Saving the oldest one again moves it to the top of both lists.
    (await client.SendAsync(HttpMethod.Put, $"/api/playlists/{first}", new { name = "First again" })).EnsureSuccessStatusCode();

    var list = (await client.Http.GetFromJsonAsync<List<PlaylistDto>>("/api/playlists"))!.Select(p => p.Id).ToList();
    var overview = (await OverviewAsync(client)).Select(p => p.Id).ToList();

    Assert.Equal(3, overview.Count);
    Assert.Equal(first, overview[0]);
    Assert.Equal(list, overview);
  }

  [Fact]
  public async Task GroupCounts_SubtractDistinctDisabledGroups_AndAreNullWhenNotStored()
  {
    var client = await SignedInAsync("overview-groups");
    var some = await CreateAsync(client, new { name = "Some", groupCount = 5, disabledGroups = new[] { "News", "News", "Kids" } });
    var tooMany = await CreateAsync(client, new { name = "Too many", groupCount = 1, disabledGroups = new[] { "News", "Kids" } });
    var unknown = await CreateAsync(client, new { name = "Unknown", disabledGroups = new[] { "News" } });

    var overview = (await OverviewAsync(client)).ToDictionary(p => p.Id);

    Assert.Equal(5, overview[some].GroupCount);
    Assert.Equal(3, overview[some].GroupsOn);
    Assert.Equal(1, overview[tooMany].GroupCount);
    Assert.Equal(0, overview[tooMany].GroupsOn);
    Assert.Null(overview[unknown].GroupCount);
    Assert.Null(overview[unknown].GroupsOn);
  }

  [Fact]
  public async Task TotalChannels_IsTheStoredValue_OrNullWhenZero()
  {
    var client = await SignedInAsync("overview-total");
    var known = await CreateAsync(client, new { name = "Known", totalChannels = 1234 });
    var unknown = await CreateAsync(client, new { name = "Unknown", totalChannels = 0 });

    var overview = (await OverviewAsync(client)).ToDictionary(p => p.Id);

    Assert.Equal(1234, overview[known].TotalChannels);
    Assert.Null(overview[unknown].TotalChannels);
  }

  [Fact]
  public async Task ViewAndRefreshTimes_AreStoredValues_OrNull()
  {
    var client = await SignedInAsync("overview-times");
    var seen = await CreateAsync(client, new { name = "Seen" });
    var never = await CreateAsync(client, new { name = "Never" });
    var viewed = new DateTimeOffset(2026, 11, 10, 20, 15, 0, TimeSpan.Zero);
    var refreshed = new DateTimeOffset(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

    using (var scope = factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var row = await db.Playlists.SingleAsync(p => p.Id == seen);
      row.LastViewedUtc = viewed;
      row.LastRefreshedUtc = refreshed;
      await db.SaveChangesAsync();
    }

    var overview = (await OverviewAsync(client)).ToDictionary(p => p.Id);

    Assert.Equal(viewed, overview[seen].LastViewedUtc);
    Assert.Equal(refreshed, overview[seen].LastRefreshedUtc);
    Assert.Null(overview[never].LastViewedUtc);
    Assert.Null(overview[never].LastRefreshedUtc);
  }

  [Fact]
  public async Task SourceHost_IsTheHostOnly_AndTheBodyCarriesNoCredentialsOrShareCode()
  {
    var client = await SignedInAsync("overview-host");
    var id = await CreateAsync(client, new { name = "Sofa", sourceUrl = CredentialedSource, shareCode = "overview-share-code-1" });
    var none = await CreateAsync(client, new { name = "Pasted" });

    var response = await client.Http.GetAsync("/api/playlists/overview");
    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadAsStringAsync();

    foreach (var leaked in new[] { "sofa-user", "sofa-secret", "get.php", "username", "password", "m3u_plus",
      // The port with its colon: a bare "8080" can occur inside a random playlist GUID.
      ":8080",
      "overview-share-code-1", "sourceUrl", "shareCode" })
    {
      Assert.DoesNotContain(leaked, body, StringComparison.OrdinalIgnoreCase);
    }

    var overview = (await response.Content.ReadFromJsonAsync<List<OverviewDto>>())!.ToDictionary(p => p.Id);
    Assert.Equal("provider.example", overview[id].SourceHost);
    Assert.Null(overview[none].SourceHost);
  }

  [Fact]
  public async Task SavingThroughPut_MovesUpdatedAtForward()
  {
    var client = await SignedInAsync("overview-updated");
    var id = await CreateAsync(client, new { name = "Before", groupCount = 4 });
    var before = (await OverviewAsync(client)).Single(p => p.Id == id).UpdatedAt;

    await Task.Delay(20);
    (await client.SendAsync(HttpMethod.Put, $"/api/playlists/{id}", new { name = "After", disabledGroups = new[] { "News" } }))
      .EnsureSuccessStatusCode();

    var after = (await OverviewAsync(client)).Single(p => p.Id == id);
    Assert.True(after.UpdatedAt > before, $"updatedAt {after.UpdatedAt:O} is not after {before:O}");
    Assert.Equal("After", after.Name);
    Assert.Equal(3, after.GroupsOn);
  }

  [Theory]
  [InlineData("POST")]
  [InlineData("PUT")]
  [InlineData("PATCH")]
  [InlineData("DELETE")]
  public async Task Overview_IsGetOnly(string method)
  {
    var client = await SignedInAsync($"overview-verb-{method.ToLowerInvariant()}");
    var response = await client.SendAsync(new HttpMethod(method), "/api/playlists/overview", new { name = "x" });
    // The JSON fallback for /api answers an unmapped verb with 404: no route other than GET exists here.
    Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
  }

  private sealed record OverviewDto(
    Guid Id,
    string Name,
    int? GroupCount,
    int? GroupsOn,
    int? TotalChannels,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastViewedUtc,
    DateTimeOffset? LastRefreshedUtc,
    string? SourceHost);
}
