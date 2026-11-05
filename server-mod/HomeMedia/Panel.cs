using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

// Веб-панель сервера и настройки устройств на сервере: профиль каждого устройства (качество, плеер, заставка, обои…) хранится в
// единой базе и применяется на устройстве при каждом запуске; менять можно и на самом устройстве (оно отправляет изменение на сервер).
// Защита изменяющих запросов от чужих сайтов: адрес сервера в домашней сети известен любому сайту, открытому в браузере в этой сети,
// и простая GET-ссылка могла бы удалить профиль или очистить данные. Браузер не позволяет сайту подделать заголовок User-Agent,
// поэтому наши приложения (Lampa — «lampa_client», оболочка — «McShell») проходят, а запросы со сторонних страниц — нет.
public static class Guard
{
    public static bool Ok(Microsoft.AspNetCore.Http.HttpRequest r)
    {
        try
        {
            string ua = r.Headers["User-Agent"].ToString();
            bool ours = ua.Contains("lampa_client") || ua.Contains("McShell");
            string site = r.Headers["Sec-Fetch-Site"].ToString();
            string origin = r.Headers["Origin"].ToString();
            bool foreign = site == "cross-site" || site == "same-site";
            if (origin.Length > 0 && origin != "null")
            {
                var u = new Uri(origin);
                if (!string.Equals(u.Authority, r.Host.Value, StringComparison.OrdinalIgnoreCase)) foreign = true;
            }
            if (foreign && !ours) return false;
            var ip = r.HttpContext.Connection.RemoteIpAddress;
            if (ip == null) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            if (b.Length == 4) return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            return b[0] == 0xfe || b[0] == 0xfc || b[0] == 0xfd;      // link-local и ULA
        }
        catch { return false; }
    }

    public static bool AdminWrite(HttpRequest r, HttpContext ctx)
        => Ok(r) && (!Profiles.Enabled || Profiles.FromRequest(ctx)?.Admin == true);
}

public static class DeviceStore
{
    static readonly Regex idRx = new("^[0-9A-Za-z_-]{3,40}$");
    // допустимые настройки устройства: ключ → (проверка значения)
    public static readonly string[] Keys = { "maxh", "saver", "wall", "hidel", "auto", "engine", "buf", "font", "alpha", "afr", "kpprof", "viewer", "mc_yt", "mc_wr", "mc_ym", "mc_src", "ytprof", "mc_auto", "mc_info", "vodbuf", "mc_top", "mc_topnew", "mc_toprate", "version" };

    public static bool ValidId(string id) => !string.IsNullOrEmpty(id) && idRx.IsMatch(id);

    public static JObject Hello(string dev, string name, string init)
    {
        return HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT cfg FROM devices WHERE id=@i", ("@i", dev));
            JObject cfg = q.Count > 0 ? JObject.Parse((string)q[0][0] ?? "{}") : new JObject();
            if (q.Count == 0 && !string.IsNullOrEmpty(init))
            {
                // новое устройство: запоминаем его текущие настройки, чтобы панель показывала реальное состояние
                try
                {
                    foreach (var p in JObject.Parse(init).Properties())
                        if (Keys.Contains(p.Name) && p.Value.Type != JTokenType.Null) cfg[p.Name] = p.Value.ToString();
                }
                catch { }
            }
            else if (q.Count > 0 && !string.IsNullOrEmpty(init))
            {
                try
                {
                    var initObj = JObject.Parse(init);
                    if (initObj["version"] != null) cfg["version"] = initObj["version"].ToString();
                }
                catch { }
            }
            HomeDb.Exec(c, @"INSERT INTO devices(id,name,seen,cfg) VALUES(@i,@n,@s,@c)
                             ON CONFLICT(id) DO UPDATE SET name=CASE WHEN @n<>'' THEN @n ELSE name END, seen=@s",
                ("@i", dev), ("@n", name ?? ""), ("@s", HomeDb.Now()), ("@c", cfg.ToString(Formatting.None)));
            return cfg;
        });
    }

    public static void Set(string dev, string key, string value)
    {
        if (!ValidId(dev) || !Keys.Contains(key)) return;
        HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT cfg FROM devices WHERE id=@i", ("@i", dev));
            var cfg = q.Count > 0 ? JObject.Parse((string)q[0][0] ?? "{}") : new JObject();
            if (value == null) cfg.Remove(key); else cfg[key] = value.Length > 200 ? value.Substring(0, 200) : value;
            HomeDb.Exec(c, @"INSERT INTO devices(id,name,seen,cfg) VALUES(@i,'',@s,@c)
                             ON CONFLICT(id) DO UPDATE SET cfg=@c",
                ("@i", dev), ("@s", HomeDb.Now()), ("@c", cfg.ToString(Formatting.None)));
        });
    }

    // Keep the administrator's label separate from the model reported by hello.
    // An empty label restores the automatic device name.
    public static bool Rename(string dev, string name)
    {
        return HomeDb.With(c =>
        {
            var rows = HomeDb.Query(c, "SELECT cfg FROM devices WHERE id=@i", ("@i", dev));
            if (rows.Count == 0) return false;
            var cfg = JObject.Parse((string)rows[0][0] ?? "{}");
            if (string.IsNullOrEmpty(name)) cfg.Remove("display_name"); else cfg["display_name"] = name;
            HomeDb.Exec(c, "UPDATE devices SET cfg=@c WHERE id=@i", ("@i", dev), ("@c", cfg.ToString(Formatting.None)));
            return true;
        });
    }

    public static JArray List()
        => HomeDb.With(c =>
        {
            var arr = new JArray();
            foreach (var r in HomeDb.Query(c, "SELECT id,name,seen,cfg FROM devices ORDER BY seen DESC"))
            {
                var cfg = JObject.Parse((string)r[3] ?? "{}");
                string model = r[1] as string ?? "", label = (string)cfg["display_name"];
                arr.Add(new JObject { ["id"] = (string)r[0], ["name"] = string.IsNullOrEmpty(label) ? model : label,
                    ["model"] = model, ["custom_name"] = label ?? "", ["seen"] = Convert.ToInt64(r[2]), ["version"] = (string)cfg["version"], ["cfg"] = cfg });
            }
            return arr;
        });
}

[ProfileResult]
public class PanelController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    static readonly string wallDir = Path.Combine(Directory.GetCurrentDirectory(), "database", "wallpapers");

    // ----- сама страница -----
    [HttpGet, AllowAnonymous]
    [Route("home")]
    public ActionResult Page()
    {
        // Панель открывают из браузера без сеанса приставки. Если у основного профиля нет ПИН, сеанс выдаётся здесь:
        // иначе все /home/* отвечают 401 и страница остаётся пустой. С заданным ПИН вход идёт через /profiles.
        // A browser has no Lampa device session. Do not rely on AsyncLocal
        // state here: it is empty on a fresh request even when another request
        // handled the same page. A missing cookie is the reliable boundary.
        if (Profiles.Enabled)
        {
            var current = Profiles.FromRequest(HttpContext);
            if (current == null)
            {
                var admin = Profiles.Get(Profiles.DefaultId);
                if (admin != null && (bool)admin["is_admin"] && !(bool)admin["is_child"] && !(bool)admin["archived"] && string.IsNullOrEmpty((string)admin["pin_hash"]) && Guard.Ok(Request))
                {
                    string device = Profiles.BrowserDevice(HttpContext);
                    var session = Profiles.CreateSession((string)admin["id"], device, Guid.NewGuid().ToString("N"));
                    Profiles.SetSessionCookie(HttpContext, session.token);
                }
            }
        }
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string html = System.IO.File.ReadAllText(Path.Combine(dir, "panel.html")).Replace("{localhost}", host);
        Response.Headers["Cache-Control"] = "no-store";
        return ContentTo(html, "text/html; charset=utf-8");
    }

    // A browser opening the panel has no Lampa bridge and therefore cannot
    // bootstrap through ProfileRuntime. Provide the same local-network login
    // as an explicit request so cookie handling is independent of page caching
    // and AsyncLocal request state.
    [HttpGet, AllowAnonymous]
    [Route("home/admin/session")]
    public ActionResult BrowserSession()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        var current = Profiles.FromRequest(HttpContext);
        if (current != null) return current.Admin ? Json(new { ok = true, profile = current.Id }) : Json(new { error = "session_required" }, 401);
        var admin = Profiles.Get(Profiles.DefaultId);
        if (admin == null || !(bool)admin["is_admin"] || (bool)admin["is_child"] || (bool)admin["archived"] || !string.IsNullOrEmpty((string)admin["pin_hash"]))
            return Json(new { error = "session_required" }, 401);
        string device = Profiles.BrowserDevice(HttpContext);
        var session = Profiles.CreateSession((string)admin["id"], device, Guid.NewGuid().ToString("N"));
        Profiles.SetSessionCookie(HttpContext, session.token);
        return Json(new { ok = true, profile = (string)admin["id"] });
    }

    // ----- состояние -----
    [HttpGet, AllowAnonymous]
    [Route("home/stats")]
    public ActionResult Stats()
    {
        var (tl, bm, wl, hid, au) = HomeDb.Counts();
        long dbSize = 0;
        try { dbSize = new FileInfo(Path.Combine(Directory.GetCurrentDirectory(), "database", "home.db")).Length; } catch { }
        return Json(new
        {
            timeline = tl, bookmarks = bm, watchlist = wl, hidden = hid, audio = au,
            devices = DeviceStore.List().Count, db_bytes = dbSize,
            bridge_last = KpBridge.LastRun == default ? (DateTime?)null : KpBridge.LastRun, bridge_result = KpBridge.LastResult,
            server_time = DateTime.UtcNow
        });
    }

    // ----- Lampa Media Center: опознание сервера и конфигурация оболочки -----
    static string ServerId()
    {
        string id = HomeDb.Get("server_id");
        if (string.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString("N").Substring(0, 12); HomeDb.Set("server_id", id); }
        return id;
    }

    // по этому ответу приложение узнаёт сервер при автопоиске в локальной сети и получает профили воспроизведения
    [HttpGet, AllowAnonymous]
    [Route("home/whoami")]
    public ActionResult WhoAmI()
    {
        var cfg = KpStore.Load();
        var profiles = ((JObject)cfg["profiles"]).Properties().Select(pr => new
        {
            id = pr.Name,
            label = (string)pr.Value["label"],
            authorized = !string.IsNullOrEmpty((string)pr.Value["access_token"]),
            maxh = pr.Name == "uhd" ? 2160 : 1080
        });
        string name = HomeDb.Get("server_name");
        return Json(new { app = "lampa-home", name = string.IsNullOrEmpty(name) ? "Lampa Media Center" : name, id = ServerId(), version = 1, profiles });
    }

    // Страница-«посев»: оболочка загружает её скрытой рамкой, чтобы записать стартовые настройки в хранилище Lampa
    // (того же источника, что и сама Lampa) и она не показывала приветственный мастер выбора языка.
    [HttpGet, AllowAnonymous]
    [Route("mc/seed")]
    public ActionResult McSeed(string lang = "ru", string prof = "main")
    {
        lang = Regex.IsMatch(lang ?? "", "^[a-z]{2}$") ? lang : "ru";
        prof = Regex.IsMatch(prof ?? "", "^[A-Za-z0-9_-]{1,32}$") ? prof : "main";
        string html = "<!doctype html><meta charset=utf-8><script>(function(){try{" +
            "if(!localStorage.getItem('language'))localStorage.setItem('language','" + lang + "');" +
            "var k=JSON.parse(localStorage.getItem('kp_pref_v1')||'{}');if(!k.profile){k.profile='" + prof + "';localStorage.setItem('kp_pref_v1',JSON.stringify(k));}" +
            "}catch(e){}try{parent.postMessage('mc-seed-done','*')}catch(e){}})();</script>";
        return ContentTo(html, "text/html; charset=utf-8");
    }

    // поиск для оболочки: фильмы и сериалы TMDB (по-русски), две страницы
    // type: movie | tv | anime (пусто — всё); from/to — годы выхода; country: all (без Индии и Турции), ru, foreign (без Индии и Турции), any (без исключений); minr — минимальный рейтинг
    static readonly HashSet<string> IndTurLang = new() { "hi", "ta", "te", "ml", "kn", "bn", "mr", "pa", "gu", "ur", "or", "as", "tr" };
    static readonly HashSet<string> IndTurCountry = new() { "IN", "TR" };

    static bool PassFilter(JObject r, string type, int from, int to, string country, double minr)
    {
        string t = (string)r["media_type"], lang = ((string)r["original_language"] ?? "").ToLowerInvariant();
        var genres = ((JArray)r["genre_ids"] ?? new JArray()).Select(x => (int)x).ToList();
        var oc = ((JArray)r["origin_country"] ?? new JArray()).Select(x => (string)x).ToList();
        bool anime = genres.Contains(16) && lang == "ja";
        if (type == "movie" && t != "movie") return false;
        if (type == "tv" && (t != "tv" || anime)) return false;
        if (type == "anime" && !anime) return false;
        string ds = (string)r["release_date"] ?? (string)r["first_air_date"] ?? "";
        if ((from > 0 || to > 0))
        {
            if (!int.TryParse(ds.Length >= 4 ? ds.Substring(0, 4) : "", out int y)) return false;
            if (from > 0 && y < from) return false;
            if (to > 0 && y > to) return false;
        }
        if (minr > 0 && ((double?)r["vote_average"] ?? 0) < minr) return false;
        bool indtur = IndTurLang.Contains(lang) || oc.Any(IndTurCountry.Contains);
        bool ru = lang == "ru" || oc.Contains("RU") || oc.Contains("SU");
        switch (country)
        {
            case "ru": if (!ru) return false; break;
            case "foreign": if (ru || indtur) return false; break;
            case "any": break;
            default: if (indtur) return false; break;
        }
        return true;
    }

    [HttpGet, AllowAnonymous]
    [Route("home/search")]
    public async Task<ActionResult> Search(string q = null, string type = "", int from = 0, int to = 0, string country = "all", double minr = 0)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 100) return Json(new { results = new JArray() });
        var all = new JArray();
        bool filtered = !string.IsNullOrEmpty(type) || from > 0 || to > 0 || minr > 0 || country != "any";
        try
        {
            string enc = Uri.EscapeDataString(q.Trim());
            var pages = await Task.WhenAll(new[] { 1, 2, 3 }.Select(pg => KpMap.TmdbRaw($"search/multi?query={enc}&language=ru-RU&include_adult=false&page={pg}")));
            foreach (var d in pages)
                foreach (var r in ((JArray)d?["results"] ?? new JArray()).Children<JObject>())
                {
                    string t = (string)r["media_type"];
                    if (t == "person")
                    {
                        if (!string.IsNullOrEmpty(type)) continue;               // при выборе «фильмы/сериалы/аниме» людей не показываем
                        if (string.IsNullOrEmpty((string)r["profile_path"])) continue;
                        var kf = ((JArray)r["known_for"] ?? new JArray()).Children<JObject>().Select(x => (string)x["title"] ?? (string)x["name"]).Where(x => !string.IsNullOrEmpty(x)).Take(3);
                        all.Add(new JObject { ["media_type"] = "person", ["id"] = r["id"], ["name"] = r["name"], ["profile_path"] = r["profile_path"], ["department"] = r["known_for_department"], ["known_for"] = string.Join(", ", kf), ["popularity"] = r["popularity"] });
                        continue;
                    }
                    if (t != "movie" && t != "tv") continue;
                    if (string.IsNullOrEmpty((string)r["poster_path"])) continue;
                    if (filtered && !PassFilter(r, type, from, to, country, minr)) continue;
                    all.Add(r);
                }
        }
        catch (Exception ex) { return Json(new { results = all, error = ex.GetType().Name + ": " + ex.Message }); }
        return Json(new { results = all });
    }

    // Поиск по одной категории (фильмы, мультфильмы, люди, сериалы, мультсериалы, аниме, аниме-сериалы): search/movie|tv|person TMDB;
    // страницы просматриваются, пока не наберётся 24 подходящих результата (максимум 3 страницы за запрос), next_page — продолжение
    static bool CatAccept(JObject item, string category)
    {
        string media = (string)item["media_type"];
        if (category == "person") return media == "person";
        bool animation = ((JArray)item["genre_ids"] ?? new JArray()).Any(g => g.Type == JTokenType.Integer && (int)g == 16);
        bool anime = CardKind.IsAnime(item);
        return category switch
        {
            "movie" => media == "movie" && !animation,
            "cartoon_movie" => media == "movie" && animation && !anime,
            "tv" => media == "tv" && !animation,
            "cartoon_tv" => media == "tv" && animation && !anime,
            "anime_movie" => media == "movie" && anime,
            "anime_tv" => media == "tv" && anime,
            _ => false
        };
    }

    [HttpGet, AllowAnonymous]
    [Route("home/search/v2")]
    public async Task<ActionResult> SearchV2(string q = null, string category = "movie", int page = 1, int from = 0, int to = 0, string country = "all", double minr = 0)
    {
        string media = category switch
        {
            "movie" or "cartoon_movie" or "anime_movie" => "movie",
            "tv" or "cartoon_tv" or "anime_tv" => "tv",
            "person" => "person",
            _ => null
        };
        if (media == null || page < 1 || page > 500) return Json(new { error = "bad_category_or_page" }, 400);
        q = (q ?? "").Trim();
        if (q.Length < 2 || q.Length > 100) return Json(new { results = new JArray(), next_page = (int?)null });
        var results = new JArray();
        var seen = new HashSet<string>();
        int cursor = page, maxPage = 500, scanned = 0;
        try
        {
            while (scanned < 3 && cursor <= maxPage && results.Count < 24)
            {
                var data = await KpMap.TmdbRaw($"search/{media}?query={Uri.EscapeDataString(q)}&language=ru-RU&include_adult=false&page={cursor}");
                if (data == null || data["results"] is not JArray items || (bool?)data["success"] == false)
                    return Json(new { error = "metadata_unavailable" }, 502);
                maxPage = Math.Min(500, (int?)data["total_pages"] ?? cursor);
                foreach (var original in items.OfType<JObject>())
                {
                    var item = (JObject)original.DeepClone();
                    item["media_type"] = media;                                        // search/movie|tv|person его не добавляет
                    if (!CatAccept(item, category)) continue;
                    if (media == "person")
                    {
                        if (string.IsNullOrEmpty((string)item["profile_path"])) continue;
                        item["department"] = item["known_for_department"];
                        item["known_for"] = string.Join(", ", ((JArray)item["known_for"] ?? new JArray()).OfType<JObject>().Take(3).Select(x => (string)x["title"] ?? (string)x["name"]));
                    }
                    else
                    {
                        if (string.IsNullOrEmpty((string)item["poster_path"])) continue;
                        if (!PassFilter(item, "", from, to, country, minr)) continue;
                    }
                    if (!seen.Add(media + ":" + (string)item["id"])) continue;
                    results.Add(item);
                }
                cursor++; scanned++;
            }
            return Json(new { results, next_page = cursor <= maxPage ? (int?)cursor : null, scanned_pages = scanned });
        }
        catch { return Json(new { error = "metadata_unavailable" }, 502); }
    }

    // страница человека: фильмография (актёр/режиссёр/сценарист) по популярности
    [HttpGet, AllowAnonymous]
    [Route("home/person")]
    public async Task<ActionResult> Person(long id = 0)
    {
        if (id <= 0) return Json(new { error = "bad_id" }, 400);
        try
        {
            var infoT = KpMap.TmdbRaw($"person/{id}?language=ru-RU");
            var credT = KpMap.TmdbRaw($"person/{id}/combined_credits?language=ru-RU");
            var info = await infoT; var cr = await credT;
            var seen = new HashSet<string>();
            var items = new List<JObject>();
            void Add(JArray arr, string role)
            {
                foreach (var r in (arr ?? new JArray()).Children<JObject>())
                {
                    string t = (string)r["media_type"];
                    if (t != "movie" && t != "tv") continue;
                    if (string.IsNullOrEmpty((string)r["poster_path"])) continue;
                    var g = ((JArray)r["genre_ids"] ?? new JArray()).Select(x => (int)x).ToList();
                    if (g.Contains(10767) || g.Contains(10763) || g.Contains(10764)) continue;      // ток-шоу, новости, реалити с «Self»
                    if (role == "cast" && ((string)r["character"] ?? "").Contains("Self")) continue;
                    if (!seen.Add(t + (long)r["id"])) continue;
                    r["role"] = role == "cast" ? "актёр" : (string)r["job"] ?? "съёмочная группа";
                    items.Add(r);
                }
            }
            // у режиссёра сперва режиссура, у актёра сперва роли
            bool dir = (string)info["known_for_department"] == "Directing" || (string)info["known_for_department"] == "Writing";
            if (dir) { Add(((JArray)cr["crew"])?.Children<JObject>().Where(x => (string)x["job"] == "Director" || (string)x["job"] == "Writer" || (string)x["job"] == "Screenplay").Aggregate(new JArray(), (a, x) => { a.Add(x); return a; }), "crew"); Add((JArray)cr["cast"], "cast"); }
            else { Add((JArray)cr["cast"], "cast"); Add(((JArray)cr["crew"])?.Children<JObject>().Where(x => (string)x["job"] == "Director").Aggregate(new JArray(), (a, x) => { a.Add(x); return a; }), "crew"); }
            var sorted = items.OrderByDescending(x => (double?)x["vote_count"] ?? 0).Take(60).ToList();
            return Json(new
            {
                person = new { id, name = (string)info["name"], biography = (string)info["biography"] ?? "", profile_path = (string)info["profile_path"], department = (string)info["known_for_department"], birthday = (string)info["birthday"], place = (string)info["place_of_birth"] },
                results = sorted
            });
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name + ": " + ex.Message, results = new JArray() }, 502); }
    }

    // Адрес TorrServer для всех устройств: задаётся в веб-панели, Lampa подхватывает его при запуске
    [HttpGet, AllowAnonymous]
    [Route("home/torrserver")]
    public async Task<ActionResult> TorrServerCfg(string url = null, bool test = false)
    {
        if (url != null)
        {
            if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
            url = url.Trim().TrimEnd('/');
            if (url.Length > 0 && !Regex.IsMatch(url, @"^https?://[\w.\-]+(:\d{1,5})?(/[\w./\-]*)?$")) return Json(new { error = "bad_url" }, 400);
            HomeDb.Set("torrserver_url", url);
        }
        string cur = HomeDb.Get("torrserver_url");
        if (string.IsNullOrEmpty(cur) && url == null) { cur = Environment.GetEnvironmentVariable("HOME_TORRSERVER") ?? ""; if (cur.Length > 0) HomeDb.Set("torrserver_url", cur); }   // по умолчанию — из переменной окружения
        if (!test) return Json(new { url = cur });
        try
        {
            using var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string v = await c.GetStringAsync(cur + "/echo");
            return Json(new { url = cur, ok = true, version = v });
        }
        catch (Exception ex) { return Json(new { url = cur, ok = false, error = ex.GetType().Name }); }
    }

    // Остаток подписки kino.pub профиля: {has:false} — аккаунта нет (ничего не показываем); {has:true, days:N, expired:bool}
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, JObject val)> subCache = new();

    [HttpGet, AllowAnonymous]
    [Route("home/kp/sub")]
    public async Task<ActionResult> KpSub(string profile = "main")
    {
        profile = string.IsNullOrEmpty(profile) ? "main" : profile;
        if (!KpStore.HasToken(profile)) return Json(new { has = false });
        if (subCache.TryGetValue(profile, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(30)) return Json(c.val);
        try
        {
            string tok = await KpStore.Token(profile);
            if (tok == null) return Json(new { has = false });
            var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/user", tok);
            if (code != 200) return Json(new { has = false });
            var sub = JObject.Parse(body)["user"]?["subscription"];
            if (sub == null) return Json(new { has = false });
            double d = (double?)sub["days"] ?? 0;
            long end = (long?)sub["end_time"] ?? 0;
            if (end > 0) d = (end - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 86400.0;
            bool active = (bool?)sub["active"] ?? d > 0;
            var val = new JObject { ["has"] = true, ["days"] = active ? Math.Max(0, (int)Math.Ceiling(d)) : 0, ["expired"] = !active || d <= 0 };
            subCache[profile] = (DateTime.UtcNow, val);
            return Json(val);
        }
        catch { return Json(new { has = false }); }
    }

    // состав главного экрана оболочки: ряды (порядок и адреса данных), переключаемые ряды по настройкам устройства
    [HttpGet, AllowAnonymous]
    [Route("home/shell/config")]
    public ActionResult ShellConfig(string dev = null, string profile = null)
    {
        string prof = string.IsNullOrEmpty(profile) ? "main" : profile;
        JObject cfg = new JObject();
        if (DeviceStore.ValidId(dev)) { try { cfg = DeviceStore.Hello(dev, null, null) ?? new JObject(); } catch { } }
        // профиль каналов YouTube не выбран на устройстве: берём тот, где больше всего подписок (а не пустой «main»)
        if (string.IsNullOrEmpty((string)cfg["ytprof"]))
        {
            try
            {
                var best = YtStore.Profiles().Children<JObject>().OrderByDescending(x => (long)x["subs"]).FirstOrDefault();
                cfg["ytprof"] = best != null && (long)best["subs"] > 0 ? (string)best["name"] : "main";
            }
            catch { cfg["ytprof"] = "main"; }
        }
        bool On(string k, bool def) { var v = (string)cfg[k]; return string.IsNullOrEmpty(v) ? def : v != "0"; }
        string P(string mode) => $"/kp/cards?mode={mode}&page=1&strip=24&profile={Uri.EscapeDataString(prof)}";
        var rows = new JArray
        {
            Row("continue", "Продолжить просмотр", P("continue"), "wide"),
            Row("history", "История", P("history"), "wide"),
            Row("watching", "Мои сериалы", P("watching"), "poster"),
            Row("watching_cartoons", "Мои мультсериалы", P("watching_cartoons"), "poster"),
            Row("my_cartoons", "Мои мультфильмы", P("my_cartoons"), "poster"),
            Row("new_movie", "Новые фильмы", P("new_movie"), "poster"),
            Row("new_serial", "Новые сериалы", P("new_serial"), "poster"),
            // «Лучшие новинки»: рейтинг и окно времени задаются настройками устройства (mc_toprate, mc_topnew); выключается mc_top=0
            Row("top_new", "Лучшие новинки", P("top_new") + (DeviceStore.ValidId(dev) ? "&dev=" + Uri.EscapeDataString(dev) : ""), "poster"),
            Row("watching_anime", "Мои аниме", P("watching_anime"), "poster"),
            Row("anime", "Аниме", P("anime"), "poster"),
            Row("ru_serial", "Отечественные сериалы", P("ru_serial"), "poster"),
            Row("ru_movie", "Отечественные фильмы", P("ru_movie"), "poster"),
            Row("new_cartoon", "Мультфильмы", P("new_cartoon"), "poster"),
            Row("new_cartoon_series", "Мультсериалы", P("new_cartoon_series"), "poster")
        };
        if (!On("mc_top", true)) { foreach (var tr in rows.Children<JObject>().Where(x => (string)x["id"] == "top_new").ToList()) tr.Remove(); }
        string ytPack = string.IsNullOrEmpty((string)cfg["ytprof"]) ? "main" : (string)cfg["ytprof"];
        if (Profiles.Enabled && Profiles.Current != null) { YtStore.AdoptDeviceChoice(ytPack); ytPack = YtStore.ResolvePack(null); }
        if (On("mc_yt", true) && ProfilePolicy.SourceEnabled("youtube")) rows.Add(Row("youtube", "YouTube · подписки", $"/yt/feed?n=30&profile={Uri.EscapeDataString(ytPack)}", "wide"));
        rows.Add(Row("my_movies", "Мои фильмы", P("my_movies"), "poster"));
        var allRows=(JArray)rows.DeepClone();
        if(Profiles.Enabled){
            var prefs=HomeDb.With(c=>Profiles.Rows(c,"SELECT row_id,enabled,sort_order FROM profile_rows WHERE profile_id=@p",("@p",Profiles.CurrentId))).ToDictionary(x=>(string)x[0]);
            rows=new JArray(rows.Children<JObject>().Where(r=>!prefs.TryGetValue((string)r["id"],out var x)||Convert.ToInt64(x[1])!=0).OrderBy(r=>prefs.TryGetValue((string)r["id"],out var x)?Convert.ToInt32(x[2]):1000+allRows.IndexOf(allRows.First(a=>(string)a["id"]==(string)r["id"]))));
        }
        return Json(new
        {
            rows, all_rows=allRows,
            sources = new { youtube = ProfilePolicy.SourceEnabled("youtube"), vk = ProfilePolicy.SourceEnabled("vk") },
            profile = prof,
            toggles = new { youtube = On("mc_yt", true) && ProfilePolicy.SourceEnabled("youtube"), wrestling = On("mc_wr", true), music = On("mc_ym", false), sources = On("mc_src", true) },
            dev = cfg,
            server = new { id = ServerId(), time = DateTime.UtcNow }
        });
    }

    static JObject Row(string id, string title, string url, string style) => new JObject { ["id"] = id, ["title"] = title, ["url"] = url, ["style"] = style };

    // ----- устройства -----
    [HttpGet, AllowAnonymous]
    [Route("home/dev/hello")]
    public ActionResult DevHello(string dev = null, string name = null, string init = null)
    {
        if (!DeviceStore.ValidId(dev)) return Json(new { error = "bad_dev" }, 400);
        if(Profiles.Enabled&&Profiles.Current?.Admin!=true&&Profiles.Current?.Device!=dev)return Json(new{error="device_forbidden"},403);
        return Json(new { cfg = DeviceStore.Hello(dev, name, init) });
    }

    [HttpGet, AllowAnonymous]
    [Route("home/dev/list")]
    public ActionResult DevList() => Json(new { devices = DeviceStore.List(), keys = DeviceStore.Keys });

    [HttpPost, AllowAnonymous]
    [Route("home/dev/rename")]
    public ActionResult DevRename(string dev = null, string name = null)
    {
        if (!Guard.Ok(Request) || (Profiles.Enabled && Profiles.Current?.Admin != true)) return Json(new { error = "admin_required" }, 403);
        if (!DeviceStore.ValidId(dev) || (name == null && !Request.Query.ContainsKey("name"))) return Json(new { error = "bad_name" }, 400);
        // ASP.NET binds an explicitly empty query value to null; it means reset.
        name = (name ?? "").Trim();
        if (name.Length > 80 || name.Any(char.IsControl)) return Json(new { error = "bad_name" }, 400);
        return DeviceStore.Rename(dev, name) ? Json(new { ok = true }) : Json(new { error = "device_not_found" }, 404);
    }

    // ?dev=&k=&v=   (v не задан — настройка снимается и устройство использует свою)
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/dev/set")]
    public ActionResult DevSet(string dev = null, string k = null, string v = null)
    {
        if(!Guard.Ok(Request))return Json(new{error="forbidden"},403);
        if(Profiles.Enabled&&Profiles.Current?.Admin!=true&&(Profiles.Current?.Device!=dev||k=="viewer"))return Json(new{error="device_forbidden"},403);
        if (!DeviceStore.ValidId(dev) || k == null || !DeviceStore.Keys.Contains(k)) return Json(new { error = "bad" }, 400);
        DeviceStore.Set(dev, k, v);
        return Json(new { ok = true });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/dev/forget")]
    public ActionResult DevForget(string dev = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!DeviceStore.ValidId(dev)) return Json(new { error = "bad" }, 400);
        HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM devices WHERE id=@i", ("@i", dev)));
        return Json(new { ok = true });
    }

    // ----- обои: загрузка и удаление -----
    static bool SafeName(string n) => !string.IsNullOrEmpty(n) && n.Length <= 80 && Regex.IsMatch(n, @"^[^/\\:*?""<>|]+\.(jpe?g|png|webp)$", RegexOptions.IgnoreCase) && !n.Contains("..");

    [HttpPost, AllowAnonymous]
    [Route("home/wallpaper/upload")]
    public async Task<ActionResult> WallUpload(string name = null)
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        if (!SafeName(name)) return Json(new { error = "bad_name" }, 400);
        Directory.CreateDirectory(wallDir);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await Request.Body.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 12 * 1024 * 1024) return Json(new { error = "too_big" }, 413);
        }
        if (ms.Length < 64) return Json(new { error = "empty" }, 400);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(wallDir, name), ms.ToArray());
        return Json(new { ok = true, size = ms.Length });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/wallpaper/delete")]
    public ActionResult WallDelete(string name = null)
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        if (!SafeName(name)) return Json(new { error = "bad_name" }, 400);
        try { System.IO.File.Delete(Path.Combine(wallDir, name)); } catch { }
        return Json(new { ok = true });
    }

    // ----- служебное -----
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("anime/cache/clear")]
    public ActionResult AnimeClear()
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM anime_map"));
        return Json(new { ok = true });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/hidden/clear")]
    public ActionResult HiddenClear()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM hidden WHERE profile_id=@profile"));
        return Json(new { ok = true });
    }

    // токен AnimeLib (anilib.me): сохраняем в cache/animelib.json — этот файл читает и модуль Lampac, и наш слой «Аниме»
    static readonly string alTokenPath = Path.Combine(Directory.GetCurrentDirectory(), "cache", "animelib.json");

    [HttpGet, AllowAnonymous]
    [Route("home/animelib/status")]
    public ActionResult AlStatus()
    {
        bool has = false, refresh = false;
        try
        {
            if (System.IO.File.Exists(alTokenPath))
            {
                var j = JObject.Parse(System.IO.File.ReadAllText(alTokenPath));
                has = !string.IsNullOrEmpty((string)j["token"]);
                refresh = !string.IsNullOrEmpty((string)j["refresh_token"]);
            }
        }
        catch { }
        return Json(new { has_token = has, has_refresh = refresh });
    }

    [HttpPost, AllowAnonymous]
    [Route("home/animelib/token")]
    public async Task<ActionResult> AlToken()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        try
        {
            using var sr = new StreamReader(Request.Body);
            var j = JObject.Parse(await sr.ReadToEndAsync());
            string access = ((string)j["access"] ?? "").Trim(), refresh = ((string)j["refresh"] ?? "").Trim();
            if (access.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) access = access.Substring(7).Trim();
            if (access.Length < 20) return Json(new { error = "token_too_short" }, 400);
            Directory.CreateDirectory(Path.GetDirectoryName(alTokenPath));
            var o = new JObject { ["token"] = access, ["refresh_token"] = refresh, ["refresh_time"] = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds() };
            System.IO.File.WriteAllText(alTokenPath, o.ToString(Formatting.None));
            return Json(new { ok = true, has_refresh = refresh.Length > 0 });
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 400); }
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/animelib/forget")]
    public ActionResult AlForget()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        try { System.IO.File.Delete(alTokenPath); } catch { }
        return Json(new { ok = true });
    }

    // ----- профили Кинопаба: добавить (новый аккаунт/устройство) и удалить -----
    // Профиль — отдельное «устройство» в аккаунте kino.pub со своим набором качества; привязывается кодом к любому аккаунту.
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/kp/profile/add")]
    public ActionResult KpProfileAdd(string label = null, string kind = "fhd")
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        label = (label ?? "").Trim();
        if (label.Length < 2 || label.Length > 40) return Json(new { error = "bad_label" }, 400);
        var preset = kind switch
        {
            "uhd" => new JObject { ["supportHevc"] = 1, ["supportHdr"] = 1, ["support4k"] = 1 },
            "uhd_sdr" => new JObject { ["supportHevc"] = 1, ["supportHdr"] = 0, ["support4k"] = 1 },
            "avc" => new JObject { ["supportHevc"] = 0, ["supportHdr"] = 0, ["support4k"] = 0 },
            _ => new JObject { ["supportHevc"] = 1, ["supportHdr"] = 0, ["support4k"] = 0 }
        };
        var o = KpStore.Load();
        var profiles = (JObject)(o["profiles"] ??= new JObject());
        string id;
        do { id = "p" + Guid.NewGuid().ToString("N").Substring(0, 6); } while (profiles[id] != null);
        profiles[id] = new JObject
        {
            ["label"] = label, ["device_title"] = "Kodi " + label, ["device_hardware"] = "x86_64", ["device_software"] = "Kodi 21.2", ["preset"] = preset
        };
        KpStore.Write(o);
        return Json(new { ok = true, id, label });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/kp/profile/delete")]
    public async Task<ActionResult> KpProfileDelete(string profile = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (string.IsNullOrEmpty(profile) || profile == KpStore.DefaultProfile || profile == "uhd") return Json(new { error = "protected" }, 400);
        var o = KpStore.Load();
        if (KpStore.Profile(o, profile) == null) return Json(new { error = "unknown" }, 404);
        // освобождаем слот устройства в аккаунте kino.pub (если профиль был привязан), затем убираем профиль
        try
        {
            using var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            h.DefaultRequestHeaders.Add("X-KP-Profile", profile);
            await h.GetAsync("http://127.0.0.1:9118/kp/auth/reset?profile=" + Uri.EscapeDataString(profile));
        }
        catch { }
        o = KpStore.Load();
        ((JObject)o["profiles"]).Remove(profile);
        KpStore.Write(o);
        return Json(new { ok = true });
    }
}
