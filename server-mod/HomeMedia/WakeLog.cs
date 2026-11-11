using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

// Журнал пробуждения оболочки. Одна строка на попытку: сон, ответ страницы, «ТВ», launchStart, плеер.
// Адреса и токены сюда не пишутся. Пока player = 0, попытка не закрыта.
public static class WakeLog
{
    static bool ready;
    static readonly object gate = new();
    static readonly Regex AttemptRx = new(@"^[a-z0-9]{8,16}$", RegexOptions.Compiled);
    static readonly Regex DevRx = new(@"^[A-Za-z0-9_-]{1,24}$", RegexOptions.Compiled);

    static void Ensure()
    {
        if (ready) return;
        HomeDb.With(c => HomeDb.Exec(c, @"CREATE TABLE IF NOT EXISTS shell_wake (
            attempt TEXT PRIMARY KEY, dev TEXT, at INTEGER, sleep_s INTEGER,
            ack_ms INTEGER, shell INTEGER, tv INTEGER, launch INTEGER, player INTEGER)"));
        ready = true;
    }

    public static bool Put(JObject o)
    {
        string attempt = (string)o["attempt"] ?? "";
        string dev = (string)o["dev"] ?? "";
        string stage = (string)o["stage"] ?? "";
        if (!AttemptRx.IsMatch(attempt) || !DevRx.IsMatch(dev)) return false;
        if (stage != "reload" && stage != "ack" && stage != "tv" && stage != "launch" && stage != "player") return false;
        long sleep = Math.Clamp((long?)o["sleep_s"] ?? 0, 0, 86400 * 14);
        long ack = (long?)o["ack_ms"] ?? -1;
        if (ack < -1 || ack > 60000) ack = -1;
        int shell = (int?)o["shell"] == 1 ? 1 : 0;
        Ensure();
        lock (gate)
        {
            HomeDb.With(c =>
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                HomeDb.Exec(c, "INSERT OR IGNORE INTO shell_wake(attempt,dev,at,sleep_s,ack_ms,shell,tv,launch,player) VALUES(@a,@d,@t,0,-1,0,0,0,0)",
                    ("@a", attempt), ("@d", dev), ("@t", now));
                if (stage == "reload")
                    HomeDb.Exec(c, "UPDATE shell_wake SET sleep_s=@s, at=@t WHERE attempt=@a", ("@s", sleep), ("@t", now), ("@a", attempt));
                else if (stage == "ack")
                    HomeDb.Exec(c, "UPDATE shell_wake SET ack_ms=@m, shell=@h WHERE attempt=@a", ("@m", ack), ("@h", shell), ("@a", attempt));
                else
                    HomeDb.Exec(c, "UPDATE shell_wake SET " + stage + "=1 WHERE attempt=@a", ("@a", attempt));
                HomeDb.Exec(c, "DELETE FROM shell_wake WHERE attempt NOT IN (SELECT attempt FROM shell_wake ORDER BY at DESC LIMIT 300)");
            });
        }
        return true;
    }

    public static JArray Recent()
    {
        Ensure();
        var a = new JArray();
        HomeDb.With(c =>
        {
            foreach (var r in HomeDb.Query(c, "SELECT attempt,dev,at,sleep_s,ack_ms,shell,tv,launch,player FROM shell_wake ORDER BY at DESC LIMIT 20"))
                a.Add(new JObject
                {
                    ["attempt"] = r[0] as string,
                    ["dev"] = r[1] as string,
                    ["at"] = Convert.ToInt64(r[2]),
                    ["sleep_s"] = Convert.ToInt64(r[3]),
                    ["ack_ms"] = Convert.ToInt64(r[4]),
                    ["shell"] = Convert.ToInt64(r[5]),
                    ["tv"] = Convert.ToInt64(r[6]),
                    ["launch"] = Convert.ToInt64(r[7]),
                    ["player"] = Convert.ToInt64(r[8])
                });
        });
        return a;
    }
}

public class WakeLogController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    [HttpPost, AllowAnonymous]
    [Route("home/shell/wake")]
    public async Task<ActionResult> Put()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        string body;
        using (var rd = new StreamReader(Request.Body, Encoding.UTF8)) body = await rd.ReadToEndAsync();
        if (body.Length > 800) return Json(new { error = "bad_body" }, 400);
        JObject o;
        try { o = JObject.Parse(body); } catch { return Json(new { error = "bad_body" }, 400); }
        if (!WakeLog.Put(o)) return Json(new { error = "bad_body" }, 400);
        return Json(new { ok = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("home/shell/wake")]
    public ActionResult List()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        return Json(new { rows = WakeLog.Recent() });
    }
}
