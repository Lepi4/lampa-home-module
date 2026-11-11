using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HomeMedia;

// Одностороннее дозаполнение основного профиля: серия уже досмотрена в «Я смотрю» Кинопаба,
// а в этом профиле строки ещё нет. Фильмы не переносятся. В Кинопаб ничего не пишется.
// ProfileMirror по-прежнему синхронизирует только членство списков, не прогресс серий.
public static class KpProgressImport
{
    public static async Task Once()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (Profiles.IsTest || HomeDb.Get("kp_main_series_filled_v3") == "1") return;
            string result = await Run(false);
            HomeDb.Set("kp_main_series_report", result.Length > 300 ? result.Substring(0, 300) : result);
            if (result.StartsWith("перенесено", StringComparison.Ordinal) || result.StartsWith("нечего", StringComparison.Ordinal))
                HomeDb.Set("kp_main_series_filled_v3", "1");
        }
        catch { }
    }

    public static async Task<string> Run(bool dry)
    {
        string tok = await KpStore.Token(KpStore.DefaultProfile);
        if (tok == null) return "нет привязки Кинопаба";
        using var hold = Profiles.Enter(new Profiles.Scope(Profiles.MainId, "kp-series", "kp-series", null, true, false, 18, 0));
        var seen = await KpUnified.WatchedSets();
        var resets = await KpUnified.RecentResets();
        var wire = new JArray();
        int page = 1, shows = 0, details = 0, marked = 0, already = 0, resetSkip = 0, unmapped = 0;
        while (page < 30)
        {
            var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/watching/serials?subscribed=1&perpage=50&page={page}", tok);
            if (code != 200) return "список «Я смотрю» не прочитан: " + code;
            var doc = JObject.Parse(body);
            var items = ((JArray)(doc["items"] ?? new JArray())).Children<JObject>().ToList();
            foreach (var it in items)
            {
                long kp = (long?)it["id"] ?? 0;
                if (kp <= 0) continue;
                string kind = (string)it["type"] ?? "";
                if (kind == "movie" || kind == "film") continue;
                shows++;
                var card = KpMap.CardForKp(kp) ?? await KpMap.ToTmdbCard(it, tok);
                long tmdb = (long?)card?["id"] ?? 0;
                if (tmdb <= 0 || CardKind.Media(card) != "tv") { unmapped++; continue; }
                string orig = (string)(card["original_name"] ?? card["name"]) ?? "";
                if (orig.Length == 0) { unmapped++; continue; }
                var (wc, wb) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/watching?id={kp}", tok);
                if (wc != 200) continue;
                details++;
                var w = JObject.Parse(wb)["item"] as JObject;
                seen.TryGetValue(tmdb, out var have);
                resets.TryGetValue(tmdb, out var dropped);
                foreach (var se in ((JArray)(w?["seasons"] ?? new JArray())).Children<JObject>())
                foreach (var ep in ((JArray)(se["episodes"] ?? new JArray())).Children<JObject>())
                {
                    if ((int?)ep["status"] != 1) continue;
                    int s = (int?)se["number"] ?? 0, e = (int?)ep["number"] ?? 0;
                    if (s <= 0 || e <= 0) continue;
                    marked++;
                    if (have != null && have.Contains((s, e))) { already++; continue; }
                    if (dropped != null && dropped.Contains((s, e))) { resetSkip++; continue; }
                    double dur = (double?)ep["duration"] ?? 0;
                    if (dur < 0) dur = 0;
                    long at = ((long?)ep["updated"] ?? 0) * 1000;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (at <= 0 || at > now) at = now;
                    wire.Add(new JObject
                    {
                        ["id"] = $"tv-{tmdb}-s{s}e{e}",
                        ["hash"] = KpBridge.EpisodeHash(s, e, orig),
                        ["card"] = $"{tmdb}_tv",
                        ["position"] = dur,
                        ["duration"] = dur,
                        ["percent"] = 100,
                        ["watched_at"] = at,
                        ["op"] = "completed",
                        ["event"] = "9:" + at,
                        ["profile"] = Profiles.MainId
                    });
                }
            }
            int cur = (int?)doc["pagination"]?["current"] ?? page;
            int tot = (int?)doc["pagination"]?["total"] ?? page;
            if (items.Count == 0 || cur >= tot) break;
            page++;
        }
        string tail = $" сериалов {shows}, карточек {details}, отметок {marked}, уже было {already}, сбросов {resetSkip}, без карточки {unmapped}";
        if (shows > 0 && details == 0) return "список есть, карточки серий не прочитаны." + tail;
        if (wire.Count == 0) return "нечего переносить." + tail;
        if (dry) return $"пробный прогон, серий к переносу: {wire.Count}." + tail;
        int ok = 0;
        var why = new Dictionary<string, int>();
        for (int i = 0; i < wire.Count; i += 80)
        {
            var batch = new JArray(wire.Skip(i).Take(80));
            var (results, _) = TlV2.Submit(batch);
            foreach (var r in results)
            {
                string key = r.Status + (string.IsNullOrEmpty(r.Reason) ? "" : ":" + r.Reason);
                why[key] = why.TryGetValue(key, out int n) ? n + 1 : 1;
                if (r.Status == "accepted" || r.Status == "duplicate") ok++;
            }
        }
        string whyText = string.Join(", ", why.Select(kv => kv.Key + " " + kv.Value));
        return $"перенесено {ok} из {wire.Count}." + tail + (whyText.Length == 0 ? "" : ". " + whyText);
    }
}

[AllowAnonymous]
public class KpProgressImportController : BaseController
{
    [HttpGet]
    [Route("kp/progress/import")]
    public async Task<ActionResult> Import(int dry = 1)
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        string text = await KpProgressImport.Run(dry != 0);
        if (!text.StartsWith("пробный", StringComparison.Ordinal) && (text.StartsWith("перенесено", StringComparison.Ordinal) || text.StartsWith("нечего", StringComparison.Ordinal)))
            HomeDb.Set("kp_main_series_filled_v3", "1");
        return Content(text, "text/plain; charset=utf-8");
    }
}
