using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HomeMedia;

[ProfileResult]
public class WrestlingController : BaseController
{
    // путь и выключатель — в настройках локальной библиотеки (database/localmedia.json)
    static string Dir => (string)LocalCfg.Source("wrestling")?["path"] ?? "/mnt/wrestling";
    static bool On => LocalCfg.Enabled("wrestling");

    static readonly string[] Exts = { ".mp4", ".mkv", ".m4v", ".avi", ".ts", ".mov", ".webm" };

    static readonly Regex reShow = new Regex(
        @"^(?:WWE\s+)?(?:Friday\s+Night\s+)?(?<show>RAW|SmackDown)\b[\s._-]*(?<y>\d{4})[.\-_ ](?<m>\d{2})[.\-_ ](?<d>\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex rePpv = new Regex(
        @"^(?:WWE\s+)?(?<title>.+?)[\s._-]+(?<y>(?:19|20)\d{2})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex reQuality = new Regex(@"(?<![\d])(2160|1080|720|480)p?(?![\d])", RegexOptions.Compiled);

    // TMDB ids, проверено через Lampac /tmdb: WWE RAW и Friday Night SmackDown
    static readonly Dictionary<string, int> Tmdb = new()
    {
        ["raw"] = 4656,
        ["smackdown"] = 1549,
    };

    static object Parse(FileInfo f)
    {
        string name = Path.GetFileNameWithoutExtension(f.Name);
        string kind, show = null, title = null, date = null;
        int year = 0;
        int? tmdb = null;

        var m = reShow.Match(name);
        if (m.Success)
        {
            kind = "episode";
            show = m.Groups["show"].Value.Equals("raw", StringComparison.OrdinalIgnoreCase) ? "raw" : "smackdown";
            year = int.Parse(m.Groups["y"].Value);
            date = $"{m.Groups["y"].Value}-{m.Groups["m"].Value}-{m.Groups["d"].Value}";
            title = (show == "raw" ? "WWE Raw " : "WWE SmackDown ") + date;
            tmdb = Tmdb[show];
        }
        else
        {
            var p = rePpv.Match(name);
            if (p.Success)
            {
                kind = "ppv";
                title = p.Groups["title"].Value.Trim();
                year = int.Parse(p.Groups["y"].Value);
            }
            else
            {
                kind = "other";
                title = Regex.Replace(name, @"\s*\([^)]*\)\s*$", "").Trim();
            }
        }

        var q = reQuality.Match(name);

        return new
        {
            id = "wr-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(f.Name))).Substring(0, 12).ToLowerInvariant(),
            file = f.Name,
            kind,
            show,
            title,
            date,
            year,
            tmdb,
            quality = q.Success ? q.Groups[1].Value : null,
            size = f.Length,
            mtime = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds(),
            url = "/wrestling/file?name=" + Uri.EscapeDataString(f.Name),
        };
    }

    // Живой листинг: каждый запрос читает папку заново, без кеша.
    [HttpGet, AllowAnonymous]
    [Route("wrestling/list")]
    public ActionResult List()
    {
        if (!On)
            return ContentTo("{\"disabled\":true,\"items\":[]}", "application/json; charset=utf-8");

        if (!Directory.Exists(Dir))
            return ContentTo("{\"error\":\"folder not mounted\",\"items\":[]}", "application/json; charset=utf-8");

        var items = new DirectoryInfo(Dir)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => Exts.Contains(f.Extension.ToLowerInvariant()) && !f.Name.StartsWith("."))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(Parse)
            .ToList();

        bool watching = (bool?)LocalCfg.Source("wrestling")?["watching"] != false;
        return ContentTo(JsonConvert.SerializeObject(new { items, watching }), "application/json; charset=utf-8");
    }

    static readonly HttpClient Dump = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Сколько выпусков не просмотрено — считает сервер по текущему каталогу файлов и общей таблице отметок (хеш имени файла, как у Lampa).
    // Параметр set оставлен для совместимости со старыми клиентами и больше ничего не меняет.
    [HttpGet, AllowAnonymous]
    [Route("wrestling/unseen")]
    public ActionResult Unseen(int set = -1)
    {
        if (!On || !Directory.Exists(Dir)) return ContentTo("{\"unseen\":0,\"total\":0}", "application/json; charset=utf-8");
        var files = new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => Exts.Contains(f.Extension.ToLowerInvariant()) && !f.Name.StartsWith(".")).Select(f => f.Name).ToList();
        int unseen = 0;
        foreach (var n in files)
        {
            var r = TlStore.Get(KpBridge.LampaHash(n));
            if (r == null || !KpUnified.IsDone(r.T, r.D, r.P)) unseen++;
        }
        return ContentTo(JsonConvert.SerializeObject(new { unseen, total = files.Count }), "application/json; charset=utf-8");
    }

    [HttpGet, HttpHead, AllowAnonymous]
    [Route("wrestling/file")]
    public ActionResult File(string name)
    {
        if (!On) return NotFound();

        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.StartsWith("."))
            return BadRequest();

        string full = Path.Combine(Dir, name);
        if (!System.IO.File.Exists(full) || !Exts.Contains(Path.GetExtension(full).ToLowerInvariant()))
            return NotFound();

        string mime = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" => "video/mp2t",
            ".avi" => "video/x-msvideo",
            _ => "video/mp4",
        };

        return PhysicalFile(full, mime, enableRangeProcessing: true);
    }


    // Постер сериала «Wrestling» для строки «Я смотрю»: рисуется в SVG, внешних картинок не нужно
    [HttpGet, AllowAnonymous]
    [Route("wrestling/poster.svg")]
    public ActionResult Poster()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 600' width='400' height='600'>");
        sb.Append("<defs>");
        sb.Append("<linearGradient id='bg' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='#071129'/><stop offset='.55' stop-color='#1a2a66'/><stop offset='1' stop-color='#86102a'/></linearGradient>");
        sb.Append("<linearGradient id='gold' x1='0' y1='0' x2='0' y2='1'><stop offset='0' stop-color='#fff0b0'/><stop offset='1' stop-color='#c98a14'/></linearGradient>");
        sb.Append("<radialGradient id='glow' cx='.5' cy='.4' r='.55'><stop offset='0' stop-color='#fff' stop-opacity='.35'/><stop offset='1' stop-color='#fff' stop-opacity='0'/></radialGradient>");
        sb.Append("</defs>");
        sb.Append("<rect width='400' height='600' fill='url(#bg)'/>");
        sb.Append("<rect width='400' height='600' fill='url(#glow)'/>");
        // лучи прожекторов
        sb.Append("<g stroke='#fff' stroke-opacity='.09' stroke-width='26'>");
        foreach (var x in new[] { -60, 60, 160, 240, 340, 460 })
            sb.Append($"<line x1='200' y1='-20' x2='{x}' y2='360'/>");
        sb.Append("</g>");
        // золотой пояс-медальон со звездой
        sb.Append("<circle cx='200' cy='235' r='112' fill='#0a1330' stroke='url(#gold)' stroke-width='10'/>");
        sb.Append("<circle cx='200' cy='235' r='92' fill='none' stroke='url(#gold)' stroke-width='3' stroke-dasharray='3 7'/>");
        sb.Append("<path d='M200 150 l23 58 62 5 -47 40 15 61 -53 -33 -53 33 15 -61 -47 -40 62 -5z' fill='url(#gold)'/>");
        // канаты ринга
        sb.Append("<g fill='none' stroke-linecap='round'>");
        sb.Append("<path d='M-10 520 Q200 490 410 520' stroke='#e11d2e' stroke-width='9'/>");
        sb.Append("<path d='M-10 548 Q200 518 410 548' stroke='#f4f4f4' stroke-width='9'/>");
        sb.Append("<path d='M-10 576 Q200 546 410 576' stroke='#2f6bff' stroke-width='9'/>");
        sb.Append("</g>");
        sb.Append("<text x='200' y='425' text-anchor='middle' font-family='Arial Black, Impact, Arial, sans-serif' font-weight='900' font-size='58' letter-spacing='3' fill='url(#gold)' stroke='#2b1a00' stroke-width='1.2'>WRESTLING</text>");
        sb.Append("<text x='200' y='462' text-anchor='middle' font-family='Arial, sans-serif' font-weight='700' font-size='19' letter-spacing='4' fill='#dbe4ff'>RAW · SMACKDOWN · PPV</text>");
        sb.Append("</svg>");
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return ContentTo(sb.ToString(), "image/svg+xml; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("wrestling.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "wrestling.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }
}
