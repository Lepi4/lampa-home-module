using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HomeMedia;

// Тестовые данные для проверки лент: синтетические просмотры известных тайтлов (по TMDB), которые можно полностью убрать.
// Всё созданное записывается в kv «synth» и удаляется операцией clean (просмотры, отслеживание, скрытое, «Продолжить»).
public class SynthController : BaseController
{
    static ActionResult J(object o, int code = 200) => new ContentResult { Content = o is string s ? s : JsonConvert.SerializeObject(o), ContentType = "application/json; charset=utf-8", StatusCode = code };

    static JObject Reg()
    {
        try { return JObject.Parse(HomeDb.Get("synth") ?? "{}"); } catch { return new JObject(); }
    }

    static void SaveReg(JObject r) => HomeDb.Set("synth", r.ToString(Formatting.None));

    static void Remember(string kind, string val)
    {
        var r = Reg(); var a = (JArray)(r[kind] ?? (r[kind] = new JArray()));
        if (!a.Any(x => (string)x == val)) a.Add(val);
        SaveReg(r);
    }

    // Тест не трогает настоящую историю: если хотя бы одна строка/запись «Продолжить» тайтла уже существует и создана не этим тестом — отказ
    static string Foreign(JArray rows, string card)
    {
        if (HomeDb.SandboxId != null) return null;          // песочница — копия: защищать в ней нечего
        var reg = Reg();
        var mine = ((JArray)(reg["hashes"] ?? new JArray())).Select(x => (string)x).ToHashSet();
        var myCards = ((JArray)(reg["cards"] ?? new JArray())).Select(x => (string)x).ToHashSet();
        // «своя» строка — только та, чей последний event тестовый («9:…»): если пользователь после теста смотрел серию, строка уже чужая
        int real = HomeDb.With(c => rows.Children<JObject>().Select(r => (string)r["h"]).Count(h => { var cur = TlV2.Get(c, h); return cur != null && !((cur.Event ?? "").StartsWith("9:")); }));
        if (real > 0) return "в общей таблице уже есть просмотры этого тайтла (" + real + " строк): синтетика их бы затёрла";
        var lpx = LastPlayStore.Get(card);
        if (lpx != null && (bool?)lpx["synth"] != true) return "для тайтла уже есть запись «Продолжить»";
        return null;
    }

    static void Mark(string card)
    {
        var r = Reg();
        var pre = (JObject)(r["pre"] ?? (r["pre"] = new JObject()));
        if (r["started"] == null) { r["started"] = HomeDb.Now(); SaveReg(r); }
        if (pre[card] == null)
        {
            long.TryParse(card.Split('_')[0], out long tm);
            bool tr = false, hd = false;
            try { tr = HomeDb.With(c => HomeDb.Query(c, "SELECT 1 FROM tracked WHERE profile_id=@profile AND tmdb=@t", ("@t", tm)).Count > 0); } catch { }
            try { hd = HomeDb.With(c => HomeDb.Query(c, "SELECT 1 FROM hidden WHERE profile_id=@profile AND k LIKE @k", ("@k", card + "%")).Count > 0); } catch { }
            pre[card] = new JObject { ["tracked"] = tr, ["hidden"] = hd };
            SaveReg(r);
        }
    }

    // op=seed&tmdb=T[&skip=S1E3,S2E1][&upto=S2E5]  — сериал: просмотрены все вышедшие серии, кроме skip, до upto включительно
    // op=movie&tmdb=T&pct=40[&lastplay=1]          — фильм с частичным просмотром (и записью «Продолжить»)
    // op=clean
    [HttpGet, AllowAnonymous]
    [Route("home/ops/synth")]
    public async Task<ActionResult> Run(string op = "status", long tmdb = 0, string skip = "", string upto = "", int pct = 40, int lastplay = 0, int done = 0, string sbx = null)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return J(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        try
        {
            JArray rows = new JArray();
            if (op == "status") return J(Reg().ToString(Formatting.None));
            // синтетические данные пишутся ТОЛЬКО в песочницу (копию базы): на рабочей базе инструмент не работает; очистка и просмотр реестра доступны везде
            if (op != "clean" && op != "find" && HomeDb.SandboxId == null) return J(new { error = "disabled", message = "synth works only inside a sandbox (sbx=<id>); see /home/ops/sandbox?op=create" }, 403);
            if (op == "clean")
            {
                var r = Reg(); int n = 0;
                int skipped = 0;
                var mineH = ((JArray)(r["hashes"] ?? new JArray())).Select(x => (string)x).ToList();
                foreach (var h in mineH)
                {
                    // удаляем только строки, которыми по-прежнему владеет тест (event «9:…»); устройствам такие строки не выдаются (TlV2.Since), поэтому следов в file_view нет
                    var cur = HomeDb.With(c => TlV2.Get(c, h));
                    if (cur == null) continue;
                    if (HomeDb.SandboxId == null && !((cur.Event ?? "").StartsWith("9:"))) { skipped++; continue; }
                    n += HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM timeline WHERE profile_id=@profile AND h=@h AND (@sb=1 OR event LIKE '9:%')", ("@h", h), ("@sb", HomeDb.SandboxId != null ? 1 : 0)));
                }
                var pre = (JObject)(r["pre"] ?? new JObject());
                foreach (var t in ((JArray)(r["cards"] ?? new JArray())).Select(x => (string)x))
                {
                    var lp = LastPlayStore.Get(t);
                    if (lp != null && (bool?)lp["synth"] == true && mineH.Contains((string)lp["hash"] ?? "")) LastPlayStore.Delete(t, false);          // только запись, созданную тестом
                    // удаляем только то, чего не было до теста и что появилось в окне теста [started, сейчас]; запись с более ранней или иной ревизией — пользовательская, её не трогаем
                    long started = (long?)r["started"] ?? long.MaxValue;
                    bool hadHidden = (bool?)pre[t]?["hidden"] ?? true, hadTracked = (bool?)pre[t]?["tracked"] ?? true;
                    if (!hadHidden) HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM hidden WHERE profile_id=@profile AND k LIKE @k AND at>=@s", ("@k", t + "%"), ("@s", started - 120000)));
                    if (!hadTracked && long.TryParse(t.Split('_')[0], out long tm)) HomeDb.With(c => { try { HomeDb.Exec(c, "DELETE FROM tracked WHERE profile_id=@profile AND tmdb=@t AND at>=@s", ("@t", tm), ("@s", started - 120000)); } catch { } });
                }
                HomeDb.Set("synth", "{}");
                return J(new { ok = true, removed_rows = n, not_owned_skipped = skipped });
            }
            if (op == "dbg")
            {
                var (lst, _) = await KpUnified.List("continue", 1, 300, "tv");
                var dump = await TlV2.DumpAsync();
                var cards = ((JArray)dump["rows"]).Children<JObject>().Select(r => (string)r["card"]).Where(x => x != null && x.EndsWith("_tv")).Distinct().Count();
                return J(new { continue_tv = lst.Children<JObject>().Select(x => new { id = (long?)x["id"], name = (string)x["name"], at = (long?)x["watch_at"] }), dump_tv_cards = cards, now = HomeDb.Now() });
            }
            if (op == "find")
            {
                var d = await KpMap.TmdbRaw("search/tv?language=ru&query=" + Uri.EscapeDataString(skip ?? ""));
                return J(((JArray)(d?["results"] ?? new JArray())).Children<JObject>().Take(8).Select(x => new { id = (long?)x["id"], name = (string)x["name"], orig = (string)x["original_name"], date = (string)x["first_air_date"], origin = x["origin_country"] }));
            }
            if (op == "season")
            {
                // НАСТОЯЩАЯ пометка просмотра пользователя: все серии сезона season=N отмечаются просмотренными (upto — номер сезона)
                int sn = pct;
                var info = await KpMap.TmdbRaw($"tv/{tmdb}");
                string orig = (string)info?["original_name"] ?? (string)info?["name"];
                var sd = await KpMap.TmdbRaw($"tv/{tmdb}/season/{sn}?language=ru");
                if (string.IsNullOrEmpty(orig) || sd == null) return J(new { error = "tmdb data" }, 502);
                var eps = ((JArray)(sd["episodes"] ?? new JArray())).Children<JObject>().Where(e => (string)e["air_date"] != null && string.CompareOrdinal((string)e["air_date"], DateTime.UtcNow.ToString("yyyy-MM-dd")) <= 0).ToList();
                long t0 = HomeDb.Now(); int k = 0;
                foreach (var e in eps)
                {
                    int en = (int)e["episode_number"]; int run = (int?)e["runtime"] ?? 24;
                    string h = KpBridge.EpisodeHash(sn, en, orig);
                    rows.Add(new JObject { ["h"] = h, ["c"] = $"{tmdb}_tv", ["id"] = $"tv-{tmdb}-s{sn}e{en}", ["t"] = run * 60, ["d"] = run * 60, ["p"] = 100, ["u"] = t0 + (k++), ["event"] = "8:" + t0 + "." + en, ["op"] = "completed" });
                }
                var (res, _) = TlV2.Submit(rows);
                KpUnified.TrackAdd(tmdb, t0);
                return J(new { ok = true, orig, season = sn, episodes = eps.Count, accepted = res.Count(x => x.Status == "accepted"), statuses = res.GroupBy(x => x.Status).ToDictionary(g => g.Key, g => g.Count()) });
            }
            if (op == "age")
            {
                // состарить синтетические просмотры тайтла на days суток (проверка постоянного отслеживания)
                long back = (long)pct * 86400000L;
                int n = HomeDb.With(c => HomeDb.Exec(c, "UPDATE timeline SET u=u-@b, s=@s WHERE profile_id=@profile AND c=@c AND event LIKE '9:%' AND h IN (" + string.Join(",", ((JArray)(Reg()["hashes"] ?? new JArray())).Select(x => "'" + (string)x + "'").DefaultIfEmpty("''")) + ")", ("@b", back), ("@s", HomeDb.Now()), ("@c", $"{tmdb}_tv")));
                return J(new { ok = true, aged = n });
            }
            if (tmdb <= 0) return J(new { error = "tmdb" }, 400);
            rows = new JArray(); long now = HomeDb.Now(); int seq = 0;
            if (op == "seed")
            {
                var info = await KpMap.TmdbRaw($"tv/{tmdb}");
                string orig = (string)info?["original_name"] ?? (string)info?["name"];
                var aired = await KpUnified.AiredCounts(tmdb);
                if (string.IsNullOrEmpty(orig) || aired == null) return J(new { error = "tmdb data", orig, aired = aired != null }, 502);
                var sk = new HashSet<(int, int)>();
                foreach (var m in System.Text.RegularExpressions.Regex.Matches(skip ?? "", @"S(\d+)E(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Cast<System.Text.RegularExpressions.Match>())
                    sk.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
                int us = 0, ue = 0; var um = System.Text.RegularExpressions.Regex.Match(upto ?? "", @"S(\d+)E(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (um.Success) { us = int.Parse(um.Groups[1].Value); ue = int.Parse(um.Groups[2].Value); }
                int created = 0;
                foreach (var kv in aired.OrderBy(k => k.Key))
                    for (int e = 1; e <= kv.Value; e++)
                    {
                        if (sk.Contains((kv.Key, e))) continue;
                        if (us > 0 && (kv.Key > us || (kv.Key == us && e > ue))) continue;
                        string h = KpBridge.EpisodeHash(kv.Key, e, orig);
                        rows.Add(new JObject { ["h"] = h, ["c"] = $"{tmdb}_tv", ["id"] = $"tv-{tmdb}-s{kv.Key}e{e}", ["t"] = 2400, ["d"] = 2400, ["p"] = 100, ["u"] = now - 20000 + created, ["event"] = "9:" + (++seq), ["op"] = "completed" });
                        created++;
                    }
                string bad = Foreign(rows, $"{tmdb}_tv");
                if (bad != null)
                {
                    var hit = HomeDb.With(c => rows.Children<JObject>().Select(r => TlV2.Get(c, (string)r["h"])).Where(x => x != null).Take(5).Select(x => new { x.H, x.C, x.T, x.P, x.Event, x.Id }).ToList());
                    return J(new { error = "refused", message = bad, examples = hit }, 409);
                }
                Mark($"{tmdb}_tv");
                foreach (var rw in rows.Children<JObject>()) Remember("hashes", (string)rw["h"]);
                Remember("cards", $"{tmdb}_tv");
                var (res, _) = TlV2.Submit(rows);
                return J(new { ok = true, orig, created, accepted = res.Count(x => x.Status == "accepted"), aired = JObject.FromObject(aired.ToDictionary(k => k.Key.ToString(), k => k.Value)) });
            }
            if (op == "movie")
            {
                var info = await KpMap.TmdbRaw($"movie/{tmdb}");
                string orig = (string)info?["original_title"] ?? (string)info?["title"];
                if (string.IsNullOrEmpty(orig)) return J(new { error = "tmdb data" }, 502);
                string h = KpBridge.LampaHash(orig); string card = $"{tmdb}_movie";
                long dur = 7200, pos = done == 1 ? 7100 : dur * pct / 100;
                rows.Add(new JObject { ["h"] = h, ["c"] = card, ["id"] = $"movie-{tmdb}", ["t"] = pos, ["d"] = dur, ["p"] = done == 1 ? 100 : pct, ["u"] = now, ["event"] = "9:1", ["op"] = done == 1 ? "completed" : "progress" });
                string bad = Foreign(rows, card);
                if (bad != null) return J(new { error = "refused", message = bad }, 409);
                Mark(card); Remember("hashes", h); Remember("cards", card);
                var (res, _) = TlV2.Submit(rows);
                if (lastplay == 1)
                    LastPlayStore.Set(card, new JObject { ["src"] = "url", ["title"] = orig, ["hash"] = h, ["done"] = done == 1, ["pos"] = pos, ["dur"] = dur, ["at"] = now, ["url"] = "http://127.0.0.1/none.mp4", ["synth"] = true });
                return J(new { ok = true, orig, hash = h, card, accepted = res.Count(x => x.Status == "accepted") });
            }
            return J(new { error = "op" }, 400);
        }
        catch (Exception ex) { return J(new { error = ex.GetType().Name, message = ex.Message }, 500); }
    }
}
