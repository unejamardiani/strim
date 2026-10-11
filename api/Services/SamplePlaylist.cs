using System.Text;

namespace Api.Services;

/// <summary>A small made-up playlist for trying the editor without a provider.</summary>
public static class SamplePlaylist
{
  public static string Build()
  {
    var channels = new (string Name, string Group, string Logo)[]
    {
      ("CNN International", "News", "https://placehold.co/40x40/ff4444/fff?text=CNN"),
      ("BBC World News", "News", "https://placehold.co/40x40/bb0000/fff?text=BBC"),
      ("Sky News", "News", "https://placehold.co/40x40/0044cc/fff?text=SKY"),
      ("Al Jazeera English", "News", "https://placehold.co/40x40/008800/fff?text=AJ"),
      ("Fox News", "News", "https://placehold.co/40x40/004488/fff?text=FN"),
      ("Euronews", "News", "https://placehold.co/40x40/222244/fff?text=EN"),
      ("Bloomberg TV", "News", "https://placehold.co/40x40/115511/fff?text=BLM"),
      ("France 24 English", "News", "https://placehold.co/40x40/112244/fff?text=F24"),
      ("Deutsche Welle", "News", "https://placehold.co/40x40/cc0000/fff?text=DW"),
      ("RT News", "News", "https://placehold.co/40x40/004488/fff?text=RT"),
      ("ESPN 1", "Sports", "https://placehold.co/40x40/cc0000/fff?text=ESPN"),
      ("ESPN 2", "Sports", "https://placehold.co/40x40/cc4400/fff?text=ES2"),
      ("Sky Sports Main Event", "Sports", "https://placehold.co/40x40/004488/fff?text=SS1"),
      ("Sky Sports Football", "Sports", "https://placehold.co/40x40/004488/fff?text=SSF"),
      ("Eurosport 1", "Sports", "https://placehold.co/40x40/004400/fff?text=ES1"),
      ("beIN Sports HD", "Sports", "https://placehold.co/40x40/660000/fff?text=BE1"),
      ("DAZN 1", "Sports", "https://placehold.co/40x40/111111/fff?text=DAZ"),
      ("NBA TV", "Sports", "https://placehold.co/40x40/cc6600/fff?text=NBA"),
      ("NFL Network", "Sports", "https://placehold.co/40x40/004400/fff?text=NFL"),
      ("Fox Sports 1", "Sports", "https://placehold.co/40x40/002266/fff?text=FS1"),
      ("Netflix Originals", "Entertainment", "https://placehold.co/40x40/cc0000/fff?text=NF"),
      ("HBO", "Entertainment", "https://placehold.co/40x40/000000/fff?text=HBO"),
      ("HBO 2", "Entertainment", "https://placehold.co/40x40/111111/fff?text=HB2"),
      ("HBO Comedy", "Entertainment", "https://placehold.co/40x40/222222/fff?text=HBC"),
      ("AMC", "Entertainment", "https://placehold.co/40x40/004400/fff?text=AMC"),
      ("FX", "Entertainment", "https://placehold.co/40x40/222244/fff?text=FX"),
      ("Comedy Central", "Entertainment", "https://placehold.co/40x40/ff8800/fff?text=CC"),
      ("MTV", "Entertainment", "https://placehold.co/40x40/00cc00/fff?text=MTV"),
      ("TLC", "Entertainment", "https://placehold.co/40x40/cc4488/fff?text=TLC"),
      ("Discovery Channel", "Entertainment", "https://placehold.co/40x40/886600/fff?text=DSC"),
      ("National Geographic", "Documentary", "https://placehold.co/40x40/ffcc00/000?text=NG"),
      ("Discovery Science", "Documentary", "https://placehold.co/40x40/004488/fff?text=DSC"),
      ("History Channel", "Documentary", "https://placehold.co/40x40/886622/fff?text=HST"),
      ("Animal Planet", "Documentary", "https://placehold.co/40x40/448844/fff?text=AP"),
      ("BBC Earth", "Documentary", "https://placehold.co/40x40/004488/fff?text=BBC"),
      ("Smithsonian Channel", "Documentary", "https://placehold.co/40x40/224466/fff?text=SMI"),
      ("Curiosity Stream", "Documentary", "https://placehold.co/40x40/004466/fff?text=CUR"),
      ("Nat Geo Wild", "Documentary", "https://placehold.co/40x40/88aa00/fff?text=NGW"),
      ("Travel Channel", "Documentary", "https://placehold.co/40x40/448866/fff?text=TRV"),
      ("Cartoon Network", "Kids", "https://placehold.co/40x40/00aaff/fff?text=CN"),
      ("Nickelodeon", "Kids", "https://placehold.co/40x40/ff8800/fff?text=NCK"),
      ("Disney Channel", "Kids", "https://placehold.co/40x40/0066cc/fff?text=DIS"),
      ("Disney Junior", "Kids", "https://placehold.co/40x40/66aaff/fff?text=DJR"),
      ("Boomerang", "Kids", "https://placehold.co/40x40/22cc44/fff?text=BOM"),
      ("CBeebies", "Kids", "https://placehold.co/40x40/44aa88/fff?text=CBB"),
      ("PBS Kids", "Kids", "https://placehold.co/40x40/4488aa/fff?text=PBS"),
      ("Nick Jr.", "Kids", "https://placehold.co/40x40/ffaa00/fff?text=NJR"),
      ("MTV Hits", "Music", "https://placehold.co/40x40/ff00ff/fff?text=MTH"),
      ("VH1", "Music", "https://placehold.co/40x40/ccaa00/fff?text=VH1"),
      ("MTV Base", "Music", "https://placehold.co/40x40/00cc88/fff?text=MTB"),
      ("MTV Rocks", "Music", "https://placehold.co/40x40/cc4400/fff?text=MTR"),
      ("CMT", "Music", "https://placehold.co/40x40/448800/fff?text=CMT"),
      ("BBC Radio 1 (Video)", "Music", "https://placehold.co/40x40/bb0000/fff?text=BR1"),
      ("Trace Urban", "Music", "https://placehold.co/40x40/004488/fff?text=TRC"),
      ("Star Plus", "Regional", "https://placehold.co/40x40/002288/fff?text=SPL"),
      ("Zee TV", "Regional", "https://placehold.co/40x40/0088cc/fff?text=ZEE"),
      ("Colors TV", "Regional", "https://placehold.co/40x40/ff0088/fff?text=COL"),
      ("Sony TV", "Regional", "https://placehold.co/40x40/0044aa/fff?text=SNY"),
      ("TV Globo", "Regional", "https://placehold.co/40x40/008800/fff?text=GLB"),
      ("Telefe", "Regional", "https://placehold.co/40x40/4488cc/fff?text=TLF"),
      ("CCTV 4", "Regional", "https://placehold.co/40x40/cc0000/fff?text=CCTV"),
      ("NHK World", "Regional", "https://placehold.co/40x40/880000/fff?text=NHK"),
      ("HBO Premium", "Premium", "https://placehold.co/40x40/111111/fff?text=HBP"),
      ("Showtime HD", "Premium", "https://placehold.co/40x40/004400/fff?text=SHW"),
      ("Starz", "Premium", "https://placehold.co/40x40/004488/fff?text=STZ"),
      ("Cinemax", "Premium", "https://placehold.co/40x40/000044/fff?text=CMX"),
      ("Sky Cinema", "Premium", "https://placehold.co/40x40/002244/fff?text=SKC"),
      ("FOX Movies", "Premium", "https://placehold.co/40x40/ccaa00/fff?text=FXM"),
      ("Paramount+", "Premium", "https://placehold.co/40x40/0044cc/fff?text=PAR"),
      ("Peacock", "Premium", "https://placehold.co/40x40/0088cc/fff?text=PEA"),
      ("UHD Discovery", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KD"),
      ("UHD Sports", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KS"),
      ("UHD Movies", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KM"),
      ("UHD Nature", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KN"),
      ("UHD Events", "4K Ultra HD", "https://placehold.co/40x40/000000/fff?text=4KE"),
      ("BBC Radio 1", "Radio", "https://placehold.co/40x40/bb0000/fff?text=R1"),
      ("BBC Radio 2", "Radio", "https://placehold.co/40x40/cc4400/fff?text=R2"),
      ("BBC Radio 4", "Radio", "https://placehold.co/40x40/aa6600/fff?text=R4"),
      ("NPR", "Radio", "https://placehold.co/40x40/004488/fff?text=NPR"),
      ("Classic FM", "Radio", "https://placehold.co/40x40/224466/fff?text=CLS"),
      ("Radio 538", "Radio", "https://placehold.co/40x40/ff6600/fff?text=538"),
    };

    var sb = new StringBuilder();
    sb.AppendLine("#EXTM3U");
    sb.AppendLine("# Created with Strim (https://strim.plis.dev)");

    var id = 1001;
    foreach (var (name, group, logo) in channels)
    {
      var tvgId = $"channel.{id}";
      var url = $"http://example.com/streams/{id}.m3u8";
      sb.AppendLine($"#EXTINF:-1 tvg-id=\"{tvgId}\" tvg-name=\"{name}\" tvg-logo=\"{logo}\" group-title=\"{group}\",{name}");
      sb.AppendLine(url);
      id++;
    }

    return sb.ToString();
  }
}
