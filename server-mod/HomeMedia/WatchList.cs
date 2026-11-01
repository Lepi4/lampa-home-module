using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HomeMedia;

// «Я смотрю» для любого тайтла (в том числе без kino.pub): список на сервере, общий для всех клиентов.
// Для сериалов, которые есть в Кинопаб, состояние ведётся прямо в kino.pub (watching/togglewatchlist),
// поэтому «Я смотрю» в приложении и в самом kino.pub — один и тот же список.
public static class LocalWatchlist
{
    public static JArray Load()
        => Profiles.Enabled ? ProfileLibrary.My() : HomeDb.With(c =>
        {
            var arr = new JArray();
            foreach (var r in HomeDb.Query(c, "SELECT media,tmdb,at FROM watchlist WHERE profile_id=@profile"))
                arr.Add(new JObject { ["media"] = (string)r[0], ["tmdb"] = Convert.ToInt64(r[1]), ["at"] = Convert.ToInt64(r[2]) });
            return arr;
        });

    public static bool Has(string media, long tmdb)
        => Profiles.Enabled ? ProfileLibrary.Has(media, tmdb) : HomeDb.With(c => HomeDb.Query(c, "SELECT 1 FROM watchlist WHERE profile_id=@profile AND media=@m AND tmdb=@t", ("@m", media), ("@t", tmdb)).Count > 0);

    public static void Set(string media, long tmdb, bool on)
        => HomeDb.With(c =>
        {
            if (Profiles.Enabled) { ProfileLibrary.Set(media, tmdb, on); return; }
            if (on)
                HomeDb.Exec(c, "INSERT INTO watchlist(profile_id,media,tmdb,at) VALUES(@profile,@m,@t,@a) ON CONFLICT(profile_id,media,tmdb) DO UPDATE SET at=@a",
                    ("@m", media), ("@t", tmdb), ("@a", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            else
                HomeDb.Exec(c, "DELETE FROM watchlist WHERE profile_id=@profile AND media=@m AND tmdb=@t", ("@m", media), ("@t", tmdb));
        });
}

public class WatchListController : BaseController
{
    // «Мои фильмы»: фильмы, которые собираемся посмотреть. Фильм остаётся в списке и пока его смотрят (он при этом есть и в «Продолжить
    // просмотр»); уходит только когда досмотрен (запись удаляется, чтобы не вернулась) или когда его убрали вручную.
    public static async Task<JArray> MyMovies()
    {
        var progress = await KpUnified.MovieWatched();
        var res = new JArray();
        var list = LocalWatchlist.Load().Children<JObject>()
            .Where(x => (string)x["media"] == "movie").OrderByDescending(x => (long?)x["at"] ?? 0).ToList();
        foreach (var w in list)
        {
            long tmdb = (long)w["tmdb"];
            if (progress.Contains(tmdb)) { if (!Profiles.Enabled) LocalWatchlist.Set("movie", tmdb, false); continue; }
            var c = await KpMap.TmdbCard("movie", tmdb);
            if (c == null) continue;
            long kp = KpMap.CachedKp("movie", tmdb);
            if (kp > 0) c["kp_id"] = kp;
            c["watch_manual"] = true;
            res.Add(c);
        }
        return res;
    }

    // Сериал добавили у нас, потому что в kino.pub его не было, а теперь он там появился: подписываемся в kino.pub
    // («Я смотрю» там) и убираем из своего списка. Проверка сопоставления с kino.pub кэшируется, лишних запросов нет.
    public static async Task<int> PromoteLocal(string prof)
    {
        if (Profiles.Enabled) return 0;
        string tok = await KpStore.Token(prof);
        if (tok == null) return 0;
        int n = 0;
        foreach (var w in LocalWatchlist.Load().Children<JObject>().Where(x => (string)x["media"] == "tv").ToList())
        {
            long tmdb = (long)w["tmdb"];
            long kp = await KpMap.LookupKp("tv", tmdb, prof);
            if (kp <= 0) continue;
            var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/{kp}", tok);
            if (code != 200) continue;
            bool subscribed = (bool?)JObject.Parse(body)["item"]?["subscribed"] ?? false;
            if (!subscribed)
            {
                var (c2, _) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/watching/togglewatchlist?id={kp}", tok);
                if (c2 != 200) continue;
            }
            LocalWatchlist.Set("tv", tmdb, false);
            n++;
        }
        return n;
    }

    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    string Prof() => KpStore.ResolveProfile(Request.Headers["X-KP-Profile"].FirstOrDefault() ?? Request.Query["profile"].FirstOrDefault());

    // Состояние: включено ли «Я смотрю» и где оно хранится
    async Task<(bool on, long kp, bool? subscribed, string tok)> StateOf(string media, long tmdb)
    {
        if (Profiles.Enabled) return (ProfileLibrary.Has(media, tmdb), KpMap.CachedKp(media, tmdb), null, null);
        string tok = await KpStore.Token(Prof());
        long kp = tok != null ? await KpMap.LookupKp(media, tmdb, Prof()) : 0;
        bool? subscribed = null;
        if (kp > 0 && media == "tv")
        {
            var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/{kp}", tok);
            if (code == 200) subscribed = (bool?)JObject.Parse(body)["item"]?["subscribed"];
        }
        bool on = (subscribed ?? false) || LocalWatchlist.Has(media, tmdb);
        return (on, kp, subscribed, tok);
    }

    // скрыть тайтл из «Продолжить просмотр» (вернётся, если его начнут смотреть снова)
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("kp/continue/hide")]
    public ActionResult HideContinue(string type, long tmdb)
    {
        if ((type != "movie" && type != "tv") || tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        KpUnified.Hide(tmdb + "_" + type);
        return Json(new { ok = true });
    }

    // убрать из «Я смотрю» совсем: свой список, подписка kino.pub и автоматическое попадание по просмотру
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("kp/watching/remove")]
    public async Task<ActionResult> RemoveWatching(string type, long tmdb)
    {
        if ((type != "movie" && type != "tv") || tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        var s = await StateOf(type, tmdb);
        if (s.kp > 0 && s.subscribed == true)
        {
            var (kc, _) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/watching/togglewatchlist?id={s.kp}", s.tok);
            if (kc != 200) return Json(new { error = "kino_failed" }, 502);        // не заявляем об удалении, если Кинопаб его не выполнил
        }
        LocalWatchlist.Set(type, tmdb, false);
        KpUnified.Hide("w_" + tmdb + "_" + type);
        return Json(new { ok = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/watchlist/state")]
    public async Task<ActionResult> State(string type, long tmdb)
    {
        if (type != "movie" && type != "tv") return Json(new { error = "bad_type" }, 400);
        var s = await StateOf(type, tmdb);
        bool on = s.on || (type == "tv" && KpUnified.TrackedVisible(tmdb));      // карточка в ряду из-за отслеживания тоже считается «добавленной»
        var card=await KpMap.TmdbCard(type,tmdb);
        return Json(new { on, kind=CardKind.Kind(card), kino = s.subscribed.HasValue, kp = s.kp > 0 ? (long?)s.kp : null });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("kp/watchlist/toggle")]
    public async Task<ActionResult> Toggle(string type, long tmdb, string on = null)
    {
        if (type != "movie" && type != "tv") return Json(new { error = "bad_type" }, 400);
        // запросы одного тайтла идут по очереди (замок не БД, а только этого тайтла: во время сети SQLite не удерживается)
        var gate = titleLocks.GetOrAdd(type + tmdb, _ => new System.Threading.SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await ToggleLocked(type, tmdb, on); }
        finally { gate.Release(); }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> titleLocks = new();

    // on=1|0 — желаемое состояние, которое клиент вычислил при первом нажатии: повтор при потерянном ответе не инвертирует уже применённое действие.
    // Без параметра — прежнее поведение (переключение).
    async Task<ActionResult> ToggleLocked(string type, long tmdb, string onParam)
    {
        var s = await StateOf(type, tmdb);
        bool on = s.on || (type == "tv" && KpUnified.TrackedVisible(tmdb));
        bool want = onParam == "1" || onParam == "true" ? true : (onParam == "0" || onParam == "false" ? false : !on);
        if (want) KpUnified.Unhide("w_" + tmdb + "_" + type);   // добавляют заново — снимаем прежнее «убрано»
        else KpUnified.Hide("w_" + tmdb + "_" + type);         // убрали — и из автоматического отбора по просмотру тоже, иначе карточка вернулась бы сама

        if (s.kp > 0 && s.subscribed.HasValue)
        {
            // сериал есть в Кинопаб — единственный источник правды там
            if (s.subscribed.Value != want)
            {
                var (kc, _) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/watching/togglewatchlist?id={s.kp}", s.tok);
                if (kc != 200) { if (want) KpUnified.Hide("w_" + tmdb + "_" + type); else KpUnified.Unhide("w_" + tmdb + "_" + type); return Json(new { error = "kino_failed" }, 502); }
            }
            LocalWatchlist.Set(type, tmdb, false);
            return Json(new { on = want, kino = true });
        }

        LocalWatchlist.Set(type, tmdb, want);
        return Json(new { on = want, kino = false });
    }
}
