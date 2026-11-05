using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

// Диагностический журнал клиентов: ограниченный срок, ограниченный объём.
// Не поток строк, а счётчики: на каждое (устройство, сутки, код, ключ) — одна строка с числом повторов, первым/последним временем и свежим примером.
// Плюс короткое кольцо последних событий (по порядку) на устройство. Срок задаёт сервер (kv dlog_until, мс): после него приём закрыт, накопленное остаётся для разбора.
public static class DLogStore
{
    static bool ready;
    static readonly object gate = new();
    const int RingPerDev = 1000;
    const int MaxRowsPerPut = 400;
    static readonly Regex urlRx = new(@"https?://\S+", RegexOptions.Compiled);
    static readonly Regex codeRx = new(@"^[a-z0-9_.-]{1,40}$", RegexOptions.Compiled);

    static void Ensure()
    {
        if (ready) return;
        HomeDb.With(c =>
        {
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS dlog_ev (dev TEXT, day TEXT, code TEXT, k TEXT, lvl TEXT, n INTEGER, first_at INTEGER, last_at INTEGER, sample TEXT, ver TEXT, ip TEXT, PRIMARY KEY(dev,day,code,k))");
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS dlog_ring (id INTEGER PRIMARY KEY AUTOINCREMENT, dev TEXT, at INTEGER, skew INTEGER, lvl TEXT, code TEXT, k TEXT, n INTEGER, x TEXT)");
            HomeDb.Exec(c, "CREATE INDEX IF NOT EXISTS ix_dlog_ring_dev ON dlog_ring(dev,id)");
        });
        ready = true;
    }

    public static long Until => long.TryParse(HomeDb.Get("dlog_until"), out long v) ? v : 0;
    public static bool On => HomeDb.Get("dlog_on") != "0" && HomeDb.Now() < Until;

    static string Clean(string s, int max)
    {
        s = urlRx.Replace(s ?? "", "<url>");                 // адреса и токены в журнал не попадают
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > max ? s.Substring(0, max) : s;
    }

    // Приём пачки от устройства. Возвращает число принятых строк; -1 — приём закрыт.
    public static int Put(string dev, string ver, string ip, long clientNow, JArray rows)
    {
        if (!On) return -1;
        Ensure();
        dev = Clean(dev, 24); ver = Clean(ver, 40);
        if (dev.Length == 0) dev = ip;
        long now = HomeDb.Now();
        long skew = clientNow > 0 ? now - clientNow : 0;      // насколько часы устройства отстают от серверных (отрицательное — спешат)
        int took = 0;
        lock (gate)
        {
            HomeDb.With(c =>
            {
                foreach (var x in rows.Children<JObject>().Take(MaxRowsPerPut))
                {
                    string code = (string)x["c"] ?? "";
                    if (!codeRx.IsMatch(code)) continue;
                    string lvl = (string)x["l"] ?? "i"; if (lvl != "i" && lvl != "w" && lvl != "e") lvl = "i";
                    string k = Clean((string)x["k"], 60);
                    string sample = Clean((string)x["x"], 160);
                    long n = Math.Clamp((long?)x["n"] ?? 1, 1, 100000);
                    long t0 = (long?)x["t0"] ?? clientNow, t1 = (long?)x["t1"] ?? clientNow;
                    // время события — клиентское, поправленное на разницу часов: так события двух устройств можно сравнивать между собой
                    long a = t0 > 0 ? t0 + skew : now, b = t1 > 0 ? t1 + skew : now;
                    a = Math.Min(a, now); b = Math.Min(b, now);
                    string day = DateTimeOffset.FromUnixTimeMilliseconds(b).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
                    HomeDb.Exec(c, @"INSERT INTO dlog_ev(dev,day,code,k,lvl,n,first_at,last_at,sample,ver,ip) VALUES(@d,@y,@c,@k,@l,@n,@a,@b,@s,@v,@ip)
                        ON CONFLICT(dev,day,code,k) DO UPDATE SET n=n+@n, first_at=MIN(first_at,@a), last_at=MAX(last_at,@b), sample=@s, lvl=@l, ver=@v, ip=@ip",
                        ("@d", dev), ("@y", day), ("@c", code), ("@k", k), ("@l", lvl), ("@n", n), ("@a", a), ("@b", b), ("@s", sample), ("@v", ver), ("@ip", ip));
                    HomeDb.Exec(c, "INSERT INTO dlog_ring(dev,at,skew,lvl,code,k,n,x) VALUES(@d,@b,@sk,@l,@c,@k,@n,@s)",
                        ("@d", dev), ("@b", b), ("@sk", skew), ("@l", lvl), ("@c", code), ("@k", k), ("@n", n), ("@s", sample));
                    took++;
                }
                // кольцо ограничено: оставляем последние RingPerDev строк устройства
                HomeDb.Exec(c, "DELETE FROM dlog_ring WHERE dev=@d AND id <= (SELECT COALESCE(MAX(id),0) FROM dlog_ring WHERE dev=@d) - @m", ("@d", dev), ("@m", RingPerDev));
            });
        }
        return took;
    }

    // Сервер пишет и свои события (dev = "server"), чтобы они были рядом с клиентскими в одном разборе
    public static void Server(string lvl, string code, string key, string sample = null)
    {
        try
        {
            if (!On) return;
            long now = HomeDb.Now();
            Put("server", "", "", now, new JArray(new JObject { ["c"] = code, ["k"] = key, ["l"] = lvl, ["x"] = sample ?? "", ["n"] = 1, ["t0"] = now, ["t1"] = now }));
        }
        catch { }
    }

    public static JObject Summary(int days)
    {
        Ensure();
        string from = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).AddDays(-Math.Max(0, days - 1)).ToString("yyyy-MM-dd");
        var rows = HomeDb.With(c => HomeDb.Query(c, "SELECT dev,day,code,k,lvl,n,first_at,last_at,sample,ver,ip FROM dlog_ev WHERE day>=@f ORDER BY dev, CASE lvl WHEN 'e' THEN 0 WHEN 'w' THEN 1 ELSE 2 END, n DESC", ("@f", from)));
        var byDev = new JObject();
        foreach (var r in rows)
        {
            string dev = (string)r[0];
            if (byDev[dev] == null) byDev[dev] = new JObject { ["ver"] = (string)r[9], ["ip"] = (string)r[10], ["events"] = new JArray() };
            ((JArray)byDev[dev]["events"]).Add(new JObject { ["day"] = (string)r[1], ["code"] = (string)r[2], ["key"] = (string)r[3], ["lvl"] = (string)r[4], ["n"] = Convert.ToInt64(r[5]), ["first"] = Convert.ToInt64(r[6]), ["last"] = Convert.ToInt64(r[7]), ["sample"] = (string)r[8] });
        }
        return new JObject { ["on"] = On, ["until"] = Until, ["now"] = HomeDb.Now(), ["devices"] = byDev };
    }

    // Те же данные короткой таблицей для чтения глазами
    public static string SummaryText(int days)
    {
        var s = Summary(days);
        var sb = new StringBuilder();
        long until = (long)s["until"];
        sb.AppendLine($"журнал: {((bool)s["on"] ? "идёт" : "закрыт")}, до {(until > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(until).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm") + " МСК" : "—")}");
        foreach (var p in ((JObject)s["devices"]).Properties())
        {
            sb.AppendLine().AppendLine($"== {p.Name} (версия {p.Value["ver"]}, ip {p.Value["ip"]})");
            foreach (var e in p.Value["events"].Children<JObject>())
            {
                string t0 = DateTimeOffset.FromUnixTimeMilliseconds((long)e["first"]).ToOffset(TimeSpan.FromHours(3)).ToString("HH:mm:ss");
                string t1 = DateTimeOffset.FromUnixTimeMilliseconds((long)e["last"]).ToOffset(TimeSpan.FromHours(3)).ToString("HH:mm:ss");
                sb.AppendLine($"{e["day"]} {(string)e["lvl"],-1} {(string)e["code"],-22} {(string)e["key"],-28} ×{e["n"],-6} {t0}–{t1}  {e["sample"]}");
            }
        }
        return sb.ToString();
    }

    public static JArray Tail(string dev, int n)
    {
        Ensure();
        var rows = HomeDb.With(c => HomeDb.Query(c, "SELECT at,skew,lvl,code,k,n,x FROM dlog_ring WHERE dev=@d ORDER BY id DESC LIMIT @n", ("@d", dev), ("@n", Math.Clamp(n, 1, 300))));
        var a = new JArray();
        foreach (var r in rows) a.Add(new JObject { ["at"] = Convert.ToInt64(r[0]), ["skew"] = Convert.ToInt64(r[1]), ["lvl"] = (string)r[2], ["code"] = (string)r[3], ["key"] = (string)r[4], ["n"] = Convert.ToInt64(r[5]), ["x"] = (string)r[6] });
        return a;
    }
}

public class DLogController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    // Конфигурация сбора: клиент спрашивает её при отправке; сроком управляет сервер, пересборка приложений не нужна
    [HttpGet, AllowAnonymous]
    [Route("home/dlog/cfg")]
    public ActionResult Cfg() => Json(new { on = DLogStore.On, until = DLogStore.Until, now = HomeDb.Now() });

    [HttpPost, AllowAnonymous]
    [Route("home/dlog/put")]
    public async Task<ActionResult> Put()
    {
        try
        {
            string body;
            using (var sr = new StreamReader(Request.Body)) body = await sr.ReadToEndAsync();
            if (body.Length > 200_000) return Json(new { error = "too_big" }, 413);
            var o = JObject.Parse(body);
            string ip = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";
            int n = DLogStore.Put((string)o["dev"], (string)o["ver"], ip, (long?)o["ct"] ?? 0, o["rows"] as JArray ?? new JArray());
            if (n < 0) return Json(new { ok = false, closed = true, until = DLogStore.Until });
            return Json(new { ok = true, accepted = n, until = DLogStore.Until });
        }
        catch (Exception ex) { return Json(new { ok = false, error = ex.GetType().Name }, 400); }
    }

    // Разбор: сводка (по умолчанию текстом), ?format=json, ?days=N; хвост устройства: ?dev=…&tail=N
    [HttpGet, AllowAnonymous]
    [Route("home/dlog/summary")]
    public ActionResult Summary(int days = 7, string format = null, string dev = null, int tail = 0)
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        if (!string.IsNullOrEmpty(dev) && tail > 0) return Json(DLogStore.Tail(dev, tail));
        if (format == "json") return Json(DLogStore.Summary(days));
        return ContentTo(DLogStore.SummaryText(days), "text/plain; charset=utf-8");
    }

    // Включить/продлить/остановить: ?days=7 (от сейчас), ?until=<мс>, ?on=0
    [HttpGet, AllowAnonymous]
    [Route("home/dlog/set")]
    public ActionResult SetCfg(int days = 0, long until = 0, string on = null)
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        if (days > 0) HomeDb.Set("dlog_until", (HomeDb.Now() + days * 86400000L).ToString());
        if (until > 0) HomeDb.Set("dlog_until", until.ToString());
        if (on != null) HomeDb.Set("dlog_on", on == "0" ? "0" : "1");
        return Json(new { on = DLogStore.On, until = DLogStore.Until, now = HomeDb.Now() });
    }
}
