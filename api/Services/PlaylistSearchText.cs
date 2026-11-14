using System.Globalization;
using System.Text;

namespace Api.Services;

/// <summary>
/// Turns an #EXTINF line into its display name and folds text into a search form, so an index
/// and a query compare the same way: case- and accent-insensitive, with separators collapsed.
/// </summary>
public static class PlaylistSearchText
{
  /// <summary>
  /// The text after the last comma outside double-quoted attributes, trimmed. An empty name
  /// falls back to the tvg-name attribute, then to an empty string.
  /// </summary>
  public static string DisplayName(string? extinfLine)
  {
    if (string.IsNullOrEmpty(extinfLine))
    {
      return string.Empty;
    }

    var lastComma = -1;
    var inQuotes = false;
    for (var i = 0; i < extinfLine.Length; i++)
    {
      var c = extinfLine[i];
      if (c == '"')
      {
        inQuotes = !inQuotes;
      }
      else if (c == ',' && !inQuotes)
      {
        lastComma = i;
      }
    }

    var name = lastComma >= 0 ? extinfLine[(lastComma + 1)..].Trim() : string.Empty;
    return name.Length > 0 ? name : AttributeValue(extinfLine, "tvg-name")?.Trim() ?? string.Empty;
  }

  /// <summary>
  /// Lower-cases with the invariant culture, removes diacritics and collapses every run of
  /// whitespace and punctuation to one space. Null or empty text gives an empty string.
  /// </summary>
  public static string Fold(string? text)
  {
    if (string.IsNullOrEmpty(text))
    {
      return string.Empty;
    }

    var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
    var folded = new StringBuilder(decomposed.Length);
    var pendingSpace = false;

    foreach (var rune in decomposed.EnumerateRunes())
    {
      var category = Rune.GetUnicodeCategory(rune);
      if (category == UnicodeCategory.NonSpacingMark)
      {
        continue;
      }

      if (Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
      {
        if (pendingSpace && folded.Length > 0)
        {
          folded.Append(' ');
        }

        pendingSpace = false;
        folded.Append(rune.ToString());
      }
      else
      {
        pendingSpace = true;
      }
    }

    return folded.ToString().Normalize(NormalizationForm.FormC);
  }

  /// <summary>The double-quoted value of the first attribute with this name, or null.</summary>
  private static string? AttributeValue(string extinf, string attribute)
  {
    var searchAt = 0;
    while (searchAt < extinf.Length)
    {
      var at = extinf.IndexOf(attribute, searchAt, StringComparison.OrdinalIgnoreCase);
      if (at < 0)
      {
        return null;
      }

      var cursor = at + attribute.Length;
      searchAt = cursor;
      if (at > 0 && (char.IsLetterOrDigit(extinf[at - 1]) || extinf[at - 1] is '-' or '_'))
      {
        continue;
      }

      while (cursor < extinf.Length && char.IsWhiteSpace(extinf[cursor])) cursor++;
      if (cursor >= extinf.Length || extinf[cursor] != '=') continue;
      cursor++;
      while (cursor < extinf.Length && char.IsWhiteSpace(extinf[cursor])) cursor++;
      if (cursor >= extinf.Length || extinf[cursor] != '"') continue;

      var closing = extinf.IndexOf('"', cursor + 1);
      return closing < 0 ? null : extinf[(cursor + 1)..closing];
    }

    return null;
  }
}
