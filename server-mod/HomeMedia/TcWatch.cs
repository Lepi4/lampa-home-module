using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Наблюдение за штатным TimeCode: кто и как обращается к /timecode/* (счётчик по часам) и меняется ли сама база TimeCode.sql.
// Нужно, чтобы перед переносом просмотров в одну таблицу убедиться, что других клиентов старого маршрута нет, а после — что писатели не остались.
// Тела запросов, query string, заголовки авторизации и токены не сохраняются. Штатный TimeCode не затрагивается.
public static class TcWatch
{
    static readonly object sl = new();
    static readonly Dictionary<string, long> hits = new();          // час|ip|метод|путь|код|клиент -> число обращений
    static readonly string file = Path.Combine(Directory.GetCurrentDirectory(), "database", "timecode_watch.json");
    static bool loaded;
    static Timer timer;
    static JObject poll = new() { ["baseline"] = new JObject(), ["changes"] = new JArray() };

    static void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(file)) return;
            var j = JObject.Parse(File.ReadAllText(file));
            foreach (var p in (JObject)(j["hits"] ?? new JObject())) hits[p.Key] = (long)p.Value;
            if (j["poll"] is JObject pj) { poll = pj; if (poll["established"] == null) { poll["changes"] = new JArray(); poll["established"] = true; } }
        }
        catch { }
    }

    public static void Start()
    {
        lock (sl) Load();
        timer ??= new Timer(_ => Poll(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));
    }

    public static void Stop() { timer?.Dispose(); timer = null; Persist.Flush(); }

    static string Norm(string path)
    {
        path = path.TrimEnd('/').ToLowerInvariant();
        if (path.StartsWith("/timecode/js/")) return "/timecode/js/*";       // токен в пути не сохраняем
        return path;
    }

    static string Client(string ua)
    {
        if (string.IsNullOrEmpty(ua)) return "none";
        if (ua.Contains("McShell")) return "McShell";
        if (ua.Contains("lampa_client")) return "lampa_client";
        if (ua.Contains("okhttp")) return "okhttp";
        if (ua.Contains("Dalvik")) return "dalvik";
        if (ua.Contains("python") || ua.Contains("curl")) return "tool";
        if (ua.Contains("Mozilla")) return "browser";
        return "other";
    }

    // Вызывается из middleware один раз на запрос (first=true). Код ответа берём по завершении.
    public static void Seen(HttpContext ctx)
    {
        try
        {
            string path = ctx.Request.Path.Value ?? "";
            if (!(path.Equals("/timecode", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/timecode/", StringComparison.OrdinalIgnoreCase) || path.Equals("/timecode.js", StringComparison.OrdinalIgnoreCase))) return;
            string np = Norm(path), method = ctx.Request.Method;
            var ipo = ctx.Connection.RemoteIpAddress;
            if (ipo != null && ipo.IsIPv4MappedToIPv6) ipo = ipo.MapToIPv4();
            string ip = ipo == null ? "?" : (System.Net.IPAddress.IsLoopback(ipo) ? "localhost" : ipo.ToString());
            string cl = Client(ctx.Request.Headers["User-Agent"].ToString());
            ctx.Response.OnCompleted(() =>
            {
                try { Add(ip, method, np, ctx.Response.StatusCode, cl); } catch { }
                return Task.CompletedTask;
            });
        }
        catch { }
    }

    static void Add(string ip, string method, string path, int code, string cl)
    {
        long hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600;
        string key = $"{hour}|{ip}|{method}|{path}|{code}|{cl}";
        lock (sl)
        {
            Load();
            hits[key] = hits.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        Save();
    }

    static void Save()
    {
        Persist.Later(file, () =>
        {
            lock (sl)
            {
                long cut = DateTimeOffset.UtcNow.AddDays(-35).ToUnixTimeSeconds();
                foreach (var k in hits.Keys.Where(k => long.TryParse(k.Split('|')[0], out var h) && h < cut).ToList()) hits.Remove(k);
                var j = new JObject { ["hits"] = JObject.FromObject(hits), ["poll"] = poll };
                return j.ToString(Formatting.None);
            }
        }, 10000);
    }

    // Резервное наблюдение за самой базой: строки и максимум updated_at по областям. Рост означает запись мимо наших путей.
    // Читателей и IP это не показывает (для них — счётчик выше).
    public static void Poll()
    {
        try
        {
            string src = Path.Combine(Directory.GetCurrentDirectory(), "database", "TimeCode.sql");
            if (!File.Exists(src)) return;
            var cur = new Dictionary<string, (long rows, long del, long max)>();
            using (var c = new SqliteConnection("Data Source=" + src + ";Mode=ReadOnly"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT user, COUNT(*), SUM(CASE WHEN deleted THEN 1 ELSE 0 END), MAX(updated_at) FROM timecodes GROUP BY user";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cur[r.GetString(0)] = (r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3));
            }
            lock (sl)
            {
                Load();
                var bl = (JObject)poll["baseline"];
                var ch = (JArray)poll["changes"];
                bool dirty = false, firstRun = bl.Count == 0;        // самый первый замер — только baseline
                foreach (var kv in cur)
                {
                    var b = bl[kv.Key] as JObject;
                    if (b == null)
                    {
                        // первый замер — это baseline, не изменение; появление новой области позже — изменение
                        if (!firstRun) ch.Add(new JObject { ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["user"] = kv.Key, ["event"] = "new_area", ["rows"] = kv.Value.rows });
                        bl[kv.Key] = new JObject { ["rows"] = kv.Value.rows, ["del"] = kv.Value.del, ["max"] = kv.Value.max };
                        dirty = true;
                        continue;
                    }
                    if ((long)b["max"] != kv.Value.max || (long)b["rows"] != kv.Value.rows || (long)b["del"] != kv.Value.del)
                    {
                        ch.Add(new JObject { ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["user"] = kv.Key, ["event"] = "changed", ["rows"] = kv.Value.rows, ["rows_was"] = (long)b["rows"], ["max"] = kv.Value.max, ["max_was"] = (long)b["max"] });
                        b["rows"] = kv.Value.rows; b["del"] = kv.Value.del; b["max"] = kv.Value.max;
                        dirty = true;
                    }
                }
                while (ch.Count > 300) ch.RemoveAt(0);
                if (dirty) Save();
            }
        }
        catch { }
    }

    public static JObject Summary(int hours = 24 * 7)
    {
        lock (sl)
        {
            Load();
            long from = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hours * 3600L;
            var groups = hits.Select(p => { var a = p.Key.Split('|'); return (hour: long.Parse(a[0]), ip: a[1], method: a[2], path: a[3], code: a[4], cl: a[5], n: p.Value); })
                .Where(x => x.hour >= from)
                .GroupBy(x => (x.ip, x.method, x.path, x.code, x.cl))
                .Select(g => new JObject
                {
                    ["ip"] = g.Key.ip, ["method"] = g.Key.method, ["path"] = g.Key.path, ["code"] = g.Key.code, ["client"] = g.Key.cl,
                    ["count"] = g.Sum(x => x.n), ["first"] = g.Min(x => x.hour) * 1000, ["last"] = g.Max(x => x.hour) * 1000
                }).OrderByDescending(o => (long)o["count"]);
            return new JObject
            {
                ["since_hours"] = hours,
                ["observed_since"] = hits.Count == 0 ? (JToken)null : hits.Keys.Min(k => long.Parse(k.Split('|')[0])) * 1000,
                ["by_client"] = new JArray(groups),
                ["db_poll"] = poll
            };
        }
    }
}

public class TcWatchController : BaseController
{
    [HttpGet, AllowAnonymous]
    [Route("home/ops/tcwatch")]
    public ActionResult Get(int hours = 168)
    {
        if (!Guard.Ok(Request)) return new ContentResult { Content = "{\"error\":\"forbidden\"}", ContentType = "application/json", StatusCode = 403 };
        return new ContentResult { Content = TcWatch.Summary(Math.Clamp(hours, 1, 24 * 35)).ToString(Formatting.None), ContentType = "application/json; charset=utf-8" };
    }
}
