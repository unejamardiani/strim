using Api.Models;

namespace Api.Services;

/// <summary>
/// Builds the read-only duplicates and variants report from <see cref="DuplicateFinder"/> and
/// <see cref="VariantFinder"/>. Counts always cover the whole list; only the listed sets are capped.
/// Addresses are not part of the report, because provider addresses carry account details.
/// </summary>
public static class PlaylistReport
{
  public const int MaxListedSetsPerKind = 500;

  public static PlaylistReportResponse Build(
    IReadOnlyList<PlaylistEntry> entries,
    int maxListedSetsPerKind = MaxListedSetsPerKind,
    CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var duplicates = DuplicateFinder.Find(entries);
    var variants = VariantFinder.Find(entries, cancellationToken);

    var duplicateSets = duplicates
      .Take(maxListedSetsPerKind)
      .Select(set => new ReportDuplicateSet(
        set.Groups,
        set.Entries.Select(e =>
        {
          cancellationToken.ThrowIfCancellationRequested();
          return new ReportEntry(e.Name, e.GroupTitle, VariantFinder.Detect(e.Name).QualityWords);
        }).ToList()))
      .ToList();
    var variantGroups = variants
      .Take(maxListedSetsPerKind)
      .Select(group => new ReportVariantGroup(
        group.BaseName,
        group.Groups,
        group.Entries.Select(v => new ReportEntry(v.Entry.Name, v.Entry.GroupTitle, v.QualityWords)).ToList()))
      .ToList();

    return new PlaylistReportResponse(
      entries.Count,
      duplicates.Count,
      duplicates.Sum(set => set.Entries.Count),
      variants.Count,
      variants.Sum(group => group.Entries.Count),
      duplicates.Count > duplicateSets.Count || variants.Count > variantGroups.Count,
      duplicateSets,
      variantGroups);
  }

  /// <summary>
  /// How often <see cref="BuildStreamingAsync"/> reads its source: twice for the duplicates, twice for the variants
  /// and once to collect the details of the listed entries.
  /// </summary>
  public const int StreamingPassCount = StreamingDuplicateFinder.PassCount + StreamingVariantFinder.PassCount + 1;

  // Estimates of the heap the final pass holds, with room for growth slack.
  private const long PerWantedBytes = 48;   // one slot of the wanted-index dictionary, doubled for growth
  private const long PerDetailBytes = 160;  // a report entry, its two string headers and its quality-word list
  private const long PerListedBytes = 96;   // a listed set or group, its entry list and its group list
  private const long PerSlotBytes = 16;     // one slot in an entry, group or word list, doubled for growth

  /// <summary>
  /// Builds the same report as <see cref="Build"/> from a source that is read <see cref="StreamingPassCount"/> times
  /// and must yield the same entries each time, without holding the entries. Only the name, group and quality words
  /// of the entries of the listed sets are kept; addresses never are. The bytes held (finder data and collected
  /// details) are added to <paramref name="counter"/>; once they pass <paramref name="maxBytes"/> the build stops with
  /// <see cref="PlaylistReportTooLargeException"/>.
  /// </summary>
  public static async Task<PlaylistReportResponse> BuildStreamingAsync(
    Func<CancellationToken, IAsyncEnumerable<PlaylistEntry>> source,
    long maxBytes,
    int maxListedSetsPerKind,
    CancellationToken cancellationToken,
    HeldBytesCounter? counter = null)
  {
    counter ??= new HeldBytesCounter();
    var previous = counter.Added;
    // Checked on every addition, so the finders stop as soon as the budget is passed, also while they read.
    counter.Added = total =>
    {
      if (total > maxBytes) throw new PlaylistReportTooLargeException();
    };
    try
    {
      return await BuildCountedAsync(source, maxListedSetsPerKind, counter, cancellationToken);
    }
    finally
    {
      counter.Added = previous;
    }
  }

  private static async Task<PlaylistReportResponse> BuildCountedAsync(
    Func<CancellationToken, IAsyncEnumerable<PlaylistEntry>> source,
    int maxListedSetsPerKind,
    HeldBytesCounter counter,
    CancellationToken cancellationToken)
  {
    void Hold(long bytes) => counter.Add(bytes);

    cancellationToken.ThrowIfCancellationRequested();
    var duplicates = await StreamingDuplicateFinder.FindAsync(source, cancellationToken, counter: counter);
    var variants = await StreamingVariantFinder.FindAsync(source, cancellationToken, counter: counter);

    var listedSets = duplicates.Sets.Take(maxListedSetsPerKind).ToList();
    var listedGroups = variants.Groups.Take(maxListedSetsPerKind).ToList();
    var wanted = new Dictionary<int, ReportEntry?>();
    foreach (var index in listedSets.SelectMany(set => set).Concat(listedGroups.SelectMany(group => group.Members)))
    {
      if (wanted.TryAdd(index, null)) Hold(PerWantedBytes);
    }

    var entryCount = 0;
    await foreach (var entry in source(cancellationToken).WithCancellation(cancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (wanted.ContainsKey(entryCount))
      {
        var words = VariantFinder.Detect(entry.Name).QualityWords;
        Hold(PerDetailBytes + 2L * (entry.Name.Length + entry.GroupTitle.Length) + PerSlotBytes * words.Count);
        wanted[entryCount] = new ReportEntry(entry.Name, entry.GroupTitle, words);
      }
      entryCount++;
    }

    List<ReportEntry> Details(IReadOnlyList<int> members)
    {
      var details = members.Select(index => wanted[index]
        ?? throw new InvalidOperationException("The source yielded fewer entries than in the first pass.")).ToList();
      Hold(PerListedBytes + PerSlotBytes * members.Count);
      return details;
    }
    List<string> Groups(List<ReportEntry> details)
    {
      var groups = details.Select(e => e.Group).Distinct(StringComparer.Ordinal).ToList();
      Hold(PerSlotBytes * groups.Count);
      return groups;
    }

    var duplicateSets = listedSets.Select(set =>
    {
      var details = Details(set);
      return new ReportDuplicateSet(Groups(details), details);
    }).ToList();
    var variantGroups = listedGroups.Select(group =>
    {
      var details = Details(group.Members);
      return new ReportVariantGroup(group.BaseName, Groups(details), details);
    }).ToList();

    return new PlaylistReportResponse(
      entryCount,
      duplicates.SetCount,
      duplicates.EntryCount,
      variants.GroupCount,
      variants.EntryCount,
      duplicates.SetCount > duplicateSets.Count || variants.GroupCount > variantGroups.Count,
      duplicateSets,
      variantGroups);
  }
}

/// <summary>The entries of a playlist pass <c>PlaylistCache:MaxReportBytes</c>; the message is safe to show.</summary>
public sealed class PlaylistReportTooLargeException : InvalidOperationException
{
  public PlaylistReportTooLargeException()
    : base("This playlist is too large for a duplicates and variants report.")
  {
  }
}
