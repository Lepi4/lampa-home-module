using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace HomeMedia;

// Яндекс Музыка: вход по коду устройства (как в Kodi-плагинах), списки треков, «Моя волна», поток через сервер.
// Токен хранится на сервере (database/yamusic.json), клиентам он не отдаётся.
public static class YmStore
{
    public const string Api = "https://api.music.yandex.net";
    const string OAuth = "https://oauth.yandex.ru";
    // публичные OAuth-данные приложения Яндекс.Музыки для Android (используют все сторонние клиенты)
    const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    const string ClientSecret = "53bc75238f0c4d08a118e51fe9203300";
    const string AndroidClient = "YandexMusicAndroid/24023621";
    // подпись get-file-info привязана к заголовку десктопного клиента
    const string DesktopClient = "YandexMusicDesktopAppWindows/5.95.0";
    const string DesktopKey = "kzqU4XhfCaY6B6JTHODeq5";

    static readonly string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "yamusic.json");
    static readonly object fl = new();
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        UseProxy = false
    }) { Timeout = TimeSpan.FromSeconds(40) };

    public static JObject Load()
    {
        lock (fl)
        {
            try { if (File.Exists(path)) return JObject.Parse(File.ReadAllText(path)); } catch { }
            return new JObject();
        }
    }

    public static void Save(JObject o)
    {
        lock (fl)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, o.ToString(Formatting.Indented));
        }
    }

    public static string Token => (string)Load()["token"];
    public static string Uid => (string)Load()["uid"];
    public static string Quality => (string)Load()["quality"] ?? "lossless";

    static HttpRequestMessage Req(HttpMethod m, string url, HttpContent content = null, string client = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = content };
        var tok = Token;
        if (!string.IsNullOrEmpty(tok)) r.Headers.TryAddWithoutValidation("Authorization", "OAuth " + tok);
        r.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", client ?? AndroidClient);
        r.Headers.TryAddWithoutValidation("Accept-Language", "ru");
        r.Headers.TryAddWithoutValidation("User-Agent", "Yandex-Music-API");
        return r;
    }

    public static async Task<(int code, JObject json)> Get(string pathAndQuery, string client = null)
    {
        using var req = Req(HttpMethod.Get, Api + pathAndQuery, null, client);
        using var res = await Http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        try { return ((int)res.StatusCode, JObject.Parse(body)); } catch { return ((int)res.StatusCode, new JObject { ["raw"] = body }); }
    }

    public static async Task<(int code, JObject json)> PostForm(string pathAndQuery, Dictionary<string, string> form)
    {
        using var req = Req(HttpMethod.Post, Api + pathAndQuery, new FormUrlEncodedContent(form));
        using var res = await Http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        try { return ((int)res.StatusCode, JObject.Parse(body)); } catch { return ((int)res.StatusCode, new JObject { ["raw"] = body }); }
    }

    public static async Task<(int code, JObject json)> PostJson(string pathAndQuery, object data)
    {
        using var req = Req(HttpMethod.Post, Api + pathAndQuery, new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json"));
        using var res = await Http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        try { return ((int)res.StatusCode, JObject.Parse(body)); } catch { return ((int)res.StatusCode, new JObject { ["raw"] = body }); }
    }

    // ---- вход по коду устройства ----
    static (string device, string user, string url, DateTime until)? pending;

    public static async Task<object> AuthStart()
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["device_id"] = Guid.NewGuid().ToString("N").Substring(0, 10),
            ["device_name"] = "Lampa Home"
        };
        using var res = await Http.PostAsync(OAuth + "/device/code", new FormUrlEncodedContent(form));
        var j = JObject.Parse(await res.Content.ReadAsStringAsync());
        if (!res.IsSuccessStatusCode || j["device_code"] == null) return new { error = "start_failed", raw = j };
        string url = (string)j["verification_url"] ?? "https://ya.ru/device";
        pending = ((string)j["device_code"], (string)j["user_code"], url, DateTime.UtcNow.AddSeconds((int?)j["expires_in"] ?? 300));
        return new { user_code = (string)j["user_code"], verification_url = url, expires_in = (int?)j["expires_in"] ?? 300 };
    }

    public static async Task<object> AuthPoll()
    {
        if (pending == null) return new { state = "none" };
        if (DateTime.UtcNow > pending.Value.until) { pending = null; return new { state = "expired" }; }
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "device_code",
            ["code"] = pending.Value.device,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret
        };
        using var res = await Http.PostAsync(OAuth + "/token", new FormUrlEncodedContent(form));
        var j = JObject.Parse(await res.Content.ReadAsStringAsync());
        if (j["access_token"] == null)
            return new { state = ((string)j["error"]) == "authorization_pending" ? "pending" : "error", error = (string)j["error_description"] ?? (string)j["error"] };

        var cfg = Load();
        cfg["token"] = (string)j["access_token"];
        Save(cfg);
        pending = null;
        var st = await Refresh();
        return new { state = "ok", account = st };
    }

    // uid, логин, наличие подписки
    public static async Task<object> Refresh()
    {
        var (code, j) = await Get("/account/status");
        if (code != 200) return new { error = "account_failed", code };
        var acc = j["result"]?["account"];
        var cfg = Load();
        cfg["uid"] = acc?["uid"]?.ToString();
        cfg["login"] = (string)acc?["login"];
        cfg["plus"] = j["result"]?["plus"]?["hasPlus"] ?? false;
        Save(cfg);
        return new { uid = (string)cfg["uid"], login = (string)cfg["login"], plus = (bool)cfg["plus"] };
    }

    // ---- нормализация треков для плагина ----
    public static JObject Norm(JToken t)
    {
        if (t == null) return null;
        if (t["track"] != null && t["track"].Type == JTokenType.Object) t = t["track"];
        string id = t["id"]?.ToString();
        if (string.IsNullOrEmpty(id)) return null;
        string album = t["albums"]?.FirstOrDefault()?["id"]?.ToString();
        string cover = (string)t["coverUri"] ?? (string)t["ogImage"];
        return new JObject
        {
            ["id"] = id,
            ["album_id"] = album,
            ["title"] = ((string)t["title"] ?? "") + (string.IsNullOrEmpty((string)t["version"]) ? "" : " (" + (string)t["version"] + ")"),
            ["artists"] = string.Join(", ", (t["artists"] ?? new JArray()).Select(a => (string)a["name"])),
            ["album"] = (string)t["albums"]?.FirstOrDefault()?["title"],
            ["cover"] = string.IsNullOrEmpty(cover) ? null : "https://" + cover.Replace("%%", "400x400"),
            ["duration"] = ((long?)t["durationMs"] ?? 0) / 1000,
            ["available"] = (bool?)t["available"] ?? true
        };
    }

    // полные треки по списку «id:albumId»
    public static async Task<JArray> Hydrate(IEnumerable<string> ids)
    {
        var list = ids.Where(x => !string.IsNullOrEmpty(x)).Take(100).ToList();
        if (list.Count == 0) return new JArray();
        var (code, j) = await PostForm("/tracks", new Dictionary<string, string> { ["track-ids"] = string.Join(",", list), ["with-positions"] = "false" });
        var arr = new JArray();
        if (code == 200 && j["result"] is JArray r)
            foreach (var t in r) { var n = Norm(t); if (n != null) arr.Add(n); }
        return arr;
    }

    // ---- ссылка на файл (get-file-info с подписью) ----
    public static async Task<(string url, string codec, int bitrate)> FileUrl(string trackId, string quality)
    {
        string[] codecs = quality == "nq" ? new[] { "mp3" } : quality == "hq" ? new[] { "aac", "mp3" } : new[] { "flac", "aac", "mp3" };
        string transport = "raw";
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string message = $"{ts}{trackId}{quality}{string.Concat(codecs)}{transport}";
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(DesktopKey));
        string sign = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(message)));
        sign = sign.Substring(0, sign.Length - 1);

        string q = $"/get-file-info?ts={ts}&trackId={Uri.EscapeDataString(trackId)}&quality={quality}&codecs={string.Join(",", codecs)}&transports={transport}&sign={Uri.EscapeDataString(sign)}";
        var (code, j) = await Get(q, DesktopClient);
        var di = j["result"]?["downloadInfo"];
        if (code != 200 || di == null) return (null, null, 0);
        return ((string)di["url"], (string)di["codec"], (int?)di["bitrate"] ?? 0);
    }
}

public class YandexMusicController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    static ActionResult NeedAuth() => Json(new { error = "no_auth" }, 401);

    [HttpGet, AllowAnonymous]
    [Route("yamusic.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "yamusic.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("ym/status")]
    public async Task<ActionResult> Status()
    {
        var cfg = YmStore.Load();
        if (string.IsNullOrEmpty((string)cfg["token"])) return Json(new { authorized = false });
        try
        {
            var st = await YmStore.Refresh();
            cfg = YmStore.Load();
            return Json(new { authorized = !string.IsNullOrEmpty((string)cfg["uid"]), uid = (string)cfg["uid"], login = (string)cfg["login"], plus = (bool?)cfg["plus"] ?? false, quality = YmStore.Quality });
        }
        catch (Exception ex) { return Json(new { authorized = true, error = ex.GetType().Name }); }
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("ym/auth/start")]
    public async Task<ActionResult> AuthStart()
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        return Json(await YmStore.AuthStart());
    }

    [HttpGet, AllowAnonymous]
    [Route("ym/auth/poll")]
    public async Task<ActionResult> AuthPoll()
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        return Json(await YmStore.AuthPoll());
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("ym/auth/reset")]
    public ActionResult AuthReset()
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        var cfg = YmStore.Load();
        cfg.Remove("token"); cfg.Remove("uid"); cfg.Remove("login"); cfg.Remove("plus");
        YmStore.Save(cfg);
        return Json(new { ok = true });
    }

    // Токен вручную (если вход по коду недоступен)
    [HttpPost, AllowAnonymous]
    [Route("ym/auth/token")]
    public async Task<ActionResult> AuthToken(string token)
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        if (string.IsNullOrWhiteSpace(token)) return Json(new { error = "empty" }, 400);
        var cfg = YmStore.Load(); cfg["token"] = token.Trim(); YmStore.Save(cfg);
        return Json(await YmStore.Refresh());
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("ym/quality")]
    public ActionResult SetQuality(string q)
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        if (q != "lossless" && q != "hq" && q != "nq") return Json(new { error = "bad_quality" }, 400);
        var cfg = YmStore.Load(); cfg["quality"] = q; YmStore.Save(cfg);
        return Json(new { quality = q });
    }

    // Список плейлистов пользователя
    [HttpGet, AllowAnonymous]
    [Route("ym/playlists")]
    public async Task<ActionResult> Playlists()
    {
        if (YmStore.Token == null) return NeedAuth();
        var (code, j) = await YmStore.Get($"/users/{YmStore.Uid}/playlists/list");
        if (code != 200) return Json(new { error = "api", code, raw = j }, 502);
        var arr = new JArray();
        foreach (var p in (j["result"] as JArray) ?? new JArray())
            arr.Add(new JObject
            {
                ["kind"] = p["kind"]?.ToString(),
                ["uid"] = p["uid"]?.ToString() ?? YmStore.Uid,
                ["title"] = (string)p["title"],
                ["count"] = (int?)p["trackCount"] ?? 0
            });
        return Json(new { playlists = arr });
    }

    // Треки: mode = likes | playlist:<uid>:<kind> | search:<текст> | chart | new | history
    [HttpGet, AllowAnonymous]
    [Route("ym/tracks")]
    public async Task<ActionResult> Tracks(string mode, int page = 0)
    {
        if (YmStore.Token == null) return NeedAuth();
        try
        {
            if (mode == "likes")
            {
                var (code, j) = await YmStore.Get($"/users/{YmStore.Uid}/likes/tracks");
                if (code != 200) return Json(new { error = "api", code, raw = j }, 502);
                var ids = ((JArray)(j["result"]?["library"]?["tracks"] ?? new JArray())).Select(t => t["id"] + (t["albumId"] != null ? ":" + t["albumId"] : "")).ToList();
                int per = 100;
                var slice = ids.Skip(page * per).Take(per);
                return Json(new { tracks = await YmStore.Hydrate(slice), has_more = ids.Count > (page + 1) * per, total = ids.Count });
            }

            if (mode != null && mode.StartsWith("playlist:"))
            {
                var p = mode.Split(':');
                var (code, j) = await YmStore.Get($"/users/{p[1]}/playlists/{p[2]}");
                if (code != 200) return Json(new { error = "api", code, raw = j }, 502);
                var items = (JArray)(j["result"]?["tracks"] ?? new JArray());
                var arr = new JArray();
                var missing = new List<string>();
                foreach (var it in items)
                {
                    var n = YmStore.Norm(it);
                    if (n != null && it["track"] != null) arr.Add(n);
                    else missing.Add(it["id"] + (it["albumId"] != null ? ":" + it["albumId"] : ""));
                }
                if (missing.Count > 0) foreach (var n in await YmStore.Hydrate(missing)) arr.Add(n);
                return Json(new { tracks = arr, has_more = false, title = (string)j["result"]?["title"] });
            }

            if (mode != null && mode.StartsWith("search:"))
            {
                string text = mode.Substring(7);
                var (code, j) = await YmStore.Get($"/search?text={Uri.EscapeDataString(text)}&nocorrect=false&type=track&page={page}&playlist-in-best=true");
                if (code != 200) return Json(new { error = "api", code, raw = j }, 502);
                var arr = new JArray();
                foreach (var t in (JArray)(j["result"]?["tracks"]?["results"] ?? new JArray())) { var n = YmStore.Norm(t); if (n != null) arr.Add(n); }
                return Json(new { tracks = arr, has_more = arr.Count >= 20 });
            }

            if (mode == "chart")
            {
                var (code, j) = await YmStore.Get("/landing3/chart");
                if (code != 200) return Json(new { error = "api", code, raw = j }, 502);
                var arr = new JArray();
                foreach (var t in (JArray)(j["result"]?["chart"]?["tracks"] ?? new JArray())) { var n = YmStore.Norm(t); if (n != null) arr.Add(n); }
                return Json(new { tracks = arr, has_more = false });
            }

            return Json(new { error = "bad_mode" }, 400);
        }
        catch (Exception ex) { return Json(new { error = "failed", message = ex.GetType().Name }, 502); }
    }

    // «Моя волна» и другие станции: следующие треки; queue — id трека, который только что был
    [HttpGet, AllowAnonymous]
    [Route("ym/wave")]
    public async Task<ActionResult> Wave(string station = "user:onyourwave", string queue = null)
    {
        if (YmStore.Token == null) return NeedAuth();
        string q = string.IsNullOrEmpty(queue) ? "?settings2=True" : "?queue=" + Uri.EscapeDataString(queue);
        var (code, j) = await YmStore.Get($"/rotor/station/{Uri.EscapeDataString(station)}/tracks{q}");
        if (code != 200) return Json(new { error = "api", code, raw = j }, 502);
        var arr = new JArray();
        foreach (var s in (JArray)(j["result"]?["sequence"] ?? new JArray())) { var n = YmStore.Norm(s["track"] ?? s); if (n != null) arr.Add(n); }
        return Json(new { tracks = arr, batch = (string)j["result"]?["batchId"] });
    }

    // Плейлист дня: персональная подборка из ленты, не обычный плейлист пользователя
    [HttpGet, AllowAnonymous]
    [Route("ym/day")]
    public async Task<ActionResult> Day()
    {
        if (YmStore.Token == null) return NeedAuth();
        JToken data = null;
        var (code, j) = await YmStore.Get("/feed");
        if (code == 200) data = FindDay(j["result"]);
        if (data == null)
        {
            var (c2, j2) = await YmStore.Get("/landing3?blocks=personalplaylists");
            if (c2 == 200) data = FindDay(j2["result"]);
        }
        if (data == null) return Json(new { error = "no_day" }, 404);
        string title = (string)data["title"] ?? "Плейлист дня";
        var tracks = await ReadTracks(data["tracks"] as JArray);
        if (tracks.Count == 0)
        {
            string uid = data["uid"]?.ToString() ?? data["owner"]?["uid"]?.ToString();
            string kind = data["kind"]?.ToString();
            if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(kind))
            {
                var (pc, pj) = await YmStore.Get($"/users/{Uri.EscapeDataString(uid)}/playlists/{Uri.EscapeDataString(kind)}");
                if (pc == 200)
                {
                    title = (string)pj["result"]?["title"] ?? title;
                    tracks = await ReadTracks(pj["result"]?["tracks"] as JArray);
                }
            }
        }
        return Json(new { title, tracks });
    }

    static JToken FindDay(JToken node)
    {
        if (node == null) return null;
        if (node.Type == JTokenType.Object)
        {
            if ((string)node["type"] == "playlistOfTheDay")
            {
                var inner = node["data"];
                if (inner != null && inner["kind"] != null) return inner;
                if (node["kind"] != null) return node;
                if (inner != null) return FindDay(inner) ?? inner;
            }
            foreach (var p in ((JObject)node).Properties())
            {
                var found = FindDay(p.Value);
                if (found != null) return found;
            }
        }
        else if (node.Type == JTokenType.Array)
        {
            foreach (var item in node)
            {
                var found = FindDay(item);
                if (found != null) return found;
            }
        }
        return null;
    }

    static async Task<JArray> ReadTracks(JArray items)
    {
        var arr = new JArray();
        var missing = new List<string>();
        if (items != null)
        {
            foreach (var it in items)
            {
                var n = YmStore.Norm(it);
                if (n != null && !string.IsNullOrEmpty((string)n["title"])) arr.Add(n);
                else
                {
                    var src = it["track"] ?? it;
                    string id = src["id"]?.ToString();
                    string album = it["albumId"]?.ToString() ?? src["albums"]?.FirstOrDefault()?["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) missing.Add(string.IsNullOrEmpty(album) ? id : id + ":" + album);
                }
            }
        }
        for (int i = 0; i < missing.Count; i += 100)
            foreach (var n in await YmStore.Hydrate(missing.Skip(i).Take(100))) arr.Add(n);
        return arr;
    }

    // Базовые настройки «Моей волны»: характер, настроение, язык. Названия приходят от Яндекса.
    [HttpGet, AllowAnonymous]
    [Route("ym/wave/categories")]
    public async Task<ActionResult> WaveCategories()
    {
        if (YmStore.Token == null) return NeedAuth();
        var (code, j) = await YmStore.Get("/rotor/station/user:onyourwave/info");
        if (code != 200) return Json(new { error = "api", code }, 502);
        var row = (j["result"] as JArray)?.FirstOrDefault();
        var restrictions = row?["station"]?["restrictions2"] as JObject;
        var settings = row?["settings2"] as JObject ?? new JObject();
        var groups = new JArray();
        foreach (var id in new[] { "diversity", "moodEnergy", "language" })
        {
            var block = restrictions?[id] as JObject;
            if (block == null) continue;
            var values = new JArray();
            foreach (var v in (block["possibleValues"] as JArray) ?? new JArray())
            {
                string value = v["value"]?.ToString();
                if (string.IsNullOrEmpty(value)) continue;
                values.Add(new JObject { ["value"] = value, ["name"] = (string)v["name"] ?? value });
            }
            string current = settings[id]?.ToString();
            string currentName = null;
            foreach (var v in values) if ((string)v["value"] == current) currentName = (string)v["name"];
            groups.Add(new JObject
            {
                ["id"] = id,
                ["title"] = (string)block["name"] ?? id,
                ["current"] = current,
                ["currentName"] = currentName,
                ["values"] = values
            });
        }
        return Json(new
        {
            groups,
            diversity = settings["diversity"]?.ToString(),
            mood = settings["moodEnergy"]?.ToString(),
            language = settings["language"]?.ToString()
        });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("ym/wave/settings")]
    public async Task<ActionResult> WaveSettings(string diversity, string mood, string language)
    {
        if (YmStore.Token == null) return NeedAuth();
        if (string.IsNullOrWhiteSpace(diversity) || string.IsNullOrWhiteSpace(mood) || string.IsNullOrWhiteSpace(language))
            return Json(new { error = "bad_settings" }, 400);
        var (code, _) = await YmStore.PostForm("/rotor/station/user:onyourwave/settings2", new Dictionary<string, string>
        {
            ["diversity"] = diversity,
            ["moodEnergy"] = mood,
            ["language"] = language,
            ["type"] = "rotor"
        });
        return Json(new { ok = code == 200 });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("ym/wave/feedback")]
    public async Task<ActionResult> WaveFeedback(string station, string type, string track = null, string batch = null, double seconds = 0)
    {
        if (YmStore.Token == null) return NeedAuth();
        var data = new Dictionary<string, object> { ["type"] = type, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 };
        if (!string.IsNullOrEmpty(track)) data["trackId"] = track;
        if (seconds > 0) data["totalPlayedSeconds"] = seconds;
        string qs = string.IsNullOrEmpty(batch) ? "" : "?batch-id=" + Uri.EscapeDataString(batch);
        var (code, j) = await YmStore.PostJson($"/rotor/station/{Uri.EscapeDataString(station)}/feedback{qs}", data);
        return Json(new { ok = code == 200 });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("ym/like")]
    public async Task<ActionResult> Like(string id, bool on = true)
    {
        if (YmStore.Token == null) return NeedAuth();
        var (code, j) = await YmStore.PostForm($"/users/{YmStore.Uid}/likes/tracks/{(on ? "add-multiple" : "remove")}", new Dictionary<string, string> { ["track-ids"] = id });
        return Json(new { ok = code == 200 });
    }

    [HttpGet, AllowAnonymous]
    [Route("ym/fileinfo")]
    public async Task<ActionResult> FileInfoDebug(string id, string q = "nq")
    {
        if (YmStore.Token == null) return NeedAuth();
        try
        {
            var (url, codec, bitrate) = await YmStore.FileUrl(id, q);
            return Json(new { has_url = url != null, host = url != null ? new Uri(url).Host : null, codec, bitrate });
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name, message = ex.Message }, 502); }
    }

    // Поток: прокси с Range (ссылка Яндекса живёт недолго и привязана к запросу)
    [HttpGet, AllowAnonymous]
    [Route("ym/stream/{id}")]
    public async Task<ActionResult> Stream(string id, string q = null)
    {
        if (YmStore.Token == null) return NeedAuth();
        string quality = q ?? YmStore.Quality;
        string url, codec; int bitrate;
        try
        {
            (url, codec, bitrate) = await YmStore.FileUrl(id, quality);
            if (url == null && quality != "nq") (url, codec, bitrate) = await YmStore.FileUrl(id, "nq");
        }
        catch (Exception ex) { return Json(new { error = "fileinfo_failed", message = ex.GetType().Name + ": " + ex.Message }, 502); }
        if (url == null) return Json(new { error = "no_file" }, 404);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (Request.Headers.TryGetValue("Range", out var range)) req.Headers.TryAddWithoutValidation("Range", range.ToString());
        HttpResponseMessage res;
        try { res = await YmStore.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted); }
        catch (Exception ex) { return Json(new { error = "upstream_failed", message = ex.GetType().Name + ": " + ex.Message, url_host = new Uri(url).Host }, 502); }
        using (res)
        {
            Response.StatusCode = (int)res.StatusCode;
            foreach (var h in new[] { "Content-Length", "Content-Range", "Accept-Ranges", "Content-Type" })
                if (res.Content.Headers.TryGetValues(h, out var v) || res.Headers.TryGetValues(h, out v))
                    Response.Headers[h] = v.ToArray();
            Response.Headers["X-YM-Codec"] = codec ?? "";
            Response.Headers["X-YM-Bitrate"] = bitrate.ToString();
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            await using var s = await res.Content.ReadAsStreamAsync();
            await s.CopyToAsync(Response.Body, HttpContext.RequestAborted);
        }
        return new EmptyResult();
    }
}
