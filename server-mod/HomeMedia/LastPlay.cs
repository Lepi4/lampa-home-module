using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

// «Продолжить» в карточке: на сервере по каждому тайтлу хранится последнее воспроизведение — источник, серия, позиция, признак «досмотрено».
// Запись общая для всех устройств (начали на ТОКС3, продолжили на Shield). Побеждает более свежая запись.
public static class LastPlayStore
{
    static bool ready;

    static void Ensure()
    {
        if (ready) return;
        HomeDb.With(c =>
        {
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS lastplay (card TEXT PRIMARY KEY, j TEXT, at INTEGER)");
            // время ручного удаления записи: запоздавшая запись «старше» удаления не должна воскресить её
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS lastplay_gone (card TEXT PRIMARY KEY, at INTEGER)");
        });
        ready = true;
    }

    public static JObject Get(string card)
    {
        Ensure();
        return HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT j FROM lastplay WHERE profile_id=@profile AND card=@k", ("@k", card));
            if (q.Count == 0) return null;
            try { return JObject.Parse((string)q[0][0]); } catch { return null; }
        });
    }

    // Запись «Продолжить» из строки просмотра (resume): время действия, позиция и признак «досмотрено» берутся из самой строки, поэтому
    // lastplay и timeline не расходятся. Вызывается внутри транзакции приёма события; применяется только к принятому (не устаревшему) событию.
    public static void FromResume(TlV2.Row n)
    {
        var rec = (JObject)n.Resume.DeepClone();
        rec["hash"] = n.H; rec["pos"] = n.T; rec["dur"] = n.D; rec["at"] = n.U;
        rec["done"] = n.Op == "completed" || KpUnified.IsDone(n.T, n.D, n.P);
        if (rec["pl"] == null) rec["plKeep"] = true;                      // список серий торрента не пришёл — остаётся прежний
        string src = (string)rec["src"] ?? ""; if (src.Length == 0 || src.Length > 20) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(n.C ?? "", "^[0-9A-Za-z_.-]{1,60}$")) return;
        Set(n.C, rec);
    }

    // true — запись применена; false — отвергнута (запоздала относительно сброса просмотра или ручного удаления) либо старше уже сохранённой
    public static bool Set(string card, JObject rec)
    {
        Ensure();
        // список серий торрента клиент шлёт один раз за серию; дальше присылает plKeep — подставляем сохранённый
        if ((bool?)rec["plKeep"] == true)
        {
            var old = Get(card);
            if (old != null && (string)old["hash"] == (string)rec["hash"] && old["pl"] != null) { rec["pl"] = old["pl"]; rec["idx"] = old["idx"]; }
        }
        rec.Remove("plKeep");
        // время клиента принимаем, только если часы устройства идут близко к серверным: сбой часов не должен надолго заблокировать более поздние действия с другого устройства
        long now = HomeDb.Now();
        long at = (long?)rec["at"] ?? now;
        if (at > now + 60_000) at = now;                  // только время «из будущего» считаем сбоем часов; запоздавший старый просмотр сохраняет свою дату и не побеждает новый
        rec["at"] = at;
        string hash = (string)rec["hash"];
        return HomeDb.With(c =>
        {
            // после удаления строки сравнивать не с чем: сверяем с авторитетным сбросом в timeline и с временем ручного удаления
            var prog = string.IsNullOrEmpty(hash) ? null : TlV2.Get(c, hash);
            if (prog != null && prog.Op == "reset" && at <= prog.U) return false;
            var gone = HomeDb.Query(c, "SELECT at FROM lastplay_gone WHERE profile_id=@profile AND card=@k", ("@k", card));
            if (gone.Count > 0 && at <= Convert.ToInt64(gone[0][0])) return false;
            return HomeDb.Exec(c, @"INSERT INTO lastplay(profile_id,card,j,at) VALUES(@profile,@k,@j,@a)
                         ON CONFLICT(profile_id,card) DO UPDATE SET j=@j, at=@a WHERE excluded.at >= lastplay.at",
                ("@k", card), ("@j", rec.ToString(Formatting.None)), ("@a", at)) > 0;
        });
    }

    public static void Delete(string card, bool remember = true)
    {
        Ensure();
        HomeDb.With(c =>
        {
            HomeDb.Exec(c, "DELETE FROM lastplay WHERE profile_id=@profile AND card=@k", ("@k", card));
            if (remember) HomeDb.Exec(c, "INSERT INTO lastplay_gone(profile_id,card,at) VALUES(@profile,@k,@a) ON CONFLICT(profile_id,card) DO UPDATE SET at=@a", ("@k", card), ("@a", HomeDb.Now()));
        });
    }

    // Сброс просмотра серии: убираем запись «Продолжить», если она стоит на этой серии (по хешу) и помечена «досмотрено».
    // Не зависит от памяти страницы: сервер сам сопоставляет хеш с сохранённой записью.
    // u — время действия сброса (мс): запись «Продолжить» новее этого времени не трогаем (запоздавшая команда не удаляет свежий просмотр). 0 — без условия.
    public static int ResetByHash(string hash, bool onlyDone = true, long u = 0)
    {
        Ensure();
        return HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM lastplay WHERE profile_id=@profile AND json_extract(j,'$.hash')=@h" + (onlyDone ? " AND json_extract(j,'$.done')=1" : "") + (u > 0 ? " AND at<=@u" : ""),
            ("@h", hash), ("@u", u)));
    }

    public static long Count()
    {
        Ensure();
        return HomeDb.With(c => Convert.ToInt64(HomeDb.Query(c, "SELECT COUNT(*) FROM lastplay WHERE profile_id=@profile")[0][0]));
    }
}

public class LastPlayController : BaseController
{
    static readonly Regex keyRx = new("^[0-9A-Za-z_.-]{1,60}$");

    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    [HttpGet, AllowAnonymous]
    [Route("continue.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "continue.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("home/lastplay/get")]
    public ActionResult GetOne(string card = null, string sbx = null)
    {
        if (string.IsNullOrEmpty(card) || !keyRx.IsMatch(card)) return Json(new { }, 400);
        if (!string.IsNullOrEmpty(sbx) && !Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return Json(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        var rec = LastPlayStore.Get(card);
        // карточку открыли: заранее собираем варианты («лучшее качество», озвучки), к моменту нажатия кнопки они уже готовы
        try
        {
            var pp = card.Split('_');
            if (pp.Length == 2 && long.TryParse(pp[0], out long tm) && (pp[1] == "tv" || pp[1] == "movie")) AltsController.Prefetch(pp[1], tm, KpStore.ResolveProfile(Request.Query["profile"].FirstOrDefault()));
        }
        catch { }
        return Json(rec == null ? new JObject() : rec);
    }

    [HttpPost, AllowAnonymous]
    [Route("home/lastplay/set")]
    public async Task<ActionResult> SetOne(string card = null, string sbx = null)
    {
        if (string.IsNullOrEmpty(card) || !keyRx.IsMatch(card)) return Json(new { error = "bad_card" }, 400);
        if (!string.IsNullOrEmpty(sbx) && !Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return Json(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        string body;
        using (var sr = new StreamReader(Request.Body)) body = await sr.ReadToEndAsync();
        if (body.Length > 40000) return Json(new { error = "too_big" }, 413);
        JObject rec;
        try { rec = JObject.Parse(body); } catch { return Json(new { error = "bad_json" }, 400); }
        string src = (string)rec["src"] ?? "";
        if (src.Length == 0 || src.Length > 20) return Json(new { error = "bad_src" }, 400);
        bool applied = LastPlayStore.Set(card, rec);
        return Json(new { ok = true, applied });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/lastplay/reset")]
    public ActionResult ResetHash(string hash = null, long u = 0, string sbx = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return Json(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        if (string.IsNullOrEmpty(hash) || !keyRx.IsMatch(hash)) return Json(new { error = "bad_hash" }, 400);
        int n = LastPlayStore.ResetByHash(hash, true, u);
        return Json(new { ok = true, removed = n });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/lastplay/delete")]
    public ActionResult Delete(string card = null, string sbx = null)
    {
        if (string.IsNullOrEmpty(card) || !keyRx.IsMatch(card)) return Json(new { error = "bad_card" }, 400);
        if (!string.IsNullOrEmpty(sbx) && !Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return Json(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        LastPlayStore.Delete(card);
        return Json(new { ok = true });
    }
}
