namespace Api.Services;

/// <summary>
/// Decides whether a playlist group title looks like adult content. Matching is case-insensitive
/// and works on whole tokens only, so substrings ("Essex", "XXXL", "Adultswim") never match.
/// Hand-written token scanning: no regex and no allocation per call.
/// </summary>
public static class AdultGroupRule
{
  // Single-token markers, stored lower-case. The '+' in "18+" and "+18" belongs to the token.
  private static readonly string[] Markers =
  {
    "xxx", "18+", "+18", "porn",
    "adult", "adults",                 // English ("For Adults" is covered by "adults")
    "erwachsene", "fsk18",             // German ("Für Erwachsene" is covered by "erwachsene")
    "adulte", "adultes",               // French
    "adultos",                         // Spanish, Portuguese
    "adulti",                          // Italian
    "yetişkin", "yetiskin",            // Turkish, with and without Turkish letters
  };

  // Two-token markers that only count as a phrase.
  private static readonly (string First, string Second)[] PhraseMarkers =
  {
    ("для", "взрослых"),               // Russian "for adults"
  };

  // Known harmless phrases. Their words may be separated by whitespace or a hyphen.
  private static readonly (string First, string Second)[] HarmlessPhrases =
  {
    ("adult", "swim"),
    ("adult", "education"),
    ("young", "adult"),
  };

  public static bool IsAdultGroupTitle(string? title)
  {
    if (string.IsNullOrWhiteSpace(title))
    {
      return false;
    }

    var text = title.AsSpan();
    var previous = Token.None;
    var position = 0;
    var current = NextToken(text, ref position);

    while (!current.IsNone)
    {
      var next = NextToken(text, ref position);

      if (IsMarker(text, previous, current) && !IsInHarmlessPhrase(text, previous, current, next))
      {
        return true;
      }

      previous = current;
      current = next;
    }

    return false;
  }

  private static bool IsMarker(ReadOnlySpan<char> text, Token previous, Token current)
  {
    var word = current.Slice(text);
    foreach (var marker in Markers)
    {
      if (EqualsFolded(word, marker))
      {
        return true;
      }
    }

    if (previous.IsNone)
    {
      return false;
    }

    var before = previous.Slice(text);
    foreach (var (first, second) in PhraseMarkers)
    {
      if (EqualsFolded(word, second) && EqualsFolded(before, first))
      {
        return true;
      }
    }

    return false;
  }

  private static bool IsInHarmlessPhrase(ReadOnlySpan<char> text, Token previous, Token current, Token next)
  {
    var word = current.Slice(text);
    foreach (var (first, second) in HarmlessPhrases)
    {
      if (!next.IsNone && EqualsFolded(word, first) && EqualsFolded(next.Slice(text), second)
          && IsPhraseGap(text, current, next))
      {
        return true;
      }

      if (!previous.IsNone && EqualsFolded(word, second) && EqualsFolded(previous.Slice(text), first)
          && IsPhraseGap(text, previous, current))
      {
        return true;
      }
    }

    return false;
  }

  // The gap between the two words of a harmless phrase holds only whitespace and at most one hyphen.
  private static bool IsPhraseGap(ReadOnlySpan<char> text, Token left, Token right)
  {
    var hyphens = 0;
    foreach (var c in text[(left.Start + left.Length)..right.Start])
    {
      if (c == '-')
      {
        hyphens++;
      }
      else if (!char.IsWhiteSpace(c))
      {
        return false;
      }
    }

    return hyphens <= 1;
  }

  private static Token NextToken(ReadOnlySpan<char> text, ref int position)
  {
    while (position < text.Length && IsSeparator(text[position]))
    {
      position++;
    }

    if (position >= text.Length)
    {
      return Token.None;
    }

    var start = position;
    while (position < text.Length && !IsSeparator(text[position]))
    {
      position++;
    }

    return new Token(start, position - start);
  }

  private static bool IsSeparator(char c) =>
    char.IsWhiteSpace(c) || c is '|' or '-' or ':' or '[' or ']' or '(' or ')' or '/' or '.' or ',';

  private static bool EqualsFolded(ReadOnlySpan<char> word, string marker)
  {
    if (word.Length != marker.Length)
    {
      return false;
    }

    for (var i = 0; i < word.Length; i++)
    {
      if (Fold(word[i]) != marker[i])
      {
        return false;
      }
    }

    return true;
  }

  // Lower-case invariantly; the Turkish dotted capital I and dotless i both fold to 'i'.
  private static char Fold(char c) => c is 'İ' or 'ı' ? 'i' : char.ToLowerInvariant(c);

  private readonly record struct Token(int Start, int Length)
  {
    public static Token None => new(-1, 0);

    public bool IsNone => Start < 0;

    public ReadOnlySpan<char> Slice(ReadOnlySpan<char> text) => text.Slice(Start, Length);
  }
}
