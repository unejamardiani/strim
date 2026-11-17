using System.Text;

namespace Api.Tests;

/// <summary>
/// Writes a deterministic M3U playlist from a seed and an entry count: the same seed and count give the same
/// bytes. It holds duplicates by tvg-id and by address, quality variants (SD/HD/FHD/4K/HEVC/H.265, bracketed
/// forms, backup/raw), entries without tvg-id, option lines (#EXTVLCOPT, #KODIPROP), #EXTGRP groups and a header
/// with url-tvg. Every address is a fake <c>example.invalid</c> one.
/// </summary>
internal static class GeneratedPlaylist
{
  private static readonly string[] Groups = ["News", "Sport", "Movies", "Kids", "Music", "Documentary"];
  private static readonly string[] Countries = ["DE", "FR", "UK", "IT", "ES"];
  private static readonly string[] Variants =
    ["", " SD", " HD", " FHD", " 4K", " HEVC", " H.265", " [HD]", " (FHD)", " [4K]", " Backup", " RAW"];

  public static string Text(int seed, int entryCount)
  {
    using var writer = new StringWriter();
    Write(writer, seed, entryCount);
    return writer.ToString();
  }

  public static void WriteFile(string path, int seed, int entryCount)
  {
    using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Write(writer, seed, entryCount);
  }

  public static void Write(TextWriter writer, int seed, int entryCount)
  {
    writer.NewLine = "\n";
    var random = new SplitMix((ulong)(uint)seed);
    var channels = Math.Max(1, entryCount / 4);
    writer.WriteLine("#EXTM3U url-tvg=\"http://example.invalid/epg.xml.gz\"");
    for (var i = 0; i < entryCount; i++)
    {
      var channel = random.Next(channels);
      var country = Countries[channel % Countries.Length];
      var group = $"{country} | {Groups[channel % Groups.Length]}";
      var name = $"{country}: Channel {channel}{Variants[random.Next(Variants.Length)]}";
      var tvgId = random.Next(100) < 15 ? "" : $" tvg-id=\"channel{channel}.{country.ToLowerInvariant()}\"";
      var useExtGrp = random.Next(100) < 20;
      var groupAttribute = useExtGrp ? "" : $" group-title=\"{group}\"";
      // About one entry in ten reuses an earlier entry's address.
      var addressId = i > 0 && random.Next(100) < 10 ? random.Next(i) : i;

      writer.WriteLine($"#EXTINF:-1{tvgId} tvg-name=\"{name}\"{groupAttribute},{name}");
      if (random.Next(100) < 10) writer.WriteLine("#EXTVLCOPT:http-user-agent=StrimTest/1.0");
      if (useExtGrp) writer.WriteLine($"#EXTGRP:{group}");
      if (random.Next(100) < 5) writer.WriteLine("#KODIPROP:inputstream.adaptive.manifest_type=mpd");
      writer.WriteLine($"http://example.invalid/live/{addressId}.ts");
    }
  }

  /// <summary>SplitMix64: a fixed algorithm, so the output does not depend on the runtime's Random.</summary>
  private sealed class SplitMix(ulong state)
  {
    public int Next(int maxExclusive)
    {
      var z = state += 0x9E3779B97F4A7C15UL;
      z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
      z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
      z ^= z >> 31;
      return (int)(z % (ulong)maxExclusive);
    }
  }
}
