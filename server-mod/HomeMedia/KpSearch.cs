using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Агрегатор торрент-индексаторов: несколько JacRed/Jackett одним адресом. JacRed в Lampac (webApiHost)
// обращается сюда как к обычному JacRed, а мы опрашиваем все источники параллельно, склеиваем дубли по
// хешу раздачи и переживаем падение любого из них.
// Источники — kinopub.json, ключ search_hosts: [{ "host": "...", "apikey": "...", "enable": true }].
public static class JacredSearch
{
    static readonly object clientGate = new();
    static HttpClient client;
    static string clientFor;
    static HttpClient Client()
    {
        lock (clientGate)
        {
            string proxy = NetworkProxy.Route("jacred", "");
            if (client == null || clientFor != proxy)
            {
                client = new HttpClient(new SocketsHttpHandler
                {
                    Proxy = NetworkProxy.Web(proxy), UseProxy = proxy.Length > 0,
                    ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                }) { Timeout = TimeSpan.FromSeconds(7) };
                clientFor = proxy;
            }
            return client;
        }
    }

    static readonly Regex Btih = new("btih:([0-9a-fA-F]{40}|[A-Za-z2-7]{32})", RegexOptions.Compiled);

    internal static JArray Hosts()
    {
        var cfg = KpStore.Load();
        if (cfg["search_hosts"] is not JArray hosts)
        {
            hosts = new JArray
            {
                new JObject { ["host"] = "http://ns3bg91xvuqfvq9h.cfhttp.top", ["enable"] = true },
                new JObject { ["host"] = "https://jacred.su", ["enable"] = true },
                new JObject { ["host"] = "https://jr.maxvol.pro", ["enable"] = true },
            };
            cfg["search_hosts"] = hosts;
            KpStore.Write(cfg);
        }
        return hosts;
    }

    static string KeyOf(JObject r)
    {
        var m = Btih.Match((string)r["MagnetUri"] ?? "");
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        string d = (string)r["Details"];
        return !string.IsNullOrEmpty(d) ? d : $"{(string)r["Title"]}|{(string)r["Size"]}";
    }

    public static async Task<JArray> Aggregate(string qs, bool applyHideFilter = false, string profileId = null)
    {
        qs = KpSearchController.CleanQuery(qs);
        var hosts = Hosts().Children<JObject>().Where(h => (bool?)h["enable"] != false && !string.IsNullOrEmpty((string)h["host"])).ToList();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var http = Client();
        var tasks = hosts.Select(async h =>
        {
            string host = ((string)h["host"]).TrimEnd('/');
            try
            {
                string url = $"{host}/api/v2.0/indexers/all/results?{qs}";
                string key = (string)h["apikey"];
                if (!string.IsNullOrEmpty(key)) url += "&apikey=" + Uri.EscapeDataString(key);
                string body = await http.GetStringAsync(url, cts.Token);
                return (JObject.Parse(body)["Results"] as JArray ?? new JArray()).Children<JObject>().ToList();
            }
            catch { return new List<JObject>(); }
        }).ToList();
        var lists = await Task.WhenAll(tasks);
        var merged = new Dictionary<string, JObject>();
        var order = new List<string>();
        foreach (var list in lists)
        {
            foreach (var r in list)
            {
                string k = KeyOf(r);
                if (!merged.TryGetValue(k, out var cur))
                {
                    merged[k] = r;
                    order.Add(k);
                    continue;
                }
                var trackers = ((string)cur["Tracker"] ?? "").Split(',').Select(t => t.Trim())
                    .Concat(((string)r["Tracker"] ?? "").Split(',').Select(t => t.Trim()))
                    .Where(t => t.Length > 0).Distinct();
                cur["Tracker"] = string.Join(", ", trackers);
                int s1 = int.TryParse((string)cur["Seeders"], out int a) ? a : 0;
                int s2 = int.TryParse((string)r["Seeders"], out int b) ? b : 0;
                if (s2 > s1) { cur["Seeders"] = (string)r["Seeders"]; cur["Peers"] = (string)r["Peers"] ?? (string)cur["Peers"]; }
            }
        }
        IEnumerable<string> keys = order;
        if (applyHideFilter && !string.IsNullOrEmpty(profileId))
            keys = keys.Where(k => !ReleasePolicy.Hide(merged[k], profileId));
        return new JArray(keys.Select(k => merged[k]));
    }
}

public class KpSearchController : BaseController
{
    static readonly object clientGate = new();
    static HttpClient client;
    static string clientFor;
    static HttpClient Client()
    {
        lock (clientGate) {
            string proxy = NetworkProxy.Route("jacred", "");
            if (client == null || clientFor != proxy) {
                client = new HttpClient(new SocketsHttpHandler { Proxy = NetworkProxy.Web(proxy), UseProxy = proxy.Length > 0,
                    ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(7) };
                clientFor = proxy;
            }
            return client;
        }
    }
    static readonly Regex Btih = new("btih:([0-9a-fA-F]{40}|[A-Za-z2-7]{32})", RegexOptions.Compiled);

    static readonly Dictionary<string, string> LastStatus = new();

    static JArray Hosts() => JacredSearch.Hosts();

    // query-строка без apikey (у каждого источника свой)
    internal static string CleanQuery(string raw)
    {
        var parts = (raw ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("apikey=", StringComparison.OrdinalIgnoreCase));
        return string.Join("&", parts);
    }

    static string KeyOf(JObject r)
    {
        var m = Btih.Match((string)r["MagnetUri"] ?? "");
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        string d = (string)r["Details"];
        return !string.IsNullOrEmpty(d) ? d : $"{(string)r["Title"]}|{(string)r["Size"]}";
    }

    [HttpGet, AllowAnonymous]
    [Route("jr/api/v2.0/indexers/all/results")]
    public async Task<ActionResult> Results()
    {
        if (SessionRequired(out var sessionEarly))
            return new ContentResult { Content = "{\"error\":\"session_required\"}", ContentType = "application/json; charset=utf-8", StatusCode = 401 };
        string profileIdEarly = sessionEarly?.Id;

        string qs = CleanQuery(Request.QueryString.Value);
        var hosts = Hosts().Children<JObject>().Where(h => (bool?)h["enable"] != false && !string.IsNullOrEmpty((string)h["host"])).ToList();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var http = Client();

        var tasks = hosts.Select(async h =>
        {
            string host = ((string)h["host"]).TrimEnd('/');
            var sw = Stopwatch.StartNew();
            try
            {
                string url = $"{host}/api/v2.0/indexers/all/results?{qs}";
                string key = (string)h["apikey"];
                if (!string.IsNullOrEmpty(key)) url += "&apikey=" + Uri.EscapeDataString(key);

                string body = await http.GetStringAsync(url, cts.Token);
                var arr = JObject.Parse(body)["Results"] as JArray ?? new JArray();
                lock (LastStatus) LastStatus[host] = $"ok {arr.Count} за {sw.ElapsedMilliseconds} мс";
                return (ok: true, rows: arr.Children<JObject>().ToList());
            }
            catch (Exception ex)
            {
                lock (LastStatus) LastStatus[host] = "ошибка " + ex.GetType().Name;
                return (ok: false, rows: new List<JObject>());
            }
        }).ToList();

        var lists = await Task.WhenAll(tasks);
        bool anyOk = lists.Any(x => x.ok);
        if (hosts.Count > 0 && !anyOk)
            return new ContentResult { Content = "{\"error\":\"search_upstream_unavailable\"}", ContentType = "application/json; charset=utf-8", StatusCode = 502 };

        // склейка дублей: трекеры объединяем, сидов берём максимум
        var merged = new Dictionary<string, JObject>();
        var order = new List<string>();
        foreach (var list in lists.Select(x => x.rows))
        {
            foreach (var r in list)
            {
                string k = KeyOf(r);
                if (!merged.TryGetValue(k, out var cur))
                {
                    merged[k] = r;
                    order.Add(k);
                    continue;
                }

                var trackers = ((string)cur["Tracker"] ?? "").Split(',').Select(t => t.Trim())
                    .Concat(((string)r["Tracker"] ?? "").Split(',').Select(t => t.Trim()))
                    .Where(t => t.Length > 0).Distinct();
                cur["Tracker"] = string.Join(", ", trackers);

                int s1 = int.TryParse((string)cur["Seeders"], out int a) ? a : 0;
                int s2 = int.TryParse((string)r["Seeders"], out int b) ? b : 0;
                if (s2 > s1) { cur["Seeders"] = (string)r["Seeders"]; cur["Peers"] = (string)r["Peers"] ?? (string)cur["Peers"]; }
            }
        }

        string profileId = profileIdEarly;
        var results = new JArray(order.Where(k => profileId == null || !ReleasePolicy.Hide(merged[k], profileId)).Select(k => merged[k]));
        return Content(JsonConvert.SerializeObject(new { Results = results }), "application/json; charset=utf-8");
    }

    bool SessionRequired(out Profiles.Scope session)
    {
        session = Profiles.FromRequest(HttpContext);
        return Profiles.Enabled && session == null;
    }

    [HttpGet, AllowAnonymous]
    [Route("jr/state")]
    public ActionResult State()
    {
        if (SessionRequired(out _))
            return new ContentResult { Content = "{\"error\":\"session_required\"}", ContentType = "application/json; charset=utf-8", StatusCode = 401 };
        lock (LastStatus)
            return Content(string.Join("\n", Hosts().Children<JObject>().Select(h =>
                $"{(string)h["host"]}  [{((bool?)h["enable"] != false ? "вкл" : "выкл")}]  {(LastStatus.TryGetValue(((string)h["host"]).TrimEnd('/'), out var s) ? s : "ещё не опрашивался")}")),
                "text/plain; charset=utf-8");
    }
}
