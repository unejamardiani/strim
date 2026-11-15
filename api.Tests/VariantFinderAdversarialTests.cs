using System.Diagnostics;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class VariantFinderAdversarialTests
{
  private const int Tokens = 20_000;
  private const int EntryCount = 500;

  private static string Repeat(string part, int count) => string.Concat(Enumerable.Repeat(part, count));

  // Adversarial name shapes, each built with a given number of quality tokens.
  private static readonly Func<int, string>[] Shapes =
  [
    tokens => "Sport 1" + Repeat(" HD", tokens),
    tokens => Repeat(" ", tokens) + "Sport 1" + Repeat(" HD", tokens),
    tokens => "Sport 1" + Repeat(" HD", tokens) + " x",
    tokens => "Sport 1" + Repeat(" (HD)", tokens / 2) + Repeat("-", tokens),
  ];

  public static TheoryData<int, string> AdversarialNames => new()
  {
    { 0, "Sport 1 HD" },
    { 1, " Sport 1 HD" },
    { 2, "Sport 1 HD x" },
    { 3, "Sport 1 (HD)-" },
  };

  public static TheoryData<int> ShapeIndexes => new() { 0, 1, 2, 3 };

  [Theory]
  [MemberData(nameof(AdversarialNames))]
  public void Report_OnManyQualityTokens_FinishesAndMatchesSmallInput(int shape, string shortName)
  {
    var longName = Shapes[shape](Tokens);
    var entries = Enumerable.Range(0, EntryCount)
      .Select(i => new PlaylistEntry(i, i == 0 ? shortName : longName, "A", null, $"http://example.invalid/live/{i}.ts"))
      .ToList();

    var watch = Stopwatch.StartNew();
    var report = PlaylistReport.Build(entries);
    watch.Stop();

    // Only a very generous guard against a stall; linearity is proven by the scaling test below.
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"The report took {watch.Elapsed}.");
    var (longBase, longWords) = VariantFinder.Detect(longName);
    var (shortBase, shortWords) = VariantFinder.Detect(shortName);
    Assert.Equal(shortBase, longBase);
    Assert.Equal(shortWords, longWords.Distinct());
    Assert.True(longWords.Count >= Tokens / 2);
    var group = Assert.Single(report.VariantGroups);
    Assert.Equal(shortBase, group.BaseName);
    Assert.Equal(EntryCount, group.Entries.Count);
  }

  [Theory]
  [MemberData(nameof(ShapeIndexes))]
  public void Report_WorkGrowsLinearlyWithTheNameLength(int shape)
  {
    // Compares the report on names four times as long instead of an absolute time, so the machine's speed
    // cancels out. Linear work grows about four times, the earlier quadratic parsing about sixteen times.
    const int SmallTokens = 10_000;
    const int Factor = 4;
    const int Entries = 20;
    var small = SameNames(Shapes[shape](SmallTokens), Entries);
    var large = SameNames(Shapes[shape](SmallTokens * Factor), Entries);

    PlaylistReport.Build(small);
    var smallTime = Fastest(small);
    var largeTime = Fastest(large);

    var ratio = largeTime.TotalMilliseconds / Math.Max(smallTime.TotalMilliseconds, 1);
    Assert.True(ratio < Factor * 2.5,
      $"Names {Factor} times as long took {ratio:F1} times as long ({smallTime} against {largeTime}).");
  }

  private static List<PlaylistEntry> SameNames(string name, int count) => Enumerable.Range(0, count)
    .Select(i => new PlaylistEntry(i, name, "A", null, $"http://example.invalid/live/{i}.ts"))
    .ToList();

  /// <summary>The fastest of a few runs, so a pause of the machine or the collector does not count.</summary>
  private static TimeSpan Fastest(List<PlaylistEntry> entries)
  {
    var best = TimeSpan.MaxValue;
    for (var run = 0; run < 5; run++)
    {
      var watch = Stopwatch.StartNew();
      PlaylistReport.Build(entries);
      watch.Stop();
      if (watch.Elapsed < best) best = watch.Elapsed;
      if (best > TimeSpan.FromSeconds(60)) break;
    }
    Assert.True(best < TimeSpan.FromSeconds(60), $"The report took {best}.");
    return best;
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
  public void Report_CancelledDuringTheVariantPass_StopsBeforeTheRemainingEntries()
  {
    using var source = new CancellationTokenSource();
    // The duplicate pass reads by index and never cancels; the variant pass enumerates and cancels on entry 3.
    var entries = new VariantPassCancellingList(Names(10), cancelAt: 3, source);

    Assert.ThrowsAny<OperationCanceledException>(() => PlaylistReport.Build(entries, cancellationToken: source.Token));
    // The duplicate pass read every entry before the variant pass began; the variant pass stopped after entry 3.
    Assert.True(entries.IndexedReads >= entries.Count, $"The duplicate pass read {entries.IndexedReads} entries.");
    Assert.Equal(4, entries.EnumeratedReads);
  }

  /// <summary>
  /// Counts the entries the variant pass enumerates and cancels the source once entry <c>cancelAt</c> has been
  /// handed out. Reads by index (the duplicate pass) are only counted, so cancelling starts inside the variant loop.
  /// </summary>
  private sealed class VariantPassCancellingList(List<PlaylistEntry> entries, int cancelAt, CancellationTokenSource source)
    : IReadOnlyList<PlaylistEntry>
  {
    public int IndexedReads { get; private set; }

    public int EnumeratedReads { get; private set; }

    public int Count => entries.Count;

    public PlaylistEntry this[int index]
    {
      get
      {
        IndexedReads++;
        return entries[index];
      }
    }

    public IEnumerator<PlaylistEntry> GetEnumerator()
    {
      for (var i = 0; i < entries.Count; i++)
      {
        EnumeratedReads++;
        if (i == cancelAt) source.Cancel();
        yield return entries[i];
      }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
