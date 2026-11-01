using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// «Аниме онлайн» по принципу kino.pub: тайтл карточки TMDB сопоставляется с релизами источников, оставляются только потоки не ниже 1080p,
// у каждого релиза показывается озвучка; просмотр идёт в общую базу (те же хэши серий, что у торрентов и kino.pub), последняя
// просмотренная серия предлагается как «продолжить». Сейчас рабочий источник — AniLibria (anilibria.top); остальные подключаются
// по мере готовности (см. Status): у AnimeLib нужен токен аккаунта, у AnimeGo сменилась вёрстка сайта.
public static partial class AnimeCatalog
{
    static readonly HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8), UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25) };
    const string AlHost = "https://anilibria.top";
    const string AnimeGoHost = "https://animego.me";
    // 720p допустим только когда для этого сезона нигде нет 1080p и выше (см. PruneLow); 4K и 1080p показываются всегда
    const int MinHeight = 720;
    static readonly TimeSpan PosTtl = TimeSpan.FromHours(6), NegTtl = TimeSpan.FromHours(3);
    static readonly SemaphoreSlim gate = new(2, 2);
    static readonly ConcurrentDictionary<string, Task<JObject>> inflight = new();

    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    // Запросы к API AniLibria идут через curl: встроенный клиент .NET на этом сервере получал от них зависание до тайм-аута,
    // а curl отвечает за 0,3 с. Аргументы передаются списком (без оболочки), тайм-аут жёсткий.
    const string UA = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";

    static async Task<JToken> GetJson(string url)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("curl")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-s", "--connect-timeout", "3", "-m", "6", "--compressed", "-A", UA, "-w", "\n%{http_code}", url }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        string outp = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        int i = outp.LastIndexOf('\n');
        if (i < 0 || outp.Substring(i + 1).Trim() != "200") return null;
        return JToken.Parse(outp.Substring(0, i));
    }

    // AnimeGo отдаёт HTML/JSON через DDoS-Guard; для него используем тот же
    // curl-путь, что и для AniLibria, чтобы не зависеть от SocketsHttpHandler.
    static async Task<string> GetText(string url, params string[] headers)
    {
        var cancellation = animeGoCancellation.Value;
        if (cancellation.IsCancellationRequested) return null;
        var psi = new System.Diagnostics.ProcessStartInfo("curl")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-s", "--connect-timeout", "4", "-m", "12", "--compressed", "-A", UA }) psi.ArgumentList.Add(a);
        foreach (var h in headers) { psi.ArgumentList.Add("-H"); psi.ArgumentList.Add(h); }
        psi.ArgumentList.Add("-w"); psi.ArgumentList.Add("\n%{http_code}"); psi.ArgumentList.Add(url);
        using var p = System.Diagnostics.Process.Start(psi);
        using var abort = cancellation.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        string outp = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        int i = outp.LastIndexOf('\n');
        if (i < 0 || outp.Substring(i + 1).Trim() != "200") return null;
        return outp.Substring(0, i);
    }

    // ---- состояние источников (для веб-панели) ----
    static JObject AnimeLibStatus()
    {
        bool has = false;
        try
        {
            string p = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "cache", "animelib.json");
            if (System.IO.File.Exists(p)) has = !string.IsNullOrEmpty((string)JObject.Parse(System.IO.File.ReadAllText(p))["token"]);
        }
        catch { }
        return new JObject
        {
            ["id"] = "AnimeLib", ["name"] = "AnimeLib", ["enabled"] = has,
            ["state"] = has ? "работает" : "нужен токен",
            ["note"] = has ? "много озвучек, потоки 1080p и выше (через прокси сервера)" : "требуется токен аккаунта anilib.me (поле ниже)",
            ["has_token"] = has
        };
    }

    public static JArray Status() => new JArray
    {
        new JObject { ["id"] = "AL", ["name"] = "AniLibria", ["enabled"] = true, ["state"] = "работает", ["note"] = "прямой API, потоки 1080p, озвучка студии AniLibria" },
        new JObject { ["id"] = "AnimeGo", ["name"] = "AnimeGo", ["enabled"] = true, ["state"] = "работает", ["note"] = "animego.me: актуальный поиск и серии, AniBoom до 1080p; Kodik как запасной поток" },
        AnimeLibStatus(),
        new JObject { ["id"] = "Kodik", ["name"] = "Kodik", ["enabled"] = true, ["state"] = "запасной", ["note"] = "потоки 720p через модуль Lampac; показывается только для сезонов, где нет 1080p и выше" }
    };

    // ---- сопоставление с карточкой TMDB ----
    static int? SeasonFromName(string ru, string en)
    {
        foreach (var (s, rx) in new[] {
            (en, @"(\d+)(?:st|nd|rd|th)\s+season"), (en, @"season\s+(\d+)"), (ru, @"(\d+)-?й\s+сезон"), (ru, @"сезон\s+(\d+)"), (ru, @"\s(\d{1,2})$"), (en, @"\s(\d{1,2})$") })
        {
            if (string.IsNullOrEmpty(s)) continue;
            var m = Regex.Match(s, rx, RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= 30) return n;
        }
        return null;
    }

    static bool Relevant(string cn, string[] bases)
    {
        if (cn == "") return false;
        foreach (var b in bases)
        {
            if (b.Length < 3) continue;
            if (cn.StartsWith(b) || b.StartsWith(cn)) return true;
            if (b.Length >= 8 && cn.Contains(b)) return true;
        }
        return false;
    }


    // Ключ озвучки для склейки одинаковых озвучек из разных источников: «AniLibria (Aemi, MyAska…)» = «AniLiberty (…)», «2×2» = «2x2 New»,
    // «Субтитры» = «Crunchyroll.Subtitles», «Mega-Anime» = «Mega Anime». Ники участников в скобках и служебные слова не учитываются.
    public static string VoiceKey(string v)
    {
        var s = (v ?? "").ToLowerInvariant().Replace('ё', 'е').Replace('×', 'x');
        s = Regex.Replace(s, @"[\(\[].*?[\)\]]", " ");
        s = Regex.Replace(s, @"[^\p{L}\p{N}]+", " ").Trim();
        if (s.Length == 0) return "";
        if (Regex.IsMatch(s, @"субтитр|subtitle|(^| )subs?( |$)")) return "subs";
        if (s.Contains("anilib")) return "anilibria";
        if (Regex.IsMatch(s, @"^(дублированн\w*|дубляж|дубль|dub|dubbed)$")) return "dub";
        var stop = new HashSet<string> { "new", "old", "studio", "студия", "студио", "team", "dub", "voice", "озвучка", "tv" };
        var toks = s.Split(' ').Where(t => !stop.Contains(t)).ToList();
        if (toks.Count == 0) toks = s.Split(' ').ToList();
        return string.Join("", toks);
    }

    // Украинские озвучки (Украинский, Укр, UA, AniUA…) в списки не попадают
    public static bool BadVoice(string v)
        => !string.IsNullOrEmpty(v) && Regex.IsMatch(v, @"укр|ukr|українськ|(^|[^a-z])ua([^a-z]|$)|aniua", RegexOptions.IgnoreCase);

    // Убирает варианты с украинской озвучкой, но только в тех сезонах, где есть другие (русские) варианты: единственный украинский остаётся
    public static List<JObject> WithoutBadVoices(List<JObject> all)
        => all.Where(x => !BadVoice((string)x["voice"]) || !all.Any(y => (int?)y["season"] == (int?)x["season"] && !BadVoice((string)y["voice"]))).ToList();

    // Раздача только на украинском: озвучки указаны и все украинские; если озвучек нет — язык «ukr» без «rus»; иначе по названию
    // (украинские буквы і ї є ґ без русских ы э ъ ё либо метка Ukr/UA без метки Rus/дубляж). Русская озвучка рядом с украинской — раздача остаётся.
    public static bool UkrOnlyRelease(JObject r)
    {
        var vs = (r["info"]?["voices"] as JArray)?.Select(x => (string)x).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        if (vs.Count > 0) return vs.All(BadVoice);
        string langs = r["languages"]?.ToString() ?? "";
        bool uk = Regex.IsMatch(langs, "ukr|ua", RegexOptions.IgnoreCase), ru = Regex.IsMatch(langs, "rus", RegexOptions.IgnoreCase);
        if (langs.Length > 0 && uk && !ru) return true;
        string t = (string)r["Title"] ?? "";
        bool ruMark = Regex.IsMatch(t, @"(^|[^a-z])(rus|ru)([^a-z]|$)|рус|дубляж", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(t, "[іїєґІЇЄҐ]") && !Regex.IsMatch(t, "[ыэъёЫЭЪЁ]") && !ruMark) return true;
        return Regex.IsMatch(t, @"(^|[^а-яa-zіїєґ])(укр|ukr)([^а-яa-zіїєґ]|$)|українськ", RegexOptions.IgnoreCase) && !ruMark;
    }

    public static string VoiceLabel(string v)
    {
        string key = VoiceKey(v);
        if (key == "subs") return "Субтитры";
        if (key == "dub") return "Дублированный";
        if (key == "anilibria") return "AniLibria";
        return Regex.Replace(v ?? "", @"\s*[\(\[].*?[\)\]]", "").Trim();
    }


    // ---------------------------------------------------------------- доступность источников
    // Раз в три минуты (по запросу) проверяем, отвечают ли серверы AniLibria, AnimeLib и Kodik, и сколько времени это занимает. Ошибка воспроизведения
    // (поток не получен) помечает источник недоступным на 15 минут. Клиент по умолчанию берёт вариант с большим числом серий и доступным сервером.
    static readonly ConcurrentDictionary<string, (bool ok, long ms, long at, long failAt)> health = new();
    static long healthRun;

    public static void MarkFail(string source)
    {
        var h = health.TryGetValue(source, out var x) ? x : (true, 0L, 0L, 0L);
        health[source] = (h.Item1, h.Item2, h.Item3, HomeDb.Now());
    }

    static async Task<(int code, long ms)> CurlCode(string url, params string[] headers)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var psi = new System.Diagnostics.ProcessStartInfo("curl") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-s", "-o", "/dev/null", "-m", "7", "-A", UA, "-w", "%{http_code}" };
        foreach (var h in headers) { args.Add("-H"); args.Add(h); }
        args.Add(url);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        string o = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (int.TryParse(o.Trim(), out int c) ? c : 0, sw.ElapsedMilliseconds);
    }

    public static async Task RefreshHealth()
    {
        if (HomeDb.Now() - healthRun < 180_000) return;
        healthRun = HomeDb.Now();
        string tok = LibToken();
        var checks = new (string name, string url, string[] hdr)[]
        {
            ("AL", AlHost + "/api/v1/app/search/releases?query=a", new string[0]),
            ("AnimeLib", LibHost + "/api/anime?q=a", tok == null ? new string[0] : new[] { "Authorization: Bearer " + tok, "Referer: https://anilib.me/", "Origin: https://anilib.me" }),
            ("AnimeGo", AnimeGoHost + "/search/anime?q=%D0%BA%D1%8D%D0%B9%D0%BE%D0%BD", new[] { "Accept: text/html" }),
            ("Kodik", "https://kodikplayer.com/", new string[0])
        };
        await Task.WhenAll(checks.Select(async c =>
        {
            try
            {
                var (code, ms) = await CurlCode(c.url, c.hdr);
                bool ok = code >= 200 && code < 500 && !(c.name == "AnimeLib" && (code == 401 || code == 403));
                var prev = health.TryGetValue(c.name, out var x) ? x.failAt : 0;
                health[c.name] = (ok, ms, HomeDb.Now(), prev);
            }
            catch { health[c.name] = (false, 0, HomeDb.Now(), health.TryGetValue(c.name, out var x) ? x.failAt : 0); }
        }));
    }

    public static JObject HealthJson()
    {
        var o = new JObject();
        foreach (var kv in health)
        {
            bool failedRecently = HomeDb.Now() - kv.Value.failAt < 15 * 60_000;
            o[kv.Key] = new JObject { ["ok"] = kv.Value.ok && !failedRecently, ["ms"] = kv.Value.ms };
        }
        return o;
    }

    public static string CardKey(string media, long tmdb) => tmdb + "_" + media;

    // Полное сопоставление: возвращает {ok, sources:[...], at}; без кеша.
    static async Task<JObject> ResolveCore(string media, long tmdb)
    {
        var res = new JObject { ["ok"] = false, ["sources"] = new JArray(), ["at"] = HomeDb.Now() };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dbg = new JArray();
        void Step(string what) => dbg.Add($"{sw.ElapsedMilliseconds} мс: {what}");
        res["debug"] = dbg;
        var card = await KpMap.TmdbCard(media, tmdb);
        Step("карточка TMDB");
        if (card == null || !KpUnified.IsAnime(card)) { res["reason"] = "не аниме"; return res; }

        bool tv = media == "tv";
        string ru = (string)(tv ? card["name"] : card["title"]) ?? "";
        string orig = (string)(tv ? (card["original_name"] ?? card["name"]) : (card["original_title"] ?? card["title"])) ?? "";
        var names = new List<string>();
        void AddName(string n) { if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n)) names.Add(n); }
        AddName(ru);
        try
        {
            var en = await KpMap.TmdbRaw($"{media}/{tmdb}?language=en-US");
            AddName((string)(tv ? en["name"] : en["title"]));
        }
        catch { }
        try
        {
            var alt = await KpMap.TmdbRaw($"{media}/{tmdb}/alternative_titles");
            foreach (var t in ((JArray)(alt["results"] ?? alt["titles"] ?? new JArray())).Children<JObject>())
            {
                string title = (string)t["title"];
                // латиница и кириллица: сопоставляем с названиями AniLibria (русское и английское)
                if (!string.IsNullOrEmpty(title) && title.Length < 80 && title.All(ch => ch < 0x250 || (ch >= 0x400 && ch <= 0x4FF) || char.IsWhiteSpace(ch))) AddName(title);
                if (names.Count >= 30) break;
            }
        }
        catch { }
        if (!string.IsNullOrEmpty(orig) && orig.All(ch => ch < 0x250)) AddName(orig);

        Step("названия: " + string.Join(" | ", names.Take(8)));
        var bases = names.Select(Norm).Where(n => n.Length >= 3).Distinct().ToArray();
        int firstYear = int.TryParse(((string)(tv ? card["first_air_date"] : card["release_date"]) ?? "").Split('-')[0], out int fy) ? fy : 0;

        // TMDB-сезоны: год выхода каждого
        var tmdbSeasons = new List<(int n, int year)>();
        var tmdbCounts = new Dictionary<int, int>();
        if (tv && card["seasons"] is JArray sj)
            foreach (var s in sj.Children<JObject>())
            {
                int sn = (int?)s["season_number"] ?? 0;
                if (sn <= 0) continue;
                int y = int.TryParse(((string)s["air_date"] ?? "").Split('-')[0], out int yy) ? yy : 0;
                tmdbSeasons.Add((sn, y));
                tmdbCounts[sn] = (int?)s["episode_count"] ?? 0;
            }

        // поиск по нескольким названиям
        var found = new Dictionary<int, JObject>();
        // запросов к AniLibria немного: русское, английское и пара латинских вариантов (поиск у них нечёткий, находит по любому)
        var queries = names.Where(n => n.Any(ch => ch >= 0x400 && ch <= 0x4FF)).Take(1)
            .Concat(names.Where(n => n.All(ch => ch < 0x250)).Take(2)).Distinct().ToList();
        // A failing search alias must not add its full timeout to every other
        // alias. There are at most three queries; merge after all tasks finish.
        var searchResults = await Task.WhenAll(queries.Select(async q =>
        {
            try { return await GetJson($"{AlHost}/api/v1/app/search/releases?query={Uri.EscapeDataString(q)}") as JArray; }
            catch { return null; }
        }));
        foreach (var arr in searchResults)
        {
            if (arr == null) continue;
            foreach (var it in arr.Children<JObject>())
            {
                int id = (int?)it["id"] ?? 0;
                if (id == 0 || found.ContainsKey(id)) continue;
                string cr = Norm((string)it["name"]?["main"]), ce = Norm((string)it["name"]?["english"]);
                if (Relevant(cr, bases) || Relevant(ce, bases)) found[id] = (JObject)it;
            }
        }

        Step($"поиск: найдено релизов {found.Count} по запросам {string.Join(" | ", queries)}");
        var releases = new List<(JObject rel, int season)>();
        foreach (var kv in found.Take(8))
        {
            try
            {
                var rel = await GetJson($"{AlHost}/api/v1/anime/releases/{kv.Key}") as JObject;
                if (rel == null) continue;
                if ((bool?)rel["is_blocked_by_geo"] == true || (bool?)rel["is_blocked_by_copyrights"] == true) continue;
                var eps = rel["episodes"] as JArray;
                if (eps == null || eps.Count == 0) continue;
                // не ниже 720p: релиз без потоков 1080/720 не показываем вовсе (720p потом отсеет PruneLow, если есть лучше)
                if (!eps.Children<JObject>().Any(e => !string.IsNullOrEmpty((string)e["hls_1080"]) || !string.IsNullOrEmpty((string)e["hls_720"]))) continue;
                string type = (string)rel["type"]?["value"] ?? "";
                int year = (int?)rel["year"] ?? 0;
                string rm = (string)rel["name"]?["main"], re = (string)rel["name"]?["english"];
                int season = 0;
                if (tv)
                {
                    season = SeasonFromName(rm, re) ?? 0;
                    if (season == 0 && tmdbSeasons.Count > 0 && year > 0)
                    {
                        var best = tmdbSeasons.Where(s => s.year > 0).OrderBy(s => Math.Abs(s.year - year)).FirstOrDefault();
                        if (best.n > 0) season = best.n;
                    }
                    if (season == 0) season = 1;
                }
                else if (type.Equals("TV", StringComparison.OrdinalIgnoreCase) && eps.Count > 3) continue;   // карточка фильма — берём только фильмы/OVA
                releases.Add((rel, season));
            }
            catch { }
        }

        Step($"релизов с 1080p: {releases.Count}");
        var arrOut = (JArray)res["sources"];
        foreach (var (rel, season) in releases.OrderBy(r => r.season).ThenBy(r => (int?)r.rel["year"] ?? 0))
        {
            var voicers = ((JArray)(rel["members"] ?? new JArray())).Children<JObject>()
                .Where(m => (string)m["role"]?["value"] == "voicing").Select(m => (string)m["nickname"]).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            var episodes = new JArray();
            // у релиза есть 1080p — берём только такие серии; иначе (старые релизы) — 720p
            bool rel1080 = ((JArray)rel["episodes"]).Children<JObject>().Any(x => !string.IsNullOrEmpty((string)x["hls_1080"]));
            foreach (var e in ((JArray)rel["episodes"]).Children<JObject>().OrderBy(x => (int?)x["ordinal"] ?? 0))
            {
                string u = rel1080 ? (string)e["hls_1080"] : (string)e["hls_720"];
                if (!string.IsNullOrEmpty(u) && u.Contains('?')) u = u.Substring(0, u.IndexOf('?'));   // параметры нужны только веб-плееру (реклама)
                if (string.IsNullOrEmpty(u)) continue;      // нет 1080p — серию не показываем
                int n = (int?)e["ordinal"] ?? 0;
                if (n <= 0) continue;
                episodes.Add(new JObject
                {
                    ["n"] = n,
                    ["title"] = string.IsNullOrWhiteSpace((string)e["name"]) ? "Серия " + n : (string)e["name"],
                    ["dur"] = (int?)e["duration"] ?? 0,
                    ["url"] = u,
                    ["hash"] = tv ? KpBridge.EpisodeHash(season, n, orig) : KpBridge.LampaHash(orig)
                });
            }
            if (episodes.Count == 0) continue;
            arrOut.Add(new JObject
            {
                ["source"] = "AL", ["name"] = "AniLibria", ["release"] = (int?)rel["id"], ["season"] = season, ["year"] = (int?)rel["year"],
                ["title"] = (string)rel["name"]?["main"], ["title_en"] = (string)rel["name"]?["english"],
                ["voice"] = "AniLibria", ["voicers"] = new JArray(voicers), ["quality"] = rel1080 ? "1080p" : "720p", ["episodes"] = episodes
            });
        }
        try { await ResolveAnimeLib(arrOut, media, bases, tmdbSeasons, orig, dbg, sw, names); }
        catch (Exception ex) { dbg.Add("AnimeLib: " + ex.GetType().Name); }
        if (tv) SplitAbsolute(arrOut, tmdbCounts, orig);
        try { await ResolveAnimeGo(arrOut, media, bases, tmdbSeasons, orig, dbg, sw, names); }
        catch (Exception ex) { dbg.Add("AnimeGo: " + ex.GetType().Name); }

        // Kodik (720p) — только для сезонов, где нет 1080p и выше
        try
        {
            int Tier(JObject s) { var qq = (string)s["quality"] ?? ""; return qq.Contains("2160") ? 3 : qq.Contains("1080") ? 2 : 1; }
            var covered = new HashSet<int>(arrOut.Children<JObject>().Where(s => Tier(s) >= 2).Select(s => tv ? ((int?)s["season"] ?? 0) : 0));
            bool need = tv ? (tmdbSeasons.Count == 0 ? covered.Count == 0 : tmdbSeasons.Any(ts => !covered.Contains(ts.n))) : covered.Count == 0;
            if (need) await ResolveKodik(arrOut, media, tmdb, ru, orig, covered, dbg, sw, bases, firstYear, names.FirstOrDefault(n => n.All(ch => ch < 0x250) && n.Any(char.IsLetter)));
        }
        catch (Exception ex) { dbg.Add("Kodik: " + ex.GetType().Name); }
        PruneLow(arrOut);
        res["ok"] = arrOut.Count > 0;
        res["orig"] = orig;
        if (!(bool)res["ok"]) res["reason"] = "нет релизов 720p и выше";
        return res;
    }


    // У долгих сериалов источники (AnimeLib и др.) нумеруют серии подряд (1…1180), а TMDB и Lampa делят их на сезоны. Если в варианте
    // серий заметно больше, чем в назначенном ему сезоне TMDB, режем номера по границам сезонов TMDB (накопительно): серия N -> сезон S,
    // серия N - смещение. Ключ позиции (сезон + серия + оригинальное название) при этом совпадает с родным ключом Lampa. Ссылки на потоки
    // не меняются — они ведут на серию по её идентификатору.
    static void SplitAbsolute(JArray arr, Dictionary<int, int> counts, string orig)
    {
        var order = counts.Where(x => x.Key > 0 && x.Value > 0).Select(x => x.Key).OrderBy(x => x).ToList();
        foreach (var src in arr.Children<JObject>().Where(x => (string)x["source"] != "Kodik").ToList())
        {
            int S = (int?)src["season"] ?? 0;
            if (S <= 0 || !counts.TryGetValue(S, out int c0) || c0 <= 0) continue;
            var eps = ((JArray)src["episodes"]).Children<JObject>().OrderBy(e => (int)e["n"]).ToList();
            if (eps.Count == 0) continue;
            int maxN = eps.Max(e => (int)e["n"]);
            if (maxN < c0 + 5 || maxN <= c0 * 1.5) continue;
            var bounds = new List<(int season, int start, int end)>();
            int acc = 0;
            foreach (var k in order.Where(k => k >= S)) { bounds.Add((k, acc + 1, acc + counts[k])); acc += counts[k]; }
            if (bounds.Count < 2 || acc < maxN * 0.8) continue;       // у TMDB слишком мало серий — не режем
            var last = bounds[bounds.Count - 1];
            bounds[bounds.Count - 1] = (last.season, last.start, int.MaxValue);   // остаток уходит в последний сезон
            var parts = eps.GroupBy(e => bounds.First(b => (int)e["n"] >= b.start && (int)e["n"] <= b.end)).OrderBy(g => g.Key.season).ToList();
            int idx = arr.IndexOf(src);
            arr.Remove(src);
            foreach (var g in parts)
            {
                var ns = (JObject)src.DeepClone();
                var neps = new JArray();
                foreach (var e in g)
                {
                    var ne = (JObject)e.DeepClone();
                    int n = (int)e["n"] - g.Key.start + 1;
                    ne["n"] = n;
                    ne["hash"] = KpBridge.EpisodeHash(g.Key.season, n, orig);
                    ne["season"] = g.Key.season;
                    neps.Add(ne);
                }
                ns["episodes"] = neps;
                ns["season"] = g.Key.season;
                ns["release"] = (src["release"]?.ToString() ?? "") + "#s" + g.Key.season;
                arr.Add(ns);
            }
        }
    }

    // 720p оставляем только если для сезона нет ни одного источника 1080p и выше (4K/1080p в приоритете везде)
    static void PruneLow(JArray sources)
    {
        int Tier(JObject s) { var q = (string)s["quality"] ?? ""; return q.Contains("2160") ? 3 : q.Contains("1080") ? 2 : 1; }
        var all = sources.Children<JObject>().ToList();
        foreach (var s in all)
            if (Tier(s) == 1 && all.Any(o => (int?)o["season"] == (int?)s["season"] && Tier(o) >= 2)) s.Remove();
    }



    // ---------------------------------------------------------------- Kodik (через модуль Lampac /lite/kodik)
    // Запасной источник: потоки 720p (старые и редкие тайтлы без 1080p в AniLibria/AnimeLib). Берётся только для сезонов, у которых нет
    // ничего лучше. Поиск по imdb id; каждая озвучка — отдельный вариант. Поток выбирает /anime/play (src=kodik) в момент воспроизведения.
    static readonly HttpClient LampHttp = new() { Timeout = TimeSpan.FromSeconds(45) };

    static async Task<JObject> LampJson(string pathAndQuery)
    {
        try { return JObject.Parse(await LampHttp.GetStringAsync("http://127.0.0.1:9118" + pathAndQuery)); } catch { return null; }
    }

    static string RelPath(string url) => Regex.Replace(url ?? "", @"^https?://[^/]+", "");

    static string LinkOf(string callUrl)
    {
        try { return System.Web.HttpUtility.ParseQueryString(new Uri(callUrl).Query)["link"]; } catch { return null; }
    }

    // диагностика: что отдаёт Kodik для тайтла, независимо от других источников
    public static async Task<JObject> KodikProbe(string media, long tmdb, string latin)
    {
        var card = await KpMap.TmdbCard(media, tmdb);
        if (card == null) return new JObject { ["sources"] = new JArray() };
        bool tv = media == "tv";
        string ru = (string)(tv ? card["name"] : card["title"]) ?? "";
        string orig = (string)(tv ? (card["original_name"] ?? card["name"]) : (card["original_title"] ?? card["title"])) ?? "";
        var arr = new JArray();
        int fy = int.TryParse(((string)(tv ? card["first_air_date"] : card["release_date"]) ?? "").Split('-')[0], out int fyy) ? fyy : 0;
        var bs = new[] { Norm(ru), Norm(orig) }.Where(n => n.Length >= 3).Distinct().ToArray();
        var dbg0 = new JArray();
        await ResolveKodik(arr, media, tmdb, ru, orig, new HashSet<int>(), dbg0, System.Diagnostics.Stopwatch.StartNew(), bs, fy, latin);
        return new JObject { ["debug"] = dbg0, ["sources"] = arr };
    }

    static async Task ResolveKodik(JArray arrOut, string media, long tmdb, string ru, string orig, HashSet<int> covered, JArray dbg, System.Diagnostics.Stopwatch sw, string[] bases, int firstYear, string latin)
    {
        bool tv = media == "tv";
        string imdb = (string)(await KpMap.TmdbRaw($"{media}/{tmdb}/external_ids"))?["imdb_id"];
        string q = $"imdb_id={imdb}&kinopoisk_id=0&title={Uri.EscapeDataString(ru)}&original_title={Uri.EscapeDataString(latin ?? orig)}&rjson=true";
        var top = await LampJson("/lite/kodik?" + q);
        if (top == null) { dbg.Add("Kodik: не ответил"); return; }
        string type = (string)top["type"];
        dbg.Add($"Kodik: imdb {(string.IsNullOrEmpty(imdb) ? "—" : imdb)}, ответ {type}");

        // верхний уровень: список сезонов, одна серия-страница или (если imdb не нашёлся) список похожих — берём записи того же года и названия
        var tops = new List<JObject>();
        if (type == "similar")
        {
            foreach (var it in ((JArray)(top["data"] ?? new JArray())).Children<JObject>())
            {
                int y = int.TryParse((string)it["year"], out int yy) ? yy : 0;
                if (firstYear > 0 && Math.Abs(y - firstYear) > 1) continue;
                var parts = ((string)it["title"] ?? "").Split(" / ");
                if (!parts.Any(p => Relevant(Norm(p), bases))) continue;
                var pg0 = await LampJson(RelPath((string)it["url"]) + "&rjson=true");
                if (pg0 != null) tops.Add(pg0);
                if (tops.Count >= 2) break;
            }
            dbg.Add($"Kodik: imdb {(string.IsNullOrEmpty(imdb) ? "нет" : "не найден")}, подходящих записей {tops.Count}");
        }
        else if (type == "season" || type == "episode") tops.Add(top);
        else if (type == "movie")
        {
            // Kodik returns films as a flat list of translations. Treat each
            // translation as a one-item source so the existing source/episode
            // UI can select it and the progress key remains movie-scoped.
            int movieCount = 0;
            foreach (var d in ((JArray)(top["data"] ?? new JArray())).Children<JObject>().Take(20))
            {
                string link = LinkOf((string)d["url"]);
                if (string.IsNullOrEmpty(link)) link = LinkOf((string)d["stream"]);
                if (string.IsNullOrEmpty(link)) continue;
                string voice = (string)d["translate"] ?? (string)d["title"] ?? "Kodik";
                var episodes = new JArray
                {
                    new JObject
                    {
                        ["n"] = 1,
                        ["title"] = ru,
                        ["dur"] = 0,
                        ["url"] = "/anime/play.m3u8?src=kodik&movie=1&link=" + Uri.EscapeDataString(link) + "&ep=1&t=" + Uri.EscapeDataString(orig),
                        ["hash"] = KpBridge.LampaHash(orig)
                    }
                };
                arrOut.Add(new JObject
                {
                    ["source"] = "Kodik", ["name"] = "Kodik", ["release"] = $"kodik:{imdb}:movie:{voice}", ["season"] = 0, ["year"] = firstYear,
                    ["title"] = ru, ["title_en"] = orig, ["voice"] = voice, ["voicers"] = new JArray(),
                    ["quality"] = "720p", ["episodes"] = episodes
                });
                if (++movieCount >= 10) break;
            }
            dbg.Add($"Kodik: фильм, вариантов озвучки {movieCount}");
        }
        else { dbg.Add("Kodik: ответ типа " + type); return; }

        var seasons = new List<(int s, JObject page)>();
        foreach (var t0 in tops)
        {
            string tt = (string)t0["type"];
            if (tt == "season")
            {
                foreach (var s in ((JArray)t0["data"]).Children<JObject>().Take(40))
                {
                    int sn = (int?)s["id"] ?? 0;
                    if (sn <= 0 || covered.Contains(sn) || seasons.Any(x => x.s == sn)) continue;
                    var pg = await LampJson(RelPath((string)s["url"]));
                    if (pg != null && (string)pg["type"] == "episode") seasons.Add((sn, pg));
                }
            }
            else if (tt == "episode") { if (!covered.Contains(0) && !seasons.Any(x => x.s == 0)) seasons.Add((0, t0)); }
        }

        foreach (var (sn, pg) in seasons)
        {
            var voices = ((JArray)(pg["voice"] ?? new JArray())).Children<JObject>().Take(10).ToList();
            if (voices.Count == 0) voices.Add(new JObject { ["name"] = "Kodik", ["active"] = true });
            // у каждой озвучки свой список серий: активная уже в ответе, остальные догружаем
            var tasks = voices.Select(async v =>
            {
                if ((bool?)v["active"] == true || v["url"] == null) return (v, pg);
                return (v, await LampJson(RelPath((string)v["url"])));
            }).ToList();
            foreach (var (v, vp) in await Task.WhenAll(tasks))
            {
                if (vp == null) continue;
                var episodes = new JArray();
                int bestQ = 0;
                foreach (var d in ((JArray)(vp["data"] ?? new JArray())).Children<JObject>())
                {
                    string link = LinkOf((string)d["url"]);
                    if (string.IsNullOrEmpty(link)) continue;
                    var qm = Regex.Match(link, @"/(\d+)p$");
                    int qh = qm.Success ? int.Parse(qm.Groups[1].Value) : 720;
                    bestQ = Math.Max(bestQ, qh);
                    int n = (int?)d["e"] ?? 0;
                    if (!tv) n = 1;
                    else if (n <= 0) continue;
                    episodes.Add(new JObject
                    {
                        ["n"] = n,
                        ["title"] = string.IsNullOrWhiteSpace((string)d["name"]) ? "Серия " + n : (string)d["name"],
                        ["dur"] = 0,
                        ["url"] = "/anime/play.m3u8?src=kodik&link=" + Uri.EscapeDataString(link) + "&ep=" + n + "&t=" + Uri.EscapeDataString(orig),
                        ["hash"] = tv ? KpBridge.EpisodeHash(sn, n, orig) : KpBridge.LampaHash(orig),
                        ["season"] = sn
                    });
                    if (!tv) break;
                }
                if (episodes.Count == 0) continue;
                string vname = (string)v["name"] ?? "Kodik";
                arrOut.Add(new JObject
                {
                    ["source"] = "Kodik", ["name"] = "Kodik", ["release"] = $"kodik:{imdb}:{sn}:{vname}", ["season"] = sn, ["year"] = 0,
                    ["title"] = ru, ["title_en"] = orig, ["voice"] = vname, ["voicers"] = new JArray(),
                    ["quality"] = bestQ >= 2160 ? "2160p/1080p" : bestQ >= 1080 ? "1080p" : "720p", ["episodes"] = episodes
                });
            }
        }
        dbg.Add($"{sw.ElapsedMilliseconds} мс: Kodik вариантов озвучки {arrOut.Children<JObject>().Count(x => (string)x["source"] == "Kodik")}");
    }

    // поток Kodik на момент воспроизведения: лучшее качество не выше предела профиля устройства
    public static async Task<string> KodikPlayUrl(string link, int ep, string orig, int maxH)
    {
        string key = "kodik|" + link + "|" + ep + "|" + maxH;
        if (playCache.TryGetValue(key, out var hit) && HomeDb.Now() - hit.at < 10 * 60 * 1000) return hit.url;
        string episode = ep > 0 ? "&episode=" + ep : "";
        var j = await LampJson($"/lite/kodik/video?title=x&original_title={Uri.EscapeDataString(orig ?? "")}&link={Uri.EscapeDataString(link)}{episode}");
        var q = j?["quality"] as JObject;
        if (q == null) return null;
        string best = null; int bestH = 0;
        foreach (var p in q.Properties())
        {
            if (!int.TryParse(p.Name.TrimEnd('p'), out int h) || h < 360 || (maxH > 0 && h > maxH)) continue;
            if (h > bestH) { bestH = h; best = (string)p.Value; }
        }
        if (best == null) return null;
        // модуль Lampac для ключа 720p отдаёт манифест 480 — настоящий 720 лежит рядом под тем же именем
        best = Regex.Replace(best, @"/\d+\.mp4:hls", "/" + bestH + ".mp4:hls");
        playCache[key] = (HomeDb.Now(), best);
        return best;
    }


    static IEnumerable<string> queriesOrder(List<string> names)
        => names.Where(n => n.All(ch => ch < 0x250)).Take(2).Concat(names.Where(n => n.Any(ch => ch >= 0x400 && ch <= 0x4FF)).Take(1));

    // ---------------------------------------------------------------- AnimeLib (anilib.me)
    // Нужен токен аккаунта (cache/animelib.json, его кладёт веб-панель; обновляет модуль Lampac). Каждая озвучка — отдельный вариант,
    // только те, у которых есть поток не ниже 1080p. Потоки отдаёт прокси Lampac (anime/play выбирает качество по профилю устройства).
    const string LibHost = "https://hapi.hentaicdn.org";

    static string LibToken()
    {
        try
        {
            string p = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "cache", "animelib.json");
            if (!System.IO.File.Exists(p)) return null;
            string t = (string)JObject.Parse(System.IO.File.ReadAllText(p))["token"];
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch { return null; }
    }

    static async Task<JToken> LibGet(string path, string token)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("curl")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-s", "-m", "20", "--compressed", "-A", UA, "-H", "Authorization: Bearer " + token, "-H", "Referer: https://anilib.me/",
                                  "-H", "Origin: https://anilib.me", "-w", "\n%{http_code}", LibHost + path }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        string outp = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        int i = outp.LastIndexOf('\n');
        if (i < 0 || outp.Substring(i + 1).Trim() != "200") return null;
        return JToken.Parse(outp.Substring(0, i));
    }

    static async Task ResolveAnimeLib(JArray arrOut, string media, string[] bases, List<(int n, int year)> tmdbSeasons, string orig, JArray dbg, System.Diagnostics.Stopwatch sw, List<string> names)
    {
        string token = LibToken();
        if (token == null) return;
        bool tv = media == "tv";
        var queries = queriesOrder(names).ToList();
        var found = new Dictionary<string, JObject>();
        foreach (var q in queries.Take(3))
        {
            var d = (await LibGet("/api/anime?fields[]=releaseDate&q=" + Uri.EscapeDataString(q), token))?["data"] as JArray;
            if (d == null) continue;
            foreach (var a in d.Children<JObject>())
            {
                string slug = (string)a["slug_url"];
                if (string.IsNullOrEmpty(slug) || found.ContainsKey(slug)) continue;
                string label = ((string)a["type"]?["label"] ?? "");
                bool isTv = label.StartsWith("TV", StringComparison.OrdinalIgnoreCase);
                bool isMovie = label.IndexOf("Фильм", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Movie", StringComparison.OrdinalIgnoreCase) >= 0;
                if (tv ? !isTv : !isMovie) continue;                       // мини-аниме, спешлы и т.п. не берём
                if (Relevant(Norm((string)a["rus_name"]), bases) || Relevant(Norm((string)a["eng_name"]), bases)) found[slug] = a;
            }
        }
        dbg.Add($"{sw.ElapsedMilliseconds} мс: AnimeLib найдено {found.Count}");
        foreach (var kv in found.Take(6))
        {
            var a = kv.Value; string slug = kv.Key;
            var eps = (await LibGet("/api/episodes?anime_id=" + Uri.EscapeDataString(slug), token))?["data"] as JArray;
            if (eps == null || eps.Count == 0) continue;
            var list = eps.Children<JObject>().Where(e => int.TryParse((string)e["number"], out _)).OrderBy(e => int.Parse((string)e["number"])).ToList();
            if (list.Count == 0) continue;
            int year = int.TryParse(((string)a["releaseDate"] ?? "").Split('-')[0], out int yy) ? yy : 0;
            int season = 0;
            if (tv)
            {
                season = SeasonFromName((string)a["rus_name"], (string)a["eng_name"]) ?? 0;
                if (season == 0 && tmdbSeasons.Count > 0 && year > 0)
                {
                    var best = tmdbSeasons.Where(s => s.year > 0).OrderBy(s => Math.Abs(s.year - year)).FirstOrDefault();
                    if (best.n > 0) season = best.n;
                }
                if (season == 0) season = 1;
            }
            // озвучки берём по первой серии: у каких команд есть поток не ниже 1080p
            var first = (await LibGet("/api/episodes/" + (long)list[0]["id"], token))?["data"]?["players"] as JArray;
            if (first == null) continue;
            foreach (var pl in first.Children<JObject>())
            {
                if ((string)pl["player"] != "Animelib") continue;
                string team = (string)pl["team"]?["name"];
                if (string.IsNullOrEmpty(team)) continue;
                var qs = ((JArray)(pl["video"]?["quality"] ?? new JArray())).Children<JObject>().Select(v => (int?)v["quality"] ?? 0).Where(h => h >= MinHeight).ToList();
                if (qs.Count == 0) continue;
                var episodes = new JArray();
                foreach (var e in list)
                {
                    int n = int.Parse((string)e["number"]);
                    int es = int.TryParse((string)e["season"], out int ess) ? ess : 1;
                    int sn = tv ? ((es > 1 && season == 1) ? es : season) : 0;
                    episodes.Add(new JObject
                    {
                        ["n"] = n,
                        ["title"] = string.IsNullOrWhiteSpace((string)e["name"]) ? "Серия " + n : (string)e["name"],
                        ["dur"] = 0,
                        ["url"] = "/anime/play?ep=" + (long)e["id"] + "&voice=" + Uri.EscapeDataString(team),
                        ["hash"] = tv ? KpBridge.EpisodeHash(sn, n, orig) : KpBridge.LampaHash(orig),
                        ["season"] = sn
                    });
                }
                arrOut.Add(new JObject
                {
                    ["source"] = "AnimeLib", ["name"] = "AnimeLib", ["release"] = "lib:" + slug + ":" + team, ["season"] = season, ["year"] = year,
                    ["title"] = (string)a["rus_name"], ["title_en"] = (string)a["eng_name"], ["voice"] = team, ["voicers"] = new JArray(),
                    ["quality"] = qs.Max() >= 2160 ? "2160p/1080p" : qs.Max() >= 1080 ? "1080p" : "720p", ["episodes"] = episodes
                });
            }
        }
        dbg.Add($"{sw.ElapsedMilliseconds} мс: AnimeLib вариантов озвучки {arrOut.Children<JObject>().Count(x => (string)x["source"] == "AnimeLib")}");
    }

    // выбор потока AnimeLib на момент воспроизведения: качество не ниже 1080p и не выше предела профиля устройства
    static readonly ConcurrentDictionary<string, (long at, string url)> playCache = new();

    public static async Task<string> LibPlayUrl(long ep, string voice, int maxH)
    {
        string key = ep + "|" + voice + "|" + maxH;
        if (playCache.TryGetValue(key, out var hit) && HomeDb.Now() - hit.at < 10 * 60 * 1000) return hit.url;
        using var http2 = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string json = await http2.GetStringAsync("http://127.0.0.1:9118/lite/animelib/video?id=" + ep + "&voice=" + Uri.EscapeDataString(voice) + "&title=anime");
        var q = JObject.Parse(json)["quality"] as JObject;
        if (q == null) return null;
        string best = null; int bestH = 0;
        foreach (var p in q.Properties())
        {
            if (!int.TryParse(p.Name.TrimEnd('p'), out int h) || h < MinHeight || (maxH > 0 && h > maxH)) continue;
            if (h > bestH) { bestH = h; best = (string)p.Value; }
        }
        if (best != null) playCache[key] = (HomeDb.Now(), best);
        return best;
    }

    // С кешем (шесть часов; отрицательный ответ — три часа), одновременные запросы объединяются.
    public static Task<JObject> Resolve(string media, long tmdb, bool force = false)
    {
        string key = media + ":" + tmdb;
        if (!force)
        {
            var hit = Cached(key);
            if (hit != null) return Task.FromResult(hit);
        }
        return inflight.GetOrAdd(key, _ => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                var r = await ResolveCore(media, tmdb);
                HomeDb.With(c => HomeDb.Exec(c, "INSERT INTO anime_map(k,j,at) VALUES(@k,@j,@a) ON CONFLICT(k) DO UPDATE SET j=@j,at=@a",
                    ("@k", key), ("@j", r.ToString(Formatting.None)), ("@a", HomeDb.Now())));
                return r;
            }
            catch (Exception ex) { return new JObject { ["ok"] = false, ["error"] = ex.GetType().Name, ["sources"] = new JArray() }; }
            finally { gate.Release(); Task<JObject> dummy; inflight.TryRemove(key, out dummy); }
        }));
    }

    // лучшее качество уже найденных аниме-источников (по кэшу, без запросов): 3 — 4K, 2 — FHD, 1 — HD; -1 — нет данных
    public static int BestTier(string media, long tmdb)
    {
        var o = Cached(media + ":" + tmdb);
        if (o == null || (bool?)o["ok"] != true) return -1;
        int best = -1;
        foreach (var s in ((JArray)(o["sources"] ?? new JArray())).Children<JObject>())
        {
            var q = (string)s["quality"] ?? "";
            best = Math.Max(best, q.Contains("2160") ? 3 : q.Contains("1080") ? 2 : q.Contains("720") ? 1 : 0);
        }
        return best;
    }

    public static JObject Cached(string key)
        => HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT j,at FROM anime_map WHERE k=@k", ("@k", key));
            if (q.Count == 0) return null;
            var j = JObject.Parse((string)q[0][0]);
            long at = Convert.ToInt64(q[0][1]);
            var ttl = (bool?)j["ok"] == true ? PosTtl : (j["reason"] != null && (string)j["reason"] == "не аниме" ? TimeSpan.FromHours(24) : NegTtl);
            return HomeDb.Now() - at < (long)ttl.TotalMilliseconds ? j : null;
        });

    // Есть ли тайтл в источниках: 1 — да, 0 — нет, null — ещё не проверено (проверка поставлена в фон).
    public static int? Has(string media, long tmdb)
    {
        var hit = Cached(media + ":" + tmdb);
        if (hit != null) return (bool?)hit["ok"] == true ? 1 : 0;
        _ = Resolve(media, tmdb);
        return null;
    }

    // Состояние серий по общей базе просмотра и «продолжить»
    public static JObject Item(string media, long tmdb, JObject resolved)
    {
        var o = (JObject)resolved.DeepClone();
        var last = HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT source,voice,season,ep,at FROM anime_last WHERE profile_id=@profile AND card=@c", ("@c", CardKey(media, tmdb)));
            return q.Count == 0 ? null : new JObject { ["source"] = q[0][0] as string, ["voice"] = q[0][1] as string, ["vkey"] = VoiceKey(q[0][1] as string), ["season"] = Convert.ToInt32(q[0][2]), ["ep"] = Convert.ToInt32(q[0][3]), ["at"] = Convert.ToInt64(q[0][4]) };
        });
        var keepSet = WithoutBadVoices(((JArray)o["sources"]).Children<JObject>().ToList());
        foreach (var drop in ((JArray)o["sources"]).Children<JObject>().Where(x => !keepSet.Contains(x)).ToList()) drop.Remove();
        foreach (var vs in ((JArray)o["sources"]).Children<JObject>())
        {
            vs["vkey"] = VoiceKey((string)vs["voice"]);
            vs["vlabel"] = VoiceLabel((string)vs["voice"]);
        }
        long bestU = 0; string bestSrc = null, bestRel = null; int bestSeason = 0, bestN = 0; bool bestDone = false; double bestPos = 0;
        bool bestPreferred = false;
        var flat = new List<(string rel, int season, int n)>();
        foreach (var s in ((JArray)o["sources"]).Children<JObject>())
        {
            string rel = s["release"]?.ToString() ?? "";
            int season = (int?)s["season"] ?? 0;
            foreach (var e in ((JArray)s["episodes"]).Children<JObject>())
            {
                var t = TlStore.Get((string)e["hash"]);
                flat.Add((rel, season, (int)e["n"]));
                if (t == null || (t.T <= 0 && t.P <= 0)) continue;
                bool done = KpUnified.IsDone(t.T, t.D, t.P);
                e["state"] = done ? "watched" : "started";
                e["pct"] = (int)Math.Round(t.P);
                e["t"] = t.T; e["d"] = t.D;
                bool preferred = last != null && (string)s["source"] == (string)last["source"] && VoiceKey((string)s["voice"]) == (string)last["vkey"] && season == (int?)last["season"];
                // Sources share a timeline hash. A tie must keep the viewer's
                // chosen source/voice rather than whichever provider is first.
                if (t.U > bestU || t.U == bestU && preferred && !bestPreferred) { bestU = t.U; bestSrc = (string)s["source"]; bestRel = rel; bestSeason = season; bestN = (int)e["n"]; bestDone = done; bestPos = t.T; bestPreferred = preferred; }
            }
        }
        JToken RelTok(string rel) => ((JArray)o["sources"]).Children<JObject>().Select(x => x["release"]).FirstOrDefault(r => r?.ToString() == rel) ?? rel;
        o["resume"] = null;
        if (bestU > 0)
        {
            string rel = bestRel; int season = bestSeason, n = bestN; double pos = bestPos; bool next = false, finished = false;
            if (bestDone)
            {
                // досмотрена — предлагаем следующую (в этом варианте, иначе первую следующего сезона)
                var inRel = flat.Where(f => f.rel == bestRel && f.n > bestN).OrderBy(f => f.n).Select(f => ((string rel, int season, int n)?)f).FirstOrDefault();
                if (inRel != null) { n = inRel.Value.n; pos = 0; next = true; }
                else
                {
                    var nr = flat.Where(f => f.season > bestSeason).OrderBy(f => f.season).ThenBy(f => f.n).Select(f => ((string rel, int season, int n)?)f).FirstOrDefault();
                    if (nr != null) { rel = nr.Value.rel; season = nr.Value.season; n = nr.Value.n; pos = 0; next = true; }
                    else finished = true;
                }
            }
            o["resume"] = finished
                ? new JObject { ["finished"] = true, ["source"] = bestSrc, ["release"] = RelTok(bestRel), ["season"] = bestSeason, ["ep"] = bestN }
                : new JObject { ["source"] = bestSrc, ["release"] = RelTok(rel), ["season"] = season, ["ep"] = n, ["pos"] = pos, ["next"] = next };
        }
        o["last"] = last;
        return o;
    }

    public static void SetLast(string card, string source, string voice, int season, int ep)
        => HomeDb.With(c => HomeDb.Exec(c, @"INSERT INTO anime_last(profile_id,card,source,voice,season,ep,at) VALUES(@profile,@c,@s,@v,@se,@e,@a)
                                             ON CONFLICT(profile_id,card) DO UPDATE SET source=@s,voice=@v,season=@se,ep=@e,at=@a",
            ("@c", card), ("@s", source ?? ""), ("@v", voice ?? ""), ("@se", season), ("@e", ep), ("@a", HomeDb.Now())));
}

[ProfileResult]
public class AnimeController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    [HttpGet, AllowAnonymous]
    [Route("anime.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "anime.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    // keys=tv:123,movie:456 → { "tv:123": 1, "movie:456": 0, "tv:789": null }
    [HttpGet, AllowAnonymous]
    [Route("anime/has")]
    public ActionResult HasMany(string keys = null)
    {
        var o = new JObject();
        foreach (var k in (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Take(60))
        {
            var p = k.Split(':');
            if (p.Length != 2 || (p[0] != "tv" && p[0] != "movie") || !long.TryParse(p[1], out long id) || id <= 0) continue;
            int? v = AnimeCatalog.Has(p[0], id);
            o[k] = v.HasValue ? (JToken)v.Value : JValue.CreateNull();
        }
        return Json(o);
    }

    [HttpGet, AllowAnonymous]
    [Route("anime/item")]
    public async Task<ActionResult> ItemGet(long tmdb, string media = "tv", bool force = false)
    {
        if ((media != "tv" && media != "movie") || tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        var r = await AnimeCatalog.Resolve(media, tmdb, force);
        var item = AnimeCatalog.Item(media, tmdb, r);
        item["tmdb"] = tmdb; item["media"] = media; item["card"] = AnimeCatalog.CardKey(media, tmdb);
        _ = AnimeCatalog.RefreshHealth();
        item["health"] = AnimeCatalog.HealthJson();
        return Json(item);
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("anime/last/set")]
    public ActionResult LastSet(string card = null, string source = null, string voice = null, int season = 0, int ep = 0)
    {
        if (string.IsNullOrEmpty(card) || !Regex.IsMatch(card, "^[0-9]{1,12}_(tv|movie)$")) return Json(new { error = "bad" }, 400);
        AnimeCatalog.SetLast(card, source, voice, season, ep);
        return Json(new { ok = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("anime/kodik/probe")]
    public async Task<ActionResult> KodikProbe(string media = "tv", long tmdb = 0, string latin = null)
    {
        if (tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        var r = await AnimeCatalog.KodikProbe(media, tmdb, latin);
        return Json(new { count = ((JArray)r["sources"]).Count, debug = r["debug"], sources = r["sources"] });
    }

    // воспроизведение серии AnimeLib/Kodik: ?ep=ID&voice=команда&h=предел высоты (0 — без ограничения) → переход на поток нужного качества
    [HttpGet, AllowAnonymous]
    [Route("anime/play")]
    [Route("anime/play.m3u8")]
    public async Task<ActionResult> Play(long ep = 0, string voice = null, int h = 0, string src = null, string link = null, string t = null, bool movie = false)
    {
        if (src == "animego")
        {
            if (ep <= 0 || string.IsNullOrWhiteSpace(voice)) return Json(new { error = "bad_args" }, 400);
            try
            {
                var stream = await AnimeCatalog.AnimeGoPlayUrl(ep, voice, h);
                if (stream.url == null) { AnimeCatalog.MarkFail("AnimeGo"); return Json(new { error = "no_stream" }, 404); }
                if (stream.manifest != null) {
                    Response.Headers["Cache-Control"] = "no-store";
                    return ContentTo(stream.manifest, "application/vnd.apple.mpegurl");
                }
                return Redirect(Regex.Replace(stream.url, @"^https?://127\.0\.0\.1(:[0-9]+)?", host));
            }
            catch { return Json(new { error = "provider_unavailable" }, 502); }
        }
        if (src == "kodik")
        {
            if (string.IsNullOrEmpty(link) || (!movie && ep <= 0)) return Json(new { error = "bad_args" }, 400);
            try
            {
                string ku = await AnimeCatalog.KodikPlayUrl(link, movie ? 0 : (int)ep, t, h);
                if (ku == null) { AnimeCatalog.MarkFail("Kodik"); return Json(new { error = "no_stream" }, 404); }
                ku = Regex.Replace(ku, @"^https?://127\.0\.0\.1(:[0-9]+)?", host);
                return Redirect(ku);
            }
            catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 502); }
        }
        if (ep <= 0 || string.IsNullOrEmpty(voice)) return Json(new { error = "bad_args" }, 400);
        try
        {
            string url = await AnimeCatalog.LibPlayUrl(ep, voice, h);
            if (url == null) { AnimeCatalog.MarkFail("AnimeLib"); return Json(new { error = "no_stream_1080" }, 404); }
            // модуль Lampac строит ссылку по адресу, с которого его вызвали (127.0.0.1) — отдаём клиенту ссылку на адрес сервера
            url = Regex.Replace(url, @"^https?://127\.0\.0\.1(:[0-9]+)?", host);
            return Redirect(url);
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 502); }
    }

    [HttpGet, AllowAnonymous]
    [Route("anime/status")]
    public ActionResult Status() => Json(new { sources = AnimeCatalog.Status(), min_height = 720 });
}
