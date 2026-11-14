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

  [Fact]
  public async Task GenerateFiltered_ChecksDisabledSetOncePerGroupVariant()
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    var urlPrefixes = new[] { "", "movie/", "series/" };
    var source = new System.Text.StringBuilder("#EXTM3U\n");
    for (var i = 0; i < 10_000; i++)
    {
      source.Append($"#EXTINF:-1 group-title=\"Title {i % 20:D2}\",Entry {i}\n");
      source.Append($"http://provider.example/{urlPrefixes[i / 20 % 3]}user/pass/{i}\n");
    }

    var disabled = new CountingSet { "Title 00 · Movies", "Title 01" };
    try
    {
      await File.WriteAllTextAsync(sourcePath, source.ToString());

      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath, outputPath, disabled, CancellationToken.None);

      Assert.Equal(10_000, result.TotalChannels);
      Assert.True(result.KeptChannels < 10_000);
      Assert.InRange(disabled.ContainsCalls, 1, 2 * 60);
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public async Task GenerateFiltered_CaseVariantsShareOneMembershipCheck()
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    var casings = new[] { "News", "NEWS", "news", "nEWS", "NeWs" };
    var urlPrefixes = new[] { "", "movie/", "series/" };
    var source = new System.Text.StringBuilder("#EXTM3U\n");
    for (var i = 0; i < 300; i++)
    {
      source.Append($"#EXTINF:-1 group-title=\"{casings[i % casings.Length]}\",Entry {i}\n");
      source.Append($"http://provider.example/{urlPrefixes[i / casings.Length % 3]}user/pass/{i}\n");
    }

    var disabled = new CountingSet { "Unrelated" };
    try
    {
      await File.WriteAllTextAsync(sourcePath, source.ToString());

      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath, outputPath, disabled, CancellationToken.None);

      Assert.Equal(300, result.KeptChannels);
      // One title in three content types, whatever its casing: two checks per content type.
      Assert.InRange(disabled.ContainsCalls, 1, 2 * 3);
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public async Task GenerateFiltered_MemoisesTitlesBeyondTheDefaultGroupLimit()
  {
    const int titles = 12_000;
    const int entriesPerTitle = 3;
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    var source = new System.Text.StringBuilder("#EXTM3U\n");
    for (var i = 0; i < titles * entriesPerTitle; i++)
    {
      source.Append($"#EXTINF:-1 group-title=\"Title {i % titles:D5}\",Entry {i}\n");
      source.Append($"http://provider.example/user/pass/{i}\n");
    }

    var disabled = new CountingSet { "Title 00000" };
    try
    {
      await File.WriteAllTextAsync(sourcePath, source.ToString());

      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath, outputPath, disabled, CancellationToken.None);

      Assert.Equal(titles * entriesPerTitle, result.TotalChannels);
      Assert.Equal((titles - 1) * entriesPerTitle, result.KeptChannels);
      Assert.InRange(disabled.ContainsCalls, 1, 2 * titles);
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  private const string CaseVariantSource = """
    #EXTM3U
    #EXTINF:-1 group-title="News",News Live
    http://provider.example/user/pass/1
    #EXTINF:-1 group-title="NEWS",NEWS Live
    http://provider.example/user/pass/2
    #EXTINF:-1 group-title="news",news Episode
    http://provider.example/series/user/pass/3.mkv
    #EXTINF:-1 group-title="News",News Episode
    http://provider.example/series/user/pass/4.mkv
    #EXTINF:-1 group-title="nEWS",nEWS Film
    http://provider.example/movie/user/pass/5.mp4
    #EXTINF:-1 group-title="Sports",Sports Live
    http://provider.example/user/pass/6
    """;

  [Theory]
  [InlineData("news · series", new[] { "News Live", "NEWS Live", "nEWS Film", "Sports Live" }, new[] { "news Episode", "News Episode" })]
  [InlineData("NEWS · Movies", new[] { "News Live", "NEWS Live", "news Episode", "News Episode", "Sports Live" }, new[] { "nEWS Film" })]
  [InlineData("nEwS", new[] { "Sports Live" }, new[] { "News Live", "NEWS Live", "news Episode", "News Episode", "nEWS Film" })]
  public async Task GenerateFiltered_CaseInsensitiveTitlesShareDecision(
    string disabledGroup,
    string[] expectedKept,
    string[] expectedRemoved)
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      await File.WriteAllTextAsync(sourcePath, CaseVariantSource);

      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(new[] { disabledGroup }, StringComparer.OrdinalIgnoreCase),
        CancellationToken.None);
      var output = await File.ReadAllLinesAsync(outputPath);

      Assert.Equal(expectedKept.Length, result.KeptChannels);
      Assert.All(expectedKept, name => Assert.Contains(output, line => line.EndsWith("," + name, StringComparison.Ordinal)));
      Assert.All(expectedRemoved, name => Assert.DoesNotContain(output, line => line.EndsWith("," + name, StringComparison.Ordinal)));
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public async Task GenerateFiltered_SplitTitlesStillRenamed()
  {
    var sourcePath = Path.GetTempFileName();
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    const string expected =
      "#EXTM3U\n" +
      "# Created with Strim (https://strim.plis.dev)\n" +
      "#EXTINF:-1 group-title=\"News\",News Live\n" +
      "http://provider.example/user/pass/1\n" +
      "#EXTINF:-1 group-title=\"NEWS\",NEWS Live\n" +
      "http://provider.example/user/pass/2\n" +
      "#EXTINF:-1 group-title=\"news · Series\",news Episode\n" +
      "http://provider.example/series/user/pass/3.mkv\n" +
      "#EXTINF:-1 group-title=\"News · Series\",News Episode\n" +
      "http://provider.example/series/user/pass/4.mkv\n" +
      "#EXTINF:-1 group-title=\"nEWS · Movies\",nEWS Film\n" +
      "http://provider.example/movie/user/pass/5.mp4\n" +
      "#EXTINF:-1 group-title=\"Sports\",Sports Live\n" +
      "http://provider.example/user/pass/6\n";
    try
    {
      await File.WriteAllTextAsync(sourcePath, CaseVariantSource);
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);
      Assert.Equal("NEWS", Assert.Single(analysis.SplitGroupTitles).ToUpperInvariant());

      await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath,
        outputPath,
        new HashSet<string>(new[] { "Unrelated" }, StringComparer.OrdinalIgnoreCase),
        CancellationToken.None,
        splitGroupTitles: analysis.SplitGroupTitles);

      Assert.Equal(expected, await File.ReadAllTextAsync(outputPath));
    }
    finally
    {
      File.Delete(sourcePath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  [Fact]
  public void GenerateFiltered_RepeatedSplitCaseVariantsBuildEachKeyOnce()
  {
    var casings = new[] { "News", "NEWS", "news" };
    var types = new[] { PlaylistContentType.Live, PlaylistContentType.Movies, PlaylistContentType.Series };
    var split = new HashSet<string>(new[] { "news" }, StringComparer.OrdinalIgnoreCase);
    var decisions = new PlaylistProcessor.GroupDecisions(
      new HashSet<string>(new[] { "Unrelated" }, StringComparer.OrdinalIgnoreCase), split);

    for (var i = 0; i < 3_000; i++)
    {
      var title = casings[i % casings.Length];
      var type = types[i / casings.Length % types.Length];
      var (disabled, suffix) = decisions.Decide(title, type);

      Assert.False(disabled);
      Assert.Equal(type == PlaylistContentType.Live ? title : PlaylistProcessor.GroupKey(title, type), title + suffix);
    }

    // Only the disabled check builds a key: one per content type, whichever casing comes first.
    Assert.Equal(3, decisions.KeysBuilt);
  }

  [Fact]
  public void GenerateFiltered_CyclingManySplitCasingsBuildsNoKeyPerEntry()
  {
    // All 2^10 casings of a 10-letter title, distinct under ordinal comparison, cycled three times.
    const string baseTitle = "abcdefghij";
    var casings = Enumerable.Range(0, 1 << baseTitle.Length)
      .Select(mask => new string(baseTitle.Select((ch, c) => (mask & (1 << c)) != 0 ? char.ToUpperInvariant(ch) : ch).ToArray()))
      .ToArray();
    var split = new HashSet<string>(new[] { baseTitle }, StringComparer.OrdinalIgnoreCase);
    var decisions = new PlaylistProcessor.GroupDecisions(
      new HashSet<string>(new[] { "Unrelated" }, StringComparer.OrdinalIgnoreCase), split);

    for (var round = 0; round < 3; round++)
    {
      foreach (var title in casings)
      {
        foreach (var type in new[] { PlaylistContentType.Movies, PlaylistContentType.Series })
        {
          var (disabled, suffix) = decisions.Decide(title, type);
          Assert.False(disabled);
          Assert.Equal(PlaylistProcessor.GroupKey(title, type), title + suffix);
        }
      }
    }

    // One key per content type for the disabled check, however many casings cycle through.
    Assert.Equal(2, decisions.KeysBuilt);
  }

  /// <summary>Counts the membership checks made through <see cref="ISet{T}"/>.</summary>
  private sealed class CountingSet : HashSet<string>, ICollection<string>
  {
    public CountingSet() : base(StringComparer.OrdinalIgnoreCase) { }

    public int ContainsCalls { get; private set; }

    bool ICollection<string>.Contains(string item)
    {
      ContainsCalls++;
      return Contains(item);
    }
  }

  private static int CountOf(PlaylistAnalysisResult analysis, string key) =>
    Assert.Single(analysis.Groups, g => g.Key == key).Count;
}
