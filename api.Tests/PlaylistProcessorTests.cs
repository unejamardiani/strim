using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistProcessorTests
{
  [Fact]
  public async Task FileProcessing_AnalyzesAndFiltersWithoutMaterializingThePlaylistResult()
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    const string source = """
      #EXTM3U
      #EXTINF:-1 tvg-name="News One" group-title="News",News One
      https://example.com/news.m3u8

      #EXTINF:-1 tvg-name="Sports One" group-title="Sports",Sports One
      https://example.com/sports.m3u8
      #EXTINF:-1 tvg-name="No Group",No Group
      https://example.com/other.m3u8
      """;

    try
    {
      await File.WriteAllTextAsync(sourcePath, source);

      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);
      Assert.Equal(3, analysis.TotalChannels);
      Assert.Equal(1, CountOf(analysis, "News"));
      Assert.Equal(1, CountOf(analysis, "Sports"));
      Assert.Equal(1, CountOf(analysis, "Ungrouped"));

      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(new[] { "sports" }, StringComparer.OrdinalIgnoreCase),
        CancellationToken.None);
      var output = await File.ReadAllTextAsync(outputPath);

      Assert.Equal(3, result.TotalChannels);
      Assert.Equal(2, result.KeptChannels);
      Assert.Contains("News One", output);
      Assert.DoesNotContain("Sports One", output);
      Assert.Contains("group-title=\"Ungrouped\"", output);
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public void InMemoryCompatibilityPath_PreservesCaseInsensitiveDisabledGroups()
  {
    const string source = """
      #EXTM3U
      #EXTINF:-1 group-title="Kids",Kids One
      https://example.com/kids.m3u8
      #EXTINF:-1 group-title="News",News One
      https://example.com/news.m3u8
      """;

    var filtered = PlaylistProcessor.GenerateFiltered(
      source,
      new HashSet<string>(new[] { "KIDS" }, StringComparer.OrdinalIgnoreCase));

    Assert.Equal(2, filtered.TotalChannels);
    Assert.Equal(1, filtered.KeptChannels);
    Assert.DoesNotContain("Kids One", filtered.Text);
    Assert.Contains("News One", filtered.Text);
  }

  [Fact]
  public async Task FileAnalysis_RejectsAnOversizedLineBeforeItCanBecomeAPlaylistResult()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, $"#EXTM3U\n#EXTINF:-1 group-title=\"{new string('a', 128)}\",Test\nhttps://example.com/test.m3u8\n");

      await Assert.ThrowsAsync<PlaylistLineTooLongException>(() =>
        PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None, maxLineLengthChars: 64));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task FileAnalysis_RejectsExcessiveDistinctGroupMetadata()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, """
        #EXTM3U
        #EXTINF:-1 group-title="News",News
        https://example.com/news.m3u8
        #EXTINF:-1 group-title="Sports",Sports
        https://example.com/sports.m3u8
        """);

      await Assert.ThrowsAsync<PlaylistGroupLimitExceededException>(() =>
        PlaylistProcessor.AnalyzeFileAsync(
          sourcePath,
          CancellationToken.None,
          maxLineLengthChars: 1024,
          maxGroupCount: 1,
          maxGroupTitleLengthChars: 128,
          maxGroupMetadataBytes: 1024));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  // Xtream Codes providers reuse one group-title for a live category and a VOD/series category.
  private const string MixedGroupSource = """
    #EXTM3U
    #EXTINF:-1 tvg-name="Das Erste HD" group-title="Germany",Das Erste HD
    http://provider.example/user/pass/1001
    #EXTINF:-1 tvg-name="ZDF HD" group-title="Germany",ZDF HD
    http://provider.example/live/user/pass/1002.ts
    #EXTINF:-1 tvg-name="The Witcher S01E01" group-title="Germany",The Witcher S01E01
    http://provider.example/series/user/pass/2001.mkv
    #EXTINF:-1 tvg-name="Some Film" group-title="German Movies",Some Film
    http://provider.example/movie/user/pass/3001.mp4
    #EXTINF:-1 tvg-name="Netflix Show" group-title="Netflix Series",Netflix Show
    http://provider.example/series/user/pass/4001.mkv
    """;

  [Fact]
  public async Task FileAnalysis_SplitsAGroupTitleReusedAcrossContentTypes()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, MixedGroupSource);

      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);

      Assert.Equal(5, analysis.TotalChannels);
      Assert.Equal(4, analysis.Groups.Count);
      Assert.Equal(2, CountOf(analysis, "Germany · Live"));
      Assert.Equal(1, CountOf(analysis, "Germany · Series"));
      Assert.DoesNotContain(analysis.Groups, g => g.Key == "Germany");
      Assert.All(
        analysis.Groups.Where(g => g.Key.StartsWith("Germany ·", StringComparison.Ordinal)),
        g => Assert.Equal("Germany", g.GroupTitle));

      // Titles with a single content type keep their plain name so saved filters still match.
      var movies = Assert.Single(analysis.Groups, g => g.Key == "German Movies");
      Assert.Equal(PlaylistContentType.Movies, movies.ContentType);
      var series = Assert.Single(analysis.Groups, g => g.Key == "Netflix Series");
      Assert.Equal(PlaylistContentType.Series, series.ContentType);
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Theory]
  [InlineData("germany · series", new[] { "Das Erste HD", "ZDF HD", "Some Film" }, new[] { "The Witcher" })]
  [InlineData("Germany · Live", new[] { "The Witcher", "Some Film" }, new[] { "Das Erste HD", "ZDF HD" })]
  [InlineData("Germany", new[] { "Some Film" }, new[] { "Das Erste HD", "ZDF HD", "The Witcher" })]
  public async Task FileFiltering_DisablesOneContentTypeOrTheWholeGroupTitle(
    string disabledGroup,
    string[] expectedKept,
    string[] expectedRemoved)
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      await File.WriteAllTextAsync(sourcePath, MixedGroupSource);

      await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(new[] { disabledGroup, "Netflix Series" }, StringComparer.OrdinalIgnoreCase),
        CancellationToken.None);
      var output = await File.ReadAllTextAsync(outputPath);

      Assert.All(expectedKept, name => Assert.Contains(name, output));
      Assert.All(expectedRemoved, name => Assert.DoesNotContain(name, output));
      Assert.DoesNotContain("Netflix Show", output);
      // Without split titles from the analysis, group-titles are written unchanged.
      Assert.DoesNotContain("·", output);
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public async Task FileFiltering_MovesSeriesOfASplitTitleIntoTheirOwnGroup()
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    const string source = """
      #EXTM3U
      #EXTINF:-1 group-title="Germany",Das Erste HD
      http://provider.example/user/pass/1001
      #EXTINF:-1 tvg-name="The Witcher S01E01" group-title="Germany" tvg-logo="",The Witcher S01E01
      http://provider.example/series/user/pass/2001.mkv
      #EXTINF:-1 group-title="German Movies",Some Film
      http://provider.example/movie/user/pass/3001.mp4
      #EXTINF:-1,Loose Channel
      http://provider.example/user/pass/1002
      #EXTINF:-1,Loose Episode
      http://provider.example/series/user/pass/2002.mkv
      """;

    try
    {
      await File.WriteAllTextAsync(sourcePath, source);
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);
      Assert.Equal(new[] { "Germany", "Ungrouped" }, analysis.SplitGroupTitles.Order());

      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        CancellationToken.None,
        splitGroupTitles: analysis.SplitGroupTitles);
      var output = await File.ReadAllLinesAsync(outputPath);

      Assert.Equal(5, result.KeptChannels);
      Assert.Contains("#EXTINF:-1 group-title=\"Germany\",Das Erste HD", output);
      Assert.Contains(
        "#EXTINF:-1 tvg-name=\"The Witcher S01E01\" group-title=\"Germany · Series\" tvg-logo=\"\",The Witcher S01E01",
        output);
      Assert.Contains("#EXTINF:-1 group-title=\"German Movies\",Some Film", output);
      Assert.Contains("#EXTINF:-1 group-title=\"Ungrouped\",Loose Channel", output);
      Assert.Contains("#EXTINF:-1 group-title=\"Ungrouped · Series\",Loose Episode", output);
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Theory]
  [InlineData("http://provider.example/user/pass/1001", PlaylistContentType.Live)]
  [InlineData("http://provider.example/live/user/pass/1001.ts", PlaylistContentType.Live)]
  [InlineData("https://provider.example:8080/MOVIE/user/pass/3001.mp4", PlaylistContentType.Movies)]
  [InlineData("http://provider.example/series/user/pass/2001.mkv", PlaylistContentType.Series)]
  [InlineData("http://cdn.example/hls/movie/channel.m3u8", PlaylistContentType.Live)]
  [InlineData("not a url", PlaylistContentType.Live)]
  [InlineData(null, PlaylistContentType.Live)]
  public void DetectContentType_UsesTheXtreamCodesPathPrefix(string? url, PlaylistContentType expected)
  {
    Assert.Equal(expected, PlaylistProcessor.DetectContentType(url));
  }

  private static int CountOf(PlaylistAnalysisResult analysis, string key) =>
    Assert.Single(analysis.Groups, g => g.Key == key).Count;

  private static readonly string[] LineReaderSource =
  {
    "#EXTM3U",
    "#EXTINF:-1 group-title=\"News\",News One",
    "https://example.com/news.m3u8",
    "#EXTINF:-1 group-title=\"Sports\",Sports One",
    "http://provider.example/series/user/pass/1.mkv",
    "#EXTINF:-1,Loose Channel",
    "https://example.com/loose.m3u8",
  };

  [Fact]
  public async Task LineReader_HandlesLfCrLfAndCrEndings()
  {
    var results = new List<(string Groups, int Total, int Kept, string Output)>();
    foreach (var ending in new[] { "\n", "\r\n", "\r" })
    {
      results.Add(await AnalyzeAndGenerateAsync(string.Join(ending, LineReaderSource) + ending, "sports"));
    }

    Assert.Equal(3, results[0].Total);
    Assert.Equal(2, results[0].Kept);
    Assert.Contains("News One", results[0].Output);
    Assert.All(results, r => Assert.Equal(results[0], r));
  }

  [Fact]
  public async Task LineReader_ReadsLinesThatSpanBufferBoundary()
  {
    // The padding entry ends 40 chars before the 4096-char boundary, so the next #EXTINF crosses it.
    var paddingName = new string('p', 4056 - "#EXTM3U\n#EXTINF:-1 group-title=\"Pad\",\nhttps://example.com/pad.m3u8\n".Length);
    var longName = new string('x', 10_000);
    var source =
      "#EXTM3U\n" +
      $"#EXTINF:-1 group-title=\"Pad\",{paddingName}\n" +
      "https://example.com/pad.m3u8\n" +
      "#EXTINF:-1 tvg-name=\"Boundary\" group-title=\"Across Boundary\",Boundary Channel\n" +
      "https://example.com/boundary.m3u8\n" +
      $"#EXTINF:-1 group-title=\"Long Line\",{longName}\n" +
      "https://example.com/long.m3u8\n";
    Assert.Equal(4056, source.IndexOf("#EXTINF:-1 tvg-name=\"Boundary\"", StringComparison.Ordinal));

    var (groups, total, kept, output) = await AnalyzeAndGenerateAsync(source);

    Assert.Equal(3, total);
    Assert.Equal(3, kept);
    Assert.Contains("Across Boundary:1", groups);
    Assert.Contains("Long Line:1", groups);
    Assert.Contains($"#EXTINF:-1 group-title=\"Long Line\",{longName}\n", output);
    Assert.Contains("Boundary Channel\nhttps://example.com/boundary.m3u8\n", output);
  }

  [Fact]
  public async Task LineReader_LastLineWithoutNewlineIsRead()
  {
    var (groups, total, kept, output) = await AnalyzeAndGenerateAsync(string.Join("\n", LineReaderSource));

    Assert.Equal(3, total);
    Assert.Equal(3, kept);
    Assert.Contains("Ungrouped:1", groups);
    Assert.EndsWith("Loose Channel\nhttps://example.com/loose.m3u8\n", output);
  }

  [Theory]
  [InlineData(64)]
  [InlineData(6000)] // the line starts near the end of the first 4096-char buffer and crosses it
  public async Task LineReader_EnforcesMaxLineLengthExactly(int maxLineLengthChars)
  {
    var prefix = "#EXTINF:-1 group-title=\"Edge\",";
    var paddingLength = maxLineLengthChars > 4096 ? 4000 : 0;
    string Source(int lineLength) =>
      "#EXTM3U\n" +
      (paddingLength > 0 ? $"#{new string('c', paddingLength)}\n" : string.Empty) +
      prefix + new string('n', lineLength - prefix.Length) + "\n" +
      "https://example.com/edge.m3u8\n";

    var (groups, total, _, _) = await AnalyzeAndGenerateAsync(Source(maxLineLengthChars), maxLineLengthChars: maxLineLengthChars);
    Assert.Equal(1, total);
    Assert.Contains("Edge:1", groups);

    var tooLong = Source(maxLineLengthChars + 1);
    await Assert.ThrowsAsync<PlaylistLineTooLongException>(() => AnalyzeAndGenerateAsync(tooLong, maxLineLengthChars: maxLineLengthChars));
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    var sourcePath = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(sourcePath, tooLong);
      await Assert.ThrowsAsync<PlaylistLineTooLongException>(() => PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath, outputPath, new HashSet<string>(), CancellationToken.None, maxLineLengthChars: maxLineLengthChars));
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public async Task LineReader_SkipsBlankAndWhitespaceOnlyLines()
  {
    var source =
      "#EXTM3U\n\n   \n" +
      "#EXTINF:-1 group-title=\"News\",News One\n \t \n\r\n" +
      "  https://example.com/news.m3u8  \n\n" +
      "\t#EXTINF:-1 group-title=\"Sports\",Sports One\r\n   \r\n" +
      "https://example.com/sports.m3u8\n   ";

    var (groups, total, kept, output) = await AnalyzeAndGenerateAsync(source);

    Assert.Equal(2, total);
    Assert.Equal(2, kept);
    Assert.Equal("News:1|Sports:1", groups);
    Assert.Equal(
      "#EXTM3U\n# Created with Strim (https://strim.plis.dev)\n" +
      "#EXTINF:-1 group-title=\"News\",News One\nhttps://example.com/news.m3u8\n" +
      "#EXTINF:-1 group-title=\"Sports\",Sports One\nhttps://example.com/sports.m3u8\n",
      output.Replace("\r\n", "\n"));
  }

  internal static async Task<(string Groups, int Total, int Kept, string Output)> AnalyzeAndGenerateAsync(
    string source,
    string? disabledGroup = null,
    int maxLineLengthChars = 64 * 1024)
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      await File.WriteAllTextAsync(sourcePath, source);
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None, maxLineLengthChars);
      var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (disabledGroup is not null) disabled.Add(disabledGroup);
      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath, outputPath, disabled, CancellationToken.None, maxLineLengthChars);
      Assert.Equal(analysis.TotalChannels, result.TotalChannels);
      var groups = string.Join("|", analysis.Groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count}"));
      return (groups, analysis.TotalChannels, result.KeptChannels, (await File.ReadAllTextAsync(outputPath)).Replace("\r\n", "\n"));
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }
}

[CollectionDefinition(nameof(AllocationMeasurement), DisableParallelization = true)]
public class AllocationMeasurement
{
}

// Runs alone so other tests' allocations do not count towards the process-wide measurement.
[Collection(nameof(AllocationMeasurement))]
public class PlaylistProcessorAllocationTests
{
  private const int EntryCount = 50_000;

  // Measured with the slice-based reader: about 750 bytes per entry. The previous char-by-char
  // reader allocated a StringBuilder per line and measured about 1 920 bytes per entry.
  private const long MaxBytesPerEntry = 1_000;

  [Fact]
  public async Task AnalyzeFile_AllocatesLessThanBoundPerEntry()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      await using (var writer = new StreamWriter(sourcePath))
      {
        await writer.WriteLineAsync("#EXTM3U");
        for (var i = 0; i < EntryCount; i++)
        {
          await writer.WriteLineAsync($"#EXTINF:-1 tvg-id=\"ch{i}\" tvg-name=\"Channel {i}\" group-title=\"Group {i % 50}\",Channel {i}");
          await writer.WriteLineAsync($"http://provider.example/live/user/pass/{i}.ts");
        }
      }

      // Warm up once so JIT and pooled buffers are not part of the measurement.
      await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);

      var before = GC.GetTotalAllocatedBytes(precise: true);
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);
      var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

      Assert.Equal(EntryCount, analysis.TotalChannels);
      var bytesPerEntry = allocated / EntryCount;
      Assert.True(
        bytesPerEntry < MaxBytesPerEntry,
        $"Analysing {EntryCount} entries allocated {allocated} bytes ({bytesPerEntry} per entry), bound {MaxBytesPerEntry}.");
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }
}
