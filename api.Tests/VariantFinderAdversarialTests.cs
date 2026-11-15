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
  public void Detect_KeepsTheCaseEquivalenceOfTheEarlierRegex()
  {
    // The long s (U+017F) upper-cases to S, but the earlier regex did not treat it as an s.
    AssertDetects("Sport ſD", "Sport ſD");
    AssertDetects("Sport hd", "Sport", "HD");
    AssertDetects("Sport h.265", "Sport", "H265");
  }

  private static void AssertDetects(string name, string baseName, params string[] words)
  {
    var result = VariantFinder.Detect(name);
    Assert.Equal(baseName, result.BaseName);
    Assert.Equal(words, result.QualityWords);
  }

  [Fact]
  public void Report_ObservesCancellationBetweenEntries()
  {
    using var source = new CancellationTokenSource();
    // No duplicates, so only the variant pass reads the entries after the duplicate pass cancelled.
    var entries = new CancellingList(Names(4), cancelAt: 2, source);

    Assert.ThrowsAny<OperationCanceledException>(() => PlaylistReport.Build(entries, cancellationToken: source.Token));
    Assert.True(entries.CancelledWhileReading);
  }

  [Fact]
  public void ReportEndpoint_PassesTheRequestAbortToken()
  {
    using var source = new CancellationTokenSource();
    var entries = new CancellingList(Names(4), cancelAt: 2, source);
    var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestAborted = source.Token };

    Assert.ThrowsAny<OperationCanceledException>(() => Api.Endpoints.ProcessingEndpoints.BuildReport(entries, context));
    Assert.True(entries.CancelledWhileReading);
  }

  private static List<PlaylistEntry> Names(int count) => Enumerable.Range(0, count)
    .Select(i => new PlaylistEntry(i, i % 2 == 0 ? $"Sport {i} HD" : $"Sport {i - 1}", "A", null, $"http://example.invalid/live/{i}.ts"))
    .ToList();

  /// <summary>Cancels the source when an entry at or after <c>cancelAt</c> is read, that is while Build is running.</summary>
  private sealed class CancellingList(List<PlaylistEntry> entries, int cancelAt, CancellationTokenSource source)
    : IReadOnlyList<PlaylistEntry>
  {
    public bool CancelledWhileReading { get; private set; }

    public int Count => entries.Count;

    public PlaylistEntry this[int index]
    {
      get
      {
        if (index >= cancelAt && !source.IsCancellationRequested)
        {
          source.Cancel();
          CancelledWhileReading = true;
        }
        return entries[index];
      }
    }

    public IEnumerator<PlaylistEntry> GetEnumerator()
    {
      for (var i = 0; i < entries.Count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
  }
}
