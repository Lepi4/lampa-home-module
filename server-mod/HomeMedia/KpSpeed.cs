using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

// Тест серверов kino.pub (Нидерланды / Россия): качаем несколько реальных сегментов потока через сервер
// (так же, как идёт воспроизведение) и меряем задержку и скорость.
public class KpSpeedController : BaseController
{
    static readonly string[][] Locations =
    {
        new[] { "nl", "Нидерланды", "1" },
        new[] { "ru", "Россия", "3" },
    };

    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    static Uri Abs(Uri baseUri, string rel) => new Uri(baseUri, rel);

    static async Task<string> Text(HttpClient c, Uri uri)
    {
        using var res = await c.GetAsync(uri);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }

    // Один замер: мастер-плейлист -> вариант ~720p -> несколько сегментов
    static async Task<JObject> Measure(string masterUrl, string loc)
    {
        var client = KpStore.Client();
        var total = Stopwatch.StartNew();
        try
        {
            var master = new Uri(masterUrl.Split('?')[0] + "?loc=" + loc);
            var swMaster = Stopwatch.StartNew();
            string m = await Text(client, master);
            long masterMs = swMaster.ElapsedMilliseconds;

            var lines = m.Split('\n').Select(l => l.Trim()).ToList();
            string variantRel = null; int bestDiff = int.MaxValue;
            for (int i = 0; i < lines.Count - 1; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF")) continue;
                var r = Regex.Match(lines[i], "RESOLUTION=(\\d+)x(\\d+)");
                int h = r.Success ? int.Parse(r.Groups[2].Value) : 720;
                int diff = Math.Abs(h - 720);
                if (diff < bestDiff) { bestDiff = diff; variantRel = lines[i + 1]; }
            }
            if (variantRel == null) return new JObject { ["loc"] = loc, ["ok"] = false, ["error"] = "нет вариантов потока" };

            var variant = Abs(master, variantRel);
            string vp = await Text(client, variant);
            var segs = vp.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            if (segs.Count == 0) return new JObject { ["loc"] = loc, ["ok"] = false, ["error"] = "нет сегментов" };

            // берём сегменты из середины, чтобы не мерить прогретое начало
            var pick = segs.Skip(Math.Min(3, Math.Max(0, segs.Count - 4))).Take(4).ToList();
            long bytes = 0; long ttfbSum = 0; int n = 0;
            var swAll = Stopwatch.StartNew();
            foreach (var s in pick)
            {
                var sw = Stopwatch.StartNew();
                using var res = await client.GetAsync(Abs(variant, s), HttpCompletionOption.ResponseHeadersRead);
                res.EnsureSuccessStatusCode();
                ttfbSum += sw.ElapsedMilliseconds;
                using var stream = await res.Content.ReadAsStreamAsync();
                var buf = new byte[81920]; int read;
                while ((read = await stream.ReadAsync(buf)) > 0) bytes += read;
                n++;
                if (swAll.Elapsed.TotalSeconds > 10) break;
            }
            double sec = Math.Max(0.001, swAll.Elapsed.TotalSeconds);
            return new JObject
            {
                ["loc"] = loc,
                ["ok"] = true,
                ["mbit"] = Math.Round(bytes * 8 / sec / 1e6, 1),
                ["latency_ms"] = n > 0 ? ttfbSum / n : 0,
                ["manifest_ms"] = masterMs,
                ["mb"] = Math.Round(bytes / 1e6, 1),
                ["total_s"] = Math.Round(total.Elapsed.TotalSeconds, 1)
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["loc"] = loc, ["ok"] = false, ["error"] = ex.GetType().Name };
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/test/servers")]
    public async Task<ActionResult> Servers()
    {
        string prof = KpStore.ResolveProfile(Request.Headers["X-KP-Profile"].FirstOrDefault());
        string tok = await KpStore.Token(prof);
        if (tok == null) return Json(new { error = "not_authorized" }, 401);

        // образец: свежий фильм с потоком
        string master = null;
        var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/fresh?type=movie&perpage=5", tok);
        if (code != 200) return Json(new { error = "no_sample" }, 502);

        foreach (var it in ((JArray)JObject.Parse(body)["items"]).Children<JObject>())
        {
            var (c2, b2) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/{(long)it["id"]}", tok);
            if (c2 != 200) continue;
            var v = ((JArray)(JObject.Parse(b2)["item"]?["videos"] ?? new JArray())).Children<JObject>().FirstOrDefault();
            var url = v?["files"]?.FirstOrDefault()?["url"] as JObject;
            master = (string)url?["hls4"] ?? (string)url?["hls2"] ?? (string)url?["hls"];
            if (!string.IsNullOrEmpty(master) && master.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || (master ?? "").Contains("/manifest/")) break;
        }
        if (string.IsNullOrEmpty(master)) return Json(new { error = "no_stream" }, 502);

        var results = new JArray();
        foreach (var l in Locations)
        {
            var r = await Measure(master, l[0]);
            r["label"] = l[1];
            r["id"] = int.Parse(l[2]);
            results.Add(r);
        }

        var best = results.Children<JObject>().Where(r => (bool)r["ok"]).OrderByDescending(r => (double)r["mbit"]).FirstOrDefault();
        return Json(new { results, recommended = best == null ? null : (string)best["loc"] });
    }
}
