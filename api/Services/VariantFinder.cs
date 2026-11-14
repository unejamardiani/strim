using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>An entry of a variant group with the quality words found at the end of its name.</summary>
public sealed record VariantEntry(PlaylistEntry Entry, IReadOnlyList<string> QualityWords);

/// <summary>Entries that are quality variants of one channel, in playlist order, and the distinct groups they sit in.</summary>
public sealed record VariantGroup(string BaseName, IReadOnlyList<VariantEntry> Entries, IReadOnlyList<string> Groups);

/// <summary>
/// Finds quality variants of one channel by the quality words SD, HD, FHD, 4K, UHD, HEVC, H265 (also H.265),
/// backup and raw. A word counts only as a whole token at the end of the name, after a space, <c>|</c>,
/// <c>-</c> or an opening bracket, so <c>HDTV Classics</c>, <c>4Kids</c> or <c>Backup Plan Movie</c> keep their
/// names. The rest is the base name (trimmed, spaces collapsed, compared case-sensitively). Entries with the same
/// base name form a group when there are at least two and at least one carries a quality word.
/// Only reads: no entry and not the input list is changed.
/// </summary>
public static class VariantFinder
{
  private static readonly Regex TrailingQualityWord = new(
    @"[\s|\-(\[]+(?<word>SD|HD|FHD|4K|UHD|HEVC|H\.?265|backup|raw)[\s)\]]*$",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

  private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

  /// <summary>Splits a name into its base name and the quality words at its end, in name order.</summary>
  public static (string BaseName, IReadOnlyList<string> QualityWords) Detect(string name)
  {
    var rest = name;
    var words = new List<string>();
    while (TrailingQualityWord.Match(rest) is { Success: true } match)
    {
      var before = rest[..match.Index];
      if (string.IsNullOrWhiteSpace(before)) break;
      words.Insert(0, Canonical(match.Groups["word"].Value));
      rest = before;
    }
    return (Spaces.Replace(rest, " ").Trim(), words);
  }

  public static IReadOnlyList<VariantGroup> Find(IReadOnlyList<PlaylistEntry> entries)
  {
    var byBase = new Dictionary<string, List<VariantEntry>>(StringComparer.Ordinal);
    var order = new List<string>();
    foreach (var entry in entries)
    {
      var (baseName, words) = Detect(entry.Name);
      if (baseName.Length == 0) continue;
      if (!byBase.TryGetValue(baseName, out var list))
      {
        byBase[baseName] = list = new List<VariantEntry>();
        order.Add(baseName);
      }
      list.Add(new VariantEntry(entry, words));
    }

    var groups = new List<VariantGroup>();
    foreach (var baseName in order)
    {
      var list = byBase[baseName];
      if (list.Count < 2 || !list.Any(v => v.QualityWords.Count > 0)) continue;
      var groupTitles = list.Select(v => v.Entry.GroupTitle).Distinct(StringComparer.Ordinal).ToList();
      groups.Add(new VariantGroup(baseName, list, groupTitles));
    }
    return groups;
  }

  private static string Canonical(string word) => word.ToUpperInvariant() switch
  {
    "H.265" => "H265",
    "BACKUP" => "backup",
    "RAW" => "raw",
    var upper => upper,
  };
}
