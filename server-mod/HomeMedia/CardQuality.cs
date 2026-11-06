using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Метка качества на постере: тип лучшего релиза и его разрешение — «WEBDL FHD», «BDRIP 4K», «HDRIP HD», «DVD», «TS» (экранная копия
// без разрешения — просто TS). Берётся из названий релизов в JacRed (фильмы — за тот же год, сериалы — по названию; разрешение ещё и из
// info.quality раздачи) и из уже найденных аниме-источников (AniLibria, AnimeLib, Kodik): если их качество выше, метка — «FHD» и т.п.
// Lampa рисует метку фильма сама из card.release_quality; у сериалов метку рисует cardplus.js из cp_quality. Результат кэшируется.
public static class CardQuality
{
    static readonly string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "quality_cache.json");
    static readonly ConcurrentDictionary<string, QEntry> cache = LoadCache();
    static readonly SemaphoreSlim gate = new(4, 4);
    static readonly object saveLock = new();

    class QEntry { public string q = ""; public long at; }

    public static readonly string[] Labels = { "SD", "HD", "FHD", "4K" };

    static readonly Regex r4k = new(@"2160[pi]?|\b4K\b|\bUHD\b|\b4К\b", RegexOptions.IgnoreCase);
    static readonly Regex r1080 = new(@"1080[pi]?|\bFull ?HD\b|\bFHD\b|BDRemux|\bRemux\b", RegexOptions.IgnoreCase);
    static readonly Regex r720 = new(@"\b720[pi]?\b", RegexOptions.IgnoreCase);
    static readonly Regex rSd = new(@"\b(480|576|360)[pi]?\b|DVD-?Rip|\bDVD\b|SATRip|TVRip|VHS|\bTS\b|\bТС\b|Telesync|Телесинк|\bTC\b|Telecine|CAM-?Rip|\bCAM\b|HDCAM", RegexOptions.IgnoreCase);
    static readonly Regex rHdSrc = new(@"BDRip|Blu-?Ray|WEB-?DL|WEB-?Rip|HDRip|HDTV", RegexOptions.IgnoreCase);

    // 3 — 4K, 2 — FHD, 1 — HD, 0 — SD, -1 — по названию не определить
    public static int Tier(string t)
    {
        if (string.IsNullOrEmpty(t)) return -1;
        if (r4k.IsMatch(t)) return 3;
        if (r1080.IsMatch(t)) return 2;
        if (r720.IsMatch(t)) return 1;
        if (rSd.IsMatch(t)) return 0;
        if (rHdSrc.IsMatch(t)) return 1;
        return -1;
    }

    // тип релиза: от лучшего к худшему
    static readonly (string label, int rank, Regex re)[] types =
    {
        ("BDRIP", 6, new Regex(@"BDRip|Blu-?Ray|BDRemux|\bRemux\b", RegexOptions.IgnoreCase)),
        ("WEBDL", 5, new Regex(@"WEB-?DL", RegexOptions.IgnoreCase)),
        ("WEBRIP", 4, new Regex(@"WEB-?Rip", RegexOptions.IgnoreCase)),
        ("HDRIP", 3, new Regex(@"HDRip|HDTV", RegexOptions.IgnoreCase)),
        ("DVD", 2, new Regex(@"DVD-?Rip|\bDVD\b|SATRip|TVRip|VHS", RegexOptions.IgnoreCase)),
        ("TS", 0, new Regex(@"\bTS(-?Rip)?\b|\bТС(-?рип)?\b|\bHDTS\b|\bHDTC\b|TS-?Rip|ТС-?Rip|Telesync|Телесинк|\bTC\b|Telecine|CAM-?Rip|\bCAM\b|HDCAM|Screener|DVD-?Scr", RegexOptions.IgnoreCase)),
    };

    // тип лучшего/первого совпавшего релиза: BDRIP/WEBDL/WEBRIP/HDRIP/DVD/TS; "" — не распознан
    public static string TypeLabel(string title)
    {
        foreach (var (l, r, re) in types) if (re.IsMatch(title ?? "")) return l;
        return "";
    }

    // разрешение, названное явно: 3 — 4K, 2 — FHD, 1 — HD, 0 — SD, -1 — не названо
    static int ResTier(string t, int infoQ)
    {
        if (!string.IsNullOrEmpty(t))
        {
            if (r4k.IsMatch(t)) return 3;
            if (r1080.IsMatch(t)) return 2;
            if (r720.IsMatch(t)) return 1;
            if (Regex.IsMatch(t, @"\b(480|576|360)[pi]?\b", RegexOptions.IgnoreCase)) return 0;
        }
        if (infoQ >= 2160) return 3;
        if (infoQ >= 1080) return 2;
        if (infoQ >= 720) return 1;
        if (infoQ > 0) return 0;
        return -1;
    }

    // (метка, оценка) для сравнения релизов: экранные копии хуже любой нормальной раздачи, дальше разрешение, дальше тип
    static (string label, int score) Describe(string title, int infoQ)
    {
        string type = ""; int rank = 1;
        foreach (var (l, r, re) in types) if (re.IsMatch(title ?? "")) { type = l; rank = r; break; }
        int tier = ResTier(title, infoQ);
        if (type == "" && tier < 0) return ("", -1);
        bool cam = type == "TS";
        int dflt = (type == "DVD" || cam) ? 0 : 1;
        int score = (cam ? 0 : 1000) + (tier >= 0 ? tier : dflt) * 100 + rank;
        string label = type;
        if (tier >= 0 && type != "DVD" && !(cam && tier <= 0)) label = (type + " " + Labels[tier]).Trim();
        return (label, score);
    }

    // разрешение метки: 3 — 4K, 2 — FHD, 1 — HD, 0 — SD; -1 — метки нет
    public static int TierOfLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return -1;
        foreach (var tok in label.Split(' ')) { int i = Array.IndexOf(Labels, tok); if (i >= 0) return i; }
        return label.StartsWith("BDRIP") || label.StartsWith("WEBDL") || label.StartsWith("WEBRIP") || label.StartsWith("HDRIP") ? 1 : 0;
    }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    static ConcurrentDictionary<string, QEntry> LoadCache()
    {
        try
        {
            if (File.Exists(path))
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, QEntry>>(File.ReadAllText(path));
                if (d != null) return new ConcurrentDictionary<string, QEntry>(d);
            }
        }
        catch { }
        return new ConcurrentDictionary<string, QEntry>();
    }

    static void Save() => Persist.Later(path, () => { lock (saveLock) return JsonConvert.SerializeObject(cache); });

    // у пустого результата срок короче: релизы появляются, качество растёт
    static bool Fresh(QEntry e) => Now - e.at < (string.IsNullOrEmpty(e.q) ? 3 * 3600 : 12 * 3600);

    static async Task<string> Fetch(string title, string year, bool tv)
    {
        string qs = "Query=" + Uri.EscapeDataString(title) + (tv ? "" : "&year=" + year);
        var results = await JacredSearch.Aggregate(qs, applyHideFilter: false, profileId: null);

        var yr = tv ? null : new Regex(@"(?<!\d)" + year + @"(?!\d)");
        string best = ""; int bestScore = -1;
        foreach (var r in results.Children<JObject>())
        {
            string t = (string)r["Title"] ?? "";
            if (yr != null && !yr.IsMatch(t)) continue;
            if (AnimeCatalog.UkrOnlyRelease(r)) continue;
            int iq = 0; try { iq = (int?)r["info"]?["quality"] ?? 0; } catch { }
            var (label, score) = Describe(t, iq);
            if (score > bestScore) { bestScore = score; best = label; }
        }
        return best;
    }

    public static void SaveNow() => Save();

    static string Key(bool tv, long id) => "q4:" + (tv ? "tv:" : "movie:") + id;

    // Лучшее качество среди найденных аниме-источников (из кэша, без запросов): "" — нет данных
    static string AnimeLabel(bool tv, long id)
    {
        try
        {
            int t = HomeMedia.AnimeCatalog.BestTier(tv ? "tv" : "movie", id);
            return t >= 0 ? Labels[t] : "";
        }
        catch { return ""; }
    }

    // метка торрентов (тип + разрешение) или, если аниме-источники лучше по разрешению, их метка
    static string Better(string torrent, string anime)
    {
        int ta = TierOfLabel(torrent), tb = TierOfLabel(anime);
        return tb > ta ? (anime ?? "") : (torrent ?? "");
    }

    // Качество одного тайтла (с кэшем): "" — релизов не найдено
    public static async Task<string> Lookup(long id, string title, string year, bool tv = false)
    {
        string key = Key(tv, id);
        if (cache.TryGetValue(key, out var hit) && Fresh(hit)) return Better(hit.q ?? "", AnimeLabel(tv, id));
        var quality = await KinoListWork<string>.Start("quality|"+key, async () => {
            await gate.WaitAsync();
            try {
                if (cache.TryGetValue(key, out var current) && Fresh(current)) return current.q ?? "";
                string q = await Fetch(title, year, tv) ?? "";
                cache[key] = new QEntry { q = q, at = Now };
                Save();
                return q;
            }
            catch { return ""; }
            finally { gate.Release(); }
        });
        return Better(quality, AnimeLabel(tv, id));
    }

    public static async Task Enrich(JArray cards)
    {
        try
        {
            var todo = new List<(JObject card, string key, string title, string year, bool tv)>();
            foreach (var c in cards.Children<JObject>().Take(60))
            {
                bool tv = c["name"] != null || c["original_name"] != null || c["number_of_seasons"] != null || c["first_air_date"] != null;
                string field = tv ? "cp_quality" : "release_quality";
                if (c[field] != null) continue;
                long id = (long?)c["id"] ?? 0;
                if (id <= 0) continue;

                string key = Key(tv, id);
                if (cache.TryGetValue(key, out var hit) && Fresh(hit))
                {
                    string q0 = Better(hit.q, AnimeLabel(tv, id));
                    if (!string.IsNullOrEmpty(q0)) c[field] = q0;
                    continue;
                }

                string title = (string)(tv ? (c["original_name"] ?? c["name"]) : (c["original_title"] ?? c["title"]));
                string rd = (string)(tv ? c["first_air_date"] : c["release_date"]);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrEmpty(rd) || rd.Length < 4) continue;
                todo.Add((c, key, title, rd.Substring(0, 4), tv));
            }
            if (todo.Count == 0) return;

            var tasks = todo.Select(t => Lookup((long)t.card["id"],t.title,t.year,t.tv)).ToList();

            // не задерживаем список: остальное дойдёт в кэш и появится при следующем открытии
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(400));
            foreach (var t in todo)
            {
                if (cache.TryGetValue(t.key, out var e) && Fresh(e))
                {
                    string q1 = Better(e.q, AnimeLabel(t.tv, (long?)t.card["id"] ?? 0));
                    if (!string.IsNullOrEmpty(q1)) t.card[t.tv ? "cp_quality" : "release_quality"] = q1;
                }
            }
        }
        catch { }
    }
}
