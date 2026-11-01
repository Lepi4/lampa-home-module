using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HomeMedia;

// Дополнения к карточкам Lampa: плагин cardplus.js (качество на любых постерах, сезоны/серии, цвет рейтинга)
// и данные для него: качество фильмов (по релизам JacRed) и рейтинги IMDb/Кинопоиск из kino.pub.
public class CardPlusController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    string Prof() => KpStore.ResolveProfile(Request.Headers["X-KP-Profile"].FirstOrDefault() ?? Request.Query["profile"].FirstOrDefault());

    [HttpGet, AllowAnonymous]
    [Route("cardplus.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "cardplus.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    // Качество набора фильмов: тело [{id, title, year}] -> { "<id>": "WEBDL", ... } (пустая строка — релизов не найдено)
    [HttpPost, AllowAnonymous]
    [Route("kp/quality")]
    public async Task<ActionResult> Quality()
    {
        try
        {
            string body;
            using (var sr = new StreamReader(Request.Body)) body = await sr.ReadToEndAsync();
            var arr = JArray.Parse(body);

            var tasks = arr.Children<JObject>().Take(15).Select(async o =>
            {
                long id = (long?)o["id"] ?? 0;
                string title = (string)o["title"];
                string year = (string)o["year"];
                bool tv = (bool?)o["tv"] == true;
                if (id <= 0 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(year)) return (tv, id, "");
                return (tv, id, await CardQuality.Lookup(id, title, year, tv));
            }).ToList();

            var res = new JObject();
            foreach (var (tv, id, q) in await Task.WhenAll(tasks))
                if (id > 0) res[(tv ? "t" : "m") + id] = q ?? "";
            CardQuality.SaveNow();
            return Json(res);
        }
        catch (Exception ex) { return Json(new { error = ex.GetType().Name }, 400); }
    }

    static readonly ConcurrentDictionary<string, (long at, JObject r)> ratCache = new();

    // Рейтинги IMDb и Кинопоиск для тайтла, который есть в kino.pub: ?type=movie|tv&tmdb=ID
    [HttpGet, AllowAnonymous]
    [Route("kp/ratings")]
    public async Task<ActionResult> Ratings(string type, long tmdb)
    {
        if ((type != "movie" && type != "tv") || tmdb <= 0) return Json(new { error = "bad_args" }, 400);
        string key = type + ":" + tmdb;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ratCache.TryGetValue(key, out var hit) && now - hit.at < 24 * 3600) return Json(hit.r);

        var r = new JObject();
        try
        {
            string prof = Prof();
            string tok = await KpStore.Token(prof);
            long kp = tok != null ? await KpMap.LookupKp(type, tmdb, prof) : 0;
            if (kp > 0)
            {
                var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/{kp}", tok);
                if (code == 200)
                {
                    var item = JObject.Parse(body)["item"] as JObject;
                    if (item != null)
                    {
                        void put(string name, string src)
                        {
                            double? v = (double?)item[src];
                            if (v.HasValue && v.Value > 0) r[name] = Math.Round(v.Value, 1);
                        }
                        put("imdb", "imdb_rating");
                        put("kp", "kinopoisk_rating");
                        long? iv = (long?)item["imdb_votes"], kv = (long?)item["kinopoisk_votes"];
                        if (iv.HasValue && iv.Value > 0) r["imdb_votes"] = iv.Value;
                        if (kv.HasValue && kv.Value > 0) r["kp_votes"] = kv.Value;
                    }
                }
            }
        }
        catch { }
        ratCache[key] = (now, r);
        return Json(r);
    }
}
