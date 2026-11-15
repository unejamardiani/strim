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
  public static string DisplayName(string? extinfLine) => DisplayNameSpan(extinfLine).ToString();

  /// <summary><see cref="DisplayName"/> as a slice of the line, so an index can copy it without a string per entry.</summary>
  internal static ReadOnlySpan<char> DisplayNameSpan(string? extinfLine)
  {
    if (string.IsNullOrEmpty(extinfLine))
    {
      return ReadOnlySpan<char>.Empty;
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

    var name = lastComma >= 0 ? extinfLine.AsSpan(lastComma + 1).Trim() : ReadOnlySpan<char>.Empty;
    return name.Length > 0 ? name : (AttributeValue(extinfLine, "tvg-name") ?? string.Empty).AsSpan().Trim();
  }

  /// <summary>
  /// Appends <see cref="Fold"/> of the text to <paramref name="target"/>. ASCII text, the common
  /// case, is folded in place without temporary strings.
  /// </summary>
  internal static StringBuilder AppendFolded(StringBuilder target, ReadOnlySpan<char> text)
  {
    if (!Ascii.IsValid(text)) return target.Append(Fold(text.ToString()));

    var start = target.Length;
    var pendingSpace = false;
    foreach (var c in text)
    {
      if (!char.IsAsciiLetterOrDigit(c))
      {
        pendingSpace = true;
        continue;
      }

      if (pendingSpace && target.Length > start) target.Append(' ');
      pendingSpace = false;
      target.Append(char.ToLowerInvariant(c));
    }
    return target;
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

    if (Ascii.IsValid(text)) return AppendFolded(new StringBuilder(text.Length), text).ToString();

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

  /// <summary>
  /// The double-quoted value of the first attribute with this name, or null. Names are matched
  /// only outside quoted values, so text inside another attribute's value is never taken as one.
  /// </summary>
  internal static string? AttributeValue(string extinf, string attribute)
  {
    var i = 0;
    while (i < extinf.Length)
    {
      if (extinf[i] == '"')
      {
        var closingQuote = extinf.IndexOf('"', i + 1);
        if (closingQuote < 0) return null;
        i = closingQuote + 1;
        continue;
      }

      var at = i++;
      if (at + attribute.Length > extinf.Length
          || !extinf.AsSpan(at, attribute.Length).Equals(attribute, StringComparison.OrdinalIgnoreCase)
          || (at > 0 && (char.IsLetterOrDigit(extinf[at - 1]) || extinf[at - 1] is '-' or '_')))
      {
        continue;
      }

      var cursor = at + attribute.Length;
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
