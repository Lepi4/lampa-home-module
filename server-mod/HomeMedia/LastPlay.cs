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
        rec["id"] = n.Id; rec["event"] = n.Event; rec["session"] = n.Session; rec["s"] = n.S;
        rec["done"] = n.Op == "completed" || (n.D > 0 && KpUnified.IsDone(n.T, n.D, n.P));
        if (rec["pl"] == null) rec["plKeep"] = true;                      // список серий торрента не пришёл — остаётся прежний
        string src = (string)rec["src"] ?? ""; if (src.Length == 0 || src.Length > 20) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(n.C ?? "", "^[0-9A-Za-z_.-]{1,60}$")) return;
        Set(n.C, rec, accepted: true);
    }

    // true — запись применена; false — отвергнута (запоздала относительно сброса просмотра или ручного удаления) либо старше уже сохранённой
    static string EventOf(JObject o) => o?["event"]?.Type == JTokenType.String ? (string)o["event"] : null;

    public static JObject GetForRead(string card)
    {
        return HomeDb.With(c => {
            var rec=Get(card);
            if(rec==null)return null;
            var row=TlV2.Get(c,(string)rec["hash"]??"");
            if(row==null || (row.C!=card && TlV2.CardFromIdentity(row.Id)!=card))return rec;
            if(TlV2.CmpPair(row.U,row.Event,(long?)rec["at"]??0,EventOf(rec))<0)return rec;
            if(row.Op=="reset")return null;
            rec["pos"]=row.T;rec["dur"]=row.D;rec["at"]=row.U;
            rec["event"]=row.Event;rec["id"]=row.Id;rec["s"]=row.S;
            rec["done"]=row.Op=="completed" || (row.D>0 && KpUnified.IsDone(row.T,row.D,row.P));
            return rec;
        });
    }

    static bool SameTransport(JObject a, JObject b)
    {
        if ((string)a["src"] != "url" || (string)b["src"] != "url") return false;
        string ah = (string)a["infohash"], bh = (string)b["infohash"];
        if (!string.IsNullOrEmpty(ah) || !string.IsNullOrEmpty(bh))
            return !string.IsNullOrEmpty(ah) && string.Equals(ah, bh, StringComparison.OrdinalIgnoreCase);
        string au = (string)a["url"];
        return !string.IsNullOrEmpty(au) && au == (string)b["url"];
    }

    public static bool Set(string card, JObject input, bool accepted = false)
    {
        Ensure();
        var rec = (JObject)input.DeepClone();
        if (!accepted) { rec.Remove("event"); rec.Remove("session"); rec.Remove("s"); rec.Remove("id"); }
        long now = HomeDb.Now(), at = (long?)rec["at"] ?? now;
        if (!accepted && at > now + 60_000) at = now;
        rec["at"] = at;
        return HomeDb.With(c =>
        {
            var old = Get(card);
            if (old != null && TlV2.CmpPair(at, EventOf(rec), (long?)old["at"] ?? 0, EventOf(old)) < 0) return false;
            string hash = (string)rec["hash"];
            var prog = string.IsNullOrEmpty(hash) ? null : TlV2.Get(c, hash);
            if (prog?.Op == "reset" && TlV2.CmpPair(at, EventOf(rec), prog.U, prog.Event) <= 0) return false;
            var gone = HomeDb.Query(c, "SELECT at FROM lastplay_gone WHERE profile_id=@profile AND card=@k", ("@k", card));
            if (gone.Count > 0 && at <= Convert.ToInt64(gone[0][0])) return false;
            if ((bool?)rec["plKeep"] == true && old != null && (string)old["hash"] == hash && SameTransport(old,rec) && old["pl"] is JArray) {
                rec["pl"] = old["pl"].DeepClone();
                rec["idx"] = old["idx"]?.DeepClone();
            }
            rec.Remove("plKeep");
            return HomeDb.Exec(c, @"INSERT INTO lastplay(profile_id,card,j,at) VALUES(@profile,@k,@j,@a)
                ON CONFLICT(profile_id,card) DO UPDATE SET j=@j,at=@a",
                ("@k", card), ("@j", rec.ToString(Formatting.None)), ("@a", at)) > 0;
        });
    }

    public static int ResetAccepted(TlV2.Row n)
    {
        Ensure();
        return HomeDb.With(c => {
            var rows = HomeDb.Query(c,"SELECT card,j FROM lastplay WHERE profile_id=@profile AND json_extract(j,'$.hash')=@h",("@h",n.H));
            int deleted = 0;
            foreach(var row in rows) {
                var old = JObject.Parse((string)row[1]);
                if(TlV2.CmpPair(n.U,n.Event,(long?)old["at"]??0,EventOf(old))>=0)
                    deleted += HomeDb.Exec(c,"DELETE FROM lastplay WHERE profile_id=@profile AND card=@k",("@k",row[0]));
            }
            return deleted;
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
    public async Task<ActionResult> GetOne(string card = null, string sbx = null)
    {
        if (string.IsNullOrEmpty(card) || !keyRx.IsMatch(card)) return Json(new { }, 400);
        if (!string.IsNullOrEmpty(sbx) && !Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return Json(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        var rec = LastPlayStore.GetForRead(card);
        if (rec != null && card.EndsWith("_tv", StringComparison.Ordinal) && !ReleasePolicy.AllowSubtitlesOnly)
        {
            int s = (int?)rec["season"] ?? 0;
            int e = (int?)rec["ep"] ?? 0;
            long tm = long.TryParse(card.Split('_')[0], out var id) ? id : 0;
            string title = (string)rec["title"] ?? "";
            if (s > 0 && e > 0 && tm > 0)
            {
                bool? ru = ReleasePolicy.EpisodeRu("tv", tm, s, e);
                if (ru == null) _ = ReleaseIndexer.Ensure("tv", tm, title);
                else if (ru == false) rec["no_ru_audio"] = true;
            }
        }
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
