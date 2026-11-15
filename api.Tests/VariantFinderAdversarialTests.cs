using System.Diagnostics;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class VariantFinderAdversarialTests
{
  private const int Tokens = 20_000;
  private const int EntryCount = 500;

  private static string Repeat(string part, int count) => string.Concat(Enumerable.Repeat(part, count));

  public static TheoryData<string, string> AdversarialNames => new()
  {
    { "Sport 1" + Repeat(" HD", Tokens), "Sport 1 HD" },
    { Repeat(" ", Tokens) + "Sport 1" + Repeat(" HD", Tokens), " Sport 1 HD" },
    { "Sport 1" + Repeat(" HD", Tokens) + " x", "Sport 1 HD x" },
    { "Sport 1" + Repeat(" (HD)", Tokens / 2) + Repeat("-", Tokens), "Sport 1 (HD)-" },
  };

  [Theory]
  [MemberData(nameof(AdversarialNames))]
  public void Report_OnManyQualityTokens_IsFastAndMatchesSmallInput(string longName, string shortName)
  {
    var entries = Enumerable.Range(0, EntryCount)
      .Select(i => new PlaylistEntry(i, i == 0 ? shortName : longName, "A", null, $"http://example.invalid/live/{i}.ts"))
      .ToList();

    var watch = Stopwatch.StartNew();
    var report = PlaylistReport.Build(entries);
    watch.Stop();

    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The report took {watch.Elapsed}.");
    var (longBase, longWords) = VariantFinder.Detect(longName);
    var (shortBase, shortWords) = VariantFinder.Detect(shortName);
    Assert.Equal(shortBase, longBase);
    Assert.Equal(shortWords, longWords.Distinct());
    Assert.True(longWords.Count >= Tokens / 2);
    var group = Assert.Single(report.VariantGroups);
    Assert.Equal(shortBase, group.BaseName);
    Assert.Equal(EntryCount, group.Entries.Count);
  }

  [Fact]
  public void Report_ObservesCancellation()
  {
    var entries = new List<PlaylistEntry>
    {
      new(0, "Sport 1 HD", "A", null, "http://example.invalid/live/0.ts"),
      new(1, "Sport 1", "A", null, "http://example.invalid/live/1.ts"),
    };
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();

    Assert.ThrowsAny<OperationCanceledException>(() => PlaylistReport.Build(entries, cancellationToken: cancelled.Token));
  }
}
