using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HomeMedia;

// Состояние домашних служб для экрана настроек оболочки: доступен или короткая ошибка, без адресов и ключей.
public class HomeDiagController : BaseController
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    [HttpGet, AllowAnonymous]
    [Route("home/diag")]
    public async Task<ActionResult> Diag()
    {
        var services = await Task.WhenAll(
            Run("TorrServer", Torr), Run("JacRed", Jac), Run("AniLibria", AniLibria), Run("AnimeGo", AnimeGo),
            Run("AnimeLib", AnimeLib), Run("Kodik", Kodik), Run("Телевидение", Tv));
        return Json(new { services });
    }

    static async Task<object> Run(string name, Func<Task<object>> probe)
    {
        try { return await probe(); }
        catch (Exception ex) { return Row(name, false, ex.GetType().Name, ""); }
    }

    static Task<object> AniLibria() => Anime("AniLibria", "https://anilibria.top/api/v1/app/search/releases?query=a");
    static Task<object> AnimeGo() => Anime("AnimeGo", "https://animego.me/");

    static async Task<object> Probe(string name, string url, string detail)
    {
        if (string.IsNullOrWhiteSpace(url)) return Row(name, false, "адрес не задан", "");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            int code = (int)res.StatusCode;
            if (code >= 200 && code < 500) return Row(name, true, "", detail);
            return Row(name, false, "HTTP " + code, detail);
        }
        catch (Exception ex) { return Row(name, false, ex.GetType().Name, detail); }
    }

    static object Row(string name, bool ok, string error, string detail) => new { name, ok, error, detail };

    static async Task<object> Torr()
    {
        string cur = HomeDb.Get("torrserver_url");
        if (string.IsNullOrEmpty(cur)) cur = Environment.GetEnvironmentVariable("HOME_TORRSERVER") ?? "";
        return await Probe("TorrServer", string.IsNullOrEmpty(cur) ? "" : cur.TrimEnd('/') + "/echo", "");
    }

    static async Task<object> Jac()
    {
        var hosts = JacredSearch.Hosts().Children<JObject>()
            .Where(h => (bool?)h["enable"] != false && !string.IsNullOrEmpty((string)h["host"]))
            .Select(h => ((string)h["host"]).TrimEnd('/'))
            .Distinct()
            .ToList();
        if (hosts.Count == 0) return Row("JacRed", false, "нет источников", "");
        var results = await Task.WhenAll(hosts.Select(async host =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, host);
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                int code = (int)res.StatusCode;
                return (ok: code >= 200 && code < 500, error: code >= 500 ? "HTTP " + code : "");
            }
            catch (Exception ex) { return (ok: false, error: ex.GetType().Name); }
        }));
        int up = results.Count(r => r.ok);
        if (up > 0) return Row("JacRed", true, "", up + " из " + hosts.Count);
        return Row("JacRed", false, results.Select(r => r.error).FirstOrDefault(e => !string.IsNullOrEmpty(e)) ?? "нет связи", "0 из " + hosts.Count);
    }

    static Task<object> Anime(string name, string url) => Probe(name, url, "");

    static Task<object> AnimeLib()
    {
        var st = AnimeCatalog.Status().Children<JObject>().FirstOrDefault(x => (string)x["id"] == "AnimeLib");
        bool has = (bool?)st?["has_token"] == true;
        return Task.FromResult(Row("AnimeLib", has, has ? "" : "нужен токен", ""));
    }

    static Task<object> Kodik() => Task.FromResult(Row("Kodik", true, "", "запасной поток"));

    static async Task<object> Tv()
    {
        string url = TvStore.BaseUrl;
        if (string.IsNullOrEmpty(url)) return Row("Телевидение", false, "адрес Channel Manager не задан", "");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url + "/playlist.m3u8");
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) return Row("Телевидение", false, "HTTP " + (int)res.StatusCode, "");
        }
        catch (Exception ex) { return Row("Телевидение", false, ex.GetType().Name, ""); }
        TvStore.EnsureEpg();
        int n = TvStore.EpgChannels;
        bool loading = TvStore.EpgBusy;
        string epg = TvStore.EpgError;
        double? age = TvStore.EpgAt == DateTime.MinValue ? (double?)null : Math.Round((DateTime.UtcNow - TvStore.EpgAt).TotalMinutes);
        string detail = loading ? "программа обновляется" : age == null ? "программа ещё не загружена" : "программа " + age + " мин назад, каналов " + n;
        if (!string.IsNullOrEmpty(epg)) return Row("Телевидение", false, epg, detail);
        if (!loading && age != null && age >= 180) return Row("Телевидение", false, "программа старше 3 часов", detail);
        if (!loading && n == 0 && age != null) return Row("Телевидение", false, "программа пустая", detail);
        return Row("Телевидение", true, "", detail);
    }
}
