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

  public static PlaylistReportResponse Build(IReadOnlyList<PlaylistEntry> entries, int maxListedSetsPerKind = MaxListedSetsPerKind)
  {
    var duplicates = DuplicateFinder.Find(entries);
    var variants = VariantFinder.Find(entries);

    var duplicateSets = duplicates
      .Take(maxListedSetsPerKind)
      .Select(set => new ReportDuplicateSet(
        set.Groups,
        set.Entries.Select(e => new ReportEntry(e.Name, e.GroupTitle, VariantFinder.Detect(e.Name).QualityWords)).ToList()))
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
}
