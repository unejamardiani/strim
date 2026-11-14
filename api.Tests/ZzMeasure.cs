using Api.Services;
using Xunit;
using Xunit.Abstractions;

namespace Api.Tests;

public class ZzMeasure(ITestOutputHelper o)
{
  private sealed class CS : HashSet<string>, ICollection<string>
  {
    public CS() : base(StringComparer.OrdinalIgnoreCase) { }
    public int N;
    bool ICollection<string>.Contains(string i) { N++; return Contains(i); }
  }

  [Fact]
  public async Task Measure()
  {
    var src = Path.GetTempFileName();
    var p = new[] { "", "movie/", "series/" };
    var sb = new System.Text.StringBuilder("#EXTM3U\n");
    for (var i = 0; i < 10_000; i++)
    {
      sb.Append($"#EXTINF:-1 group-title=\"Title {i % 20:D2}\",Entry {i}\n");
      sb.Append($"http://provider.example/{p[i / 20 % 3]}user/pass/{i}\n");
    }
    await File.WriteAllTextAsync(src, sb.ToString());
    var split = new HashSet<string>(Enumerable.Range(0, 20).Select(i => $"Title {i:D2}"), StringComparer.OrdinalIgnoreCase);
    for (var r = 0; r < 4; r++)
    {
      var outp = Path.Combine(Path.GetTempPath(), $"m-{Guid.NewGuid():N}.m3u");
      var d = new CS { "Title 00 · Movies", "Title 01" };
      GC.Collect();
      var b = GC.GetTotalAllocatedBytes(true);
      await PlaylistProcessor.GenerateFilteredFileAsync(src, outp, d, CancellationToken.None, splitGroupTitles: split);
      var a = GC.GetTotalAllocatedBytes(true);
      o.WriteLine($"MEASURE run {r}: contains={d.N} bytes={a - b}");
      File.Delete(outp);
    }
    File.Delete(src);
  }
}
