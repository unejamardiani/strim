using System.Globalization;
using System.Text.Json;
using Found = System.Collections.Generic.Dictionary<
  string, (System.Collections.Generic.SortedSet<string> Shapes, System.Collections.Generic.List<string> Samples)>;

namespace Api.Services;

/// <summary>One key of a provider's title details that looks like language data.</summary>
/// <param name="Path">Dotted key path; array items are <c>[]</c>, numeric keys (seasons) are <c>*</c>.</param>
/// <param name="Shapes">JSON value kinds seen at this path, for example <c>string</c> or <c>object</c>.</param>
/// <param name="Samples">Up to three short distinct sample values of scalar fields.</param>
public sealed record ProviderLanguageField(string Path, IReadOnlyList<string> Shapes, IReadOnlyList<string> Samples);

/// <summary>Sanitised summary of a title's details: never the address, host, credentials or raw body.</summary>
public sealed record ProviderLanguageSummary(string Kind, bool Found, string Message, IReadOnlyList<ProviderLanguageField> Fields);

/// <summary>
/// Read-only probe of the language fields a provider's <c>player_api.php</c> returns for one film
/// (<c>get_vod_info</c>) or one episode (<c>get_series_info</c> of its series, narrowed to that episode).
/// </summary>
public static class ProviderLanguageProbe
{
  /// <summary>Series details with every episode can be larger than an account answer.</summary>
  public const int MaxDetailsBytes = 2 * 1024 * 1024;

  private const int MaxDepth = 12;
  private const int MaxFields = 60;
  private const int MaxSamples = 3;
  private const int MaxSampleLength = 40;
  private static readonly string[] LanguageWords = ["audio", "subtitle", "lang", "dub", "caption"];
  private static readonly string[] SensitiveWords = ["token", "pass", "user", "key", "url", "auth", "secret", "sig"];

  /// <summary>
  /// The details address on the playlist's own scheme, host, port and directory, or null when the
  /// link is not a <c>get.php</c> link with a username and password.
  /// </summary>
  public static Uri? BuildEndpoint(string? playlistLink, string kind, long id)
  {
    if (ProviderAccountInfo.BuildEndpoint(playlistLink) is not { } account) return null;
    var action = kind == "series"
      ? $"action=get_series_info&series_id={id.ToString(CultureInfo.InvariantCulture)}"
      : $"action=get_vod_info&vod_id={id.ToString(CultureInfo.InvariantCulture)}";
    return new UriBuilder(account) { Query = account.Query.TrimStart('?') + "&" + action }.Uri;
  }

  /// <summary>
  /// Lists keys whose name looks like language data; for a series only those of episode
  /// <paramref name="episodeId"/>. Keys that look like credentials are skipped, key names and samples
  /// that could carry an address or one of <paramref name="secrets"/> are hidden. Null when the body is
  /// not a JSON object, is a provider error or (series) lacks the episode.
  /// </summary>
  public static ProviderLanguageSummary? Summarise(
    string kind, string? body, long? episodeId, IReadOnlyCollection<string> secrets)
  {
    if (string.IsNullOrWhiteSpace(body)) return null;
    try
    {
      using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
      var root = document.RootElement;
      var usable = root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("error", out _) && root.EnumerateObject().Any();
      if (!usable) return null;
      var found = new Found(StringComparer.Ordinal);
      if (kind != "series") Walk(root, "", 0, found, secrets);
      else if (FindEpisode(root, episodeId) is { } episode) Walk(episode, "episode", 1, found, secrets);
      else return null;
      var fields = found.OrderBy(f => f.Key, StringComparer.Ordinal)
        .Select(f => new ProviderLanguageField(f.Key, f.Value.Shapes.ToList(), f.Value.Samples)).ToList();
      var message = fields.Count > 0
        ? $"Found {fields.Count} field(s) that look like language data."
        : "No language fields were found in the provider's details for this title.";
      return new ProviderLanguageSummary(kind, fields.Count > 0, message, fields);
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
    {
      return null;
    }
  }

  private static void Walk(JsonElement element, string path, int depth, Found found, IReadOnlyCollection<string> secrets)
  {
    if (depth > MaxDepth) return;
    if (element.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in element.EnumerateArray()) Walk(item, path + "[]", depth + 1, found, secrets);
      return;
    }

    if (element.ValueKind != JsonValueKind.Object) return;
    foreach (var property in element.EnumerateObject())
    {
      if (Has(property.Name, SensitiveWords)) continue;
      var name = property.Name.Length > 0 && property.Name.All(char.IsAsciiDigit) ? "*" : SafeName(property.Name, secrets);
      var childPath = path.Length == 0 ? name : path + "." + name;
      if (Has(property.Name, LanguageWords)) Record(childPath, property.Value, found, secrets);
      Walk(property.Value, childPath, depth + 1, found, secrets);
    }
  }

  // Seasons come as an object of arrays (or an array of arrays); the episode is matched on its "id".
  private static JsonElement? FindEpisode(JsonElement root, long? episodeId)
  {
    if (episodeId is null || !root.TryGetProperty("episodes", out var seasons)) return null;
    IEnumerable<JsonElement> lists = seasons.ValueKind == JsonValueKind.Object
      ? seasons.EnumerateObject().Select(season => season.Value)
      : seasons.ValueKind == JsonValueKind.Array ? seasons.EnumerateArray() : [];
    var wanted = episodeId.Value.ToString(CultureInfo.InvariantCulture);
    return lists.Where(list => list.ValueKind == JsonValueKind.Array).SelectMany(list => list.EnumerateArray())
      .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ToString() == wanted)
      .Select(e => (JsonElement?)e).FirstOrDefault();
  }

  private static bool Has(string name, string[] words) =>
    words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

  private static bool HasSecret(string text, IReadOnlyCollection<string> secrets) =>
    secrets.Any(secret => secret.Length > 0 && text.Contains(secret, StringComparison.OrdinalIgnoreCase));

  // Key names are provider-controlled, so only short plain names that echo no secret are shown.
  private static string SafeName(string name, IReadOnlyCollection<string> secrets) =>
    name.Length is > 0 and <= 40 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !HasSecret(name, secrets)
      ? name : "?";

  private static void Record(string path, JsonElement value, Found found, IReadOnlyCollection<string> secrets)
  {
    if (!found.TryGetValue(path, out var field))
    {
      if (found.Count >= MaxFields) return;
      field = (new SortedSet<string>(StringComparer.Ordinal), new List<string>());
      found[path] = field;
    }

    field.Shapes.Add(value.ValueKind.ToString().ToLowerInvariant());
    var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
    foreach (var item in items) AddSample(field.Samples, item, secrets);
  }

  private static void AddSample(List<string> samples, JsonElement value, IReadOnlyCollection<string> secrets)
  {
    var sample = value.ValueKind switch
    {
      JsonValueKind.String => value.GetString(),
      JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
      _ => null,
    };
    if (samples.Count >= MaxSamples || string.IsNullOrWhiteSpace(sample)) return;

    sample = sample.Trim();
    if (sample.Contains("://", StringComparison.Ordinal) || HasSecret(sample, secrets)) return;
    if (sample.Length > MaxSampleLength) sample = sample[..MaxSampleLength] + "…";
    if (!samples.Contains(sample)) samples.Add(sample);
  }
}
