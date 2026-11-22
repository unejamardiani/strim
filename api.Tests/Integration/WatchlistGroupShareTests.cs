using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>One host for the class: revalidates the source on every share request and records every log entry.</summary>
public sealed class WatchlistShareApp : IDisposable
{
  public const string Url = "http://93.184.215.14/get.php?username=w&password=p";

  public const string Source = """
    #EXTM3U url-tvg="http://epg.example/guide.xml"
    #EXTINF:-1 group-title="News",News One
    http://line.example/live/u/p/1.ts
    #EXTINF:-1 group-title="Films",Little Motel (1995)
    http://line.example/movie/u/p/1.mp4
    #EXTINF:-1 group-title="Films",Other Film (2001)
    http://line.example/movie/u/p/2.mp4
    #EXTINF:-1 group-title="Films",Third Film (2010)
    http://line.example/movie/u/p/3.mp4
    #EXTINF:-1 group-title="Old Films",Hidden Film (1980)
    http://line.example/movie/u/p/4.mp4
    #EXTINF:-1 group-title="Adults",Night Film (1999)
    http://line.example/movie/u/p/5.mp4
    #EXTINF:-1 group-title="Shows",Harbour Watch S01E01
    http://line.example/series/u/p/10.mkv
    #EXTINF:-1 group-title="Shows",Harbour Watch S01E02
    http://line.example/series/u/p/11.mkv
    #EXTINF:-1 group-title="Shows",Quiet Hills S01E01
    http://line.example/series/u/p/12.mkv
    """;

  public StrimAppFactory Upstream { get; } = new();
  public WebApplicationFactory<Program> App { get; }
  public ConcurrentQueue<string> Logs { get; } = new();

  public WatchlistShareApp()
  {
    Upstream.UpstreamPlaylists[Url] = Source;
    App = Upstream.WithWebHostBuilder(builder => builder
      .UseSetting("PlaylistCache:RevalidationIntervalMinutes", "0")
      .UseSetting("PlaylistCache:HeaderTimeoutSeconds", "1")
      .ConfigureLogging(logging =>
      {
        logging.AddProvider(new AllLogs(Logs));
        logging.AddFilter<AllLogs>(null, LogLevel.Trace);
      }));
  }

  public void Dispose()
  {
    App.Dispose();
    Upstream.Dispose();
  }

  private sealed class AllLogs(ConcurrentQueue<string> entries) : ILoggerProvider, ILogger
  {
    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
      entries.Enqueue($"scope: {state}");
      return null;
    }
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
      entries.Enqueue($"{formatter(state, exception)} {values} {exception}");
    }
    public void Dispose()
    {
    }
  }
}

public class WatchlistGroupShareTests(WatchlistShareApp host) : IClassFixture<WatchlistShareApp>
{
  private const string Label = "Sofa label q7w3";
  private const string Little = "Little Motel (1995)", Other = "Other Film (2001)", Third = "Third Film (2010)";
  private const string Harbour = "Harbour Watch S01E01", Quiet = "Quiet Hills S01E01";

  private sealed record Setup(ApiClient Owner, string OwnerName, Guid Id, string Path, ApiClient Viewer, string ViewerName, Guid ShareId);

  private async Task<ApiClient> UserAsync(string name)
  {
    var client = new ApiClient(host.App.CreateClient());
    await client.RegisterAsync(name);
    return client;
  }

  /// <summary>An owner with a playlist ("Old Films" off), an accepted viewer and, unless off, the switch on.</summary>
  private async Task<Setup> SetupAsync(bool switchOn = true)
  {
    var tag = Guid.NewGuid().ToString("N")[..8];
    var owner = await UserAsync($"wl-owner-{tag}");
    var created = await owner.PostAsync("/api/playlists", new { name = "Sofa", sourceUrl = WatchlistShareApp.Url, disabledGroups = new[] { "Old Films" } });
    created.EnsureSuccessStatusCode();
    var playlist = await created.Content.ReadFromJsonAsync<JsonElement>();
    var id = playlist.GetProperty("id").GetGuid();
    if (switchOn) await SwitchAsync(owner, id, true);

    var invite = await (await owner.PostAsync($"/api/playlists/{id}/shares", new { label = Label })).Content.ReadFromJsonAsync<JsonElement>();
    var viewer = await UserAsync($"wl-viewer-{tag}");
    (await viewer.PostAsync("/api/shares/accept", new { token = invite.GetProperty("inviteToken").GetString() })).EnsureSuccessStatusCode();
    return new Setup(owner, $"wl-owner-{tag}", id, $"/api/playlists/{id}/share/{playlist.GetProperty("shareCode").GetString()}",
      viewer, $"wl-viewer-{tag}", invite.GetProperty("share").GetProperty("id").GetGuid());
  }

  private static async Task SwitchAsync(ApiClient owner, Guid id, bool on) =>
    (await owner.SendAsync(HttpMethod.Patch, $"/api/playlists/{id}/watchlist-group", new { enabled = on })).EnsureSuccessStatusCode();

  private static async Task MarkAsync(ApiClient user, string name, bool watchlist = true, bool? seen = null)
  {
    var kind = name.Contains("S01") ? "series" : "film";
    var response = await user.SendAsync(HttpMethod.Put, "/api/marks", new { name, kind, watchlist, seen });
    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    await Task.Delay(15); // distinct watchlist times
  }

  private Task<string> ShareAsync(Setup s) => host.App.CreateClient().GetStringAsync(s.Path);

  private static List<(string Group, string Name, string Url)> Entries(string text)
  {
    var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var entries = new List<(string, string, string)>();
    for (var i = 0; i < lines.Length; i++)
    {
      if (!lines[i].StartsWith("#EXTINF")) continue;
      var group = Regex.Match(lines[i], "group-title=\"([^\"]*)\"").Groups[1].Value;
      var url = lines.Skip(i + 1).First(l => !l.StartsWith('#'));
      entries.Add((group, lines[i][(lines[i].LastIndexOf(',') + 1)..], url));
    }
    return entries;
  }

  /// <summary>The copies as "group|name", in output order.</summary>
  private static List<string> Copies(string text) =>
    Entries(text).Where(e => e.Group.StartsWith(WatchlistGroup.FilmsTitle)).Select(e => $"{e.Group}|{e.Name}").ToList();

  private static string F(string name) => $"{WatchlistGroup.FilmsTitle}|{name}";
  private static string S(string name) => $"{WatchlistGroup.SeriesTitle}|{name}";

  [Fact]
  public async Task Share_WithTheSwitchOn_StartsWithTheOwnersWatchlist()
  {
    var s = await SetupAsync();
    var plain = await ShareAsync(s);
    await MarkAsync(s.Owner, Little);
    await MarkAsync(s.Owner, "Harbour Watch S01E02");

    var text = await ShareAsync(s);

    var lines = text.Replace("\r\n", "\n").Split('\n');
    Assert.Equal("#EXTM3U url-tvg=\"http://epg.example/guide.xml\"", lines[0]);
    Assert.StartsWith("# Created with Strim", lines[1]);
    Assert.Equal([F(Little), S("Harbour Watch S01E01"), S("Harbour Watch S01E02")], Entries(text).Take(3).Select(e => $"{e.Group}|{e.Name}"));
    Assert.Equal(Entries(plain), Entries(text).Skip(3));
  }

  [Fact]
  public async Task Share_WithTheSwitchOff_ServesTodaysBytes()
  {
    var s = await SetupAsync(switchOn: false);
    var before = await ShareAsync(s);
    await MarkAsync(s.Owner, Little);
    await MarkAsync(s.Viewer, Quiet);

    Assert.Equal(before, await ShareAsync(s));
    Assert.Empty(Copies(before));
  }

  [Fact]
  public async Task Share_AfterTheWatchlistChanged_ServesTheNewGroupAtOnce()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Other);
    Assert.Equal([F(Other)], Copies(await ShareAsync(s)));
    await MarkAsync(s.Owner, Other, watchlist: false);
    Assert.Empty(Copies(await ShareAsync(s)));
  }

  [Fact]
  public async Task Share_AnAcceptedViewersWatchlist_IsInTheSameGroups()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Little);
    await MarkAsync(s.Viewer, Quiet);
    await MarkAsync(s.Viewer, Third);
    await MarkAsync(s.Owner, Other);

    Assert.Equal([F(Other), F(Third), F(Little), S(Quiet)], Copies(await ShareAsync(s)));
  }

  [Fact]
  public async Task Share_TheSameTitleFromOwnerAndViewer_IsWrittenOnce()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Little);
    await MarkAsync(s.Viewer, Little);
    Assert.Equal([F(Little)], Copies(await ShareAsync(s)));
  }

  [Fact]
  public async Task Share_OwnerMarksATitleSeen_ItLeaves_UnlessAViewerStillListsIt()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Little);
    await MarkAsync(s.Owner, Other);
    await MarkAsync(s.Viewer, Other);

    await MarkAsync(s.Owner, Little, seen: true);
    await MarkAsync(s.Owner, Other, seen: true);

    Assert.Equal([F(Other)], Copies(await ShareAsync(s)));
  }

  [Fact]
  public async Task Share_AViewersChange_ShowsAtTheNextRequest()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Viewer, Harbour);
    Assert.Equal([S(Harbour), S("Harbour Watch S01E02")], Copies(await ShareAsync(s)));
    await MarkAsync(s.Viewer, Harbour, watchlist: false);
    Assert.Empty(Copies(await ShareAsync(s)));
  }

  [Fact]
  public async Task Share_ARevokedInvitation_StopsContributingAtTheNextRequest()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Viewer, Third);
    Assert.Equal([F(Third)], Copies(await ShareAsync(s)));
    Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.SendAsync(HttpMethod.Delete, $"/api/playlists/{s.Id}/shares/{s.ShareId}")).StatusCode);
    Assert.Empty(Copies(await ShareAsync(s)));

    var leaver = await SetupAsync();
    await MarkAsync(leaver.Viewer, Third);
    Assert.Equal([F(Third)], Copies(await ShareAsync(leaver)));
    Assert.True((await leaver.Viewer.SendAsync(HttpMethod.Delete, $"/api/shares/mine/{leaver.Id}")).IsSuccessStatusCode);
    Assert.Empty(Copies(await ShareAsync(leaver)));
  }

  [Fact]
  public async Task Share_AnOpenInvitationAndAStranger_ContributeNothing()
  {
    var s = await SetupAsync();
    (await s.Owner.PostAsync($"/api/playlists/{s.Id}/shares", new { label = "Open" })).EnsureSuccessStatusCode();
    var stranger = await UserAsync($"wl-stranger-{Guid.NewGuid():N}"[..20]);
    await MarkAsync(stranger, Little);
    var other = await SetupAsync(); // a viewer of another playlist
    await MarkAsync(other.Viewer, Quiet);

    Assert.Empty(Copies(await ShareAsync(s)));
  }

  [Fact]
  public async Task Share_NoNameReachesTheOutput()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Little);
    await MarkAsync(s.Viewer, Quiet);

    var response = await host.App.CreateClient().GetAsync(s.Path);
    var all = await response.Content.ReadAsStringAsync()
      + string.Join("\n", response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));

    Assert.Equal(2, Copies(all).Count);
    foreach (var secret in new[] { Label, s.OwnerName, s.ViewerName, "@" }) Assert.DoesNotContain(secret, all);
  }

  [Fact]
  public async Task Share_NobodyHasAWatchlist_ServesThePlainList()
  {
    var plain = await ShareAsync(await SetupAsync(switchOn: false));
    Assert.Equal(plain, await ShareAsync(await SetupAsync()));
  }

  [Fact]
  public async Task Share_SwitchedOffAndAdultGroups_StayOutOfTheGroup()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, "Hidden Film (1980)");
    await MarkAsync(s.Viewer, "Night Film (1999)");
    var text = await ShareAsync(s);
    Assert.Empty(Copies(text));
    Assert.Equal(await ShareAsync(await SetupAsync(switchOn: false)), text);
  }

  [Fact]
  public async Task Share_UpstreamError_ServesTheLastGoodCopyWithItsGroup()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Viewer, Little);
    var good = await ShareAsync(s);
    try
    {
      host.Upstream.Fault = UpstreamFault.ServerError;
      var stale = await host.App.CreateClient().GetAsync(s.Path);
      Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
      Assert.Equal("1", Assert.Single(stale.Headers.GetValues("X-Strim-Stale")));
      var text = await stale.Content.ReadAsStringAsync();
      Assert.Equal(good, text);
      Assert.Equal([F(Little)], Copies(text));
    }
    finally
    {
      host.Upstream.Fault = UpstreamFault.None;
    }
  }

  [Fact]
  public async Task Share_WrongCodeOrLinkOff_Is404_AndReadsNoMarks()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Little);
    var anonymous = host.App.CreateClient();
    var requests = host.Upstream.UpstreamRequests.Count;
    host.Logs.Clear();

    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/playlists/{s.Id}/share/wrong")).StatusCode);
    (await s.Owner.SendAsync(HttpMethod.Patch, $"/api/playlists/{s.Id}/toggle-active")).EnsureSuccessStatusCode();
    host.Logs.Clear();
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(s.Path)).StatusCode);

    // The marks are read after the source; neither request got that far, and no query touched the marks table.
    Assert.Equal(requests, host.Upstream.UpstreamRequests.Count);
    Assert.DoesNotContain(host.Logs, e => e.Contains("TitleMarks", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task Generate_FromTheEditor_NeverHoldsTheGroup()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Little);
    async Task<string> GenerateAsync()
    {
      var response = await s.Owner.PostAsync("/api/playlist/generate", new { sourceUrl = WatchlistShareApp.Url, disabledGroups = new[] { "Old Films" } });
      Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
      var key = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outputKey").GetString();
      return await s.Owner.Http.GetStringAsync($"/api/playlist/output/{key}");
    }

    var before = await GenerateAsync();
    Assert.Equal([F(Little)], Copies(await ShareAsync(s)));
    var after = await GenerateAsync();

    Assert.Empty(Copies(before));
    Assert.Equal(before, after);
  }

  [Fact]
  public async Task Share_LogsNoTitleNoMarkKeyAndNoPerson()
  {
    var s = await SetupAsync();
    await MarkAsync(s.Owner, Little);
    await MarkAsync(s.Viewer, Quiet);
    host.Logs.Clear();

    Assert.Equal(2, Copies(await ShareAsync(s)).Count);

    var secrets = new[]
    {
      "Little Motel", "Quiet Hills", MarkKey.From(Little, MarkKind.Film)!.Key, MarkKey.From(Quiet, MarkKind.Series)!.Key,
      Label, s.OwnerName, s.ViewerName,
    };
    // The marks query itself is logged (without parameters), which the 404 test relies on.
    Assert.Contains(host.Logs, e => e.Contains("TitleMarks", StringComparison.OrdinalIgnoreCase));
    foreach (var entry in host.Logs)
    {
      foreach (var secret in secrets) Assert.DoesNotContain(secret, entry, StringComparison.OrdinalIgnoreCase);
    }
  }
}
