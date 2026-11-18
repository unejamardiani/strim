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

    var duplicateMembers = new HashSet<PlaylistEntry>(
      duplicates.SelectMany(set => set.Entries), ReferenceEqualityComparer.Instance);
    var variantMembers = new HashSet<PlaylistEntry>(
      variants.SelectMany(group => group.Entries.Select(v => v.Entry)), ReferenceEqualityComparer.Instance);
    var tally = new GroupCountTally(_ => { });
    foreach (var entry in entries)
    {
      tally.Add(entry, duplicateMembers.Contains(entry), variantMembers.Contains(entry));
    }

    return new PlaylistReportResponse(
      entries.Count,
      duplicates.Count,
      duplicates.Sum(set => set.Entries.Count),
      variants.Count,
      variants.Sum(group => group.Entries.Count),
      duplicates.Count > duplicateSets.Count || variants.Count > variantGroups.Count,
      duplicateSets,
      variantGroups,
      tally.Build());
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

    // Every member of every set, not only the listed ones, so the per-group counts cover the whole list.
    var duplicateMembers = SortedMembers(duplicates.Sets, Hold);
    var variantMembers = SortedMembers(variants.Groups.Select(group => group.Members).ToList(), Hold);
    var duplicateAt = 0;
    var variantAt = 0;
    var tally = new GroupCountTally(Hold);

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
      tally.Add(
        entry,
        IsMember(duplicateMembers, ref duplicateAt, entryCount),
        IsMember(variantMembers, ref variantAt, entryCount));
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
      variantGroups,
      tally.Build());
  }

  private static int[] SortedMembers(IReadOnlyList<IReadOnlyList<int>> sets, Action<long> hold)
  {
    var members = new int[sets.Sum(set => set.Count)];
    hold(sizeof(int) * (long)members.Length);
    var at = 0;
    foreach (var set in sets)
    {
      foreach (var index in set) members[at++] = index;
    }
    Array.Sort(members);
    return members;
  }

  /// <summary>Whether <paramref name="index"/> is in the sorted members; indices must be asked in rising order.</summary>
  private static bool IsMember(int[] members, ref int at, int index)
  {
    while (at < members.Length && members[at] < index) at++;
    return at < members.Length && members[at] == index;
  }

  /// <summary>
  /// Counts duplicate and variant entries per analysis group key. As in the analysis, a title gets one key per
  /// content type when the whole list holds it with more than one content type, so every entry is added.
  /// </summary>
  private sealed class GroupCountTally(Action<long> hold)
  {
    private const long PerTitleBytes = 256;  // a dictionary slot, the title's record and its three arrays, doubled
    private const long PerCountBytes = 96;   // a group count, its key string header and its list slot
    private static readonly int ContentTypeCount = Enum.GetValues<PlaylistContentType>().Length;

    private readonly Dictionary<string, Title> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Title> _order = new();

    public void Add(PlaylistEntry entry, bool isDuplicate, bool isVariant)
    {
      if (!_titles.TryGetValue(entry.GroupTitle, out var title))
      {
        hold(PerTitleBytes + 2L * entry.GroupTitle.Length);
        title = new Title(entry.GroupTitle, new int[ContentTypeCount], new int[ContentTypeCount], new bool[ContentTypeCount]);
        _titles.Add(entry.GroupTitle, title);
        _order.Add(title);
      }
      var type = (int)PlaylistProcessor.DetectContentType(entry.Address);
      title.Seen[type] = true;
      if (isDuplicate) title.Duplicates[type]++;
      if (isVariant) title.Variants[type]++;
    }

    public List<ReportGroupCount> Build()
    {
      var counts = new List<ReportGroupCount>();
      foreach (var title in _order)
      {
        var split = title.Seen.Count(seen => seen) > 1;
        for (var type = 0; type < ContentTypeCount; type++)
        {
          if (title.Duplicates[type] == 0 && title.Variants[type] == 0) continue;
          var key = split ? PlaylistProcessor.GroupKey(title.Name, (PlaylistContentType)type) : title.Name;
          hold(PerCountBytes + 2L * key.Length);
          counts.Add(new ReportGroupCount(key, title.Duplicates[type], title.Variants[type]));
        }
      }
      return counts;
    }

    private sealed record Title(string Name, int[] Duplicates, int[] Variants, bool[] Seen);
  }
}

/// <summary>
/// What a report holds passes <c>PlaylistCache:MaxReportBytes</c>: in the endpoint's multi-pass build the finder data
/// and the details of the listed entries, in <see cref="PlaylistProcessor.ReadEntriesFromFileAsync"/> the entries
/// read. The message is safe to show.
/// </summary>
public sealed class PlaylistReportTooLargeException : InvalidOperationException
{
  public PlaylistReportTooLargeException()
    : base("This playlist is too large for a duplicates and variants report.")
  {
  }
}
