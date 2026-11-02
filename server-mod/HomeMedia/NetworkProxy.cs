using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Connection secrets stay server-side. Existing installations are imported on read,
// without rewriting their settings until the administrator explicitly saves.
public static class NetworkProxy
{
    public static readonly string[] Services = { "tmdb_api", "tmdb_images", "kinopub", "youtube", "jacred" };
    static readonly object gate = new();
    const string Key = "network_proxy_config", Previous = "network_proxy_previous";
    static string InitPath => Path.Combine(Directory.GetCurrentDirectory(), "init.conf");
    static JObject Init() => JObject.Parse(File.ReadAllText(InitPath));
    static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    static string Revision(JObject config) => Hash(config.ToString(Formatting.None) + "\n" + (Init()["tmdb"]?.ToString(Formatting.None) ?? ""));
    static string NativeRoute(JObject init, string part)
    {
        var block = (init["tmdb"] as JObject)?[part] as JObject;
        if ((bool?)block?["useproxy"] != true) return "";
        var list = block?["proxy"]?["list"] as JArray;
        if (list == null && block?["globalnameproxy"] != null)
            list = (init["globalproxy"] as JArray)?.Children<JObject>().FirstOrDefault(p => (string)p["name"] == (string)block["globalnameproxy"])?["list"] as JArray;
        return (string)list?.FirstOrDefault() ?? "";
    }
    public static JObject Load()
    {
        string raw = HomeDb.Get(Key);
        if (!string.IsNullOrEmpty(raw)) return JObject.Parse(raw);
        var init = Init(); var connections = new JArray(); var routes = new JObject();
        string Add(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "direct";
            var found = connections.Children<JObject>().FirstOrDefault(p => (string)p["url"] == url);
            if (found != null) return (string)found["id"];
            string id = "p" + (connections.Count + 1); var uri = new Uri(url);
            string name = uri.Host == "192.168.1.61" ? "Mihomo · " + (uri.Scheme == "socks5" ? "SOCKS5" : "HTTP") : "Прокси " + (connections.Count + 1);
            connections.Add(new JObject { ["id"] = id, ["name"] = name, ["url"] = url }); return id;
        }
        routes["tmdb_api"] = Add(NativeRoute(init, "proxyapi"));
        routes["tmdb_images"] = Add(NativeRoute(init, "proxyimg"));
        string kp = Path.Combine(Directory.GetCurrentDirectory(), "database", "kinopub.json");
        routes["kinopub"] = Add(File.Exists(kp) ? (string)JObject.Parse(File.ReadAllText(kp))["proxy"] : Environment.GetEnvironmentVariable("HOME_KP_PROXY"));
        routes["youtube"] = Add(string.IsNullOrWhiteSpace(HomeDb.Get("yt_proxy")) ? Environment.GetEnvironmentVariable("HOME_YT_PROXY") : HomeDb.Get("yt_proxy"));
        routes["jacred"] = "direct";
        return new JObject { ["connections"] = connections, ["routes"] = routes };
    }
    public static string Resolve(JObject config, string service)
    {
        string id = (string)config["routes"]?[service];
        return id == "direct" ? "" : (string)(config["connections"] as JArray)?.Children<JObject>().FirstOrDefault(p => (string)p["id"] == id)?["url"] ?? "";
    }
    public static string Route(string service, string fallback)
    {
        string raw = HomeDb.Get(Key);
        return string.IsNullOrEmpty(raw) ? fallback ?? "" : Resolve(JObject.Parse(raw), service);
    }
    public static string Display(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "[некорректный адрес]";
        return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Authority);
    }
    public static WebProxy Web(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var uri = new Uri(url); var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        var proxy = new WebProxy(clean);
        if (!string.IsNullOrEmpty(uri.UserInfo)) {
            var pair = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(pair[0]), pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "");
        }
        return proxy;
    }
    public static JObject PublicState()
    {
        lock (gate) {
            var config = Load();
            return new JObject {
                ["revision"] = Revision(config), ["routes"] = config["routes"].DeepClone(),
                ["connections"] = new JArray(((JArray)config["connections"]).Children<JObject>().Select(p => new JObject {
                    ["id"] = p["id"], ["name"] = p["name"], ["url"] = Display((string)p["url"]),
                    ["has_auth"] = !string.IsNullOrEmpty(new Uri((string)p["url"]).UserInfo)
                })), ["can_rollback"] = !string.IsNullOrEmpty(HomeDb.Get(Previous))
            };
        }
    }
    public static JObject Validate(JObject body, JObject old)
    {
        if (body["connections"] is not JArray connections || connections.Count > 12 || body["routes"] is not JObject routes) throw new ArgumentException("invalid_config");
        var result = new JArray(); var ids = new HashSet<string>();
        foreach (var item in connections) {
            if (item is not JObject p) throw new ArgumentException("invalid_connection");
            string id = (string)p["id"], name = ((string)p["name"] ?? "").Trim(), url = ((string)p["url"] ?? "").Trim();
            if (!Regex.IsMatch(id ?? "", @"^[a-zA-Z0-9_-]{1,40}$") || id == "direct" || !ids.Add(id) || name.Length == 0 || name.Length > 80) throw new ArgumentException("invalid_connection");
            if (url.Length > 2048 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !new[] { "http", "https", "socks5" }.Contains(uri.Scheme) || uri.Host.Length == 0 || uri.Port < 1 || uri.Port > 65535 ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath is not ("" or "/")) throw new ArgumentException("invalid_proxy_url");
            var previous = ((JArray)old["connections"]).Children<JObject>().FirstOrDefault(x => (string)x["id"] == id);
            if (p["auth_from"] != null) {
                previous = ((JArray)old["connections"]).Children<JObject>().FirstOrDefault(x => (string)x["id"] == (string)p["auth_from"]);
                if (previous == null) throw new ArgumentException("invalid_auth_source");
            }
            var builder = new UriBuilder(uri); string username = (string)p["username"] ?? "", password = (string)p["password"] ?? "";
            if (username.Length + password.Length > 1024 || username.Any(char.IsControl) || password.Any(char.IsControl)) throw new ArgumentException("invalid_credentials");
            if ((bool?)p["clear_auth"] == true) { builder.UserName = ""; builder.Password = ""; }
            else if (username.Length > 0 || password.Length > 0) {
                var oldAuth = previous == null ? Array.Empty<string>() : new Uri((string)previous["url"]).UserInfo.Split(':', 2);
                builder.UserName = Uri.EscapeDataString(username.Length > 0 ? username : oldAuth.Length > 0 ? Uri.UnescapeDataString(oldAuth[0]) : "");
                builder.Password = Uri.EscapeDataString(password);
                if (builder.UserName.Length == 0) throw new ArgumentException("username_required");
            }
            else if (uri.UserInfo.Length == 0 && previous != null) {
                var auth = new Uri((string)previous["url"]).UserInfo.Split(':', 2);
                builder.UserName = auth[0]; builder.Password = auth.Length == 2 ? auth[1] : "";
            }
            result.Add(new JObject { ["id"] = id, ["name"] = name, ["url"] = builder.Uri.AbsoluteUri.TrimEnd('/') });
        }
        if (routes.Properties().Any(p => !Services.Contains(p.Name))) throw new ArgumentException("invalid_service");
        var selected = new JObject();
        foreach (string service in Services) {
            string id = (string)routes[service];
            if (id != "direct" && !ids.Contains(id ?? "")) throw new ArgumentException("invalid_route");
            selected[service] = id;
        }
        return new JObject { ["connections"] = result, ["routes"] = selected };
    }
    static void AtomicInit(string raw)
    {
        string temp = InitPath + ".proxy-next";
        try {
            File.WriteAllText(temp, raw);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(InitPath));
            File.Move(temp, InitPath, true);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static JObject Save(JObject body, bool rollback = false)
    {
        lock (gate) {
            var old = Load();
            if ((string)body["revision"] != Revision(old)) throw new InvalidOperationException("settings_changed");
            var init = Init(); var before = init.ToString(Formatting.Indented);
            JObject next; var tmdb = (init["tmdb"] as JObject)?.DeepClone() as JObject ?? new JObject();
            if (rollback) {
                string raw = HomeDb.Get(Previous); if (string.IsNullOrEmpty(raw)) throw new InvalidOperationException("no_previous_settings");
                var saved = JObject.Parse(raw); next = (JObject)saved["config"];
                foreach (string part in new[] { "proxyapi", "proxyimg" }) {
                    if (saved["tmdb"]?[part] == null || saved["tmdb"][part].Type == JTokenType.Null) tmdb.Remove(part); else tmdb[part] = saved["tmdb"][part].DeepClone();
                }
            } else {
                next = Validate(body, old);
                foreach (var pair in new[] { ("tmdb_api", "proxyapi"), ("tmdb_images", "proxyimg") }) {
                    string oldUrl = NativeRoute(init, pair.Item2), newUrl = Resolve(next, pair.Item1);
                    if (oldUrl == newUrl) continue;
                    var block = tmdb[pair.Item2] as JObject ?? new JObject();
                    block["useproxy"] = newUrl.Length > 0; block["globalnameproxy"] = null;
                    block["proxy"] = newUrl.Length > 0 ? new JObject { ["list"] = new JArray(newUrl) } : null;
                    tmdb[pair.Item2] = block;
                }
            }
            var previous = new JObject { ["config"] = old, ["tmdb"] = new JObject {
                ["proxyapi"] = init["tmdb"]?["proxyapi"]?.DeepClone(), ["proxyimg"] = init["tmdb"]?["proxyimg"]?.DeepClone()
            }};
            bool initChanged = !JToken.DeepEquals(init["tmdb"] ?? new JObject(), tmdb);
            if (!rollback && !initChanged && JToken.DeepEquals(old, next) && !string.IsNullOrEmpty(HomeDb.Get(Key))) {
                var unchanged = PublicState(); unchanged["ok"] = true; unchanged["tmdb_reload"] = false; return unchanged;
            }
            if (initChanged) { init["tmdb"] = tmdb; AtomicInit(init.ToString(Formatting.Indented)); }
            try {
                HomeDb.With(c => {
                    using var transaction = c.BeginTransaction();
                    HomeDb.Exec(c, "INSERT INTO kv(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v", ("@k", Previous), ("@v", previous.ToString(Formatting.None)));
                    HomeDb.Exec(c, "INSERT INTO kv(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v", ("@k", Key), ("@v", next.ToString(Formatting.None)));
                    transaction.Commit();
                });
            } catch { if (initChanged) AtomicInit(before); throw; }
            var state = PublicState(); state["ok"] = true; state["tmdb_reload"] = initChanged; return state;
        }
    }
}

public class NetworkProxyController : BaseController
{
    static readonly SemaphoreSlim probes = new(2);
    bool Admin() => Guard.Ok(Request) && Profiles.FromRequest(HttpContext)?.Admin == true;
    static ActionResult J(object o, int status = 200) => new ContentResult { Content = JsonConvert.SerializeObject(o), ContentType = "application/json; charset=utf-8", StatusCode = status };
    async Task<JObject> Body()
    {
        using var reader = new StreamReader(Request.Body); var buffer = new char[16385]; int used = 0, read;
        while (used < buffer.Length && (read = await reader.ReadAsync(buffer, used, buffer.Length - used)) > 0) used += read;
        if (used == buffer.Length) throw new ArgumentException("body_too_large");
        return JObject.Parse(new string(buffer, 0, used));
    }
    [HttpGet, AllowAnonymous, Route("home/admin/proxy")]
    public ActionResult State()
    {
        if (!Admin()) return J(new { error = "admin_required" }, 403);
        Response.Headers["Cache-Control"] = "no-store";
        return J(NetworkProxy.PublicState());
    }
    [HttpPost, AllowAnonymous, Route("home/admin/proxy"), Route("home/admin/proxy/rollback")]
    public async Task<ActionResult> Save()
    {
        if (!Admin()) return J(new { error = "admin_required" }, 403);
        try { return J(NetworkProxy.Save(await Body(), Request.Path.Value.EndsWith("/rollback"))); }
        catch (JsonException) { return J(new { error = "invalid_json" }, 400); }
        catch (ArgumentException e) { return J(new { error = e.Message }, 400); }
        catch (InvalidOperationException e) { return J(new { error = e.Message }, 409); }
        catch { return J(new { error = "save_failed" }, 500); }
    }
    [HttpPost, AllowAnonymous, Route("home/admin/proxy/test")]
    public async Task<ActionResult> Test()
    {
        if (!Admin()) return J(new { error = "admin_required" }, 403);
        bool held = false;
        try {
            var body = await Body(); string service = (string)body["service"];
            if (!NetworkProxy.Services.Contains(service)) throw new ArgumentException("invalid_service");
            var config = NetworkProxy.Validate(body, NetworkProxy.Load());
            if (!await probes.WaitAsync(0, HttpContext.RequestAborted)) return J(new { error = "probe_busy" }, 429);
            held = true; string proxy = NetworkProxy.Resolve(config, service);
            string url = service switch {
                "tmdb_api" => "https://api.themoviedb.org/3/configuration",
                "tmdb_images" => "https://image.tmdb.org/t/p/w92/8Gxv8gSFCU0XGDykEGv7zR1n2ua.jpg",
                "kinopub" => "https://api.srvkp.com/v1/user",
                "youtube" => "https://www.youtube.com/generate_204",
                _ => "https://jacred.su/api/v2.0/indexers/all/results?query=Matrix"
            };
            using var handler = new SocketsHttpHandler { Proxy = NetworkProxy.Web(proxy), UseProxy = proxy.Length > 0, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            var watch = Stopwatch.StartNew();
            if (service == "jacred") {
                string file = Path.Combine(Directory.GetCurrentDirectory(), "database", "kinopub.json");
                var hosts = System.IO.File.Exists(file) ? JObject.Parse(System.IO.File.ReadAllText(file))["search_hosts"] as JArray : null;
                hosts ??= new JArray(new JObject { ["host"] = "http://ns3bg91xvuqfvq9h.cfhttp.top" }, new JObject { ["host"] = "https://jacred.su" }, new JObject { ["host"] = "https://jr.maxvol.pro" });
                var targets = hosts.Children<JObject>().Where(h => (bool?)h["enable"] != false && Uri.TryCreate((string)h["host"], UriKind.Absolute, out var u) && u.Scheme is "http" or "https").Take(12).ToArray();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); budget.CancelAfter(TimeSpan.FromSeconds(12));
                var checks = await Task.WhenAll(targets.Select(async h => {
                    string address = ((string)h["host"]).TrimEnd('/') + "/api/v2.0/indexers/all/results?query=Matrix";
                    if (!string.IsNullOrEmpty((string)h["apikey"])) address += "&apikey=" + Uri.EscapeDataString((string)h["apikey"]);
                    try { using var r = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, budget.Token); return (int)r.StatusCode; }
                    catch { return 0; }
                }));
                int good = checks.Count(c => c is >= 200 and < 400);
                return J(new { ok = good > 0, milliseconds = watch.ElapsedMilliseconds,
                    message = targets.Length == 0 ? "Нет включённых источников JacRed." : "Доступно источников JacRed: " + good + " из " + targets.Length + ". Ответы: " + string.Join(", ", checks.Select(c => c == 0 ? "нет связи" : "HTTP " + c)) + ". Проверено соединение, без оценки результатов поиска." });
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "HomeMedia proxy connection test");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            int code = (int)response.StatusCode;
            // API 401 proves connectivity without using an account token; it does not prove playback.
            bool reachable = code is >= 200 and < 400 || code == 401 && service is "tmdb_api" or "kinopub";
            return J(new { ok = reachable, status = code, milliseconds = watch.ElapsedMilliseconds,
                message = reachable ? "Соединение установлено. Проверен доступ к сервису, без запуска видео." : code == 429 ? "Сервис ограничивает запросы (429)." : code == 407 ? "Прокси отклонил логин или пароль (407)." : "Сервис вернул HTTP " + code + "." });
        }
        catch (JsonException) { return J(new { error = "invalid_json" }, 400); }
        catch (ArgumentException e) { return J(new { error = e.Message }, 400); }
        catch (OperationCanceledException) { return J(new { ok = false, message = "Проверка отменена или истекло время ожидания." }); }
        catch { return J(new { ok = false, message = "Соединение не установлено. Проверьте адрес, порт и авторизацию прокси." }); }
        finally { if (held) probes.Release(); }
    }
}
