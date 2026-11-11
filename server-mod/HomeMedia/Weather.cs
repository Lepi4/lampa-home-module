using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace HomeMedia;

// Погода для часов: XML-информер Gismeteo, без ключа. Город хранится на сервере своим id.
// Название с панели само не сохраняется: сначала список пунктов, и только выбранный записывается.
public static class WeatherStore
{
    const string CityKey = "weather_city";
    const string CacheKey = "weather_now";
    const string Gis = "https://services.gismeteo.ru/inform-service/inf_chrome";
    static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);
    static readonly TimeSpan HideAfter = TimeSpan.FromHours(3);

    public static JObject City()
    {
        try { return JObject.Parse(HomeDb.Get(CityKey) ?? "{}"); }
        catch { return new JObject(); }
    }

    public static async Task<JObject> Current(bool force = false)
    {
        var city = City();
        int id = (int?)city["city_id"] ?? 0;
        if (id <= 0) return new JObject { ["configured"] = false };
        int rev = (int?)city["revision"] ?? 0;
        var cached = ReadCache();
        bool same = cached != null && (int?)cached["city_id"] == id && (int?)cached["revision"] == rev;
        long at = same ? (long?)cached["fetched_at"] ?? 0 : 0;
        bool fresh = same && at > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at < Fresh.TotalMilliseconds;
        if (!force && fresh) return View(city, cached, false);
        if (!await Gate.WaitAsync(0))
            return same ? View(city, cached, false) : new JObject { ["configured"] = true, ["pending"] = true, ["city_name"] = (string)city["city_name"] };
        try
        {
            cached = ReadCache();
            same = cached != null && (int?)cached["city_id"] == id && (int?)cached["revision"] == rev;
            at = same ? (long?)cached["fetched_at"] ?? 0 : 0;
            if (!force && same && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at < Fresh.TotalMilliseconds)
                return View(city, cached, false);
            try
            {
                var fact = await Forecast(id);
                if ((int?)fact["city_id"] != id) throw new InvalidOperationException("city_mismatch");
                fact["revision"] = rev;
                fact["fetched_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                HomeDb.Set(CacheKey, fact.ToString(Newtonsoft.Json.Formatting.None));
                return View(city, fact, false);
            }
            catch
            {
                if (!same) return new JObject { ["configured"] = true, ["city_name"] = (string)city["city_name"], ["error"] = "unavailable" };
                long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at;
                if (age > HideAfter.TotalMilliseconds)
                    return new JObject { ["configured"] = true, ["city_id"] = id, ["city_name"] = (string)city["city_name"], ["city_region"] = (string)city["city_region"], ["weather"] = false, ["stale"] = true };
                return View(city, cached, true);
            }
        }
        finally { Gate.Release(); }
    }

    public static async Task<JObject> Search(string query)
    {
        string q = (query ?? "").Trim();
        if (q.Length < 2 || q.Length > 80) return new JObject { ["error"] = "bad_query" };
        var geo = await Geocode(q);
        if (geo.Count == 0) return new JObject { ["error"] = "not_found", ["places"] = new JArray() };
        if (Spread(geo) > 30)
            return new JObject { ["step"] = "geo", ["places"] = new JArray(geo.Select(PlaceGeo)) };
        return new JObject { ["step"] = "gismeteo", ["places"] = await Near((double)geo[0]["lat"], (double)geo[0]["lon"]) };
    }

    public static async Task<JArray> Near(double lat, double lon)
    {
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return new JArray();
        string url = Gis + "/cities/?lat=" + lat.ToString(CultureInfo.InvariantCulture) + "&lng=" + lon.ToString(CultureInfo.InvariantCulture) + "&count=8&lang=ru";
        string xml = await GetText(url);
        var list = new List<JObject>();
        foreach (var item in Elements(xml, "item"))
        {
            if (!int.TryParse(Attr(item, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0) continue;
            if (!double.TryParse(Attr(item, "distance"), NumberStyles.Float, CultureInfo.InvariantCulture, out double km)) km = 999;
            if (km > 40) continue;
            if (!double.TryParse(Attr(item, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double la)) continue;
            if (!double.TryParse(Attr(item, "lng"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)) continue;
            list.Add(new JObject
            {
                ["id"] = id,
                ["name"] = Attr(item, "n"),
                ["region"] = Attr(item, "district_name"),
                ["kind"] = Attr(item, "kind"),
                ["distance"] = Math.Round(km, 1),
                ["lat"] = la,
                ["lon"] = lo
            });
        }
        return new JArray(list.OrderBy(x => (double)x["distance"]).Take(8));
    }

    public static async Task<JObject> Save(int id, string name, string region, double lat, double lon)
    {
        if (id <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > 80) return null;
        var fact = await Forecast(id);
        if ((int?)fact["city_id"] != id) return null;
        var prev = City();
        int rev = (int?)prev["revision"] ?? 0;
        if ((int?)prev["city_id"] != id) rev++;
        var city = new JObject
        {
            ["provider"] = "gismeteo_informer",
            ["city_id"] = id,
            ["city_name"] = name.Trim(),
            ["city_region"] = (region ?? "").Trim(),
            ["latitude"] = lat,
            ["longitude"] = lon,
            ["revision"] = rev
        };
        HomeDb.Set(CityKey, city.ToString(Newtonsoft.Json.Formatting.None));
        fact["revision"] = rev;
        fact["fetched_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HomeDb.Set(CacheKey, fact.ToString(Newtonsoft.Json.Formatting.None));
        return View(city, fact, false);
    }

    static JObject PlaceGeo(JObject g) => new JObject
    {
        ["name"] = (string)g["name"],
        ["region"] = (string)g["region"],
        ["lat"] = g["lat"],
        ["lon"] = g["lon"]
    };

    static async Task<List<JObject>> Geocode(string name)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(name) + "&count=6&language=ru&format=json";
        string body = await GetText(url);
        var doc = JObject.Parse(body);
        var all = new List<JObject>();
        foreach (var r in (doc["results"] as JArray ?? new JArray()).Children<JObject>())
        {
            double lat = (double?)r["latitude"] ?? 999, lon = (double?)r["longitude"] ?? 999;
            if (lat > 90 || lon > 180) continue;
            string region = string.Join(", ", new[] { (string)r["admin1"], (string)r["country"] }.Where(s => !string.IsNullOrWhiteSpace(s)));
            all.Add(new JObject { ["name"] = (string)r["name"], ["region"] = region, ["lat"] = lat, ["lon"] = lon, ["cc"] = (string)r["country_code"] });
        }
        var ru = all.Where(x => (string)x["cc"] == "RU").ToList();
        return (ru.Count > 0 ? ru : all).Take(6).ToList();
    }

    static double Spread(List<JObject> pts)
    {
        double max = 0;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
                max = Math.Max(max, Km((double)pts[i]["lat"], (double)pts[i]["lon"], (double)pts[j]["lat"], (double)pts[j]["lon"]));
        return max;
    }

    static double Km(double la1, double lo1, double la2, double lo2)
    {
        double r = 6371, p = Math.PI / 180;
        double x = (la2 - la1) * p, y = (lo2 - lo1) * p;
        double a = Math.Sin(x / 2) * Math.Sin(x / 2) + Math.Cos(la1 * p) * Math.Cos(la2 * p) * Math.Sin(y / 2) * Math.Sin(y / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    static async Task<JObject> Forecast(int id)
    {
        string xml = await GetText(Gis + "/forecast/?city=" + id + "&lang=ru");
        string locId = null, icon = "", descr = "", tflt = null;
        foreach (var el in Elements(xml, null))
        {
            if (el.Name == "location") locId = Attr(el, "id");
            if (el.Name == "values" && tflt == null)
            {
                tflt = Attr(el, "tflt");
                icon = Attr(el, "icon");
                descr = Attr(el, "descr");
            }
        }
        if (locId == null || tflt == null) throw new InvalidOperationException("bad_forecast");
        if (!int.TryParse(locId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int got) || got != id)
            throw new InvalidOperationException("city_mismatch");
        if (!double.TryParse(tflt, NumberStyles.Float, CultureInfo.InvariantCulture, out double temp) || temp < -80 || temp > 60)
            throw new InvalidOperationException("bad_temp");
        return new JObject
        {
            ["city_id"] = id,
            ["temp"] = temp,
            ["descr"] = descr,
            ["icon"] = icon,
            ["condition"] = Condition(icon)
        };
    }

    public static string Condition(string icon)
    {
        string s = (icon ?? "").ToLowerInvariant();
        int dot = s.LastIndexOf('.');
        string code = dot >= 0 ? s.Substring(dot + 1) : s;
        if (code.StartsWith("s")) return "snow";
        if (code.StartsWith("r") || code.StartsWith("d")) return "rain";
        if (code.StartsWith("t")) return "storm";
        if (code == "n" || code == "c0" || code == "c1") return "clear";
        if (code == "c2") return "partly";
        if (code == "c3" || code == "c4") return "cloudy";
        return "neutral";
    }

    static JObject View(JObject city, JObject fact, bool stale) => new JObject
    {
        ["configured"] = true,
        ["city_id"] = city["city_id"],
        ["city_name"] = city["city_name"],
        ["city_region"] = city["city_region"],
        ["revision"] = city["revision"],
        ["temp"] = fact["temp"],
        ["descr"] = fact["descr"],
        ["icon"] = fact["icon"],
        ["condition"] = Condition((string)fact["icon"]),
        ["fetched_at"] = fact["fetched_at"],
        ["stale"] = stale,
        ["weather"] = true
    };

    static JObject ReadCache()
    {
        try { return JObject.Parse(HomeDb.Get(CacheKey) ?? ""); }
        catch { return null; }
    }

    static async Task<string> GetText(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https") throw new InvalidOperationException("bad_host");
        if (uri.Host != "services.gismeteo.ru" && uri.Host != "geocoding-api.open-meteo.com") throw new InvalidOperationException("bad_host");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "lampa-home-weather");
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if ((int)res.StatusCode != 200) throw new InvalidOperationException("http_" + (int)res.StatusCode);
        var buf = new byte[1024 * 1024];
        using var stream = await res.Content.ReadAsStreamAsync();
        int n = 0, read;
        while (n < buf.Length && (read = await stream.ReadAsync(buf, n, buf.Length - n)) > 0) n += read;
        if (n == buf.Length) throw new InvalidOperationException("too_large");
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    static IEnumerable<XmlReader> Elements(string xml, string name)
    {
        var set = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new StringReader(xml), set);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (name != null && reader.Name != name) continue;
            yield return reader;
        }
    }

    static string Attr(XmlReader reader, string name)
    {
        string v = reader.GetAttribute(name);
        return v ?? "";
    }
}

public class WeatherController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    [HttpGet, AllowAnonymous]
    [Route("home/weather")]
    public async Task<ActionResult> Get()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        return Json(await WeatherStore.Current());
    }

    [HttpGet, AllowAnonymous]
    [Route("home/weather/search")]
    public async Task<ActionResult> Search(string q = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        try { return Json(await WeatherStore.Search(q)); }
        catch { return Json(new { error = "unavailable" }, 502); }
    }

    [HttpGet, AllowAnonymous]
    [Route("home/weather/near")]
    public async Task<ActionResult> Near(double lat = 999, double lon = 999)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        try { return Json(new { step = "gismeteo", places = await WeatherStore.Near(lat, lon) }); }
        catch { return Json(new { error = "unavailable" }, 502); }
    }

    [HttpPost, AllowAnonymous]
    [Route("home/weather/city")]
    public async Task<ActionResult> City()
    {
        if (!Guard.AdminWrite(Request, HttpContext)) return Json(new { error = "forbidden" }, 403);
        string body;
        using (var rd = new StreamReader(Request.Body, Encoding.UTF8)) body = await rd.ReadToEndAsync();
        JObject o;
        try { o = JObject.Parse(body); } catch { return Json(new { error = "bad_body" }, 400); }
        int id = (int?)o["id"] ?? 0;
        var saved = await WeatherStore.Save(id, (string)o["name"], (string)o["region"], (double?)o["lat"] ?? 999, (double?)o["lon"] ?? 999);
        if (saved == null) return Json(new { error = "not_found" }, 404);
        return Json(saved);
    }
}
