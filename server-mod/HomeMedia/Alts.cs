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
using System.Threading.Tasks;

namespace HomeMedia;

// Варианты для «Продолжить» и озвучки для карточки: где ещё есть нужная серия (или фильм) и в каком качестве — Кинопаб (с озвучками), аниме-источники
// (AniLibria, AnimeLib, Kodik, с озвучками) и торренты (лучшее качество среди раздач, озвучки из раздач). Данные собираются тремя независимыми
// частями параллельно; каждая часть кэшируется: свежая (30 минут) отдаётся сразу, устаревшая (до 12 часов) отдаётся сразу и обновляется в фоне.
public class AltsController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    // 3 — 4K, 2 — FHD, 1 — HD, 0 — SD
    static int QT(string q)
    {
        q = (q ?? "").ToLowerInvariant();
        return q.Contains("2160") || q.Contains("4k") ? 3 : q.Contains("1080") ? 2 : q.Contains("720") ? 1 : 0;
    }

    static int FilesTier(JToken files)
        => (files as JArray)?.Children<JObject>().Select(f => QT((string)f["quality"])).DefaultIfEmpty(-1).Max() ?? -1;

    static List<string> Voices(JToken v)
        => ((v?["audios"] as JArray) ?? new JArray()).Children<JObject>()
            .Select(a => (string)(a["author"] is JObject au ? au["title"] : null) ?? (string)(a["type"] is JObject ty ? ty["title"] : null))
            .Where(x => !string.IsNullOrEmpty(x) && !AnimeCatalog.BadVoice(x)).Distinct().Take(6).ToList();

    // ------------------------------------------------------------------ кэш частей (свежее / устаревшее + фоновое обновление)
    const long Fresh = 30 * 60_000, Stale = 12L * 3600_000;
    static readonly ConcurrentDictionary<string, (long at, Task<JObject> task)> parts = new();
    static readonly ConcurrentDictionary<string, byte> refreshing = new();

    static async Task<JObject> Safe(Func<Task<JObject>> make)
    {
        try { return await make(); } catch { return new JObject { ["_err"] = true }; }
    }

    static Task<JObject> Part(string key, Func<Task<JObject>> make)
    {
        long now = HomeDb.Now();
        if (parts.TryGetValue(key, out var e))
        {
            long age = now - e.at;
            bool err = e.task.IsCompletedSuccessfully && e.task.Result["_err"] != null;
            if (age < (err ? 60_000 : Fresh)) return e.task;
            if (!err && age < Stale && e.task.IsCompletedSuccessfully)
            {
                if (refreshing.TryAdd(key, 0))
                    _ = Task.Run(async () =>
                    {
                        try { var r = await Safe(make); if (r["_err"] == null) parts[key] = (HomeDb.Now(), Task.FromResult(r)); }
                        finally { refreshing.TryRemove(key, out _); }
                    });
                return e.task;
            }
        }
        var t = Safe(make);
        parts[key] = (now, t);
        return t;
    }

    // фоновый прогрев при открытии карточки
    public static void Prefetch(string media, long tmdb, string prof = null) { _ = Collect(media, tmdb, prof ?? KpStore.DefaultProfile); }

    // ------------------------------------------------------------------ части
    static Task<JObject> PartKp(string media, long tmdb, string prof) => Part($"kp:{prof}:{media}:{tmdb}", async () =>
    {
        var o = new JObject { ["kinopub_eps"] = new JArray() };
        string tok = await KpStore.Token(prof);
        if (tok == null) return o;
        long kp = KpMap.CachedKp(media, tmdb);
        if (kp == 0 && !KpMap.IsKnown(media, tmdb)) kp = await KpMap.LookupKp(media, tmdb, prof);
        if (kp <= 0) return o;
        var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/{kp}", tok);
        if (code != 200) return new JObject { ["_err"] = true };
        o["kp"] = kp;
        var item = JObject.Parse(body)["item"] as JObject;
        var arr = (JArray)o["kinopub_eps"];
        if (item?["seasons"] is JArray seasons && seasons.Count > 0)
        {
            foreach (var s in seasons.Children<JObject>())
                foreach (var e in ((JArray)s["episodes"]).Children<JObject>())
                    arr.Add(new JObject { ["s"] = (int)s["number"], ["e"] = (int)e["number"], ["tier"] = FilesTier(e["files"]), ["voices"] = new JArray(Voices(e)) });
        }
        else if (item?["videos"] is JArray vids && vids.Count > 0)
        {
            var v = vids[0];
            arr.Add(new JObject { ["s"] = 0, ["e"] = 1, ["tier"] = FilesTier(v["files"]), ["voices"] = new JArray(Voices(v)) });
        }
        return o;
    });

    static Task<JObject> PartAnime(string media, long tmdb) => Part($"an:{media}:{tmdb}", async () =>
    {
        var o = new JObject { ["anime"] = new JArray() };
        bool tv = media == "tv";
        var r = await AnimeCatalog.Resolve(media, tmdb);
        if (r != null && (bool?)r["ok"] == true)
            // украинские варианты убираем, только если для этого сезона есть другие (русские); единственный остаётся
            foreach (var s in AnimeCatalog.WithoutBadVoices(((JArray)r["sources"]).Children<JObject>().ToList()))
                ((JArray)o["anime"]).Add(new JObject
                {
                    ["source"] = s["source"], ["release"] = s["release"], ["voice"] = s["voice"], ["season"] = tv ? (int?)s["season"] ?? 0 : 0,
                    ["tier"] = QT((string)s["quality"]), ["eps"] = new JArray(((JArray)s["episodes"]).Children<JObject>().Select(e => (int)e["n"]))
                });
        return o;
    });

    static Task<JObject> PartTorrent(string media, long tmdb, string profileId) => Part($"tr:{profileId}:{media}:{tmdb}", async () =>
    {
        var o = new JObject { ["torrent"] = null, ["tvoices"] = new JArray() };
        bool tv = media == "tv";
        var card = await KpMap.TmdbCard(media, tmdb);
        if (card == null) return o;
        string title = (string)(tv ? (card["original_name"] ?? card["name"]) : (card["original_title"] ?? card["title"]));
        string year = ((string)(tv ? card["first_air_date"] : card["release_date"]) ?? "").Split('-')[0];
        if (string.IsNullOrEmpty(title)) return o;

        var qTask = year.Length == 4 ? CardQuality.Lookup(tmdb, title, year, tv) : Task.FromResult("");
        var vTask = Task.Run(async () =>
        {
            var tr = new JArray();
            string qs = "Query=" + Uri.EscapeDataString(title) + (!tv && year.Length == 4 ? "&year=" + year : "");
            var results = await JacredSearch.Aggregate(qs, applyHideFilter: true, profileId: profileId);
            string nt = AnimeCatalog.Norm(title);
            var cnt = new Dictionary<string, (int n, int tier)>();
            foreach (var r in results.Children<JObject>())
            {
                string t = (string)r["Title"] ?? "";
                if (nt.Length >= 4 && !AnimeCatalog.Norm(t).Contains(nt)) continue;
                if (AnimeCatalog.UkrOnlyRelease(r)) continue;
                if (!tv && year.Length == 4 && !System.Text.RegularExpressions.Regex.IsMatch(t, @"(?<!\d)" + year + @"(?!\d)")) continue;
                if (!(r["info"]?["voices"] is JArray vs)) continue;
                int tier = Math.Max(0, CardQuality.Tier(t));
                int q = (int?)r["info"]?["quality"] ?? 0;
                if (q >= 2160) tier = 3; else if (q >= 1080) tier = Math.Max(tier, 2); else if (q >= 720) tier = Math.Max(tier, 1);
                foreach (var v in vs) { string n = ((string)v ?? "").Trim(); if (n.Length < 2 || n.Length > 40 || AnimeCatalog.BadVoice(n)) continue; var c = cnt.GetValueOrDefault(n); cnt[n] = (c.n + 1, Math.Max(c.tier, tier)); }
            }
            foreach (var x in cnt.OrderByDescending(x => x.Value.n).Take(14))
                tr.Add(new JObject { ["voice"] = x.Key, ["n"] = x.Value.n, ["tier"] = x.Value.tier });
            return tr;
        });
        try { o["tvoices"] = await vTask; } catch { }
        try
        {
            string lab = await qTask;
            int t = CardQuality.TierOfLabel(lab);
            if (t >= 0) o["torrent"] = new JObject { ["tier"] = t, ["label"] = lab, ["coarse"] = tv };
        }
        catch { }
        return o;
    });

    // все части вместе (для «Продолжить»)
    static async Task<JObject> Collect(string media, long tmdb, string prof)
    {
        string profileId = Profiles.CurrentId;
        var tk = PartKp(media, tmdb, prof); var ta = PartAnime(media, tmdb); var tt = PartTorrent(media, tmdb, profileId);
        await Task.WhenAll(tk, ta, tt);
        var o = new JObject { ["kinopub_eps"] = tk.Result["kinopub_eps"] ?? new JArray(), ["anime"] = ta.Result["anime"] ?? new JArray(), ["torrent"] = tt.Result["torrent"], ["tvoices"] = tt.Result["tvoices"] ?? new JArray() };
        if (tk.Result["kp"] != null) o["kp"] = tk.Result["kp"];
        return o;
    }

    // ------------------------------------------------------------------ маршруты
    // media=tv|movie, tmdb, season/ep — где остановились; next=true — серия досмотрена, нужна следующая
    [HttpGet, AllowAnonymous]
    [Route("home/alts")]
    public async Task<ActionResult> Alts(string media = "tv", long tmdb = 0, int season = 0, int ep = 0, bool next = false)
    {
        if ((media != "tv" && media != "movie") || tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        var data = await Collect(media, tmdb, KpStore.ResolveProfile(Request.Query["profile"].FirstOrDefault()));
        bool tv = media == "tv";

        // все известные серии: (сезон, серия) — для расчёта цели
        var all = new SortedSet<(int s, int e)>();
        foreach (var x in ((JArray)data["kinopub_eps"]).Children<JObject>()) all.Add(((int)x["s"], (int)x["e"]));
        foreach (var x in ((JArray)data["anime"]).Children<JObject>())
            foreach (var n in (JArray)x["eps"]) all.Add(((int?)x["season"] ?? 0, (int)n));

        int ts = season, te = ep; bool exists = true;
        if (tv)
        {
            if (next)
            {
                te = ep + 1;
                if (!all.Contains((ts, te)))
                {
                    var later = all.Where(z => z.s > ts).ToList();
                    if (later.Count > 0) { ts = later[0].s; te = later[0].e; } else exists = false;
                }
            }
            else if (!all.Contains((ts, te)) && all.Count > 0) exists = false;
        }

        var alts = new JArray();
        var kp = (JArray)data["kinopub_eps"];
        var kpHit = tv ? kp.Children<JObject>().FirstOrDefault(x => (int)x["s"] == ts && (int)x["e"] == te) : kp.Children<JObject>().FirstOrDefault();
        if (kpHit != null && data["kp"] != null)
            alts.Add(new JObject { ["src"] = "kinopub", ["name"] = "Кинопаб", ["tier"] = (int)kpHit["tier"], ["kp"] = (long)data["kp"], ["voices"] = kpHit["voices"] });
        foreach (var a in ((JArray)data["anime"]).Children<JObject>())
        {
            if (tv && (int?)a["season"] != ts) continue;
            if (tv && !((JArray)a["eps"]).Any(n => (int)n == te)) continue;
            alts.Add(new JObject
            {
                ["src"] = "anime", ["name"] = (string)a["source"], ["release"] = a["release"], ["voice"] = a["voice"],
                ["tier"] = (int)a["tier"], ["season"] = a["season"]
            });
        }
        return Json(new { target = new { season = ts, ep = te, exists }, alts, torrent = data["torrent"] });
    }

    // Максимальное качество по источникам для меню «Источник»: «4K · 2160p», «WEBDL 4K · 2160p», «TS»
    [HttpGet, AllowAnonymous]
    [Route("home/srcq")]
    public async Task<ActionResult> SrcQ(string media = "tv", long tmdb = 0)
    {
        if ((media != "tv" && media != "movie") || tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        var tk = PartKp(media, tmdb, KpStore.ResolveProfile(Request.Query["profile"].FirstOrDefault())); var ta = PartAnime(media, tmdb); var tt = PartTorrent(media, tmdb, Profiles.CurrentId);
        await Task.WhenAll(tk, ta, tt);
        int[] px = { 480, 720, 1080, 2160 };
        string F(int t) => t < 0 ? null : CardQuality.Labels[t] + " · " + px[t] + "p";
        int kpT = ((JArray)(tk.Result["kinopub_eps"] ?? new JArray())).Children<JObject>().Select(e => (int)e["tier"]).DefaultIfEmpty(-1).Max();
        int anT = ((JArray)(ta.Result["anime"] ?? new JArray())).Children<JObject>().Select(a => (int)a["tier"]).DefaultIfEmpty(-1).Max();
        string trLabel = (string)tt.Result["torrent"]?["label"];
        string tr = null;
        if (!string.IsNullOrEmpty(trLabel))
        {
            bool hasTier = trLabel.Split(' ').Any(x => Array.IndexOf(CardQuality.Labels, x) >= 0);
            tr = hasTier ? trLabel + " · " + px[CardQuality.TierOfLabel(trLabel)] + "p" : trLabel;
        }
        return Json(new { kinopub = F(kpT), anime = F(anT), torrents = tr });
    }

    // «FHD WEB-DL», «4K BDRip», «TS»: метка CardQuality в виде «разрешение тип»
    static string PrettyQ(string label)
    {
        if (string.IsNullOrEmpty(label)) return null;
        var toks = label.Split(' ');
        string tier = toks.FirstOrDefault(t => Array.IndexOf(CardQuality.Labels, t) >= 0);
        string type = toks.FirstOrDefault(t => Array.IndexOf(CardQuality.Labels, t) < 0);
        string tp = type switch { "BDRIP" => "BDRip", "WEBDL" => "WEB-DL", "WEBRIP" => "WEBRip", "HDRIP" => "HDRip", "DVD" => "DVDRip", _ => type };
        return string.Join(" ", new[] { tier, tp }.Where(x => !string.IsNullOrEmpty(x)));
    }

    // Озвучки тайтла для карточки; part=kinopub|anime|torrents отдаёт одну часть (клиент запрашивает три параллельно и показывает по мере готовности)
    [HttpGet, AllowAnonymous]
    [Route("home/voices")]
    public async Task<ActionResult> VoicesRoute(string media = "tv", long tmdb = 0, string part = null)
    {
        if ((media != "tv" && media != "movie") || tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        var res = new JObject();
        if (part == null || part == "kinopub")
        {
            var kp = await PartKp(media, tmdb, KpStore.ResolveProfile(Request.Query["profile"].FirstOrDefault()));
            var cnt = new Dictionary<string, int>();
            foreach (var e in ((JArray)(kp["kinopub_eps"] ?? new JArray())).Children<JObject>())
                foreach (var v in (JArray)e["voices"]) { string n = (string)v; cnt[n] = cnt.GetValueOrDefault(n) + 1; }
            res["kinopub"] = new JArray(cnt.OrderByDescending(x => x.Value).Select(x => x.Key).Take(14));
            int kt = ((JArray)(kp["kinopub_eps"] ?? new JArray())).Children<JObject>().Select(e => (int)e["tier"]).DefaultIfEmpty(-1).Max();
            res["kinopub_q"] = kt >= 0 ? CardQuality.Labels[kt] : null;
        }
        if (part == null || part == "anime")
        {
            var an = await PartAnime(media, tmdb);
            var map = new Dictionary<string, (int tier, HashSet<string> src)>();
            foreach (var a in ((JArray)(an["anime"] ?? new JArray())).Children<JObject>())
            {
                string v = AnimeCatalog.VoiceLabel((string)a["voice"]); if (v.Length == 0 || AnimeCatalog.BadVoice(v)) continue;   // в списке на карточке украинские названия не пишем
                int t = (int)a["tier"]; string sname = (string)a["source"] == "AL" ? "AniLibria" : (string)a["source"];
                if (!map.TryGetValue(v, out var cur)) cur = (t, new HashSet<string>());
                cur.src.Add(sname); map[v] = (Math.Max(cur.tier, t), cur.src);
            }
            res["anime"] = new JArray(map.OrderByDescending(x => x.Value.tier).Take(14).Select(x => new JObject { ["voice"] = x.Key, ["tier"] = x.Value.tier, ["src"] = string.Join(", ", x.Value.src) }));
            int at = map.Count == 0 ? -1 : map.Values.Max(v => v.tier);
            res["anime_q"] = at >= 0 ? CardQuality.Labels[at] : null;
        }
        if (part == null || part == "torrents")
        {
            var tr = await PartTorrent(media, tmdb, Profiles.CurrentId);
            res["torrents"] = tr["tvoices"] ?? new JArray();
            res["torrents_q"] = PrettyQ((string)tr["torrent"]?["label"]);
        }
        return Json(res);
    }
}
