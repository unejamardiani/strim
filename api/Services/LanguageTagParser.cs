namespace Api.Services;

/// <summary>Where a language, marker or tag was read: the entry name, the group title or both.</summary>
[Flags]
public enum LanguageTagSource
{
  None = 0,
  Name = 1,
  Group = 2,
}

/// <summary>
/// The tags read from one entry; each set maps a value to where it was read. Audio and subtitle languages are
/// lower-case ISO 639-1 codes; markers and prefix tags stay upper-case as written. <see cref="PrefixTags"/> holds
/// leading codes that are not a known language or marker (UK, US, EX-YU). An empty result means "unknown",
/// never a default language.
/// </summary>
public sealed record LanguageTags(
  IReadOnlyDictionary<string, LanguageTagSource> AudioLanguages,
  IReadOnlyDictionary<string, LanguageTagSource> SubtitleLanguages,
  IReadOnlyDictionary<string, LanguageTagSource> Markers,
  IReadOnlyDictionary<string, LanguageTagSource> PrefixTags)
{
  public bool IsEmpty =>
    AudioLanguages.Count == 0 && SubtitleLanguages.Count == 0 && Markers.Count == 0 && PrefixTags.Count == 0;
}

/// <summary>
/// Reads clear language tags from an entry name and a group title. Pure: no network, database or pipeline.
/// Only upper-case codes count, so ordinary words ("It", "Us", "Den of Thieves") never match.
/// <list type="bullet">
/// <item>A leading code followed by <c>:</c>, <c>|</c> or <c> - </c> (<c>DE: Film</c>, <c>FR - Film</c>).</item>
/// <item>A code alone in square brackets or parentheses (<c>Film [EN]</c>, <c>Film (DE)</c>).</item>
/// <item>The markers <c>MULTI</c> and <c>VOSTFR</c> (subtitles fr), and <c>&lt;code&gt; SUB</c> (subtitles).</item>
/// <item>A leading code (2 to 5 upper-case letters, at most one inner hyphen) that is not a known language or
/// marker becomes a prefix tag as written (<c>UK: Film</c> gives the tag UK, not English). Only the leading
/// position counts for tags, so <c>Film (HD)</c> gives nothing.</item>
/// </list>
/// Codes outside <see cref="LanguageCodes"/> are never read as a language.
/// </summary>
public static class LanguageTagParser
{
  public const string Multi = "MULTI";
  private const string Vostfr = "VOSTFR";

  /// <summary>
  /// The fixed list of recognised language codes and the ISO 639-1 language each one means. Codes that are
  /// also common country prefixes in playlists (UK, AR, TR, AL) are left out on purpose and become tags.
  /// </summary>
  public static readonly IReadOnlyDictionary<string, string> LanguageCodes = new Dictionary<string, string>(StringComparer.Ordinal)
  {
    ["DE"] = "de", ["GER"] = "de", ["DEU"] = "de",
    ["EN"] = "en", ["ENG"] = "en",
    ["FR"] = "fr", ["FRA"] = "fr", ["FRE"] = "fr",
    ["ES"] = "es", ["SPA"] = "es", ["ESP"] = "es",
    ["IT"] = "it", ["ITA"] = "it",
    ["PT"] = "pt", ["POR"] = "pt",
    ["NL"] = "nl", ["NLD"] = "nl", ["DUT"] = "nl",
    ["PL"] = "pl", ["POL"] = "pl",
    ["RU"] = "ru", ["RUS"] = "ru",
    ["SV"] = "sv", ["SWE"] = "sv",
    ["DA"] = "da", ["DAN"] = "da",
    ["FI"] = "fi", ["FIN"] = "fi",
    ["HU"] = "hu", ["HUN"] = "hu",
    ["CS"] = "cs", ["CZE"] = "cs", ["CES"] = "cs",
    ["RO"] = "ro", ["RON"] = "ro", ["RUM"] = "ro",
    ["EL"] = "el", ["GRE"] = "el", ["ELL"] = "el",
    ["JA"] = "ja", ["JPN"] = "ja",
    ["KO"] = "ko", ["KOR"] = "ko",
    ["HI"] = "hi", ["HIN"] = "hi",
  };

  public static LanguageTags Parse(string? entryName, string? groupTitle)
  {
    var result = new LanguageTags(NewSet(), NewSet(), NewSet(), NewSet());
    Read(entryName, LanguageTagSource.Name, result);
    Read(groupTitle, LanguageTagSource.Group, result);
    return result;
  }

  private static Dictionary<string, LanguageTagSource> NewSet() => new(StringComparer.Ordinal);

  private static void Read(string? text, LanguageTagSource source, LanguageTags result)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return;
    }

    text = text.Trim();
    var tokens = Tokenize(text);

    for (var i = 0; i < tokens.Count; i++)
    {
      var (start, length) = tokens[i];
      var token = text.Substring(start, length);
      if (!IsCode(token))
      {
        continue;
      }

      if (i + 1 < tokens.Count && LanguageCodes.TryGetValue(token, out var subtitle)
          && text.Substring(tokens[i + 1].Start, tokens[i + 1].Length) is "SUB" or "SUBS")
      {
        Add(result.SubtitleLanguages, subtitle, source);
        i++;
        continue;
      }

      if (token is Multi or Vostfr)
      {
        Add(token == Multi ? result.Markers : result.SubtitleLanguages, token == Multi ? Multi : "fr", source);
        continue;
      }

      var bracketed = IsBracketed(text, start, length);
      var leading = i == 0 && (bracketed || HasSeparatorAfter(text, start + length));
      if (LanguageCodes.TryGetValue(token, out var language) && (bracketed || leading))
      {
        Add(result.AudioLanguages, language, source);
      }
      else if (leading)
      {
        Add(result.PrefixTags, token, source);
      }
    }
  }

  private static void Add(IReadOnlyDictionary<string, LanguageTagSource> set, string value, LanguageTagSource source)
  {
    var target = (Dictionary<string, LanguageTagSource>)set;
    target[value] = target.GetValueOrDefault(value) | source;
  }

  // Runs of letters, digits and hyphens, so "DE2" or "Spider-Man" stay one token.
  private static List<(int Start, int Length)> Tokenize(string text)
  {
    var tokens = new List<(int, int)>();
    for (var i = 0; i < text.Length; i++)
    {
      var start = i;
      while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '-'))
      {
        i++;
      }

      if (i > start)
      {
        tokens.Add((start, i - start));
      }
    }

    return tokens;
  }

  // 2 to 5 upper-case ASCII letters, with at most one hyphen between letters (EX-YU), or the marker VOSTFR.
  private static bool IsCode(string token)
  {
    var letters = 0;
    var hyphens = 0;
    for (var i = 0; i < token.Length; i++)
    {
      var c = token[i];
      if (c is >= 'A' and <= 'Z')
      {
        letters++;
      }
      else if (c == '-' && i > 0 && i < token.Length - 1 && hyphens++ == 0)
      {
        continue;
      }
      else
      {
        return false;
      }
    }

    return token == Vostfr || letters is >= 2 and <= 5;
  }

  private static bool IsBracketed(string text, int start, int length)
  {
    var before = start - 1;
    while (before >= 0 && text[before] == ' ')
    {
      before--;
    }

    var after = start + length;
    while (after < text.Length && text[after] == ' ')
    {
      after++;
    }

    return before >= 0 && after < text.Length
      && ((text[before] == '[' && text[after] == ']') || (text[before] == '(' && text[after] == ')'));
  }

  // ':' or '|' after the leading code (spaces allowed before it), or a hyphen with a space on both sides, so
  // "EX-YU" and "Spider-Man" are never split.
  private static bool HasSeparatorAfter(string text, int position)
  {
    var spaces = 0;
    for (; position < text.Length && text[position] == ' '; position++)
    {
      spaces++;
    }

    return position < text.Length && text[position] switch
    {
      ':' or '|' => true,
      '-' => spaces > 0 && (position + 1 >= text.Length || text[position + 1] == ' '),
      _ => false,
    };
  }
}
