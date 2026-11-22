using System.Text;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class WatchlistGroupGenerationTests
{
  private const string Header = "#EXTM3U url-tvg=\"http://line.example/epg.xml\"";

  private static string Film(string name) => MarkKey.From(name, MarkKind.Film)!.Key;

  private static string Series(string name) => MarkKey.From(name, MarkKind.Series)!.Key;

  private static string Movie(int id) => $"http://line.example/movie/u/p/{id}.mp4";

  private static string Episode(int id) => $"http://line.example/series/u/p/{id}.mkv";

  private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

  private static HashSet<string> Disabled(params string[] groups) => new(groups, StringComparer.OrdinalIgnoreCase);

  private sealed record Run(byte[] Bytes, string[] Lines, PlaylistGenerationResult Result);

  /// <summary>Generates without the watchlist argument when <paramref name="passWatchlist"/> is false.</summary>
  private static async Task<Run> GenerateAsync(string sourcePath, WatchlistGroup? watchlist, ISet<string>? disabled = null,
    IReadOnlySet<string>? split = null, long maxOutputBytes = 0, bool passWatchlist = true)
  {
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      var result = passWatchlist
        ? await PlaylistProcessor.GenerateFilteredFileAsync(sourcePath, outputPath, disabled ?? Disabled(),
          CancellationToken.None, maxOutputBytes: maxOutputBytes, splitGroupTitles: split, watchlist: watchlist)
        : await PlaylistProcessor.GenerateFilteredFileAsync(sourcePath, outputPath, disabled ?? Disabled(),
          CancellationToken.None, maxOutputBytes: maxOutputBytes, splitGroupTitles: split);
      var bytes = await File.ReadAllBytesAsync(outputPath);
      return new Run(bytes, Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries), result);
    }
    finally
    {
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  private static async Task<T> WithSourceAsync<T>(string source, Func<string, Task<T>> run)
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, source);
      return await run(sourcePath);
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  /// <summary>The plain generation and the one with the watchlist, of the same source.</summary>
  private static Task<(Run Plain, Run With)> CompareAsync(string source, IReadOnlyList<string> keys,
    ISet<string>? disabled = null, IReadOnlySet<string>? split = null) =>
    WithSourceAsync(source, async path => (
      await GenerateAsync(path, null, disabled, split, passWatchlist: false),
      await GenerateAsync(path, new WatchlistGroup(keys), disabled, split)));

  private static readonly string HandWritten = Lines(
    Header,
    "#EXTINF:-1 tvg-name=\"News One\" group-title=\"News\",News One", "http://line.example/live/u/p/1.ts",
    "#EXTINF:-1 group-title=\"Films\",Little Motel (1995)", Movie(1),
    "#EXTINF:-1 group-title=\"Films\",Other Film (2001)", Movie(2));

  [Fact]
  public async Task WithoutAWatchlist_OrWithAnEmptyOne_TheOutputIsByteIdentical()
  {
    foreach (var source in new[] { GeneratedPlaylist.Text(seed: 7, entryCount: 500), HandWritten })
    {
      await WithSourceAsync(source, async path =>
      {
        var plain = await GenerateAsync(path, null, passWatchlist: false);
        foreach (var watchlist in new[] { null, new WatchlistGroup([]) })
        {
          var other = await GenerateAsync(path, watchlist);
          Assert.Equal(plain.Bytes, other.Bytes);
          Assert.Equal(plain.Result, other.Result);
        }
        return 0;
      });
    }
  }

  [Fact]
  public async Task Films_AreCopiedIntoTheFirstGroup_AndStayInTheirOwnGroup()
  {
    var (plain, with) = await CompareAsync(HandWritten, [Film("Little Motel (1995)")]);

    // Header and "Created with Strim", the copy, then the plain output from its first entry on.
    string[] expected = [.. plain.Lines[..2], "#EXTINF:-1 group-title=\"Watchlist\",Little Motel (1995)", Movie(1),
      .. plain.Lines[2..]];
    Assert.Equal(expected, with.Lines);
    Assert.Contains("#EXTINF:-1 group-title=\"Films\",Little Motel (1995)", with.Lines[4..]);
    Assert.Equal((plain.Result.TotalChannels, plain.Result.KeptChannels), (with.Result.TotalChannels, with.Result.KeptChannels));
    Assert.Equal(1, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task Series_EveryEpisodeGoesToTheSeriesGroup_InSourceOrder()
  {
    var source = Lines(
      Header,
      "#EXTINF:-1 group-title=\"Series A\",Night Desk S01E01", Episode(1),
      "#EXTINF:-1 group-title=\"Series A\",Other Show S01E01", Episode(2),
      "#EXTINF:-1 group-title=\"Series A\",Night Desk S01E02", Episode(3),
      "#EXTINF:-1 group-title=\"Series B\",Night Desk S01E03", Episode(4),
      "#EXTINF:-1 group-title=\"Films\",Little Motel (1995)", Movie(1),
      "#EXTINF:-1 group-title=\"Series B\",Night Desk S02E01", Episode(5),
      "#EXTINF:-1 group-title=\"Series A\",Night Desk S02E02", Episode(6),
      "#EXTINF:-1 group-title=\"Series B\",Night Desk S02E03", Episode(7));

    var (plain, with) = await CompareAsync(source, [Series("Night Desk S01E01"), Film("Little Motel (1995)")]);

    var episodes = new[] { (1, 1, 1), (1, 2, 3), (1, 3, 4), (2, 1, 5), (2, 2, 6), (2, 3, 7) }.SelectMany(e =>
      new[] { $"#EXTINF:-1 group-title=\"Watchlist · Series\",Night Desk S0{e.Item1}E0{e.Item2}", Episode(e.Item3) });
    string[] expected = ["#EXTINF:-1 group-title=\"Watchlist\",Little Motel (1995)", Movie(1), .. episodes];
    Assert.Equal(expected, with.Lines[2..16]);
    Assert.Equal(plain.Lines[2..], with.Lines[16..]);
    Assert.Equal(7, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task Titles_ComeInWatchlistOrder_NotSourceOrder()
  {
    var (_, with) = await CompareAsync(HandWritten, [Film("Other Film (2001)"), Film("Little Motel (1995)")]);

    Assert.Equal("#EXTINF:-1 group-title=\"Watchlist\",Other Film (2001)", with.Lines[2]);
    Assert.Equal("#EXTINF:-1 group-title=\"Watchlist\",Little Motel (1995)", with.Lines[4]);
    Assert.Equal(2, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task AllVariantsOfAFilmAreCopied_ButOneAddressOnlyOnce()
  {
    Assert.Equal(Film("Little Motel HD (1995)"), Film("Little Motel 4K (1995)"));
    var source = Lines(
      Header,
      "#EXTINF:-1 group-title=\"Films HD\",Little Motel HD (1995)", Movie(1),
      "#EXTINF:-1 group-title=\"Films 4K\",Little Motel 4K (1995)", Movie(2),
      "#EXTINF:-1 group-title=\"Films Again\",Little Motel HD (1995)", Movie(1));

    var (plain, with) = await CompareAsync(source, [Film("Little Motel (1995)")]);

    Assert.Equal("#EXTINF:-1 group-title=\"Watchlist\",Little Motel HD (1995)", with.Lines[2]);
    Assert.Equal(Movie(1), with.Lines[3]);
    Assert.Equal("#EXTINF:-1 group-title=\"Watchlist\",Little Motel 4K (1995)", with.Lines[4]);
    Assert.Equal(Movie(2), with.Lines[5]);
    Assert.Equal(plain.Lines[2..], with.Lines[6..]);
    Assert.Equal(2, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task SwitchedOffGroups_AreNotCopied()
  {
    var source = Lines(
      Header,
      "#EXTINF:-1 group-title=\"Films\",Little Motel (1995)", Movie(1),
      "#EXTINF:-1 group-title=\"Germany\",Das Erste", "http://line.example/live/u/p/1.ts",
      "#EXTINF:-1 group-title=\"Germany\",Little Motel (1995)", Movie(2),
      "#EXTINF:-1 group-title=\"Kino\",Little Motel (1995)", Movie(3));
    var split = new HashSet<string>(["Germany"], StringComparer.OrdinalIgnoreCase);

    var (plain, with) = await CompareAsync(
      source, [Film("Little Motel (1995)")], Disabled("films", "Germany · Movies"), split);

    Assert.Equal("#EXTINF:-1 group-title=\"Watchlist\",Little Motel (1995)", with.Lines[2]);
    Assert.Equal(Movie(3), with.Lines[3]);
    Assert.Equal(plain.Lines[2..], with.Lines[4..]);
    Assert.Equal(1, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task AdultGroups_AreNotCopied()
  {
    var source = Lines(
      Header,
      "#EXTINF:-1 group-title=\"Adult Films\",Little Motel (1995)", Movie(1),
      "#EXTINF:-1 group-title=\"XXX\",Little Motel (1995)", Movie(2));

    var (plain, with) = await CompareAsync(source, [Film("Little Motel (1995)")]);

    Assert.Equal(plain.Bytes, with.Bytes);
    Assert.Equal(0, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task ALiveChannelWithAWatchlistedName_IsNotCopied()
  {
    var source = Lines(
      Header,
      "#EXTINF:-1 group-title=\"News\",Little Motel (1995)", "http://line.example/live/u/p/1.ts",
      "#EXTINF:-1 group-title=\"News\",Night Desk S01E01", "http://line.example/u/p/2");

    var (plain, with) = await CompareAsync(source, [Film("Little Motel (1995)"), Series("Night Desk S01E01")]);

    Assert.Equal(plain.Bytes, with.Bytes);
    Assert.Equal(0, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task OptionLinesTravelWithTheCopy_AndExtGrpIsRewritten()
  {
    var source = Lines(
      Header,
      "#EXTINF:-1 group-title=\"Films\",Little Motel (1995)",
      "#EXTVLCOPT:http-user-agent=StrimTest/1.0",
      "#EXTGRP:Films",
      "#EXTVLCOPT:http-referrer=http://line.example/",
      Movie(1));

    var (plain, with) = await CompareAsync(source, [Film("Little Motel (1995)")]);

    string[] expected =
    [
      "#EXTINF:-1 group-title=\"Watchlist\",Little Motel (1995)",
      "#EXTVLCOPT:http-user-agent=StrimTest/1.0",
      "#EXTGRP:Watchlist",
      "#EXTVLCOPT:http-referrer=http://line.example/",
      Movie(1),
    ];
    Assert.Equal(expected, with.Lines[2..7]);
    Assert.Equal(plain.Lines[2..], with.Lines[7..]);
    Assert.Contains("#EXTGRP:Films", with.Lines[7..]);
  }

  [Fact]
  public async Task AnEntryWithoutGroupTitle_GetsTheAttribute_AndOneWithAnUnquotedValueIsSkipped()
  {
    var source = Lines(
      Header,
      "#EXTINF:-1 tvg-id=\"motel\",Little Motel (1995)", Movie(1),
      "#EXTINF:-1 group-title=Films,Quiet Harbour (2003)", Movie(2));

    var (plain, with) = await CompareAsync(source, [Film("Little Motel (1995)"), Film("Quiet Harbour (2003)")]);

    Assert.Equal("#EXTINF:-1 tvg-id=\"motel\" group-title=\"Watchlist\",Little Motel (1995)", with.Lines[2]);
    Assert.Equal(Movie(1), with.Lines[3]);
    Assert.Equal(plain.Lines[2..], with.Lines[4..]);
    Assert.Equal(1, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task NothingOnTheWatchlistIsInTheList_NoGroupIsWritten()
  {
    var (plain, with) = await CompareAsync(HandWritten, [Film("Nothing Here (1999)"), Series("Night Desk S01E01")]);

    Assert.Equal(plain.Bytes, with.Bytes);
    Assert.Equal(plain.Result, with.Result);
    Assert.Equal(0, with.Result.WatchlistEntries);
  }

  [Fact]
  public async Task PerTitleLimit_KeepsTheFirstEntriesInSourceOrder()
  {
    var count = WatchlistGroup.MaxEntriesPerTitle + 20;
    var source = new StringBuilder(Header).Append('\n');
    for (var i = 0; i < count; i++)
    {
      source.Append($"#EXTINF:-1 group-title=\"Series\",Night Desk S{i / 50 + 1:00}E{i % 50 + 1:00}\n{Episode(i)}\n");
    }

    var (_, with) = await CompareAsync(source.ToString(), [Series("Night Desk S01E01")]);

    Assert.Equal(WatchlistGroup.MaxEntriesPerTitle, with.Result.WatchlistEntries);
    var copied = with.Lines.Skip(2).Take(2 * WatchlistGroup.MaxEntriesPerTitle).Where(line => !line.StartsWith('#'));
    Assert.Equal(Enumerable.Range(0, WatchlistGroup.MaxEntriesPerTitle).Select(Episode), copied);
  }

  [Fact]
  public async Task TotalLimit_DropsEntriesOfTheOldestWatchlistedTitleFirst()
  {
    // Five titles of MaxEntriesPerTitle episodes each, interleaved so the oldest title comes first in the source.
    string[] shows = ["Alpha Desk", "Bravo Desk", "Charlie Desk", "Delta Desk", "Echo Desk"];
    var source = new StringBuilder(Header).Append('\n');
    var id = 0;
    for (var i = 0; i < WatchlistGroup.MaxEntriesPerTitle; i++)
    {
      foreach (var show in shows.Reverse())
      {
        source.Append($"#EXTINF:-1 group-title=\"Series\",{show} S{i / 50 + 1:00}E{i % 50 + 1:00}\n{Episode(id++)}\n");
      }
    }

    var (_, with) = await CompareAsync(source.ToString(), shows.Select(show => Series($"{show} S01E01")).ToList());

    Assert.Equal(WatchlistGroup.MaxEntries, with.Result.WatchlistEntries);
    var copies = with.Lines.Skip(2).Take(2 * WatchlistGroup.MaxEntries).Where(line => line.StartsWith('#')).ToList();
    Assert.All(copies, line => Assert.Contains("group-title=\"Watchlist · Series\"", line));
    foreach (var show in shows[..4])
    {
      Assert.Equal(WatchlistGroup.MaxEntriesPerTitle, copies.Count(line => line.Contains($",{show} ")));
    }
    Assert.DoesNotContain(copies, line => line.Contains(",Echo Desk "));
    Assert.StartsWith("#EXTINF:-1 group-title=\"Watchlist · Series\",Alpha Desk S01E01", copies[0]);
  }

  [Fact]
  public async Task TheCopiesCountAgainstTheOutputByteLimit()
  {
    await WithSourceAsync(HandWritten, async path =>
    {
      var plain = await GenerateAsync(path, null, passWatchlist: false);
      var limit = plain.Result.OutputBytes;
      var fits = await GenerateAsync(path, null, maxOutputBytes: limit, passWatchlist: false);
      Assert.Equal(plain.Bytes, fits.Bytes);

      await Assert.ThrowsAsync<PlaylistGeneratedSizeExceededException>(() =>
        GenerateAsync(path, new WatchlistGroup([Film("Little Motel (1995)")]), maxOutputBytes: limit));
      return 0;
    });
  }
}
