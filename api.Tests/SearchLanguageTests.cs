using System.Reflection;
using Api.Services;
using Xunit;

namespace Api.Tests;

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
