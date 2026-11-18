using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Api.Services;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>The <c>lang</c> filter of <c>/api/playlist/search</c> and the languages it reports as present.</summary>
public class SearchLanguageFilterTests(StrimAppFactory factory) : IClassFixture<StrimAppFactory>
{
  private const string Films = """
    #EXTM3U
    #EXTINF:-1 group-title="Films",DE: Example Film One
    https://example.com/movie/u/p/1.mp4
    #EXTINF:-1 group-title="Films",Example Film Two (DE)
    https://example.com/movie/u/p/2.mp4
    #EXTINF:-1 group-title="Films",Example Film Plain
    https://example.com/movie/u/p/3.mp4
    #EXTINF:-1 group-title="FR | Films",Example Film Drama
    https://example.com/movie/u/p/4.mp4
    #EXTINF:-1 group-title="Films",Example Film Kino VOSTFR
    https://example.com/movie/u/p/5.mp4
    #EXTINF:-1 group-title="Films",UK: Example Film Six
    https://example.com/movie/u/p/6.mp4
    #EXTINF:-1 group-title="EX-YU | Films",Example Film Serbian
    https://example.com/movie/u/p/7.mp4
    #EXTINF:-1 group-title="Films",MULTI Example Film Eight
    https://example.com/movie/u/p/8.mp4
    """;

  [Fact]
  public async Task Lang_KeepsOnlyTaggedEntries()
  {
    var (client, cacheKey) = await AnalyzeAsync(Films);

    var de = await SearchOkAsync(client, new { cacheKey, query = "example", lang = "de" });

    Assert.Equal(new[] { "DE: Example Film One", "Example Film Two (DE)" }, de.Matches.Select(m => m.Name));
    Assert.Equal(2, de.Total);
    Assert.False(de.HasMore);
  }

  [Fact]
  public async Task Lang_CombinesValuesAsOrAndMatchesSubtitles()
  {
    var (client, cacheKey) = await AnalyzeAsync(Films);

    var result = await SearchOkAsync(client, new { cacheKey, query = "example", lang = "de, FR" });

    Assert.Equal(
      new[] { "DE: Example Film One", "Example Film Two (DE)", "Example Film Drama", "Example Film Kino VOSTFR" },
      result.Matches.Select(m => m.Name));
  }

  [Fact]
  public async Task Lang_MatchesPrefixTagsCaseInsensitively()
  {
    var (client, cacheKey) = await AnalyzeAsync(Films);

    var tagOnly = await SearchOkAsync(client, new { cacheKey, query = "example", lang = "uk" });
    Assert.Equal(new[] { "UK: Example Film Six" }, tagOnly.Matches.Select(m => m.Name));

    var mixed = await SearchOkAsync(client, new { cacheKey, query = "example", lang = "de,EX-YU" });
    Assert.Equal(
      new[] { "DE: Example Film One", "Example Film Two (DE)", "Example Film Serbian" }, mixed.Matches.Select(m => m.Name));
  }

  [Fact]
  public async Task Lang_CombinesWithQueryAndPaging()
  {
    var playlist = new StringBuilder("#EXTM3U\n");
    for (var i = 1; i <= 60; i++)
    {
      playlist.Append($"#EXTINF:-1 group-title=\"News\",DE: News {i}\nhttps://example.com/live/u/p/{i}.ts\n");
      playlist.Append($"#EXTINF:-1 group-title=\"News\",News Plain {i}\nhttps://example.com/live/u/p/p{i}.ts\n");
      playlist.Append($"#EXTINF:-1 group-title=\"Sport\",DE: Sport {i}\nhttps://example.com/live/u/p/s{i}.ts\n");
    }
    var (client, cacheKey) = await AnalyzeAsync(playlist.ToString());

    var result = await SearchOkAsync(client, new { cacheKey, query = "news", lang = "de" });

    Assert.Equal(50, result.Matches.Count);
    Assert.All(result.Matches, m => Assert.StartsWith("DE: News ", m.Name));
    Assert.True(result.HasMore);
    Assert.Equal(60, result.Total);
    var unfiltered = await SearchOkAsync(client, new { cacheKey, query = "news" });
    Assert.Equal(120, unfiltered.Total);
  }

  [Theory]
  [InlineData("d")]
  [InlineData("toolong")]
  [InlineData("A-B-C")]
  [InlineData("-de")]
  [InlineData("de;fr")]
  [InlineData("d3")]
  [InlineData("de,,,fr,it,es,pt,nl,pl,ru,sv,da,fi,hu,cs,ro,el,ja,ko,hi,UK,US,TR")]
  public async Task Lang_Malformed_ReturnsPlainBadRequest(string lang)
  {
    var (client, cacheKey) = await AnalyzeAsync(Films);

    var response = await client.PostAsync("/api/playlist/search", new { cacheKey, query = "example", lang });

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var error = (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Error;
    Assert.StartsWith("Give at most 20 languages or tags", error);
  }

  [Fact]
  public async Task Lang_UnknownWellFormedCode_MatchesNothing()
  {
    // Documented behaviour: a well-formed code that no entry carries is not an error; it simply matches nothing.
    var (client, cacheKey) = await AnalyzeAsync(Films);

    var result = await SearchOkAsync(client, new { cacheKey, query = "example", lang = "XX" });

    Assert.Empty(result.Matches);
    Assert.Equal(0, result.Total);
  }

  [Fact]
  public async Task NoLang_GivesTheSameResultsAsBefore()
  {
    var (client, cacheKey) = await AnalyzeAsync(Films);
    var all = Films.Split('\n').Where(l => l.Contains(',')).Select(l => l[(l.IndexOf(',') + 1)..].Trim()).ToArray();

    foreach (var body in new object[] { new { cacheKey, query = "example" }, new { cacheKey, query = "example", lang = " " } })
    {
      var result = await SearchOkAsync(client, body);
      Assert.Equal(all, result.Matches.Select(m => m.Name));
      Assert.False(result.HasMore);
      Assert.Equal(0, result.HiddenCount);
      Assert.Equal(8, result.Total);
    }
  }

  [Fact]
  public async Task Search_StaysReadOnlyWithoutCsrf()
  {
    var (client, cacheKey) = await AnalyzeAsync(Films);

    var response = await client.PostAsync("/api/playlist/search", new { cacheKey, query = "example", lang = "de" }, withCsrf: false);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public async Task Languages_ListsPresentValuesWithCountsAndPrefixTagsFromTwenty()
  {
    var playlist = new StringBuilder("#EXTM3U\n");
    var n = 0;
    void Add(string name) =>
      playlist.Append($"#EXTINF:-1 group-title=\"Films\",{name}\nhttps://example.com/movie/u/p/{++n}.mp4\n");
    for (var i = 0; i < 20; i++) Add($"UK: Film {i}");
    for (var i = 0; i < 19; i++) Add($"AL: Film {i}");
    for (var i = 0; i < 3; i++) Add($"DE: Film {i}");
    Add("Film A VOSTFR");
    Add("DE: Film B VOSTFR");
    Add("Plain Film");
    var (client, cacheKey) = await AnalyzeAsync(playlist.ToString());

    var result = await SearchOkAsync(client, new { cacheKey, query = "film", lang = "de" });

    // Counts cover the whole playlist, not the query or the filter; AL stays below the threshold.
    Assert.Equal(
      new[] { new LanguageDto("UK", "tag", 20), new LanguageDto("de", "language", 4), new LanguageDto("fr", "language", 2) },
      result.Languages);
    var al = await SearchOkAsync(client, new { cacheKey, query = "film", lang = "AL" });
    Assert.Equal(19, al.Total);
  }

  private async Task<(ApiClient Client, string CacheKey)> AnalyzeAsync(string rawText)
  {
    var client = new ApiClient(factory.CreateClient());
    var response = await client.PostAsync("/api/playlist/analyze", new { rawText });
    response.EnsureSuccessStatusCode();
    return (client, (await response.Content.ReadFromJsonAsync<AnalysisDto>())!.CacheKey);
  }

  private static async Task<SearchDto> SearchOkAsync(ApiClient client, object body)
  {
    var response = await client.PostAsync("/api/playlist/search", body);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<SearchDto>())!;
  }

  private sealed record AnalysisDto(string CacheKey);
  private sealed record MatchDto(string Name);
  private sealed record LanguageDto(string Value, string Kind, int Count);
  private sealed record SearchDto(List<MatchDto> Matches, bool HasMore, int HiddenCount, int Total, List<LanguageDto> Languages);
  private sealed record ErrorDto(string Error);
}

/// <summary>Language tags stored in <see cref="PlaylistSearchIndex"/> and returned on its matches.</summary>
public class SearchLanguageTests
{
  private const string Tagged = """
    #EXTM3U url-tvg="https://example.com/epg.xml"
    #EXTINF:-1 tvg-id="a.example" group-title="Films",DE: Example Film
    #EXTVLCOPT:http-user-agent=Example
    https://example.com/movie/u/p/1.mp4
    #EXTINF:-1 group-title="FR | Films",Example Drama
    https://example.com/movie/u/p/2.mp4
    #EXTINF:-1 group-title="Films",Example Plain
    https://example.com/movie/u/p/3.mp4
    #EXTINF:-1 group-title="Films",Example Kino VOSTFR
    https://example.com/movie/u/p/4.mp4
    #EXTINF:-1 group-title="Films",UK: Example
    https://example.com/movie/u/p/5.mp4
    #EXTINF:-1 group-title="EX-YU | Films",Example Serbian
    https://example.com/movie/u/p/6.mp4
    """;

  [Fact]
  public async Task Search_CarriesLanguagesFromNameAndGroup()
  {
    var index = await Analyze(Tagged);

    var de = Find(index, "DE: Example Film").Languages;
    Assert.Equal(new[] { "de" }, de.Audio);
    Assert.Equal("title", de.Source);
    Assert.Equal(new[] { "fr" }, Find(index, "Example Drama").Languages.Audio);
    var plain = Find(index, "Example Plain").Languages;
    Assert.Empty(plain.Audio.Concat(plain.Subtitles).Concat(plain.Markers).Concat(plain.PrefixTags));
    Assert.Null(plain.Source);
    var vostfr = Find(index, "Example Kino VOSTFR").Languages;
    Assert.Empty(vostfr.Audio);
    Assert.Equal(new[] { "fr" }, vostfr.Subtitles);
  }

  [Fact]
  public async Task Search_CarriesPrefixTagsAsWritten()
  {
    var index = await Analyze(Tagged);

    var uk = Find(index, "UK: Example").Languages;
    Assert.Equal(new[] { "UK" }, uk.PrefixTags);
    Assert.Empty(uk.Audio);
    Assert.Equal("title", uk.Source);
    var group = Find(index, "Example Serbian").Languages;
    Assert.Equal(new[] { "EX-YU" }, group.PrefixTags);
    Assert.Empty(group.Audio);
  }

  [Fact]
  public void LanguageTable_StaysWithinFourBytesPerEntry()
  {
    const int count = 10_000;
    var tagged = new PlaylistSearchIndex(long.MaxValue);
    var plain = new PlaylistSearchIndex(long.MaxValue);
    for (var i = 0; i < count; i++)
    {
      // Same lengths; only the first set carries tags, in a handful of combinations.
      var code = new[] { "DE", "FR", "UK", "IT" }[i % 4];
      Assert.True(tagged.TryAdd($"#EXTINF:-1,{code}: Film {i}", "Films", PlaylistContentType.Movies));
      Assert.True(plain.TryAdd($"#EXTINF:-1,Xy: Film {i}", "Films", PlaylistContentType.Movies));
    }

    // The per-entry fields grow by one int at most, and the shared table costs well under a byte per entry.
    var blocks = Get<List<int[]>>(tagged, "_blocks");
    Assert.True(blocks[0].Length * sizeof(int) / 4096 <= (3 * sizeof(int)) + 4);
    Assert.True(tagged.EstimatedBytes - plain.EstimatedBytes <= 4 * 1024, $"{tagged.EstimatedBytes - plain.EstimatedBytes} bytes");
    Assert.Equal(5, Get<System.Collections.IList>(tagged, "_languages").Count);
    Assert.True(tagged.EstimatedBytes > plain.EstimatedBytes);
  }

  [Fact]
  public void LanguageTable_CountsAgainstBudget()
  {
    var plain = new PlaylistSearchIndex(long.MaxValue);
    Assert.True(plain.TryAdd("#EXTINF:-1,Xy: Example", "Films", PlaylistContentType.Movies));

    // A budget that fits the entry without tags no longer fits it with its language combination.
    var bounded = new PlaylistSearchIndex(plain.EstimatedBytes);
    Assert.False(bounded.TryAdd("#EXTINF:-1,DE: Example", "Films", PlaylistContentType.Movies));
  }

  [Fact]
  public async Task Search_CarriesHyphenatedTagsWithoutAdjacentCapitals()
  {
    var index = await Analyze("""
      #EXTM3U
      #EXTINF:-1 group-title="Films",A-B: Example
      https://example.com/movie/u/p/1.mp4
      #EXTINF:-1 group-title="Films",Example [A-B]
      https://example.com/movie/u/p/2.mp4
      """);

    foreach (var name in new[] { "A-B: Example", "Example [A-B]" })
    {
      var tags = Find(index, name).Languages;
      Assert.Equal(new[] { "A-B" }, tags.PrefixTags);
      Assert.Equal("title", tags.Source);
    }
  }

  // Each tag keeps at least a string object (about 32 bytes for five letters) and an array reference.
  private const int RetainedBytesPerTag = 40;

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void LanguageTable_CountsEveryDistinctTag(bool inGroupTitle)
  {
    const int count = 200;
    const int tagsPerEntry = 10;
    var tagged = new PlaylistSearchIndex(long.MaxValue);
    var plain = new PlaylistSearchIndex(long.MaxValue);
    for (var i = 0; i < count; i++)
    {
      // Distinct bracketed tags per entry; the plain twin has the same lengths in lower case, which hold no tag.
      var tags = string.Join(' ', Enumerable.Range(i * tagsPerEntry, tagsPerEntry).Select(n => $"[{Code(n)}]"));
      var (name, group) = inGroupTitle ? ($"Film {i}", $"{tags} Films") : ($"{tags} Film {i}", "Films");
      Assert.True(tagged.TryAdd($"#EXTINF:-1,{name}", group, PlaylistContentType.Movies));
      Assert.True(plain.TryAdd($"#EXTINF:-1,{name.ToLowerInvariant()}", group.ToLowerInvariant(), PlaylistContentType.Movies));
    }

    Assert.Equal(count + 1, Get<System.Collections.IList>(tagged, "_languages").Count);
    var floor = (long)count * tagsPerEntry * RetainedBytesPerTag;
    Assert.True(tagged.EstimatedBytes - plain.EstimatedBytes >= floor, $"{tagged.EstimatedBytes - plain.EstimatedBytes} bytes");

    // A budget that holds the plain list and the floor is exceeded once every tag is counted.
    var bounded = new PlaylistSearchIndex(plain.EstimatedBytes + floor);
    var added = Enumerable.Range(0, count).All(i =>
    {
      var tags = string.Join(' ', Enumerable.Range(i * tagsPerEntry, tagsPerEntry).Select(n => $"[{Code(n)}]"));
      var (name, group) = inGroupTitle ? ($"Film {i}", $"{tags} Films") : ($"{tags} Film {i}", "Films");
      return bounded.TryAdd($"#EXTINF:-1,{name}", group, PlaylistContentType.Movies);
    });
    Assert.False(added);
  }

  private static string Code(int n) =>
    $"Q{(char)('A' + (n / 17576 % 26))}{(char)('A' + (n / 676 % 26))}{(char)('A' + (n / 26 % 26))}{(char)('A' + (n % 26))}";

  [Fact]
  public async Task OverBudget_AnalysisWithLanguagesStillSucceeds()
  {
    var path = await WriteTemp(Tagged);
    try
    {
      var full = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None);
      var bounded = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None, maxSearchIndexBytes: 200);

      Assert.True(full.SearchIndexAvailable);
      Assert.False(bounded.SearchIndexAvailable);
      Assert.Equal(full.TotalChannels, bounded.TotalChannels);
      Assert.Equal(full.Groups, bounded.Groups);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public async Task Output_KeepsHeaderOptionLinesAttributesAndOrder()
  {
    var sourcePath = await WriteTemp(Tagged);
    var outputPath = Path.Combine(Path.GetTempPath(), $"strim-test-{Guid.NewGuid():N}.m3u");
    try
    {
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(sourcePath, CancellationToken.None);
      Assert.True(analysis.SearchIndexAvailable);
      await PlaylistProcessor.GenerateFilteredFileAsync(
        sourcePath, outputPath, new HashSet<string>(), CancellationToken.None, splitGroupTitles: analysis.SplitGroupTitles);

      // Every source line comes back unchanged and in order; Strim only adds its own comment line.
      var written = (await File.ReadAllLinesAsync(outputPath))
        .Where(l => l.Length > 0 && !l.StartsWith("# Created with Strim", StringComparison.Ordinal));
      Assert.Equal(Tagged.Split('\n').Select(l => l.TrimEnd('\r')), written);
    }
    finally
    {
      File.Delete(sourcePath);
      File.Delete(outputPath);
    }
  }

  private static PlaylistSearchMatch Find(PlaylistSearchIndex index, string name) =>
    Assert.Single(index.Search(name, new HashSet<string>(), 10).Matches, m => m.Name == name);

  private static T Get<T>(PlaylistSearchIndex index, string name) =>
    (T)typeof(PlaylistSearchIndex).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index)!;

  private static async Task<PlaylistSearchIndex> Analyze(string source)
  {
    var path = await WriteTemp(source);
    try
    {
      var analysis = await PlaylistProcessor.AnalyzeFileAsync(path, CancellationToken.None);
      return Assert.IsType<PlaylistSearchIndex>(analysis.SearchIndex);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private static async Task<string> WriteTemp(string text)
  {
    var path = Path.GetTempFileName();
    await File.WriteAllTextAsync(path, text);
    return path;
  }
}
