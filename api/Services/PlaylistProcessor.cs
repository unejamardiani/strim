using System.Text;
using System.Text.RegularExpressions;
using Api.Models;

namespace Api.Services;

public static class PlaylistProcessor
{
  private static readonly Regex AttrRegex = new(@"(\w[\w-]*)=""([^""]*)""", RegexOptions.Compiled);

  public static (Dictionary<string, int> Groups, int Total) CountGroups(string text)
  {
    var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var lines = NormalizeLines(text);
    var total = 0;

    for (var i = 0; i < lines.Length; i++)
    {
      var line = lines[i];
      if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      total++;
      var groupTitle = ExtractGroupTitle(line);
      groups[groupTitle] = groups.TryGetValue(groupTitle, out var existing) ? existing + 1 : 1;
      i++; // Skip URL line
    }

    return (groups, total);
  }

  public static PlaylistFilterResult GenerateFiltered(string text, HashSet<string> disabledGroups)
  {
    var lines = NormalizeLines(text);
    var sb = new StringBuilder();
    sb.AppendLine("#EXTM3U");

    var total = 0;
    var kept = 0;

    for (var i = 0; i < lines.Length; i++)
    {
      var line = lines[i];
      if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      total++;
      var extinf = line;
      var url = (i + 1) < lines.Length ? lines[i + 1].Trim() : string.Empty;
      i++; // Skip URL line

      var groupTitle = ExtractGroupTitle(extinf);
      if (disabledGroups.Contains(groupTitle))
      {
        continue;
      }

      kept++;
      var normalizedExtinf = EnsureGroupInExtinf(extinf, groupTitle);
      sb.AppendLine(normalizedExtinf);
      sb.AppendLine(url);
    }

    // Trim trailing newline but retain valid output.
    var textResult = sb.ToString().TrimEnd();
    return new PlaylistFilterResult(textResult, total, kept);
  }

  public static string DeriveNameFromUrl(string? url)
  {
    if (string.IsNullOrWhiteSpace(url))
    {
      return "playlist";
    }

    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
      var lastSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
      return string.IsNullOrWhiteSpace(lastSegment) ? uri.Host : lastSegment;
    }

    return url;
  }

  public static List<GroupResult> ToGroupResults(Dictionary<string, int> groups)
  {
    return groups.Select(kvp => new GroupResult(kvp.Key, kvp.Value)).ToList();
  }

  private static string[] NormalizeLines(string text) =>
    (text ?? string.Empty)
      .Replace("\r\n", "\n", StringComparison.Ordinal)
      .Replace('\r', '\n')
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  private static string ExtractGroupTitle(string extinf)
  {
    foreach (Match match in AttrRegex.Matches(extinf))
    {
      if (match.Groups.Count >= 3 && match.Groups[1].Value.Equals("group-title", StringComparison.OrdinalIgnoreCase))
      {
        return string.IsNullOrWhiteSpace(match.Groups[2].Value) ? "Ungrouped" : match.Groups[2].Value.Trim();
      }
    }
    return "Ungrouped";
  }

  private static string EnsureGroupInExtinf(string extinf, string groupTitle)
  {
    if (extinf.Contains("group-title=\"", StringComparison.OrdinalIgnoreCase))
    {
      return extinf;
    }
    return $"{extinf} group-title=\"{groupTitle}\"";
  }
}
