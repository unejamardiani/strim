using System.Text;
using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>An entry of a variant group with the quality words found in its name.</summary>
public sealed record VariantEntry(PlaylistEntry Entry, IReadOnlyList<string> QualityWords);

/// <summary>Entries that are quality variants of one channel, in playlist order, and the distinct groups they sit in.</summary>
public sealed record VariantGroup(string BaseName, IReadOnlyList<VariantEntry> Entries, IReadOnlyList<string> Groups);

/// <summary>
/// Finds quality variants of one channel by the quality words SD, HD, FHD, 4K, UHD, HEVC, H265 (also H.265),
/// backup and raw. A word counts only as a whole token: it stands between the start of a token (the name start, a
/// space, <c>|</c>, <c>-</c> or an opening bracket) and its end (the same separators, a closing bracket or the end
/// of the name), optionally in round or square brackets, which are removed with it. backup and raw count only after
/// some name text; the other words in any position. So <c>HD Sport 1</c>, <c>Sport (HD) 1</c>, <c>Sport(HD)</c> and
/// <c>Sport 1 [FHD]</c> are variants of <c>Sport 1</c> or <c>Sport</c>, while <c>HDTV Classics</c>, <c>4Kids</c>,
/// <c>Sport1HDPlus</c> or <c>Backup Plan Movie</c> keep their names. The rest is the base name (separators behind
/// the last token removed, trimmed, spaces collapsed, compared case-sensitively); a name without a quality token
/// stays unchanged, so <c>Sport -</c> stays <c>Sport -</c>. Entries with the same base name form a group when there
/// are at least two and at least one carries a quality word.
/// Only reads: no entry and not the input list is changed.
/// </summary>
public static class VariantFinder
{
  // The quality words, anchored at the start of the span. The regex engine keeps the case equivalence of the
  // earlier whole-name regex (#170); for example it does not treat the long s (U+017F) as an s.
  private static readonly Regex WordAtStart = new(
    @"^(?:SD|HD|FHD|4K|UHD|HEVC|H\.?265|backup|raw)",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

  /// <summary>
  /// Splits a name into its base name and the quality words in it, in name order. One pass over the name
  /// (story #183): names are untrusted and may carry tens of thousands of tokens.
  /// </summary>
  public static (string BaseName, IReadOnlyList<string> QualityWords) Detect(string name)
  {
    var words = new List<string>();
    // backup and raw count only after some name text, that is after a character other than these, so titles
    // such as "Backup Plan Movie" keep their names. The technical words also count before the name (#199).
    var firstText = name.AsSpan().IndexOfAnyExcept(" \t|-([");
    var rest = new StringBuilder(name.Length);
    // The length of the rest right after the last removed token: separators are removed only behind a token.
    var afterLastToken = -1;
    var i = 0;
    while (i < name.Length)
    {
      if (!TryMatchWord(name, i, out var end, out var wordStart, out var wordEnd))
      {
        rest.Append(name[i++]);
        continue;
      }
      var word = Canonical(name.AsSpan(wordStart, wordEnd - wordStart));
      if ((firstText >= 0 && firstText < i) || !IsTitleWord(word))
      {
        words.Add(word);
        rest.Append(' ');
        afterLastToken = rest.Length;
      }
      else
      {
        rest.Append(name, i, end - i);
      }
      i = end;
    }
    // A name without a quality token is returned unchanged, and so is a name that is nothing but quality tokens.
    if (words.Count == 0) return (name, words);
    var baseName = BaseName(rest, afterLastToken);
    return baseName.Length == 0 ? (name, []) : (baseName, words);
  }

  /// <summary>Quality words that are also common words in titles, so they count only after some name text.</summary>
  private static bool IsTitleWord(string word) => word is "backup" or "raw";

  /// <summary>
  /// Matches an optionally bracketed quality word that starts at <paramref name="start"/>: after the name start or
  /// a separator or opening bracket, and before the name end or a separator or closing bracket.
  /// </summary>
  private static bool TryMatchWord(string name, int start, out int end, out int wordStart, out int wordEnd)
  {
    end = wordStart = wordEnd = 0;
    var bracketed = name[start] is '(' or '[';
    // An opening bracket starts a token even right after name text, as in Sport(HD) (#199).
    if (!bracketed && start > 0 && !IsSeparator(name[start - 1]) && name[start - 1] is not ('(' or '[')) return false;
    wordStart = bracketed ? start + 1 : start;
    if (wordStart >= name.Length) return false;
    wordEnd = MatchWordAt(name, wordStart);
    if (wordEnd < 0) return false;

    if (wordEnd < name.Length && name[wordEnd] is ')' or ']')
    {
      // A closing bracket ends the token; it belongs to the match only when the token ends right after it.
      end = wordEnd + 1 == name.Length || IsTokenEnd(name[wordEnd + 1]) ? wordEnd + 1 : wordEnd;
      return true;
    }
    end = wordEnd;
    return wordEnd == name.Length || IsTokenEnd(name[wordEnd]);
  }

  /// <summary>Returns the end of the quality word at <paramref name="start"/>, or -1 when none starts there.</summary>
  private static int MatchWordAt(string name, int start)
  {
    // Anchored, so the engine reads at most the length of the longest word: each call is bounded.
    foreach (var match in WordAtStart.EnumerateMatches(name.AsSpan(start))) return start + match.Length;
    return -1;
  }

  private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c is '|' or '-';

  private static bool IsTokenEnd(char c) => IsSeparator(c) || c is ')' or ']';

  /// <summary>
  /// Removes trailing separators when only separators follow the last removed token (at
  /// <paramref name="afterLastToken"/> in the rest), collapses white space to single spaces and trims.
  /// </summary>
  private static string BaseName(StringBuilder rest, int afterLastToken)
  {
    var end = rest.Length;
    var trailing = end;
    while (trailing > 0 && IsSeparator(rest[trailing - 1])) trailing--;
    if (trailing < afterLastToken) end = trailing;
    var result = new StringBuilder(end);
    var inSpace = false;
    for (var i = 0; i < end; i++)
    {
      if (char.IsWhiteSpace(rest[i]))
      {
        inSpace = true;
        continue;
      }
      if (inSpace && result.Length > 0) result.Append(' ');
      inSpace = false;
      result.Append(rest[i]);
    }
    return result.ToString();
  }

  public static IReadOnlyList<VariantGroup> Find(IReadOnlyList<PlaylistEntry> entries, CancellationToken cancellationToken = default)
  {
    var byBase = new Dictionary<string, List<VariantEntry>>(StringComparer.Ordinal);
    var order = new List<string>();
    foreach (var entry in entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
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

  private static string Canonical(ReadOnlySpan<char> word)
  {
    // Plain ASCII spellings map to a shared string, so many tokens do not allocate one string each.
    foreach (var known in KnownSpellings)
    {
      if (word.Equals(known.Spelling, StringComparison.OrdinalIgnoreCase) && Ascii.IsValid(word)) return known.Canonical;
    }
    return Canonical(word.ToString());
  }

  private static readonly (string Spelling, string Canonical)[] KnownSpellings =
  [
    ("SD", "SD"), ("HD", "HD"), ("FHD", "FHD"), ("4K", "4K"), ("UHD", "UHD"), ("HEVC", "HEVC"),
    ("H265", "H265"), ("H.265", "H265"), ("BACKUP", "backup"), ("RAW", "raw"),
  ];

  private static string Canonical(string word) => word.ToUpperInvariant() switch
  {
    "H.265" => "H265",
    "BACKUP" => "backup",
    "RAW" => "raw",
    var upper => upper,
  };
}
