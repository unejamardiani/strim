using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>The content type a mark belongs to.</summary>
public enum MarkKind
{
  Film,
  Series,
}

/// <summary>
/// The key a film or series mark is stored under. <see cref="Title"/> is the normalised title (lower case, no
/// diacritics, punctuation and repeated white space folded to single spaces); <see cref="DisplayTitle"/> is the
/// cleaned name as written. <see cref="Key"/> joins kind, year and title, so variants of one film share a key and a
/// film never collides with a series.
/// </summary>
public sealed record MarkKey(string Key, string Title, int? Year, string DisplayTitle, MarkKind Kind)
{
  /// <summary>The maximum length of <see cref="Title"/> and <see cref="DisplayTitle"/>; longer titles are cut.</summary>
  public const int MaxTitleLength = 120;

  /// <summary>The maximum length of <see cref="Key"/>: kind, year and the cut title.</summary>
  public const int MaxKeyLength = 6 + 1 + 4 + 1 + MaxTitleLength;

  /// <summary>
  /// Derives the mark key from an entry name. Pure. Quality words go through <see cref="VariantFinder.Detect"/>
  /// and a leading language or country prefix is removed when <see cref="LanguageTagParser"/> reads it as one.
  /// Square-bracketed noise is removed; a year in brackets or at the end becomes <see cref="Year"/>. For a series,
  /// everything from the first season or episode marker on is cut. Returns null when no title is left.
  /// </summary>
  public static MarkKey? From(string? entryName, MarkKind kind)
  {
    if (string.IsNullOrWhiteSpace(entryName)) return null;
    try
    {
      return Derive(entryName.Trim().Normalize(NormalizationForm.FormC), kind);
    }
    catch (RegexMatchTimeoutException)
    {
      // Entry names are untrusted; a name the patterns cannot read in time gets no key rather than an error.
      return null;
    }
  }

  private static MarkKey? Derive(string entryName, MarkKind kind)
  {
    // White space is folded first, so the patterns below see single spaces only. A prefix is read before quality
    // words go, while its separator is still there ("DE - 4K"); a code standing alone is the title.
    var name = Collapse(VariantFinder.Detect(StripPrefix(Collapse(entryName))).BaseName);
    if (kind == MarkKind.Series)
    {
      var marker = SeriesMarker.Match(name);
      if (marker.Success) name = name[..marker.Index];
    }

    int? year = null;
    var bracketedYear = BracketedYear.Matches(name);
    if (bracketedYear.Count > 0)
    {
      var match = bracketedYear[^1];
      year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
      name = name.Remove(match.Index, match.Length);
    }

    // Square-bracketed noise goes before the trailing year is looked for, so "Title 2020 [Multi-Sub]" keeps its year.
    name = Collapse(StripSquareBrackets(name));
    if (year is null)
    {
      var trailing = TrailingYear.Match(name);
      if (trailing.Success && trailing.Index > 0)
      {
        year = int.Parse(trailing.Groups[1].Value, CultureInfo.InvariantCulture);
        name = name[..trailing.Index];
      }
    }

    name = StripPrefix(name);
    var display = Cut(Collapse(name).Trim(Separators));
    var title = Cut(Normalise(display));
    if (title.Length == 0 || title.Split(' ').All(QualityWords.Contains)) return null;

    var kindText = kind == MarkKind.Film ? "film" : "series";
    var key = $"{kindText}:{year?.ToString(CultureInfo.InvariantCulture)}:{title}";
    return new MarkKey(key, title, year, display, kind);
  }

  // A name of quality words alone, which VariantFinder.Detect keeps unchanged, is noise (normalised forms).
  private static readonly HashSet<string> QualityWords = new(StringComparer.Ordinal)
  {
    "sd", "hd", "fhd", "4k", "uhd", "hevc", "h265",
  };

  private static readonly char[] Separators = [' ', '-', '|', ':', '.', ',', '_'];

  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

  // S01E02, S1 E2, S01, 1x02, Season 2, Staffel 2 as whole words.
  private static readonly Regex SeriesMarker = new(
    @"(?<![\p{L}\p{N}])(?:S\d{1,3}\s?E\d{1,4}|S\d{1,3}|\d{1,3}x\d{1,4}|(?:Season|Staffel|Saison|Temporada)\s?\d{1,3})(?![\p{L}\p{N}])",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

  private static readonly Regex BracketedYear = new(
    @"[(\[]\s*((?:19|20)\d{2})\s*[)\]]", RegexOptions.CultureInvariant, Timeout);

  private static readonly Regex TrailingYear = new(
    @"(?<![\p{L}\p{N}])((?:19|20)\d{2})[\s\-|.]*$", RegexOptions.CultureInvariant, Timeout);

  // A leading upper-case code after an optional pipe, followed by ':', '|' or ' - '. A code that stands alone is
  // the title itself ("MIST"), never a prefix.
  private static readonly Regex Prefix = new(
    @"^\s?\|?\s?([A-Z]{2,5}(?:-[A-Z]{2,5})?)\s?(?:[:|]|-(?=\s|$))", RegexOptions.CultureInvariant, Timeout);

  private static string StripPrefix(string name)
  {
    var match = Prefix.Match(name);
    if (!match.Success) return name;
    var code = match.Groups[1].Value;
    var rest = name[match.Length..];
    // The parser decides whether the code is a language or prefix tag; quality labels and words stay.
    var tags = LanguageTagParser.Parse(code + " | " + rest, null);
    var isTag = tags.PrefixTags.ContainsKey(code)
      || (LanguageTagParser.LanguageCodes.TryGetValue(code, out var language) && tags.AudioLanguages.ContainsKey(language));
    return isTag ? rest : name;
  }

  // Replaces each [..] with a space in one linear pass; an unclosed '[' and what follows it stay.
  private static string StripSquareBrackets(string text)
  {
    var result = new StringBuilder(text.Length);
    var open = -1;
    for (var i = 0; i < text.Length; i++)
    {
      var c = text[i];
      if (open < 0)
      {
        if (c == '[') open = i;
        else result.Append(c);
      }
      else if (c == ']')
      {
        result.Append(' ');
        open = -1;
      }
    }
    if (open >= 0) result.Append(text, open, text.Length - open);
    return result.ToString();
  }

  private static string Collapse(string text)
  {
    var result = new StringBuilder(text.Length);
    var inSpace = false;
    foreach (var c in text)
    {
      if (char.IsWhiteSpace(c))
      {
        inSpace = true;
        continue;
      }
      if (inSpace && result.Length > 0) result.Append(' ');
      inSpace = false;
      result.Append(c);
    }
    return result.ToString();
  }

  // Lower case without diacritics; anything but letters and digits becomes a single space.
  private static string Normalise(string text)
  {
    var result = new StringBuilder(text.Length);
    foreach (var c in text.Normalize(NormalizationForm.FormD))
    {
      var category = CharUnicodeInfo.GetUnicodeCategory(c);
      if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
      result.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
    }
    return Collapse(result.ToString().Normalize(NormalizationForm.FormC)).Trim();
  }

  // Cuts to the maximum length without splitting a surrogate pair, then trims trailing spaces.
  private static string Cut(string text)
  {
    if (text.Length <= MaxTitleLength) return text;
    var length = char.IsHighSurrogate(text[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
    return text[..length].TrimEnd();
  }
}
