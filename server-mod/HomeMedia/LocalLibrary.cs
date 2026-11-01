using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Локальная библиотека: источники настраиваются вручную и отключаются (database/localmedia.json).
//  - folder: папка с файлами (Wrestling), путь внутри контейнера Lampac;
//  - plex: сервер Plex (адрес + токен), сопоставление с карточками Lampa по TMDB id.
public static class LocalCfg
{
    static readonly string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "localmedia.json");
    static readonly object fl = new();

    public static JObject Load()
    {
        lock (fl)
        {
            JObject o = null;
            try { if (File.Exists(path)) o = JObject.Parse(File.ReadAllText(path)); } catch { }
            o ??= new JObject();
            if (o["sources"] is not JArray)
            {
                o["sources"] = new JArray
                {
                    new JObject { ["id"] = "wrestling", ["type"] = "folder", ["title"] = "Wrestling", ["path"] = "/mnt/wrestling", ["enable"] = true },
                    new JObject { ["id"] = "plex", ["type"] = "plex", ["title"] = "Plex", ["url"] = "", ["token"] = "", ["playback"] = "file", ["enable"] = false },
                };
                Save(o);
            }
            return o;
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

    public static JObject Source(string id)
        => ((JArray)Load()["sources"]).Children<JObject>().FirstOrDefault(s => (string)s["id"] == id);

    public static bool Enabled(string id)
    {
        var s = Source(id);
        return s != null && (bool?)s["enable"] != false;
    }
}

public static class PlexClient
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate })
    { Timeout = TimeSpan.FromSeconds(60) };

    public static JObject Cfg() => LocalCfg.Source("plex");

    public static bool Ready()
    {
        var c = Cfg();
        return c != null && (bool?)c["enable"] != false && !string.IsNullOrEmpty((string)c["url"]) && !string.IsNullOrEmpty((string)c["token"]);
    }

    public static string Base() => ((string)Cfg()["url"]).TrimEnd('/');
    public static string Host() => new Uri(Base()).Host;

    public static HttpRequestMessage Req(string pathOrUrl, HttpMethod method = null)
    {
        string url = pathOrUrl.StartsWith("http") ? pathOrUrl : Base() + pathOrUrl;
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-Plex-Token", (string)Cfg()["token"]);
        req.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", "lampac-home");
        req.Headers.TryAddWithoutValidation("X-Plex-Product", "Lampa Home");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return req;
    }

    public static async Task<JObject> Json(string path)
    {
        using var res = await Http.SendAsync(Req(path));
        res.EnsureSuccessStatusCode();
        return JObject.Parse(await res.Content.ReadAsStringAsync());
    }

    public static Task<HttpResponseMessage> Send(HttpRequestMessage req, HttpCompletionOption opt = HttpCompletionOption.ResponseHeadersRead)
        => Http.SendAsync(req, opt);

    // ---------- индекс: "movie:tmdb" / "tv:tmdb" -> элемент Plex ----------
    sealed class Item { public string Rk, Title, Section, SectionKey, Kind; public long Tmdb, Added; }
    static Dictionary<string, Item> index = new();
    // все элементы с одним TMDB id (многосерийные фильмы лежат в Plex отдельными «фильмами»: «… 1 серия», «… 2 серия»)
    static Dictionary<string, List<Item>> partsIdx = new();
    static DateTime built = DateTime.MinValue;
    static readonly SemaphoreSlim buildLock = new(1, 1);

    public static async Task EnsureIndex(bool force = false)
    {
        if (!force && DateTime.UtcNow - built < TimeSpan.FromMinutes(3) && index.Count > 0) return;
        if (!await buildLock.WaitAsync(0)) { if (index.Count == 0) { await buildLock.WaitAsync(); buildLock.Release(); } return; }
        try
        {
            var idx = new Dictionary<string, Item>();
            var pidx = new Dictionary<string, List<Item>>();
            var secs = (JArray)(await Json("/library/sections"))["MediaContainer"]?["Directory"] ?? new JArray();
            foreach (var sec in secs.Children<JObject>())
            {
                string type = (string)sec["type"];
                if (type != "movie" && type != "show") continue;
                var d = await Json($"/library/sections/{(string)sec["key"]}/all?includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=10000");
                foreach (var m in ((JArray)(d["MediaContainer"]?["Metadata"] ?? new JArray())).Children<JObject>())
                {
                    string tmdb = null;
                    foreach (var g in ((JArray)(m["Guid"] ?? new JArray())).Children<JObject>())
                    {
                        string id = (string)g["id"];
                        if (id != null && id.StartsWith("tmdb://")) { tmdb = id.Substring(7); break; }
                    }
                    if (tmdb == null) continue;
                    string kind = type == "show" ? "tv" : "movie";
                    string ikey = $"{kind}:{tmdb}";
                    var item = new Item
                    {
                        Rk = (string)m["ratingKey"], Title = (string)m["title"], Section = (string)sec["title"], SectionKey = (string)sec["key"],
                        Kind = kind, Tmdb = long.Parse(tmdb), Added = (long?)m["addedAt"] ?? 0
                    };
                    if (kind == "movie" && idx.TryGetValue(ikey, out var prev))
                    {
                        if (!pidx.TryGetValue(ikey, out var lst)) pidx[ikey] = lst = new List<Item> { prev };
                        lst.Add(item);
                        if (PartNo(item.Title) < PartNo(prev.Title)) idx[ikey] = item;   // карточка ведёт на первую серию
                    }
                    else idx[ikey] = item;
                }
            }
            index = idx;
            partsIdx = pidx;
            built = DateTime.UtcNow;
        }
        finally { buildLock.Release(); }
    }

    // номер серии из названия: «12 стульев- 2 серия», «… cd2», «Part 3»; без номера — в конец
    static int PartNo(string title)
    {
        var m = System.Text.RegularExpressions.Regex.Match(title ?? "", @"(\d+)\s*(?:сер|ч\b|час|cd|part|pt)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(title ?? "", @"(\d+)(?!.*\d)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
    }

    // Серии многосерийного «фильма» по порядку (пусто, если элемент один)
    public static List<(string rk, string title, int part)> Parts(string kind, long tmdb)
        => partsIdx.TryGetValue($"{kind}:{tmdb}", out var l) && l.Count > 1
            ? l.OrderBy(x => PartNo(x.Title)).ThenBy(x => long.TryParse(x.Rk, out long r) ? r : 0).Select(x => (x.Rk, x.Title, PartNo(x.Title))).ToList()
            : new List<(string, string, int)>();

    public static (string rk, string title, string section)? Lookup(string kind, long tmdb)
        => index.TryGetValue($"{kind}:{tmdb}", out var i) ? (i.Rk, i.Title, i.Section) : null;

    public static int IndexSize => index.Count;

    // Все элементы индекса (для списков «недавно добавлено» и по разделам)
    public static List<(string kind, long tmdb, long added, string sectionKey, string section)> All()
        => index.Values.Select(i => (i.Kind, i.Tmdb, i.Added, i.SectionKey, i.Section)).ToList();
}

[ProfileResult]
public class LocalLibraryController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    // ---------- настройки ----------
    [HttpGet, AllowAnonymous]
    [Route("local/config")]
    public ActionResult Config()
    {
        var res = new JArray();
        foreach (var s in ((JArray)LocalCfg.Load()["sources"]).Children<JObject>())
        {
            var c = (JObject)s.DeepClone();
            if (c["token"] != null) { c["has_token"] = !string.IsNullOrEmpty((string)c["token"]); c.Remove("token"); }
            res.Add(c);
        }
        return Json(new { sources = res });
    }

    // POST /local/config  { id, patch: { enable, path, url, token, playback, title } }
    [HttpPost, AllowAnonymous]
    [Route("local/config")]
    public async Task<ActionResult> SetConfig()
    {
        string body;
        using (var sr = new StreamReader(Request.Body, Encoding.UTF8)) body = await sr.ReadToEndAsync();
        JObject req;
        try { req = JObject.Parse(body); } catch { return Json(new { error = "bad_json" }, 400); }

        var cfg = LocalCfg.Load();
        var src = ((JArray)cfg["sources"]).Children<JObject>().FirstOrDefault(s => (string)s["id"] == (string)req["id"]);
        if (src == null) return Json(new { error = "unknown_source" }, 404);

        var allowed = new[] { "enable", "path", "url", "token", "playback", "title", "watching" };
        foreach (var p in ((JObject)(req["patch"] ?? new JObject())).Properties())
        {
            if (!allowed.Contains(p.Name)) continue;
            if (p.Name == "path") { string v = (string)p.Value ?? ""; if (v.Contains("..")) continue; }
            src[p.Name] = p.Value;
        }
        LocalCfg.Save(cfg);

        if ((string)src["type"] == "plex") _ = PlexClient.EnsureIndex(force: true);
        return Json(new { ok = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("local/status")]
    public async Task<ActionResult> Status()
    {
        var res = new JArray();
        foreach (var s in ((JArray)LocalCfg.Load()["sources"]).Children<JObject>())
        {
            string id = (string)s["id"];
            bool on = (bool?)s["enable"] != false;
            string state = !on ? "off" : "ok";
            int count = 0;

            if (on && (string)s["type"] == "plex")
            {
                if (!PlexClient.Ready()) state = "not_configured";
                else
                {
                    try { await PlexClient.EnsureIndex(); count = PlexClient.IndexSize; }
                    catch { state = "unreachable"; }
                }
            }
            else if (on && (string)s["type"] == "folder")
            {
                string p = (string)s["path"];
                if (!Directory.Exists(p)) state = "no_folder";
                else count = Directory.EnumerateFiles(p).Count();
            }
            res.Add(new JObject { ["id"] = id, ["type"] = s["type"], ["title"] = s["title"], ["enable"] = on, ["state"] = state, ["count"] = count });
        }
        return Json(new { sources = res });
    }

    // Разделы Plex с числом тайтлов (для меню «Plex»)
    [HttpGet, AllowAnonymous]
    [Route("local/sections")]
    public async Task<ActionResult> Sections()
    {
        if (!PlexClient.Ready()) return Json(new { ready = false, sections = new object[0] });
        try
        {
            await PlexClient.EnsureIndex();
            var secs = PlexClient.All().GroupBy(i => (i.sectionKey, i.section))
                .Select(g => new { key = g.Key.sectionKey, title = g.Key.section, count = g.Count() })
                .OrderBy(x => int.TryParse(x.key, out int k) ? k : 99).ToList();
            return Json(new { ready = true, total = PlexClient.IndexSize, sections = secs });
        }
        catch { return Json(new { ready = false, sections = new object[0] }); }
    }

    // ---------- поиск тайтла в локальной библиотеке ----------
    [HttpGet, AllowAnonymous]
    [Route("local/lookup")]
    public async Task<ActionResult> Lookup(string type, long tmdb)
    {
        if (type != "movie" && type != "tv") return Json(new { error = "bad_type" }, 400);
        if (!PlexClient.Ready()) return Json(new { found = false });

        try
        {
            await PlexClient.EnsureIndex();
            var hit = PlexClient.Lookup(type, tmdb);
            if (hit == null) return Json(new { found = false });

            var (rk, title, section) = hit.Value;
            object info = null;
            if (type == "movie")
            {
                var m = (await PlexClient.Json($"/library/metadata/{rk}"))["MediaContainer"]?["Metadata"]?[0] as JObject;
                var media = m?["Media"]?[0];
                info = new
                {
                    duration = (long?)m?["duration"] / 1000,
                    viewOffset = Profiles.Enabled ? (long?)((await KpUnified.Progress(type,tmdb))?["position"]) ?? 0 : (long?)m?["viewOffset"] / 1000,
                    container = (string)media?["container"],
                    video = (string)media?["videoCodec"],
                    resolution = (string)media?["videoResolution"]
                };
            }
            // многосерийный «фильм»: серии лежат в Plex отдельными элементами — отдаём весь список
            object parts = null;
            if (type == "movie")
            {
                var pl = PlexClient.Parts(type, tmdb);
                if (pl.Count > 1)
                    parts = await Task.WhenAll(pl.Select(async p =>
                    {
                        var mm = (await PlexClient.Json($"/library/metadata/{p.rk}"))["MediaContainer"]?["Metadata"]?[0] as JObject;
                        return new
                        {
                            rk = p.rk, title = p.title, part = p.part,
                            duration = (long?)mm?["duration"] / 1000,
                            viewOffset = Profiles.Enabled ? 0 : (long?)mm?["viewOffset"] / 1000,
                            watched = !Profiles.Enabled && ((int?)mm?["viewCount"] ?? 0) > 0
                        };
                    }));
            }
            return Json(new { found = true, source = "plex", kind = type, rk, title, section, info, parts });
        }
        catch (Exception ex)
        {
            return Json(new { found = false, error = ex.GetType().Name });
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("local/episodes")]
    public async Task<ActionResult> Episodes(string rk,long tmdb=0)
    {
        if (!PlexClient.Ready() || string.IsNullOrEmpty(rk) || !rk.All(char.IsDigit)) return Json(new { error = "bad_request" }, 400);
        try
        {
            var d = await PlexClient.Json($"/library/metadata/{rk}/allLeaves");
            var own=new System.Collections.Generic.Dictionary<string,JObject>();
            if(Profiles.Enabled){var dump=await TlV2.DumpAsync();foreach(var row in ((JArray)dump["rows"]).Children<JObject>())if((string)row["id"] is string id)own[id]=row;}
            JObject Progress(JObject e)=>own.TryGetValue($"tv-{tmdb}-s{(int?)e["parentIndex"]??0}e{(int?)e["index"]??0}",out var row)?row:null;
            var eps = ((JArray)(d["MediaContainer"]?["Metadata"] ?? new JArray())).Children<JObject>().Select(e => new
            {
                rk = (string)e["ratingKey"],
                season = (int?)e["parentIndex"] ?? 0,
                episode = (int?)e["index"] ?? 0,
                title = (string)e["title"],
                duration = (long?)e["duration"] / 1000,
                viewOffset = Profiles.Enabled ? (long?)Progress(e)?["position"] ?? 0 : (long?)e["viewOffset"] / 1000,
                watched = Profiles.Enabled ? Progress(e) is JObject progress && KpUnified.RowDone(progress) : ((int?)e["viewCount"] ?? 0) > 0
            }).OrderBy(e => e.season).ThenBy(e => e.episode).ToList();
            return Json(new { episodes = eps });
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 502); }
    }

    // ---------- раздача Plex через сервер (токен остаётся на сервере) ----------
    static async Task Copy(HttpResponseMessage res, bool head, HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = (int)res.StatusCode;
        if (res.Content.Headers.ContentType != null) response.ContentType = res.Content.Headers.ContentType.ToString();
        if (res.Content.Headers.ContentLength.HasValue) response.ContentLength = res.Content.Headers.ContentLength;
        if (res.Content.Headers.ContentRange != null) response.Headers["Content-Range"] = res.Content.Headers.ContentRange.ToString();
        response.Headers["Accept-Ranges"] = "bytes";
        if (!head) await res.Content.CopyToAsync(response.Body, ct);
    }

    // Прямой файл с поддержкой Range
    [HttpGet, HttpHead, AllowAnonymous]
    [Route("local/plex/file/{rk}")]
    public async Task<ActionResult> PlexFile(string rk)
    {
        if (!PlexClient.Ready() || !rk.All(char.IsDigit)) return Json(new { error = "bad_request" }, 400);
        try
        {
            var m = (await PlexClient.Json($"/library/metadata/{rk}"))["MediaContainer"]?["Metadata"]?[0] as JObject;
            string key = (string)m?["Media"]?[0]?["Part"]?[0]?["key"];
            if (string.IsNullOrEmpty(key)) return Json(new { error = "no_part" }, 404);

            var req = PlexClient.Req(key);
            string range = Request.Headers["Range"].FirstOrDefault();
            if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);
            using var res = await PlexClient.Send(req);
            await Copy(res, HttpMethods.IsHead(Request.Method), Response, HttpContext.RequestAborted);
            return new EmptyResult();
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex) { if (Response.HasStarted) return new EmptyResult(); return Json(new { error = ex.GetType().Name }, 502); }
    }

    // Транскод Plex в HLS (для встроенного плеера: HEVC/MKV напрямую часто не играют)
    [HttpGet, AllowAnonymous]
    [Route("local/plex/hls/{rk}.m3u8")]
    public async Task<ActionResult> PlexHls(string rk, int offset = 0)
    {
        if (!PlexClient.Ready() || !rk.All(char.IsDigit)) return Json(new { error = "bad_request" }, 400);
        try
        {
            string session = Guid.NewGuid().ToString("N");
            string path = $"/video/:/transcode/universal/start.m3u8?hasMDE=1&path={Uri.EscapeDataString("/library/metadata/" + rk)}" +
                          $"&mediaIndex=0&partIndex=0&protocol=hls&fastSeek=1&directPlay=0&directStream=1&subtitleSize=100&audioBoost=100" +
                          $"&maxVideoBitrate=40000&videoQuality=100&offset={offset}&session={session}" +
                          "&X-Plex-Platform=Chrome&X-Plex-Client-Identifier=lampac-home&X-Plex-Product=Lampa%20Home";
            return await PlaylistThrough(PlexClient.Base() + path);
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 502); }
    }

    // Любой следующий запрос Plex (плейлисты, сегменты) — только к адресу Plex из настроек
    [HttpGet, HttpHead, AllowAnonymous]
    [Route("local/plex/seg")]
    public async Task<ActionResult> PlexSeg(string u)
    {
        if (!PlexClient.Ready()) return Json(new { error = "not_ready" }, 400);
        Uri uri;
        try { uri = new Uri(UnB64(u ?? "")); } catch { return Json(new { error = "bad_url" }, 400); }
        if (!string.Equals(uri.Host, PlexClient.Host(), StringComparison.OrdinalIgnoreCase) || uri.Port != new Uri(PlexClient.Base()).Port)
            return Json(new { error = "host_not_allowed" }, 403);

        try
        {
            if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                return await PlaylistThrough(uri.ToString());

            var req = PlexClient.Req(uri.ToString());
            string range = Request.Headers["Range"].FirstOrDefault();
            if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);
            using var res = await PlexClient.Send(req);
            await Copy(res, HttpMethods.IsHead(Request.Method), Response, HttpContext.RequestAborted);
            return new EmptyResult();
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex) { if (Response.HasStarted) return new EmptyResult(); return Json(new { error = ex.GetType().Name }, 502); }
    }

    async Task<ActionResult> PlaylistThrough(string url)
    {
        using var res = await PlexClient.Send(PlexClient.Req(url), HttpCompletionOption.ResponseContentRead);
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return new ContentResult { Content = text, ContentType = "text/plain", StatusCode = (int)res.StatusCode };

        var baseUri = new Uri(url);
        var sb = new StringBuilder(text.Length + 256);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
                line = System.Text.RegularExpressions.Regex.Replace(line, "URI=\"([^\"]+)\"", m => "URI=\"" + host + "/local/plex/seg?u=" + B64(new Uri(baseUri, m.Groups[1].Value).ToString()) + "\"");
            else if (line.Length > 0)
                line = host + "/local/plex/seg?u=" + B64(new Uri(baseUri, line).ToString());
            sb.Append(line).Append('\n');
        }
        return new ContentResult { Content = sb.ToString(), ContentType = "application/vnd.apple.mpegurl", StatusCode = 200 };
    }
}
