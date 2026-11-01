using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Current public AnimeGo markup. Keep parsing pure so captured responses can be
// checked without contacting providers or modifying viewing history.
public static class AnimeGoMarkup
{
    public static HtmlDocument Document(string html) { var d = new HtmlDocument(); d.LoadHtml(html ?? ""); return d; }
    static string Text(HtmlNode n) => WebUtility.HtmlDecode(n?.InnerText ?? "").Trim();
    public static List<JObject> Search(string html)
    {
        var d = Document(html); var rows = new List<JObject>();
        foreach (var n in d.DocumentNode.SelectNodes("//div[contains(concat(' ',normalize-space(@class),' '),' ani-grid__item ')]")?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>())
        {
            var a = n.SelectSingleNode(".//div[contains(@class,'ani-grid__item-title')]/a");
            string path = a?.GetAttributeValue("href", "") ?? "";
            var m = Regex.Match(path, @"^/anime/[^/?#]+-(\d+)$");
            if (!m.Success) continue;
            var extra = n.SelectNodes(".//span[contains(@class,'ani-grid__item-genres__link')]")?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>();
            int year = extra.Select(x => int.TryParse(Text(x), out int y) && y >= 1900 && y <= 2100 ? y : 0).FirstOrDefault(x => x > 0);
            rows.Add(new JObject { ["id"] = m.Groups[1].Value, ["path"] = path, ["title"] = Text(a),
                ["original"] = Text(n.SelectSingleNode(".//div[contains(@class,'fw-lighter')]")), ["year"] = year,
                ["type"] = extra.Select(Text).FirstOrDefault(x => !int.TryParse(x, out _) && x != "/") ?? "" });
        }
        return rows.GroupBy(x => (string)x["id"]).Select(x => x.First()).ToList();
    }
    public static List<JObject> Episodes(string html)
    {
        var d = Document(html);
        return (d.DocumentNode.SelectNodes("//*[@data-episode-number and @data-episode]")?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>())
            .Select(n => new JObject { ["n"] = int.TryParse(n.GetAttributeValue("data-episode-number", ""), out int v) ? v : 0,
                ["id"] = n.GetAttributeValue("data-episode", "") })
            .Where(x => (int)x["n"] > 0 && Regex.IsMatch((string)x["id"], @"^\d{1,12}$"))
            .GroupBy(x => (int)x["n"]).Select(x => x.First()).OrderBy(x => (int)x["n"]).ToList();
    }
    public static List<JObject> Providers(string html)
    {
        var d = Document(html); var rows = new List<JObject>();
        foreach (var n in d.DocumentNode.SelectNodes("//*[@data-player and @data-translation-title]")?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>())
        {
            string kind = n.GetAttributeValue("data-provider-slug", "");
            string link = WebUtility.HtmlDecode(n.GetAttributeValue("data-player", ""));
            string voice = WebUtility.HtmlDecode(n.GetAttributeValue("data-translation-title", "")).Trim();
            if (voice.Length == 0 || !Uri.TryCreate(link.StartsWith("//") ? "https:" + link : link, UriKind.Absolute, out var u) || u.Scheme != "https") continue;
            if (kind == "aniboom" && u.Host == "aniboom.one" && Regex.IsMatch(u.AbsolutePath, @"^/embed/[A-Za-z0-9_-]+$") ||
                kind == "kodik" && u.Host == "kodikplayer.com" && Regex.IsMatch(u.AbsolutePath, @"^/(seria|video)/[0-9]+/[a-zA-Z0-9]+/\d+p$"))
                rows.Add(new JObject { ["kind"] = kind, ["link"] = u.AbsoluteUri, ["voice"] = voice });
        }
        return rows.GroupBy(x => (string)x["kind"] + "|" + (string)x["voice"]).Select(x => x.First()).ToList();
    }
    public static string Hls(string html)
    {
        try
        {
            var n = Document(html).DocumentNode.SelectSingleNode("//video[@data-parameters]");
            var p = JObject.Parse(WebUtility.HtmlDecode(n?.GetAttributeValue("data-parameters", "{}") ?? "{}"));
            var h = p["hls"] is JObject o ? o : JObject.Parse((string)p["hls"] ?? "{}");
            string value = (string)h["src"] ?? "";
            if (value.StartsWith("//")) value = "https:" + value;
            return Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "https" ? u.AbsoluteUri : null;
        }
        catch { return null; }
    }
    public static List<(int height, string url)> Variants(string manifest, string url)
    {
        var rows = new List<(int height, string url)>();
        if (manifest == null || !manifest.TrimStart().StartsWith("#EXTM3U")) return rows;
        var lines = manifest.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:")) continue;
            var m = Regex.Match(lines[i], @"RESOLUTION=\d+x(\d+)");
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int h)) continue;
            int next = i + 1; while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
            if (next < lines.Length && !lines[next].StartsWith("#") && Uri.TryCreate(new Uri(url), lines[next].Trim(), out var u) && u.Scheme == "https") rows.Add((h, u.AbsoluteUri));
        }
        return rows;
    }
    public static string Master(string manifest, string url, int height)
    {
        var selected = Variants(manifest, url).Where(x => x.height == height).Select(x => x.url).FirstOrDefault();
        if (selected == null) return null;
        var output = new List<string> { "#EXTM3U" };
        var lines = manifest.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("#EXT-X-MEDIA:") || line.StartsWith("#EXT-X-SESSION-KEY:"))
                output.Add(Regex.Replace(line, "URI=\"([^\"]+)\"", m => "URI=\"" + new Uri(new Uri(url), m.Groups[1].Value).AbsoluteUri + "\""));
            else if (line.StartsWith("#EXT-X-VERSION:") || line == "#EXT-X-INDEPENDENT-SEGMENTS") output.Add(line);
            else if (line.StartsWith("#EXT-X-STREAM-INF:") && Regex.IsMatch(line, @"RESOLUTION=\d+x" + height + @"(?:,|$)"))
            { output.Add(line); output.Add(selected); }
        }
        return string.Join("\n", output) + "\n";
    }
}

public static partial class AnimeCatalog
{
    static readonly AsyncLocal<CancellationToken> animeGoCancellation = new();
    static async Task<JObject> AnimeGoJson(string path) => JObject.Parse(await GetText(AnimeGoHost + path, "Accept: application/json", "X-Requested-With: XMLHttpRequest", "Referer: " + AnimeGoHost + "/") ?? "{}");

    static async Task<List<(int height, string url, string manifest)>> AnimeGoStreams(JObject provider)
    {
        if ((string)provider["kind"] != "aniboom") return new();
        var u = new Uri((string)provider["link"]);
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        // The embed needs episode/translation, whereas copied website-only
        // parent/episodes parameters can cause a 404 outside the browser.
        string embed = u.GetLeftPart(UriPartial.Path) + "?episode=" + Uri.EscapeDataString(q["episode"] ?? "1") + "&translation=" + Uri.EscapeDataString(q["translation"] ?? "");
        string html = await GetText(embed, "Referer: " + AnimeGoHost + "/");
        string hls = AnimeGoMarkup.Hls(html);
        if (hls == null) return new();
        string manifest = await GetText(hls, "Referer: https://aniboom.one/", "Origin: https://aniboom.one");
        return AnimeGoMarkup.Variants(manifest, hls).Select(v => (v.height, url: hls, manifest: AnimeGoMarkup.Master(manifest, hls, v.height))).ToList();
    }

    static async Task ResolveAnimeGo(JArray arrOut, string media, string[] bases, List<(int n, int year)> tmdbSeasons, string orig, JArray dbg, System.Diagnostics.Stopwatch sw, List<string> names)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var before = animeGoCancellation.Value; animeGoCancellation.Value = budget.Token;
        try { await ResolveAnimeGoCore(arrOut, media, bases, tmdbSeasons, orig, dbg, sw, names); }
        finally { animeGoCancellation.Value = before; }
    }

    static async Task ResolveAnimeGoCore(JArray arrOut, string media, string[] bases, List<(int n, int year)> tmdbSeasons, string orig, JArray dbg, System.Diagnostics.Stopwatch sw, List<string> names)
    {
        // Match series by both title and season year. Films/specials are not
        // attached to a series merely because their title shares a prefix.
        if (media != "tv") return;
        var found = new Dictionary<string, JObject>();
        var queries = names.Where(x => x.Any(ch => ch >= 0x400 && ch <= 0x4ff)).Take(1)
            .Concat(names.Where(x => x.All(ch => ch < 0x250)).Take(1)).Distinct();
        foreach (string query in queries)
        {
            string html = await GetText(AnimeGoHost + "/search/anime?q=" + Uri.EscapeDataString(query));
            foreach (var row in AnimeGoMarkup.Search(html))
                if ((string)row["type"] == "Сериал" && (Relevant(Norm((string)row["title"]), bases) || Relevant(Norm((string)row["original"]), bases))) found[(string)row["id"]] = row;
        }
        foreach (var row in found.Values.Take(4))
        {
            int year = (int)row["year"];
            var matches = tmdbSeasons.Where(x => x.year == year).ToList();
            int season = SeasonFromName((string)row["title"], (string)row["original"]) ?? (matches.Count == 1 ? matches[0].n : 0);
            if (season <= 0 || !tmdbSeasons.Any(x => x.n == season && x.year == year)) continue;
            string id = (string)row["id"];
            var player = await AnimeGoJson("/player/" + id);
            string content = (string)player["data"]?["content"] ?? "";
            var episodes = AnimeGoMarkup.Episodes(content);
            // Fetch all declared episode pages; do not silently truncate long series at 100.
            var doc = AnimeGoMarkup.Document(content);
            int total = int.TryParse(doc.DocumentNode.SelectSingleNode("//*[@data-anime-player-episodes-total-value]")?.GetAttributeValue("data-anime-player-episodes-total-value", "0"), out int count) ? count : episodes.Count;
            if (total > 2000) continue;
            for (int page = 1; episodes.Count < total && page < 20; page++)
            {
                var more = await AnimeGoJson("/player/" + id + "/episodes?page=" + page);
                var add = AnimeGoMarkup.Episodes((string)more["data"]?["content"]);
                int before = episodes.Count;
                episodes = episodes.Concat(add).GroupBy(x => (int)x["n"]).Select(x => x.First()).OrderBy(x => (int)x["n"]).ToList();
                if (episodes.Count == before) break;
            }
            if (episodes.Count == 0 || episodes.Count < total) continue;
            var providers = AnimeGoMarkup.Providers(content);
            if (providers.Count == 0) providers = AnimeGoMarkup.Providers((string)(await AnimeGoJson("/player/videos/" + (string)episodes[0]["id"]))["data"]?["content"]);
            foreach (var provider in providers.Where(x => (string)x["kind"] == "aniboom").Take(8))
            {
                var streams = await AnimeGoStreams(provider);
                int height = streams.Count == 0 ? 0 : streams.Max(x => x.height);
                if (height < MinHeight) continue;
                string voice = (string)provider["voice"];
                var eps = new JArray(episodes.Select(e => new JObject { ["n"] = e["n"], ["title"] = "Серия " + (int)e["n"], ["dur"] = 0,
                    ["url"] = "/anime/play.m3u8?src=animego&ep=" + (string)e["id"] + "&voice=" + Uri.EscapeDataString(voice),
                    ["hash"] = KpBridge.EpisodeHash(season, (int)e["n"], orig), ["season"] = season }));
                arrOut.Add(new JObject { ["source"] = "AnimeGo", ["name"] = "AnimeGo", ["release"] = "ag:" + id + ":" + voice,
                    ["season"] = season, ["year"] = year, ["title"] = row["title"], ["title_en"] = row["original"], ["voice"] = voice,
                    ["voicers"] = new JArray(), ["quality"] = height >= 2160 ? "2160p/1080p" : height >= 1080 ? "1080p" : "720p", ["episodes"] = eps });
            }
        }
        dbg.Add($"{sw.ElapsedMilliseconds} мс: AnimeGo вариантов {arrOut.Children<JObject>().Count(x => (string)x["source"] == "AnimeGo")}");
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long at, string url, string manifest)> animeGoStreams = new();
    public static async Task<(string url, string manifest)> AnimeGoPlayUrl(long episode, string voice, int maxHeight)
    {
        if (episode <= 0 || string.IsNullOrWhiteSpace(voice)) return (null, null);
        string key = "animego|" + episode + "|" + voice + "|" + maxHeight;
        if (animeGoStreams.TryGetValue(key, out var hit) && HomeDb.Now() - hit.at < 5 * 60_000) return (hit.url, hit.manifest);
        var j = await AnimeGoJson("/player/videos/" + episode);
        var providers = AnimeGoMarkup.Providers((string)j["data"]?["content"]).Where(x => (string)x["voice"] == voice).ToList();
        foreach (var provider in providers.Where(x => (string)x["kind"] == "aniboom"))
        {
            var streams = await AnimeGoStreams(provider);
            var stream = streams.Where(x => x.height >= 360 && (maxHeight <= 0 || x.height <= maxHeight)).OrderByDescending(x => x.height).FirstOrDefault();
            if (stream.url != null) {
                if (animeGoStreams.Count > 256) animeGoStreams.Clear();
                animeGoStreams[key] = (HomeDb.Now(), stream.url, stream.manifest); return (stream.url, stream.manifest);
            }
        }
        // A per-episode Kodik link from AnimeGo is already an exact series item;
        // passing episode=N again can select a different item in the player.
        foreach (var provider in providers.Where(x => (string)x["kind"] == "kodik"))
        {
            string url = await KodikPlayUrl((string)provider["link"], 0, "", maxHeight);
            if (url != null) return (url, null);
        }
        return (null, null);
    }
}
