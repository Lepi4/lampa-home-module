using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Состояние kino.pub на сервере. Каждый профиль — отдельное «устройство» в аккаунте со своими
// настройками (HEVC/HDR/4K) и своим токеном. Клиент выбирает профиль, остальное делает сервер.
public static class KpStore
{
    // Адреса API kino.pub. Сервис сменил домен, и ни один адрес не отвечает стабильно, поэтому перебираем по очереди:
    // тот, что ответил, остаётся текущим до следующего сбоя.
    static readonly string[] ApiHosts = { "https://api.srvkp.com", "https://kino.watch", "https://api.service-kp.com" };
    static int apiIdx;
    public static string Api => ApiHosts[apiIdx];

    static string ForCurrentApi(string url)
    {
        foreach (var h in ApiHosts)
            if (url.StartsWith(h + "/", StringComparison.Ordinal)) return Api + url.Substring(h.Length);
        return url;
    }

    static bool IsApiUrl(string url) => ApiHosts.Any(h => url.StartsWith(h + "/", StringComparison.Ordinal));
    public const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";
    public const string DefaultProfile = "main";

    static readonly string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "kinopub.json");
    static readonly object fileLock = new();
    static readonly Dictionary<string, SemaphoreSlim> refreshLocks = new();

    static SemaphoreSlim RefreshLock(string profile)
    {
        lock (refreshLocks)
        {
            if (!refreshLocks.TryGetValue(profile, out var l))
                refreshLocks[profile] = l = new SemaphoreSlim(1, 1);
            return l;
        }
    }

    static JObject DefaultProfiles() => new JObject
    {
        ["main"] = new JObject
        {
            ["label"] = "1080p · HEVC",
            ["device_title"] = "Kodi",
            ["device_hardware"] = "x86_64",
            ["device_software"] = "Kodi 21.2",
            ["preset"] = new JObject { ["supportHevc"] = 1, ["supportHdr"] = 0, ["support4k"] = 0 }
        },
        ["uhd"] = new JObject
        {
            ["label"] = "4K · HEVC · HDR",
            ["device_title"] = "Kodi 4K",
            ["device_hardware"] = "x86_64",
            ["device_software"] = "Kodi 21.2",
            ["preset"] = new JObject { ["supportHevc"] = 1, ["supportHdr"] = 1, ["support4k"] = 1 }
        }
    };

    public static JObject Load()
    {
        lock (fileLock)
        {
            JObject o = null;
            try { if (File.Exists(path)) o = JObject.Parse(File.ReadAllText(path)); } catch { }
            o ??= new JObject();

            bool dirty = false;
            void def(string k, string v) { if (o[k] == null) { o[k] = v; dirty = true; } }
            // client_id/secret публичные, из открытого плагина Kodi (quarckster/kodi.kino.pub)
            def("client_id", "xbmc");
            def("client_secret", "cgg3gtifu46urtfp2zp1nqtba0k2ezxh");
            def("proxy", Environment.GetEnvironmentVariable("HOME_KP_PROXY") ?? "");   // например socks5://user:pass@host:port; пусто — напрямую
            // какой кодек оставлять в мастер-плейлисте, если есть оба: hevc | h264 | both
            def("prefer_codec", "hevc");

            if (o["profiles"] == null) { o["profiles"] = DefaultProfiles(); dirty = true; }

            // миграция: токен из первой версии (один общий) переезжает в профиль main
            if (o["access_token"] != null)
            {
                var main = (JObject)o["profiles"][DefaultProfile];
                if (main != null && main["access_token"] == null)
                {
                    main["access_token"] = o["access_token"];
                    main["refresh_token"] = o["refresh_token"];
                    main["expires_at"] = o["expires_at"];
                }
                o.Remove("access_token"); o.Remove("refresh_token"); o.Remove("expires_at");
                foreach (var k in new[] { "device_title", "device_hardware", "device_software" }) o.Remove(k);
                dirty = true;
            }

            if (dirty) Write(o);
            return o;
        }
    }

    public static void Write(JObject o)
    {
        lock (fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, o.ToString(Formatting.Indented));
        }
    }

    public static JObject Profile(JObject cfg, string profile) => cfg["profiles"]?[profile] as JObject;

    // fallback: профиль необязателен — если запрошенный не подключён, берём любой подключённый.
    // Для входа/сброса/статуса (fallback=false) профиль нужен ровно тот, что запросили.
    public static string ResolveProfile(string requested, bool fallback = true)
    {
        var cfg = Load();
        string want = !string.IsNullOrEmpty(requested) && Profile(cfg, requested) != null ? requested : DefaultProfile;
        if (!fallback || HasToken(want)) return want;

        foreach (var p in ((JObject)cfg["profiles"]).Properties())
            if (!string.IsNullOrEmpty((string)p.Value["access_token"])) return p.Name;

        return want;
    }

    static readonly Dictionary<string, HttpClient> clients = new();

    public static HttpClient Client()
    {
        string proxy = NetworkProxy.Route("kinopub", (string)Load()["proxy"] ?? "");
        lock (clients)
        {
            if (clients.TryGetValue(proxy, out var c))
                return c;

            var h = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(6),
                UseProxy = false,
            };

            if (!string.IsNullOrWhiteSpace(proxy))
            {
                h.Proxy = NetworkProxy.Web(proxy);
                h.UseProxy = true;
            }

            c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(40) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
            clients[proxy] = c;
            return c;
        }
    }

    // Автоотключатель: если kino.pub не отвечает (два сбоя подряд), следующие 45 секунд вызовы падают сразу,
    // а не висят по 15 секунд каждый и не тянут за собой списки, которым kino.pub не нужен.
    // не больше 3 запросов к API kino.pub одновременно и пауза между стартами: страховка от лавины запросов
    static readonly System.Threading.SemaphoreSlim apiGate = new(3, 3);
    static int sendFails;
    static DateTime downUntil = DateTime.MinValue;
    public static bool IsDown => DateTime.UtcNow < downUntil;

    public static async Task<(int code, string body)> Send(HttpMethod method, string url, string bearer = null, HttpContent content = null)
    {
        if (Profiles.IsTest && (method != HttpMethod.Get || new[] { "toggle", "marktime", "reset", "clear" }.Any(x => url.Contains(x, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException("test instance: cloud mutation blocked");
        if (IsApiUrl(url) && IsDown) throw new HttpRequestException("kino.pub временно недоступен");
        int tries = IsApiUrl(url) ? ApiHosts.Length : 1;
        Exception last = null;
        for (int attempt = 0; attempt < tries; attempt++)
        {
            bool gated = IsApiUrl(url);
            if (gated) { await apiGate.WaitAsync(); await Task.Delay(120); }
            try
            {
                using var req = new HttpRequestMessage(method, ForCurrentApi(url)) { Content = content };
                if (bearer != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                using var res = await Client().SendAsync(req);
                string body = await res.Content.ReadAsStringAsync();
                sendFails = 0;
                return ((int)res.StatusCode, body);
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is HttpRequestException)
            {
                last = ex;
                if (!IsApiUrl(url)) break;
                apiIdx = (apiIdx + 1) % ApiHosts.Length;   // следующий адрес API
                // тело запроса одноразовое: повторять можно только запросы без него
                if (content != null) break;
            }
            finally { if (gated) apiGate.Release(); }
        }
        if (++sendFails >= 2) downUntil = DateTime.UtcNow.AddSeconds(45);
        throw last ?? new HttpRequestException("kino.pub не ответил");
    }

    public static Task<(int code, string body)> PostForm(string url, Dictionary<string, string> form, string bearer = null)
        => Send(HttpMethod.Post, url, bearer, new FormUrlEncodedContent(form));

    public static Task<(int code, string body)> PostJson(string url, object data, string bearer)
        => Send(HttpMethod.Post, url, bearer, new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json"));

    static readonly Dictionary<string, (string code, DateTime until)> pending = new();

    public static void SetPending(string profile, string code, int expiresIn)
    {
        lock (pending) pending[profile] = (code, DateTime.UtcNow.AddSeconds(expiresIn));
    }

    public static string GetPending(string profile)
    {
        lock (pending)
            return pending.TryGetValue(profile, out var p) && DateTime.UtcNow < p.until ? p.code : null;
    }

    public static void SaveTokens(string profile, JObject t)
    {
        var o = Load();
        var p = Profile(o, profile);
        // вход после отключения (токена не было) — новое «поколение» аккаунта; обычное обновление токена его не меняет
        if (string.IsNullOrEmpty((string)p["access_token"]) || string.IsNullOrEmpty((string)p["acct_gen"])) p["acct_gen"] = Guid.NewGuid().ToString("N").Substring(0, 12);
        p["access_token"] = (string)t["access_token"];
        p["refresh_token"] = (string)t["refresh_token"];
        p["expires_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)(t["expires_in"] ?? 3600);
        Write(o);
    }

    // Поколение аккаунта профиля: меняется при входе после отключения/смене аккаунта. Нужно для ключа резервного кэша ответов.
    // У уже подключённого профиля без метки она создаётся при первом обращении.
    public static string AcctGen(string profile)
    {
        lock (fileLock)
        {
            var o = Load();
            var p = Profile(o, profile);
            if (p == null || string.IsNullOrEmpty((string)p["access_token"])) return null;
            string g = (string)p["acct_gen"];
            if (string.IsNullOrEmpty(g)) { g = Guid.NewGuid().ToString("N").Substring(0, 12); p["acct_gen"] = g; Write(o); }
            return g;
        }
    }

    public static bool HasToken(string profile)
        => !string.IsNullOrEmpty((string)Profile(Load(), profile)?["access_token"]);

    // Действующий токен профиля; обновляется заранее (за минуту до конца) или принудительно.
    public static async Task<string> Token(string profile, bool force = false)
    {
        var o = Load();
        var p = Profile(o, profile);
        string access = (string)p?["access_token"];
        if (string.IsNullOrEmpty(access)) return null;

        long exp = (long)(p["expires_at"] ?? 0);
        if (!force && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp - 60)
            return access;

        var gate = RefreshLock(profile);
        await gate.WaitAsync();
        try
        {
            // другой запрос мог уже обновить токен
            o = Load();
            p = Profile(o, profile);
            if (!force && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < (long)(p["expires_at"] ?? 0) - 60)
                return (string)p["access_token"];

            var (code, body) = await PostForm(Api + "/oauth2/token", new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = (string)p["refresh_token"] ?? "",
                ["client_id"] = (string)o["client_id"],
                ["client_secret"] = (string)o["client_secret"],
            });

            if (code != 200) return null;
            var t = JObject.Parse(body);
            SaveTokens(profile, t);
            return (string)t["access_token"];
        }
        finally { gate.Release(); }
    }

    // Подпись устройства в аккаунте и пресет HEVC/HDR/4K профиля. Сервер и тип потока не трогаем.
    public static async Task<bool> ApplyPreset(string profile)
    {
        var o = Load();
        var p = Profile(o, profile);
        string tok = await Token(profile);
        if (p == null || tok == null) return false;

        try
        {
            await PostForm(Api + "/v1/device/notify", new()
            {
                ["title"] = (string)p["device_title"],
                ["hardware"] = (string)p["device_hardware"],
                ["software"] = (string)p["device_software"],
            }, tok);

            var (code, body) = await Send(HttpMethod.Get, Api + "/v1/device/info", tok);
            if (code != 200) return false;
            long id = (long)JObject.Parse(body)["device"]["id"];

            if (p["preset"] is JObject preset && preset.HasValues)
            {
                var (c2, _) = await PostJson($"{Api}/v1/device/{id}/settings", preset, tok);
                if (c2 != 200) return false;
            }

            p["device_id"] = id;
            Write(o);
            return true;
        }
        catch { return false; }
    }
}

public class KinoPubController : BaseController
{
    static readonly string[] AllowedApi =
    {
        "items", "watching", "bookmarks", "genres", "types", "user", "collections", "countries", "device", "tv", "playlists"
    };

    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    // Профиль клиента: заголовок X-KP-Profile или параметр ?profile=
    string Prof(bool strict = false)
    {
        string v = Request.Headers["X-KP-Profile"].FirstOrDefault();
        if (string.IsNullOrEmpty(v)) v = Request.Query["profile"].FirstOrDefault();
        return KpStore.ResolveProfile(v, fallback: !strict);
    }

    [HttpGet, AllowAnonymous]
    [Route("kinopub.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "kinopub.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/profiles")]
    public ActionResult Profiles()
    {
        var cfg = KpStore.Load();
        var list = ((JObject)cfg["profiles"]).Properties().Select(pr => new
        {
            id = pr.Name,
            label = (string)pr.Value["label"],
            authorized = !string.IsNullOrEmpty((string)pr.Value["access_token"]),
            device = (string)pr.Value["device_title"]
        });
        return Json(new { profiles = list });
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/status")]
    public async Task<ActionResult> Status()
    {
        string prof = Prof(strict: true);
        if (!KpStore.HasToken(prof)) return Json(new { authorized = false, profile = prof });

        string tok = await KpStore.Token(prof);
        if (tok == null) return Json(new { authorized = false, profile = prof, reason = "token_refresh_failed" });
        return Json(new { authorized = true, profile = prof });
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/auth/start")]
    public async Task<ActionResult> AuthStart()
    {
        string prof = Prof(strict: true);
        var o = KpStore.Load();
        try
        {
            var (code, body) = await KpStore.PostForm(KpStore.Api + "/oauth2/device", new()
            {
                ["grant_type"] = "device_code",
                ["client_id"] = (string)o["client_id"],
                ["client_secret"] = (string)o["client_secret"],
            });
            if (code != 200) return Json(new { error = "start_failed", http = code }, 502);

            var j = JObject.Parse(body);
            int exp = (int)(j["expires_in"] ?? 300);
            KpStore.SetPending(prof, (string)j["code"], exp);
            return Json(new
            {
                profile = prof,
                user_code = (string)j["user_code"],
                verification_uri = (string)j["verification_uri"],
                interval = (int)(j["interval"] ?? 5),
                expires_in = exp
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = "unreachable", message = ex.GetType().Name }, 502);
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/auth/poll")]
    public async Task<ActionResult> AuthPoll()
    {
        string prof = Prof(strict: true);
        string device = KpStore.GetPending(prof);
        if (device == null) return Json(new { status = "expired" });

        var o = KpStore.Load();
        try
        {
            var (code, body) = await KpStore.PostForm(KpStore.Api + "/oauth2/device", new()
            {
                ["grant_type"] = "device_token",
                ["client_id"] = (string)o["client_id"],
                ["client_secret"] = (string)o["client_secret"],
                ["code"] = device,
            });

            if (code == 200)
            {
                KpStore.SaveTokens(prof, JObject.Parse(body));
                bool applied = await KpStore.ApplyPreset(prof);
                return Json(new { status = "ok", preset_applied = applied });
            }

            string err = null;
            try { err = (string)JObject.Parse(body)["error"]; } catch { }
            if (err == "authorization_pending") return Json(new { status = "pending" });
            if (err == "code_expired" || err == "authorization_expired") return Json(new { status = "expired" });
            return Json(new { status = "error", http = code, error = err }, 502);
        }
        catch (Exception ex)
        {
            return Json(new { status = "error", message = ex.GetType().Name }, 502);
        }
    }

    // Повторно применить подпись и пресет HEVC/HDR/4K профиля
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("kp/profile/apply")]
    public async Task<ActionResult> ProfileApply()
    {
        string prof = Prof(strict: true);
        bool ok = await KpStore.ApplyPreset(prof);
        return Json(new { profile = prof, applied = ok }, ok ? 200 : 502);
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("kp/auth/reset")]
    public async Task<ActionResult> AuthReset()
    {
        string prof = Prof(strict: true);
        var o = KpStore.Load();
        var p = KpStore.Profile(o, prof);

        // освобождаем слот в аккаунте (лимит устройств); если не вышло — токен всё равно забываем
        bool unlinked = false;
        string tok = (string)p["access_token"];
        if (!string.IsNullOrEmpty(tok))
        {
            try
            {
                var (code, _) = await KpStore.PostForm(KpStore.Api + "/v1/device/unlink", new(), tok);
                unlinked = code == 200;
            }
            catch { }
        }

        p.Remove("access_token");
        p.Remove("refresh_token");
        p.Remove("expires_at");
        p.Remove("device_id");
        p.Remove("acct_gen");
        KpStore.Write(o);
        return Json(new { authorized = false, profile = prof, unlinked });
    }

    // ---------- HLS через сервер ----------
    // Плейлисты kino.pub переписываются так, чтобы плеер ходил только к Lampac: у api.srvkp.com цепочка
    // сертификатов с просроченным кросс-подписанным корнем, Android/ExoPlayer её отвергает.
    static readonly ConcurrentDictionary<string, byte> allowedHosts = new();

    static bool HostAllowed(string host)
        => host == "kino.watch" || host.EndsWith(".kino.watch") || host == "srvkp.com" || host.EndsWith(".srvkp.com") || host.EndsWith(".cdntogo.net") || allowedHosts.ContainsKey(host);

    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    string ProxyUri(string uri, Uri baseUri)
    {
        var abs = new Uri(baseUri, uri);
        allowedHosts[abs.Host] = 1;
        return host + "/kp/stream?u=" + B64(abs.ToString());
    }

    static int CodecRank(string line, string prefer)
    {
        var m = Regex.Match(line, "CODECS=\"([^\"]+)\"");
        string c = m.Success ? m.Groups[1].Value : "";
        bool hevc = c.Contains("hvc1") || c.Contains("hev1");
        bool avc = c.Contains("avc1");
        if (prefer == "h264") return avc ? 0 : hevc ? 1 : 2;
        return hevc ? 0 : avc ? 1 : 2;
    }

    // Кинотеатральные пропорции дают «302p» вместо «480p»: приводим подписи к стандартным 16:9
    static string NormalizeResolution(string line)
    {
        if (!line.StartsWith("#EXT-X-STREAM-INF")) return line;
        return Regex.Replace(line, "RESOLUTION=(\\d+)x(\\d+)", m =>
        {
            int w = int.Parse(m.Groups[1].Value);
            string std = w >= 3000 ? "3840x2160" : w >= 1700 ? "1920x1080" : w >= 1100 ? "1280x720" : "854x480";
            return "RESOLUTION=" + std;
        });
    }

    // В мастер-плейлисте при включённом HEVC каждое разрешение встречается дважды (hvc1 и avc1).
    // Оставляем по одному варианту на разрешение: предпочтительный кодек из настройки prefer_codec.
    // codec — переопределение на один запрос (h264 для штатного плеера Lampa в WebView: HEVC он не играет)
    static string DedupeVariants(string text, string codec = null)
    {
        if (!text.Contains("#EXT-X-STREAM-INF")) return text;

        string prefer = (codec ?? (string)KpStore.Load()["prefer_codec"] ?? "hevc").ToLowerInvariant();
        if (prefer == "both") return text;

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var best = new Dictionary<string, (int rank, int idx)>();

        for (int i = 0; i < lines.Count - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF")) continue;
            var r = Regex.Match(lines[i], "RESOLUTION=(\\d+x\\d+)");
            string key = r.Success ? r.Groups[1].Value : "i" + i;
            int rank = CodecRank(lines[i], prefer);
            if (!best.TryGetValue(key, out var cur) || rank < cur.rank)
                best[key] = (rank, i);
        }

        var keep = new HashSet<int>(best.Values.Select(v => v.idx));
        var res = new List<string>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("#EXT-X-STREAM-INF") && !keep.Contains(i))
            {
                i++; // пропускаем и строку с адресом варианта
                continue;
            }
            res.Add(NormalizeResolution(lines[i]));
        }
        return string.Join("\n", res);
    }

    // q=max — самый качественный вариант, q=1080 (и т.п.) — лучший не выше указанной высоты.
    // Нужно внешним плеерам (Vimu и др.): по мастер-плейлисту они сами берут не лучший вариант и 4K не запускают.
    // класс качества по ширине кадра: 1920x804 — это 1080p
    static int QClass(int w, int h) => w >= 3200 || h >= 2000 ? 2160 : w >= 2400 || h >= 1300 ? 1440 : w >= 1800 || h >= 1000 ? 1080 : w >= 1200 || h >= 700 ? 720 : h;

    static string PickQuality(string text, string q)
    {
        if (string.IsNullOrEmpty(q) || !text.Contains("#EXT-X-STREAM-INF")) return text;

        int limit = q == "max" ? int.MaxValue : int.TryParse(q, out int h) ? h : int.MaxValue;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int bestIdx = -1, bestHeight = -1;
        long bestBw = -1;

        for (int i = 0; i < lines.Count - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF")) continue;
            var r = Regex.Match(lines[i], "RESOLUTION=(\\d+)x(\\d+)");
            int height = r.Success ? QClass(int.Parse(r.Groups[1].Value), int.Parse(r.Groups[2].Value)) : 0;
            var b = Regex.Match(lines[i], "BANDWIDTH=(\\d+)");
            long bw = b.Success ? long.Parse(b.Groups[1].Value) : 0;
            if (height > limit) continue;
            if (height > bestHeight || (height == bestHeight && bw > bestBw))
            {
                bestIdx = i; bestHeight = height; bestBw = bw;
            }
        }

        if (bestIdx < 0) return text;

        var res = new List<string>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("#EXT-X-STREAM-INF") && i != bestIdx)
            {
                i++;
                continue;
            }
            res.Add(lines[i]);
        }
        return string.Join("\n", res);
    }

    // В мастер-плейлисте каждая озвучка повторяется для каждого качества (группы audio1080/audio720/audio480):
    // плеер показывает дубли, а переключение работает только внутри группы выбранного варианта.
    // Оставляем одну группу (у лучшего варианта) и направляем на неё все варианты.
    static string CollapseAudio(string text)
    {
        if (!text.Contains("TYPE=AUDIO") || !text.Contains("#EXT-X-STREAM-INF")) return text;

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        string keep = null;
        int bestH = -1;
        foreach (var l in lines)
        {
            if (!l.StartsWith("#EXT-X-STREAM-INF")) continue;
            var a = Regex.Match(l, "AUDIO=\"([^\"]+)\"");
            var r = Regex.Match(l, "RESOLUTION=\\d+x(\\d+)");
            int h = r.Success ? int.Parse(r.Groups[1].Value) : 0;
            if (a.Success && h > bestH) { bestH = h; keep = a.Groups[1].Value; }
        }
        if (keep == null) return text;

        var res = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (l.StartsWith("#EXT-X-MEDIA:") && l.Contains("TYPE=AUDIO"))
            {
                var g = Regex.Match(l, "GROUP-ID=\"([^\"]+)\"");
                if (g.Success && g.Groups[1].Value != keep) continue;
            }
            res.Add(l.StartsWith("#EXT-X-STREAM-INF") ? Regex.Replace(l, "AUDIO=\"[^\"]+\"", "AUDIO=\"" + keep + "\"") : l);
        }
        return string.Join("\n", res);
    }

    // Дорожка звука по умолчанию: ту, что выбрана во встроенном плеере (по названию, затем по языку), помечаем DEFAULT=YES, остальные — NO.
    // Нужна для внешнего плеера (VIMU), у которого нет параметра выбора дорожки: он берёт ту, что по умолчанию.
    static string PreferAudio(string text, string audio)
    {
        if (string.IsNullOrWhiteSpace(audio) || !text.Contains("TYPE=AUDIO")) return text;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int hit = -1;
        foreach (var byName in new[] { true, false })
        {
            for (int i = 0; i < lines.Count && hit < 0; i++)
            {
                var l = lines[i];
                if (!l.StartsWith("#EXT-X-MEDIA:") || !l.Contains("TYPE=AUDIO")) continue;
                var m = Regex.Match(l, (byName ? "NAME" : "LANGUAGE") + "=\"([^\"]*)\"");
                if (m.Success && m.Groups[1].Value.Equals(audio, StringComparison.OrdinalIgnoreCase)) hit = i;
            }
            if (hit >= 0) break;
        }
        if (hit < 0) return text;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-MEDIA:") || !lines[i].Contains("TYPE=AUDIO")) continue;
            lines[i] = Regex.Replace(lines[i], ",DEFAULT=(YES|NO)", "") + (i == hit ? ",DEFAULT=YES" : ",DEFAULT=NO");
        }
        return string.Join("\n", lines);
    }

    string RewritePlaylist(string text, Uri baseUri, string q = null, string codec = null, string audio = null)
    {
        text = DedupeVariants(text, codec);
        text = PickQuality(text, q);
        text = CollapseAudio(text);
        text = PreferAudio(text, audio);
        var sb = new StringBuilder(text.Length + 512);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
                line = Regex.Replace(line, "URI=\"([^\"]+)\"", m => "URI=\"" + ProxyUri(m.Groups[1].Value, baseUri) + "\"");
            else if (line.Length > 0)
                line = ProxyUri(line, baseUri);
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    [HttpGet, HttpHead, AllowAnonymous]
    [Route("kp/stream")]
    [Route("kp/stream/{u}.m3u8")]
    public async Task<ActionResult> Stream(string u, string q, string codec, string audio)
    {
        Uri uri;
        try { uri = new Uri(UnB64(u ?? "")); }
        catch { return Json(new { error = "bad_url" }, 400); }

        if (uri.Scheme != "https" && uri.Scheme != "http") return Json(new { error = "bad_scheme" }, 400);
        if (!HostAllowed(uri.Host)) return Json(new { error = "host_not_allowed", host = uri.Host }, 403);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            string range = Request.Headers["Range"].FirstOrDefault();
            if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);

            using var res = await KpStore.Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            string ctype = res.Content.Headers.ContentType?.MediaType ?? "";
            bool playlist = ctype.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

            if (playlist && res.IsSuccessStatusCode)
            {
                string text = await res.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                return new ContentResult { Content = RewritePlaylist(text, uri, q, codec, audio), ContentType = "application/vnd.apple.mpegurl", StatusCode = 200 };
            }

            Response.StatusCode = (int)res.StatusCode;
            if (!string.IsNullOrEmpty(ctype)) Response.ContentType = res.Content.Headers.ContentType.ToString();
            if (res.Content.Headers.ContentLength.HasValue) Response.ContentLength = res.Content.Headers.ContentLength;
            if (res.Content.Headers.ContentRange != null) Response.Headers["Content-Range"] = res.Content.Headers.ContentRange.ToString();
            Response.Headers["Accept-Ranges"] = "bytes";

            if (!HttpMethods.IsHead(Request.Method))
                await res.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);

            return new EmptyResult();
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            if (Response.HasStarted) return new EmptyResult();
            return Json(new { error = "unreachable", message = ex.GetType().Name }, 502);
        }
    }

    // Прозрачный прокси к API v1 с токеном профиля: /kp/api/items/fresh?type=movie -> /v1/items/fresh?type=movie
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("kp/api/{**path}")]
    public async Task<ActionResult> Api(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains(".."))
            return Json(new { error = "bad_path" }, 400);

        if(HomeMedia.Profiles.Enabled && (HttpMethods.IsPost(Request.Method)||path.StartsWith("watching")||path.StartsWith("bookmarks")))return Json(new{error="use_local_profile_library"},403);
        string first = path.Split('/')[0];
        if (!AllowedApi.Contains(first))
            return Json(new { error = "forbidden_path" }, 403);

        string prof = Prof();
        string tok = await KpStore.Token(prof);
        if (tok == null)
            return Json(new { error = "not_authorized", profile = prof }, 401);

        string url = $"{KpStore.Api}/v1/{path}{Request.QueryString.Value}";
        string reqBody = null;
        string reqType = null;
        if (HttpMethods.IsPost(Request.Method))
        {
            using var sr = new StreamReader(Request.Body, Encoding.UTF8);
            reqBody = await sr.ReadToEndAsync();
            reqType = Request.ContentType;
        }

        async Task<(int, string, string)> Send(string token)
        {
            using var req = new HttpRequestMessage(new HttpMethod(Request.Method), url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (reqBody != null)
                req.Content = new StringContent(reqBody, Encoding.UTF8, (reqType ?? "application/x-www-form-urlencoded").Split(';')[0]);
            using var res = await KpStore.Client().SendAsync(req);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(), res.Content.Headers.ContentType?.ToString());
        }

        try
        {
            var (code, body, ctype) = await Send(tok);
            if (code == 401)
            {
                string fresh = await KpStore.Token(prof, force: true);
                if (fresh == null) return Json(new { error = "not_authorized", profile = prof }, 401);
                (code, body, ctype) = await Send(fresh);
            }

            if(HomeMedia.Profiles.Enabled && code==200){
                var payload=JObject.Parse(body);
                if(payload["item"] is JObject item){
                    var card=await KpMap.ToTmdbCard(item,tok);
                    if(HomeMedia.Profiles.Current?.Child==true&&(card==null||!await ProfilePolicy.Allows(card)))return Json(new{error="parental_blocked"},403);
                    var watched=await KpUnified.WatchedSets();long tid=(long?)card?["id"]??0;watched.TryGetValue(tid,out var seen);
                    void Personal(JToken value,int season=0){if(value is JObject obj){int sn=(int?)obj["number"]??season;
                        if(obj["seasons"] is JArray ss)foreach(var se in ss.Children<JObject>())Personal(se,(int?)se["number"]??0);
                        if(obj["episodes"] is JArray es)foreach(var ep in es.Children<JObject>()){int en=(int?)ep["number"]??0;bool done=seen?.Contains((sn,en))==true;ep["watched"]=done?1:0;ep["watching"]=new JObject{["status"]=done?1:0,["time"]=0};}
                        if(obj["videos"] is JArray vs)foreach(var v in vs.Children<JObject>()){v["watched"]=0;v["watching"]=new JObject{["status"]=0,["time"]=0};}
                        obj["watched"]=0;obj["watching"]=new JObject{["status"]=0,["time"]=0};
                    }}
                    Personal(item);if(card!=null){item["subscribed"]=ProfileLibrary.Has(CardKind.Media(card),tid);item["home_kind"]=CardKind.Kind(card);}
                }
                body=payload.ToString(Formatting.None);
            }
            return new ContentResult { Content = body, ContentType = ctype ?? "application/json; charset=utf-8", StatusCode = code };
        }
        catch (Exception ex)
        {
            return Json(new { error = "unreachable", message = ex.GetType().Name }, 502);
        }
    }
}
