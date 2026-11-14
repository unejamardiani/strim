using System.Globalization;
using System.Text;

namespace Api.Services;

/// <summary>
/// Splits a group title into the distinct, lower-cased whole words a search can match.
/// A word is a run of letters, digits and combining marks; everything else (whitespace,
/// punctuation such as | : - / [ ] ( ) and bar separators like ▎ ┃ │) separates words.
/// </summary>
public static class GroupSearchWords
{
  public static IReadOnlyList<string> From(string? title)
  {
    if (string.IsNullOrWhiteSpace(title))
    {
      return Array.Empty<string>();
    }

    var words = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var current = new StringBuilder();

    void Flush()
    {
      if (current.Length == 0)
      {
        return;
      }

      var word = current.ToString().ToLowerInvariant();
      current.Clear();
      if (seen.Add(word))
      {
        words.Add(word);
      }
    }

    foreach (var rune in title.EnumerateRunes())
    {
      if (IsWordRune(rune))
      {
        current.Append(rune.ToString());
      }
      else
      {
        Flush();
      }
    }

    Flush();
    return words;
  }

  private static bool IsWordRune(Rune rune)
  {
    if (Rune.IsLetterOrDigit(rune))
    {
      return true;
    }

    return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
      or UnicodeCategory.SpacingCombiningMark
      or UnicodeCategory.EnclosingMark;
  }
}
