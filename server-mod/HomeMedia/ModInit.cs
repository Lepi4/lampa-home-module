using Shared;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shared.Models.Base;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;

namespace HomeMedia;

public class ModInit : IModuleLoaded
{
    public static string modpath;

    // Один общий пользователь на всю локальную сеть: закладки, история и позиции просмотра
    // (модули Sync/TimeCode/Storage) хранятся на сервере под этим идентификатором, а не по клиентам.
    public const string SharedUid = "home";

    static bool Middleware(bool first, EventMiddleware e)
    {
        if (first)
        {
            Profiles.SetScope(null);
            var user = Profiles.ProgressOwner(e.httpContext) ?? Profiles.FromRequest(e.httpContext);
            if (user != null) Profiles.SetScope(user);
            string requestedPath = e.httpContext.Request.Path.Value ?? "";
            // Браузер всегда открывает /lampa: там есть сеанс и скрипт профиля. Приставка (lampa_client) со своим cookie остаётся на корне.
            // Тело ответа нужно записать сразу: иначе штатная страница Lampa затирает код 302 и каталог открывается без сеанса.
            string ua=e.httpContext.Request.Headers["User-Agent"].ToString()??"";
            bool appClient=ua.Contains("lampa_client")||ua.Contains("McShell");
            if(Profiles.Enabled&&requestedPath=="/"&&e.httpContext.Request.Headers["X-Home-Web-Render"]!="1"&&(!appClient||user==null))
            {
                string target="/lampa"+e.httpContext.Request.QueryString.Value;
                e.httpContext.Response.StatusCode=302;
                e.httpContext.Response.Headers["Location"]=target;
                e.httpContext.Response.Headers["Cache-Control"]="no-store";
                e.httpContext.Response.ContentType="text/html; charset=utf-8";
                e.httpContext.Response.WriteAsync("<!doctype html><meta http-equiv=\"refresh\" content=\"0;url="+target+"\">").GetAwaiter().GetResult();
                return false;
            }
            bool publicProfileRoute = requestedPath == "/home/profiles" || requestedPath == "/home/session/bootstrap" || requestedPath == "/home/session/login" || requestedPath == "/home/session/renew" || requestedPath == "/home/admin/login" || requestedPath == "/home/admin/session" || requestedPath == "/home/session/entry";
            bool jrResults = requestedPath == "/jr/api/v2.0/indexers/all/results";
            bool apiResults = Regex.IsMatch(requestedPath, @"^/api/v2\.0/indexers/[^/]+/results$", RegexOptions.CultureInvariant);
            bool personalRoute = jrResults || apiResults || requestedPath.StartsWith("/kp/") || requestedPath.StartsWith("/home/") || requestedPath.StartsWith("/anime/") || requestedPath.StartsWith("/yt/") || requestedPath.StartsWith("/local/") || requestedPath.StartsWith("/wrestling/") || requestedPath.StartsWith("/bookmark/") || requestedPath.StartsWith("/timecode/") || requestedPath.StartsWith("/storage/") || requestedPath.StartsWith("/tv/") || requestedPath.StartsWith("/ym/");
            // Media URLs remain LAN streams. Profile authorization applies to catalogs, selection and native launch, not to HLS segment requests.
            bool mediaRoute=requestedPath=="/wrestling/poster.svg"||requestedPath=="/wrestling/file"||requestedPath=="/home/wallpaper"||requestedPath.StartsWith("/kp/stream")||requestedPath=="/yt/p"||requestedPath.StartsWith("/anime/play")||requestedPath.StartsWith("/local/plex/file/")||requestedPath.StartsWith("/local/plex/hls/")||requestedPath=="/local/plex/seg";
            bool discoveryRoute = requestedPath.StartsWith("/home/app-updates/") || requestedPath == "/home/health" || requestedPath == "/home/whoami" || requestedPath == "/home/hello" || requestedPath == "/home/discover" || requestedPath == "/home/seed";
            // Browser navigation to a service must lead to a login/recovery page, not raw JSON.
            // API clients keep the machine-readable 401/403 below.
            bool htmlNavigation = HttpMethods.IsGet(e.httpContext.Request.Method)
                && e.httpContext.Request.Headers["Accept"].ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
            if (Profiles.Enabled && requestedPath.StartsWith("/home/services/", StringComparison.Ordinal) && htmlNavigation && user?.Admin != true)
            {
                string next = Uri.EscapeDataString(requestedPath + e.httpContext.Request.QueryString.Value);
                e.httpContext.Response.StatusCode = 302;
                e.httpContext.Response.Headers["Location"] = "/home/session/entry?next=" + next;
                e.httpContext.Response.Headers["Cache-Control"] = "no-store";
                return false;
            }
            if (Profiles.Enabled && personalRoute && !publicProfileRoute && !discoveryRoute && !mediaRoute && user == null)
            {
                e.httpContext.Response.StatusCode = 401;
                e.httpContext.Response.ContentType = "application/json";
                e.httpContext.Response.WriteAsync("{\"error\":\"session_required\"}").GetAwaiter().GetResult();
                return false;
            }
            if (Profiles.Enabled && (requestedPath.StartsWith("/home/ops/") || requestedPath.StartsWith("/home/admin/")) && !publicProfileRoute && user?.Admin != true)
            {
                e.httpContext.Response.StatusCode = 403;
                return false;
            }
            if(Profiles.Enabled && user!=null){
                if(requestedPath.StartsWith("/yt/") && requestedPath!="/yt/p" && !ProfilePolicy.SourceEnabled("youtube")){
                    e.httpContext.Response.StatusCode=403;
                    e.httpContext.Response.ContentType="application/json";
                    e.httpContext.Response.WriteAsync("{\"error\":\"source_disabled\",\"source\":\"youtube\"}").GetAwaiter().GetResult();
                    return false;
                }
                bool adminWriteRoute=requestedPath=="/home/kp/folders"||requestedPath=="/home/kp/folders/set"||requestedPath=="/anime/cache/clear"||requestedPath=="/home/wallpaper/upload"||requestedPath=="/home/wallpaper/delete"||(requestedPath=="/home/torrserver"&&e.httpContext.Request.Query.ContainsKey("url"));
                if(adminWriteRoute&&!user.Admin){e.httpContext.Response.StatusCode=403;e.httpContext.Response.ContentType="application/json";e.httpContext.Response.WriteAsync("{\"error\":\"admin_required\"}").GetAwaiter().GetResult();return false;}
                bool adminPath=requestedPath.StartsWith("/home/ops/")||requestedPath.StartsWith("/home/admin/")||requestedPath.StartsWith("/kp/auth/")||requestedPath.StartsWith("/kp/profile/")||requestedPath.StartsWith("/home/kp/profile/")||requestedPath=="/home/dev/list"||requestedPath=="/home/dev/forget"||requestedPath=="/home/dev/rename"||requestedPath.StartsWith("/home/animelib/")||requestedPath.StartsWith("/yt/profiles/remove")||requestedPath=="/yt/dbg";
                bool childDenied=user.Child&&((requestedPath.StartsWith("/wrestling/")&&requestedPath!="/wrestling/poster.svg"&&requestedPath!="/wrestling/file")||requestedPath.StartsWith("/ym/")||requestedPath.StartsWith("/tmdb/api/")||requestedPath.StartsWith("/tv/config/set")||requestedPath.StartsWith("/tv/refresh")||requestedPath.StartsWith("/tv/keep")||requestedPath.StartsWith("/local/config")||requestedPath.StartsWith("/kp/config")||((string)Profiles.Get(user.Id)?["settings"]?["youtube_mode"]=="channels"&&(requestedPath.StartsWith("/yt/subs/add")||requestedPath.StartsWith("/yt/subs/remove")||requestedPath.StartsWith("/yt/subs/import"))));
                if(adminPath&&!user.Admin||childDenied){e.httpContext.Response.StatusCode=403;e.httpContext.Response.ContentType="application/json";e.httpContext.Response.WriteAsync("{\"error\":\"profile_forbidden\"}").GetAwaiter().GetResult();return false;}
            }
            if(Profiles.Enabled&&personalRoute&&!mediaRoute)e.httpContext.Response.OnStarting(()=>{e.httpContext.Response.Headers["Cache-Control"]="private, no-store";e.httpContext.Response.Headers["Vary"]="X-Home-Session, Cookie";return System.Threading.Tasks.Task.CompletedTask;});
            TcWatch.Seen(e.httpContext);        // счётчик обращений к /timecode/* (без тел и токенов)
            var ri = e.httpContext.Features.Get<RequestModel>();
            if (ri != null)
                ri.user_uid = Profiles.Enabled && user != null ? "home:" + user.Id : SharedUid;

            // Кеш клиента: файлы Lampa и постеры не качаются при каждом запуске, а без сети приложение открывается из кеша.
            // Свои плагины (tv.js и др.) — no-cache: всегда актуальные, но лежат в кеше для офлайна.
            try
            {
                var ctx = e.httpContext;
                string path = ctx.Request.Path.Value ?? "";
                string cc = null;
                // lampainit.js и privateinit.js Lampac отдаёт с no-store — такие файлы не попадают в кеш и без сети не загрузятся
                bool force = path == "/lampainit.js" || path.StartsWith("/privateinit.js");
                if (path.StartsWith("/lampa-main/") && !path.EndsWith("personal.lampa") && !path.EndsWith("plugins_black_list.json")) cc = "public, max-age=604800";
                else if (path.StartsWith("/tmdb/img/")) cc = "public, max-age=2592000";
                else if (path == "/" || path == "/lampainit.js" || path.StartsWith("/privateinit.js") || path == "/tv.js" || path == "/kinopub.js" || path == "/anime.js" || path == "/continue.js" || path == "/wrestling.js" || path == "/cardplus.js" || path == "/apps.js" || path == "/yamusic.js" || path == "/youtube.js") cc = "no-cache";
                if (cc != null)
                    ctx.Response.OnStarting(() =>
                    {
                        if(ctx.Response.StatusCode>=400)ctx.Response.Headers["Cache-Control"]="no-store";
                        else if (force || !ctx.Response.Headers.ContainsKey("Cache-Control")) ctx.Response.Headers["Cache-Control"] = cc;
                        return System.Threading.Tasks.Task.CompletedTask;
                    });
            }
            catch { }
        }

        return true;
    }

    // Корень кэшируется Staticache без учёта cookie. Пока кэш жив, браузер без сеанса получает чужой HTML и плагины отвечают 401.
    static bool AllowShellCache(EventStaticache e) => (e.httpContext.Request.Path.Value ?? "") != "/";

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;
        EventListener.Middleware += Middleware;
        EventListener.Staticache += AllowShellCache;
        _ = HomeDb.Counts(); // migrate candidate schema before accepting profile traffic
        if (!Profiles.IsTest) KpBridge.Start();
        Ops.Start();
        TcWatch.Start();
        if (!Profiles.IsTest) TvStore.StartKeep();
        if (!Profiles.IsTest) TvStore.StartEpgTimer();
        _ = System.Threading.Tasks.Task.Run(() => ProfilePolicy.RunAgeQueue(System.Threading.CancellationToken.None));
        CoreInit.BaseModPathWhiteList.Add("/wrestling");
        CoreInit.BaseModPathWhiteList.Add("/kp");
        CoreInit.BaseModPathWhiteList.Add("/jr");
        CoreInit.BaseModPathWhiteList.Add("/local");
        CoreInit.BaseModPathWhiteList.Add("/ym");
        CoreInit.BaseModPathWhiteList.Add("/tv");
        CoreInit.BaseModPathWhiteList.Add("/anime");
        CoreInit.BaseModPathWhiteList.Add("/home");
    }

    public void Dispose()
    {
        EventListener.Middleware -= Middleware;
        EventListener.Staticache -= AllowShellCache;
        KpBridge.Stop();
        Ops.Stop();
        TcWatch.Stop();
    }
}
