using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace HomeMedia;

// Телевидение: каналы и программа передач из Channel Manager (адрес настраивается).
// Плейлист /playlist.m3u8 даёт каналы и ссылки, /xmltv — программу; сервер разбирает её и отдаёт клиентам
// короткий JSON (сейчас/далее), чтобы приложение не качало 100 МБ XMLTV.
public static class TvStore
{
    static readonly string cfgPath = Path.Combine(Directory.GetCurrentDirectory(), "database", "tv.json");
    static readonly string epgPath = Path.Combine(Directory.GetCurrentDirectory(), "database", "tv_epg.json");
    static readonly object fl = new();
    static readonly HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8), UseProxy = false }) { Timeout = TimeSpan.FromMinutes(4) };

    public class Chan
    {
        public string Id, Name, Logo, Epg, Url;
        public int Num;
    }

    public class Prog { public long S, E; public string T, D; }

    static List<Chan> channels = new();
    static DateTime channelsAt = DateTime.MinValue;
    static Dictionary<string, List<Prog>> epg = new();
    public static DateTime EpgAt = DateTime.MinValue;
    static bool epgBusy;
    static string epgError;

    public static JObject Config()
    {
        lock (fl)
        {
            try { if (File.Exists(cfgPath)) return JObject.Parse(File.ReadAllText(cfgPath)); } catch { }
            return new JObject { ["url"] = Environment.GetEnvironmentVariable("HOME_CM_URL") ?? "", ["enable"] = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME_CM_URL")) };
        }
    }

    public static void SaveConfig(JObject o)
    {
        lock (fl)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
            File.WriteAllText(cfgPath, o.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        channelsAt = DateTime.MinValue; // адрес сменился — перечитать
        EpgAt = DateTime.MinValue;
    }

    // последний канал хранится отдельно для каждого устройства (dev), без dev — общий запасной
    public static string LastFor(string dev)
    {
        var cfg = Config();
        if (!string.IsNullOrEmpty(dev) && cfg["last_by_dev"] is JObject m && m[dev] != null) return (string)m[dev];
        return (string)cfg["last"]; // новое устройство: пока нет своего — последний общий
    }

    public static void SetLast(string dev, string id)
    {
        var cfg = Config();
        if (string.IsNullOrEmpty(dev)) cfg["last"] = id;
        else
        {
            if (cfg["last_by_dev"] is not JObject m) cfg["last_by_dev"] = m = new JObject();
            m[dev] = id;
        }
        cfg["last_any"] = id;
        WriteConfig(cfg);
        Touch();
    }

    public static string Last
    {
        get => (string)Config()["last"];
        set
        {
            var cfg = Config();
            cfg["last"] = value;
            lock (fl)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
                File.WriteAllText(cfgPath, cfg.ToString(Newtonsoft.Json.Formatting.Indented));
            }
        }
    }

    public static string BaseUrl => ((string)Config()["url"] ?? "").TrimEnd('/');
    public static bool Enabled => (bool?)Config()["enable"] ?? true;

    static readonly Regex attr = new("([\\w-]+)=\"([^\"]*)\"", RegexOptions.Compiled);

    // тип первого источника канала в Channel Manager: ts — спутник (TVHeadend), hls — интернет; кэш 30 минут, заполняется в фоне
    static readonly Dictionary<string, (string t, DateTime at)> srcTypes = new();
    static bool srcBusy;

    public static string SrcType(string id)
    {
        lock (srcTypes) return srcTypes.TryGetValue(id, out var v) ? v.t : "";
    }

    public static void FillSrcTypesPublic(List<Chan> list) => FillSrcTypes(list);

    static void FillSrcTypes(List<Chan> list)
    {
        string b = BaseUrl;
        if (srcBusy || string.IsNullOrEmpty(b)) return;
        List<string> todo;
        lock (srcTypes) todo = list.Where(c => !srcTypes.TryGetValue(c.Id, out var v) || DateTime.UtcNow - v.at > TimeSpan.FromMinutes(30)).Select(c => c.Id).ToList();
        if (todo.Count == 0) return;
        srcBusy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                using var sem = new System.Threading.SemaphoreSlim(8);
                await Task.WhenAll(todo.Select(async id =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        string j = await http.GetStringAsync($"{b}/tsb/sources/{id}");
                        var arr = Newtonsoft.Json.Linq.JObject.Parse(j)["sources"] as Newtonsoft.Json.Linq.JArray;
                        string t = arr != null && arr.Count > 0 ? ((string)arr[0]["type"] ?? "") : "";
                        lock (srcTypes) srcTypes[id] = (t, DateTime.UtcNow);
                    }
                    catch { }
                    finally { sem.Release(); }
                }));
            }
            finally { srcBusy = false; }
        });
    }

    public static async Task<List<Chan>> Channels(bool force = false)
    {
        if (!force && channels.Count > 0 && DateTime.UtcNow - channelsAt < TimeSpan.FromMinutes(5)) return channels;
        string b = BaseUrl;
        if (string.IsNullOrEmpty(b)) return channels;
        string text = await http.GetStringAsync(b + "/playlist.m3u8");
        var list = ParsePlaylist(text);
        channels = list;
        channelsAt = DateTime.UtcNow;
        return list;
    }

    public static List<Chan> ParsePlaylist(string text)
    {
        var list = new List<Chan>();
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            if (!l.StartsWith("#EXTINF")) continue;
            string url = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
            if (!url.Contains("/stream/")) continue; // служебные строки («Обновить все»)
            var at = attr.Matches(l).Cast<Match>().ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            int comma = l.LastIndexOf(',');
            string name = at.TryGetValue("tvg-name", out var n) && n != "" ? n : (comma >= 0 ? l.Substring(comma + 1) : "");
            list.Add(new Chan
            {
                Id = url.Substring(url.LastIndexOf('/') + 1),
                Name = name.Trim(),
                Logo = at.TryGetValue("tvg-logo", out var lg) ? lg : null,
                Epg = at.TryGetValue("tvg-id", out var e) ? e : "",
                Url = url,
                // Preserve Channel Manager's numbers, including gaps after child filtering.
                // Old playlists without a channel number retain their positional fallback.
                Num = at.TryGetValue("tvg-chno", out var cn) && int.TryParse(cn, out var num) && num > 0 ? num : list.Count + 1
            });
        }
        return list;
    }

    static long ParseDt(string s)
    {
        // 20261018120000 +0300
        s = s.Trim();
        var dt = DateTime.ParseExact(s.Substring(0, 14), "yyyyMMddHHmmss", null);
        TimeSpan off = TimeSpan.Zero;
        if (s.Length >= 19)
        {
            int sign = s[15] == '-' ? -1 : 1;
            off = new TimeSpan(sign * int.Parse(s.Substring(16, 2)), sign * int.Parse(s.Substring(18, 2)), 0);
        }
        return new DateTimeOffset(dt, off).ToUnixTimeSeconds();
    }

    static void LoadEpgFile()
    {
        if (epg.Count > 0 || !File.Exists(epgPath)) return;
        try
        {
            var o = JObject.Parse(File.ReadAllText(epgPath));
            var d = new Dictionary<string, List<Prog>>();
            foreach (var p in (JObject)o["epg"])
                d[p.Key] = p.Value.Select(x => new Prog { S = (long)x[0], E = (long)x[1], T = (string)x[2], D = (string)x[3] }).ToList();
            epg = d;
            EpgAt = DateTime.Parse((string)o["at"]).ToUniversalTime();
        }
        catch { }
    }

    public static void ForceRefresh()
    {
        channelsAt = DateTime.MinValue;
        EpgAt = DateTime.MinValue;
        EnsureEpg();
    }

    // ---- «горячий» канал: Lampac держит открытым поток последнего канала, чтобы Channel Manager и тюнер уже работали ----
    // (холодный старт источника — от 0,5 до 10 с, поэтому кнопка «Телевидение» без этого запускается не мгновенно)
    static DateTime lastUse = DateTime.MinValue;
    static string keepId;
    static System.Threading.CancellationTokenSource keepCts;
    static bool keepLoopStarted;

    public static int KeepMinutes
    {
        get { var v = Config()["keep_min"]; return v == null ? 360 : (int)v; }   // по умолчанию 6 часов после последнего просмотра
        set { var cfg = Config(); cfg["keep_min"] = value; WriteConfig(cfg); }
    }

    static void WriteConfig(JObject cfg)
    {
        lock (fl)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
            File.WriteAllText(cfgPath, cfg.ToString(Newtonsoft.Json.Formatting.Indented));
        }
    }

    public static void Touch() { lastUse = DateTime.UtcNow; StartKeep(); }

    public static void StartKeep()
    {
        if (keepLoopStarted) return;
        keepLoopStarted = true;
        if (lastUse == DateTime.MinValue) lastUse = DateTime.UtcNow;   // после перезапуска считаем, что только что смотрели
        _ = Task.Run(KeepLoop);
    }

    static async Task KeepLoop()
    {
        while (true)
        {
            try
            {
                int keep = KeepMinutes;
                bool want = Enabled && keep != 0 && (keep < 0 || DateTime.UtcNow - lastUse < TimeSpan.FromMinutes(keep));
                string id = want ? (string)Config()["last_any"] ?? (string)Config()["last"] : null;
                if (id != keepId)
                {
                    keepCts?.Cancel();
                    keepCts = null; keepId = null;
                    if (id != null)
                    {
                        var chs = await Channels();
                        if (chs.Any(c => c.Id == id)) { keepId = id; keepCts = new(); _ = Task.Run(() => HoldStream(id, keepCts.Token)); }
                    }
                }
            }
            catch { }
            await Task.Delay(5000);
        }
    }

    // читаем поток канала «вхолостую»; при обрыве — переподключение
    static async Task HoldStream(string id, System.Threading.CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var res = await http.GetAsync(BaseUrl + "/stream/" + id, HttpCompletionOption.ResponseHeadersRead, ct);
                await using var s = await res.Content.ReadAsStreamAsync(ct);
                var buf = new byte[64 * 1024];
                while (await s.ReadAsync(buf, ct) > 0) { }
            }
            catch { }
            try { await Task.Delay(3000, ct); } catch { return; }
        }
    }

    static string warmId;
    static DateTime warmAt = DateTime.MinValue;

    // Прогрев: Channel Manager стартует источник канала не сразу (до ~8 с на подключение и буфер), поэтому,
    // пока пользователь подносит курсор к «Телевидение», держим канал открытым с сервера — плеер получит данные сразу.
    public static async Task<string> Warm(string dev)
    {
        Touch();
        var chs = await Channels();
        string id = LastFor(dev);
        if (string.IsNullOrEmpty(id) || !chs.Any(c => c.Id == id)) id = chs.FirstOrDefault()?.Id;
        if (id == null) return null;
        if (warmId == id && DateTime.UtcNow - warmAt < TimeSpan.FromSeconds(20)) return id;
        warmId = id; warmAt = DateTime.UtcNow;
        string url = BaseUrl + "/stream/" + id;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                await using var s = await res.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[64 * 1024];
                while (await s.ReadAsync(buf, cts.Token) > 0) { }
            }
            catch { }
        });
        return id;
    }

    public static bool EpgBusy => epgBusy;
    public static string EpgError => epgError;
    public static int EpgChannels => epg.Count;

    static System.Threading.Timer epgTimer;

    // программа передач обновляется сама: через полминуты после старта сервера и затем каждые 8 часов
    // (плюс при запросах плеера, если она старше 30 минут — см. EnsureEpg)
    public static void StartEpgTimer()
    {
        epgTimer ??= new System.Threading.Timer(_ => { try { EpgAt = DateTime.MinValue; EnsureEpg(); } catch { } }, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(8));
    }

    // фоновая загрузка программы: не блокирует ответы
    public static void EnsureEpg()
    {
        LoadEpgFile();
        if (epgBusy || DateTime.UtcNow - EpgAt < TimeSpan.FromMinutes(30)) return;
        epgBusy = true;
        _ = Task.Run(async () =>
        {
            try { await RefreshEpg(); epgError = null; }
            catch (Exception ex) { epgError = ex.GetType().Name + ": " + ex.Message; }
            finally { epgBusy = false; }
        });
    }

    static async Task RefreshEpg()
    {
        var chs = await Channels();
        var wanted = new HashSet<string>(chs.Select(c => c.Epg).Where(x => !string.IsNullOrEmpty(x)));
        var d = new Dictionary<string, List<Prog>>();
        long from = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        long to = DateTimeOffset.UtcNow.AddHours(48).ToUnixTimeSeconds();

        using var res = await http.GetAsync(BaseUrl + "/xmltv", HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        await using var s = await res.Content.ReadAsStreamAsync();
        using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
        xr.MoveToContent();
        while (!xr.EOF)
        {
            if (xr.NodeType == XmlNodeType.Element && xr.Name == "programme")
            {
                string ch = xr.GetAttribute("channel");
                if (ch != null && wanted.Contains(ch))
                {
                    var el = (XElement)XNode.ReadFrom(xr);
                    try
                    {
                        long st = ParseDt((string)el.Attribute("start")), en = ParseDt((string)el.Attribute("stop"));
                        if (en >= from && st <= to)
                        {
                            if (!d.TryGetValue(ch, out var l)) d[ch] = l = new List<Prog>();
                            string title = (string)el.Element("title") ?? "";
                            string desc = (string)el.Element("desc");
                            l.Add(new Prog { S = st, E = en, T = title, D = desc != null && desc.Length > 400 ? desc.Substring(0, 400) : desc });
                        }
                    }
                    catch { }
                    continue;
                }
                xr.Skip();
                continue;
            }
            xr.Read();
        }
        foreach (var l in d.Values) l.Sort((a, b) => a.S.CompareTo(b.S));
        epg = d;
        EpgAt = DateTime.UtcNow;
        try
        {
            var o = new JObject
            {
                ["at"] = EpgAt.ToString("o"),
                ["epg"] = new JObject(d.Select(p => new JProperty(p.Key, new JArray(p.Value.Select(x => new JArray(x.S, x.E, x.T, x.D))))))
            };
            File.WriteAllText(epgPath, o.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch { }
    }

    public static (Prog now, Prog next) NowNext(string epgId)
    {
        if (string.IsNullOrEmpty(epgId) || !epg.TryGetValue(epgId, out var l)) return (null, null);
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Prog now = null, next = null;
        for (int i = 0; i < l.Count; i++)
        {
            if (l[i].S <= t && l[i].E > t) { now = l[i]; next = i + 1 < l.Count ? l[i + 1] : null; break; }
            if (l[i].S > t) { next = l[i]; break; }
        }
        return (now, next);
    }

    public static List<Prog> Day(string epgId)
        => epg.TryGetValue(epgId ?? "", out var l) ? l : new List<Prog>();
}

// Напоминания «включить передачу»: отдельно для каждого устройства (dev). Сработавшие и устаревшие удаляются.
public static class RemindStore
{
    static readonly string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "tv_remind.json");
    static readonly object fl = new();

    static JObject Load()
    {
        try { if (File.Exists(path)) return JObject.Parse(File.ReadAllText(path)); } catch { }
        return new JObject();
    }

    static void Save(JObject o)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, o.ToString(Newtonsoft.Json.Formatting.None));
    }

    // список устройства; старше 15 минут после начала — выбрасываем
    public static JArray List(string dev)
    {
        lock (fl)
        {
            var o = Load();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var arr = o[dev] as JArray ?? new JArray();
            var keep = new JArray(arr.Where(x => (long)x["s"] > now - 900).OrderBy(x => (long)x["s"]));
            if (keep.Count != arr.Count) { o[dev] = keep; Save(o); }
            return keep;
        }
    }

    // поставить или снять; возвращает true, если теперь напоминание стоит
    public static bool Toggle(string dev, string id, long s, string title, string name)
    {
        lock (fl)
        {
            var o = Load();
            var arr = o[dev] as JArray ?? new JArray();
            var ex = arr.FirstOrDefault(x => (string)x["id"] == id && (long)x["s"] == s);
            bool on;
            if (ex != null) { ex.Remove(); on = false; }
            else { arr.Add(new JObject { ["id"] = id, ["s"] = s, ["t"] = title ?? "", ["n"] = name ?? "" }); on = true; }
            o[dev] = arr;
            Save(o);
            return on;
        }
    }

    public static void Done(string dev, string id, long s)
    {
        lock (fl)
        {
            var o = Load();
            if (o[dev] is JArray arr)
            {
                var ex = arr.FirstOrDefault(x => (string)x["id"] == id && (long)x["s"] == s);
                if (ex != null) { ex.Remove(); Save(o); }
            }
        }
    }
}

[ProfileResult]
public class TvGuideController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    static object Pr(TvStore.Prog p, bool full = false)
        => p == null ? null : full ? new { t = p.T, s = p.S, e = p.E, d = p.D } : (object)new { t = p.T, s = p.S, e = p.E };

    [HttpGet, AllowAnonymous]
    [Route("tv.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "tv.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("apps.js")]
    public ActionResult AppsPlugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "apps.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("tv/config")]
    public ActionResult Config() => Json(TvStore.Config());

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("tv/config/set")]
    public async Task<ActionResult> SetConfig(string url = null, bool? enable = null)
    {
        var cfg = TvStore.Config();
        if (url != null)
        {
            url = url.Trim();
            if (url != "" && !url.StartsWith("http")) url = "http://" + url;
            cfg["url"] = url.TrimEnd('/');
        }
        if (enable != null) cfg["enable"] = enable;
        TvStore.SaveConfig(cfg);
        try
        {
            var ch = await TvStore.Channels(true);
            return Json(new { ok = true, url = (string)cfg["url"], channels = ch.Count });
        }
        catch (Exception ex) { return Json(new { ok = false, url = (string)cfg["url"], error = ex.GetType().Name + ": " + ex.Message }); }
    }

    // последний просмотренный канал: плеер сообщает его сюда, меню «Телевидение» включает его сразу
    [HttpGet, AllowAnonymous]
    [Route("tv/last")]
    public ActionResult LastChannel(string set = null, string dev = null)
    {
        if (!string.IsNullOrEmpty(dev) && !Regex.IsMatch(dev, "^[0-9A-Za-z_-]{3,40}$")) dev = null;
        if (!string.IsNullOrEmpty(set) && Regex.IsMatch(set, "^[0-9A-Za-z]{4,32}$")) TvStore.SetLast(dev, set);
        return Json(new { id = TvStore.LastFor(dev) });
    }

    // прогреть последний канал устройства (вызывается, когда курсор встал на пункт «Телевидение»)
    [HttpGet, AllowAnonymous]
    [Route("tv/warm")]
    public async Task<ActionResult> WarmUp(string dev = null)
    {
        if (!string.IsNullOrEmpty(dev) && !Regex.IsMatch(dev, "^[0-9A-Za-z_-]{3,40}$")) dev = null;
        try { return Json(new { id = await TvStore.Warm(dev) }); }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 502); }
    }

    // сколько держать последний канал наготове: 0 — выкл, минуты, -1 — всегда
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("tv/keep")]
    public ActionResult Keep(int? min = null)
    {
        if (min != null) TvStore.KeepMinutes = min.Value;
        return Json(new { keep_min = TvStore.KeepMinutes });
    }

    // обновить каналы и программу сейчас
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("tv/refresh")]
    public async Task<ActionResult> Refresh()
    {
        try { await TvStore.Channels(true); } catch { }
        TvStore.ForceRefresh();
        return Json(new { ok = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("tv/status")]
    public async Task<ActionResult> Status()
    {
        if (!TvStore.Enabled) return Json(new { enable = false });
        try
        {
            var ch = await TvStore.Channels();
            TvStore.EnsureEpg();
            return Json(new
            {
                enable = true, ok = ch.Count > 0, url = TvStore.BaseUrl, channels = ch.Count, keep_min = TvStore.KeepMinutes,
                epg_channels = TvStore.EpgChannels, epg_loading = TvStore.EpgBusy, epg_error = TvStore.EpgError,
                epg_age_min = TvStore.EpgAt == DateTime.MinValue ? (double?)null : Math.Round((DateTime.UtcNow - TvStore.EpgAt).TotalMinutes)
            });
        }
        catch (Exception ex) { return Json(new { enable = true, ok = false, url = TvStore.BaseUrl, error = ex.GetType().Name + ": " + ex.Message }); }
    }

    [HttpGet, AllowAnonymous]
    [Route("tv/channels")]
    public async Task<ActionResult> Channels()
    {
        if (!TvStore.Enabled) return Json(new { error = "disabled" }, 404);
        try
        {
            var ch = await TvStore.Channels();
            TvStore.EnsureEpg();
            var list = ch.Select(c =>
            {
                var (n, x) = TvStore.NowNext(c.Epg);
                return new { id = c.Id, num = c.Num, name = c.Name, logo = c.Logo, epg = c.Epg, url = c.Url, src = TvStore.SrcType(c.Id), n = Pr(n), x = Pr(x) };
            }).ToList();
            TvStore.FillSrcTypesPublic(ch);
            return Json(new { channels = list, epg_loading = TvStore.EpgBusy });
        }
        catch (Exception ex) { return Json(new { error = "failed", message = ex.GetType().Name + ": " + ex.Message }, 502); }
    }

    // сейчас/далее для всех каналов, ключ — id потока (для плеера)
    [HttpGet, AllowAnonymous]
    [Route("tv/now")]
    public async Task<ActionResult> Now()
    {
        try
        {
            var ch = await TvStore.Channels();
            TvStore.EnsureEpg();
            var o = new Dictionary<string, object>();
            foreach (var c in ch)
            {
                var (n, x) = TvStore.NowNext(c.Epg);
                if (n != null || x != null) o[c.Id] = new { n = Pr(n), x = Pr(x) };
            }
            return Json(o);
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 502); }
    }

    // программа канала на ближайшие сутки
    [HttpGet, AllowAnonymous]
    [Route("tv/day")]
    public async Task<ActionResult> DayList(string epg = null, string id = null)
    {
        if (string.IsNullOrEmpty(epg) && !string.IsNullOrEmpty(id))
            epg = (await TvStore.Channels()).FirstOrDefault(c => c.Id == id)?.Epg;
        TvStore.EnsureEpg();
        var l = TvStore.Day(epg);
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Json(new { programs = l.Where(p => p.E > t - 3600).Select(p => Pr(p, true)) });
    }

    static string RDev(string dev) => !string.IsNullOrEmpty(dev) && Regex.IsMatch(dev, "^[0-9A-Za-z_-]{3,40}$") ? dev : "default";

    // напоминания устройства: [{id, s, t, n}]
    [HttpGet, AllowAnonymous]
    [Route("tv/remind")]
    public ActionResult RemindList(string dev = null) => Json(new { list = RemindStore.List(RDev(dev)) });

    // поставить/снять напоминание на передачу (id потока канала, s — начало, unix)
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("tv/remind/toggle")]
    public ActionResult RemindToggle(string dev = null, string id = null, long s = 0, string t = null, string n = null)
    {
        if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[0-9A-Za-z]{4,32}$") || s <= 0) return Json(new { error = "bad" }, 400);
        dev = RDev(dev);
        bool on = RemindStore.Toggle(dev, id, s, t, n);
        return Json(new { on, list = RemindStore.List(dev) });
    }

    // напоминание сработало — убрать
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("tv/remind/done")]
    public ActionResult RemindDone(string dev = null, string id = null, long s = 0)
    {
        if (!string.IsNullOrEmpty(id)) RemindStore.Done(RDev(dev), id, s);
        return Json(new { ok = true });
    }

    // общая синхронизация просмотренного: отдать всё новее since / принять отметки устройства
    [HttpGet, AllowAnonymous]
    [Route("home/tl/all")]
    public ActionResult TlAll(long since = 0, string epoch = null, string sbx = null)
    {
        if (!string.IsNullOrEmpty(sbx) && !Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return Json(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        string ep = TlV2.Epoch;
        if (epoch != null && epoch != ep) since = 0;               // другое поколение синхронизации: курсор клиента ничего не значит — полный снимок
        return Json(new { rows = TlStore.Since(since), epoch = ep });
    }

    [HttpPost, AllowAnonymous]
    [Route("home/tl/set")]
    public async Task<ActionResult> TlSet(string sbx = null)
    {
        if (!string.IsNullOrEmpty(sbx) && !Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return Json(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        try
        {
            using var sr = new StreamReader(Request.Body);
            var body = JToken.Parse(await sr.ReadToEndAsync());
            if(!Profiles.CheckProgressGrant(Profiles.Current,body)) return Json(new {error="progress_grant_scope"},403);
            // принимаем прежний массив {h,t,d,p,u,c}, объект {rows:[...]} и штатный формат TimeCode; на каждое событие — свой результат
            var (results, rev) = TlV2.Submit(body);
            int merged = results.Count(r => r.Status == "accepted");
            // диагностика: необычные исходы по событиям (принятые и повторы не пишем) — счётчиками, не строками
            foreach (var g in results.Where(r => r.Status != "accepted" && r.Status != "duplicate").GroupBy(r => r.Status + (r.Reason != null ? ":" + r.Reason : "")))
                DLogStore.Server(g.Key.StartsWith("stale") ? "i" : "w", "tl_set_" + g.First().Status, g.Key, "n=" + g.Count());
            if (results.Any(r => r.ResumeIgnored)) DLogStore.Server("w", "tl_resume_ignored", "", "n=" + results.Count(r => r.ResumeIgnored));
            bool retry = results.Any(r => r.Status == "retryable" && r.Reason != "clock_ahead");     // clock_ahead — ответ по конкретной строке при коде 200, остальные строки пачки подтверждаются как обычно
            return Json(new { ok = !retry, success = !retry, merged, results = results.Select(r => r.ToJson()), rev, epoch = TlV2.Epoch }, retry ? 503 : 200);
        }
        catch (Newtonsoft.Json.JsonException) { return Json(new { error = "bad_json", success = false }, 400); }
        catch (Exception ex) { DLogStore.Server("e", "tl_set_exception", ex.GetType().Name, ex.Message); return Json(new { error = ex.GetType().Name, message = ex.Message, success = false }, 503); }
    }

    // память звуковой дорожки по тайтлу
    [HttpGet, AllowAnonymous]
    [Route("home/audio")]
    public ActionResult AudioGet(string key = null)
    {
        if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, "^[0-9A-Za-z_.-]{1,60}$")) return Json(new { }, 400);
        var o = AudioStore.Get(key);
        return Json(o == null ? (object)new { } : new { label = (string)o["label"], lang = (string)o["lang"] });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/audio/set")]
    public ActionResult AudioSet(string key = null, string label = null, string lang = null)
    {
        if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, "^[0-9A-Za-z_.-]{1,60}$")) return Json(new { error = "bad" }, 400);
        AudioStore.Set(key, (label ?? "").Length > 200 ? label.Substring(0, 200) : label, (lang ?? "").Length > 12 ? lang.Substring(0, 12) : lang);
        return Json(new { ok = true });
    }

    // обои: картинки лежат в database/wallpapers на сервере, устройство выбирает любую
    static readonly string wallDir = Path.Combine(Directory.GetCurrentDirectory(), "database", "wallpapers");

    [HttpGet, AllowAnonymous]
    [Route("home/wallpapers")]
    public ActionResult WallList()
    {
        Directory.CreateDirectory(wallDir);
        var names = Directory.GetFiles(wallDir).Select(Path.GetFileName)
            .Where(n => Regex.IsMatch(n, @"\.(jpe?g|png|webp)$", RegexOptions.IgnoreCase)).OrderBy(n => n).ToList();
        return Json(new { files = names });
    }

    [HttpGet, AllowAnonymous]
    [Route("home/wallpaper")]
    public ActionResult WallFile(string name = null)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..")) return Json(new { error = "bad" }, 400);
        string p = Path.Combine(wallDir, name);
        if (!System.IO.File.Exists(p)) return Json(new { error = "not_found" }, 404);
        string ext = Path.GetExtension(p).ToLowerInvariant();
        string ct = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg";
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return PhysicalFile(p, ct);
    }

    // общие закладки устройств: отдать всё новее since / принять изменения
    [HttpGet, AllowAnonymous]
    [Route("home/bm/all")]
    public ActionResult BmAll(long since = 0) => Json(new { rows = BmStore.Since(since) });

    [HttpPost, AllowAnonymous]
    [Route("home/bm/set")]
    public async Task<ActionResult> BmSet()
    {
        try
        {
            using var sr = new StreamReader(Request.Body);
            var arr = JArray.Parse(await sr.ReadToEndAsync());
            return Json(new { ok = true, merged = BmStore.Merge(arr) });
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 400); }
    }
}
