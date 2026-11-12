using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
namespace HomeMedia;

public static class LiftRuntime
{
    public static string Agent=>Environment.GetEnvironmentVariable("LIFT_USER_AGENT")
        ??"Mozilla/5.0 (Linux; Android 11; Android TV; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/130.0.6723.107 Safari/537.36";
    static LiftApiClient CreateClient() {
      try {return new LiftApiClient(
        (Environment.GetEnvironmentVariable("LIFT_API_ROOTS")??"https://api.faphz.com/,https://api.twemd.ws/")
            .Split(',',StringSplitOptions.RemoveEmptyEntries).Select(x=>new Uri(x.Trim())),
        (Environment.GetEnvironmentVariable("LIFT_CDN_HOSTS")??"").Split(',',StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()),
        Environment.GetEnvironmentVariable("LIFT_API_BASIC_B64"),Agent);}
      catch(Exception e) when(e is ArgumentException || e is FormatException) {throw new InvalidOperationException("lift_not_configured");}
    }
    static readonly Lazy<LiftApiClient> client=new(CreateClient);
    public static LiftApiClient Client=>client.Value;
    public static LiftService Service=>new(Client,Agent);
}

// Middleware keeps the existing Home scope/session boundary.
public class LiftController:BaseController
{
    static ContentResult J(object value,int code=200)=>new(){Content=JsonConvert.SerializeObject(value),ContentType="application/json; charset=utf-8",StatusCode=code};
    static void Admin(){if(Profiles.Current==null)throw new UnauthorizedAccessException("session_required");if(!Profiles.Current.Admin||Profiles.Current.Child)throw new UnauthorizedAccessException("admin_required");}
    static long N(JObject b,string key,long fallback=0)=>b[key]==null?fallback:long.TryParse(b[key].ToString(),out long n)?n:throw new ArgumentException("bad_"+key);
    static LiftTarget Target(JObject b)=>LiftTarget.Parse((string)b["media"],N(b,"tmdb"),checked((int)N(b,"season")),checked((int)N(b,"episode")));
    async Task<JObject> Body()
    {
        using var r=new StreamReader(Request.Body);char[] buf=new char[4097];int n=0;
        while(n<buf.Length){int k=await r.ReadAsync(buf.AsMemory(n),HttpContext.RequestAborted);if(k==0)break;n+=k;}
        if(n>4096)throw new ArgumentException("payload_too_large");
        try{return JObject.Parse(new string(buf,0,n));}catch(JsonException){throw new ArgumentException("bad_json");}
    }
    async Task<ActionResult> Run(Func<Task<object>> action)
    {
        Response.Headers["Cache-Control"]="no-store";
        try{return J(await action());}
        catch(UnauthorizedAccessException e){return J(new{error=e.Message},e.Message=="session_required"?401:403);}
        catch(ArgumentException){return J(new{error="bad_request"},400);}
        catch(OverflowException){return J(new{error="bad_request"},400);}
        catch(JsonException){return J(new{error="lift_invalid_json"},502);}
        catch(System.Xml.XmlException){return J(new{error="lift_invalid_mpd"},502);}
        catch(InvalidOperationException e){return J(new{error=e.Message},e.Message=="lift_not_configured"?503:e.Message=="source_disabled"?403:409);}
        catch(InvalidDataException e){return J(new{error=e.Message},e.Message.Contains("voice")?409:502);}
        catch(OperationCanceledException){return J(new{error="lift_timeout"},504);}
        catch(HttpRequestException){return J(new{error="lift_upstream_unavailable"},502);}
    }
    [HttpGet,AllowAnonymous,Route("lift.js")]
    public ActionResult Plugin()
    {
        string dir=string.IsNullOrEmpty(ModInit.modpath)?"/opt/lampac/mods/HomeMedia":ModInit.modpath;
        bool enabled=Profiles.Current!=null&&HomeDb.Get("module_lift")=="1"&&ProfilePolicy.SourceEnabled("lift");
        string text="window.__home_lift_enabled="+(enabled?"true":"false")+";\n"
            +System.IO.File.ReadAllText(Path.Combine(dir,"lift-flow.js"))+"\n"
            +System.IO.File.ReadAllText(Path.Combine(dir,"lift.js")).Replace("{localhost}",host)+"\n"
            +System.IO.File.ReadAllText(Path.Combine(dir,"LiftBeforeNative.js"));
        return ContentTo(text,"application/javascript; charset=utf-8");
    }
    [HttpPost,AllowAnonymous,Route("home/lift/find")]
    public Task<ActionResult> Find()=>Run(async()=> {
        Admin();var b=await Body();string q=(string)b["query"];
        var raw=await LiftRuntime.Client.Search(q,HttpContext.RequestAborted);
        if(raw["items"] is not JArray items)throw new InvalidDataException("lift_search_schema_changed");
        return new {items=new JArray(items.Children<JObject>().Select(x=>new JObject{
            ["id"]=x["id"],["name"]=x["name"],["origin_name"]=x["origin_name"],["year"]=x["year"],["type"]=x["type"]}))};
    });
    [HttpPost,AllowAnonymous,Route("home/lift/bind")]
    public Task<ActionResult> Bind()=>Run(async()=> {
        Admin();var b=await Body();var t=Target(b);await LiftService.Require(t);long item=N(b,"item");
        if(t.Media=="tv") {
            var canonical=await KpMap.TmdbRaw($"tv/{t.Tmdb}/season/{t.Season}/episode/{t.Episode}");
            if(canonical?["id"]==null || (int?)canonical["season_number"]!=t.Season || (int?)canonical["episode_number"]!=t.Episode)
                throw new InvalidDataException("lift_tmdb_episode_missing");
        }
        int season=checked((int)N(b,"provider_season")),episode=checked((int)N(b,"provider_episode"));
        var p=await LiftRuntime.Client.Playlist(item,HttpContext.RequestAborted);
        var e=LiftPlaylist.Select(p,t.Media=="movie",season,episode);
        var uri=LiftVp9Policy.Manifest(e);var xml=await LiftRuntime.Client.Mpd(uri.AbsoluteUri,HttpContext.RequestAborted);
        var descriptor=LiftVp9Policy.Describe(e,xml,0);
        if(((JArray)descriptor["voices"]).Count==0)throw new InvalidDataException("lift_audio_mapping_unverified");
        var binding=LiftBindings.Put(t,item,t.Media=="movie",season,episode);
        return new{ok=true,binding};
    });
    [HttpPost,AllowAnonymous,Route("home/lift/episodes")]
    public Task<ActionResult> Episodes()=>Run(async()=> {
        Admin();var b=await Body();var t=Target(b);await LiftService.Require(t);
        if(t.Media!="tv")throw new ArgumentException("series_required");
        var raw=await KpMap.TmdbRaw($"tv/{t.Tmdb}?language=ru");
        return new {seasons=new JArray((raw?["seasons"] as JArray??new JArray()).Children<JObject>()
            .Where(s=>(int?)s["season_number"]>=0 && (int?)s["episode_count"]>0)
            .Select(s=>new JObject{["season"]=s["season_number"],["count"]=s["episode_count"],["title"]=s["name"]}))};
    });
    [HttpPost,AllowAnonymous,Route("home/lift/catalog")]
    public Task<ActionResult> Catalog()=>Run(async()=> {
        Admin();var b=await Body();var t=Target(b);await LiftService.Require(t);
        var p=await LiftRuntime.Client.Playlist(N(b,"item"),HttpContext.RequestAborted);
        if(t.Media=="movie") {
            if(p["streams"] is not JObject)throw new InvalidDataException("lift_not_a_movie");
            return new {items=new JArray(new JObject{["season"]=0,["episode"]=0,["title"]=(string)p["title"]??"Фильм"})};
        }
        var rows=new JArray();
        foreach(var season in (p["seasons"] as JArray??new JArray()).Children<JObject>())
        foreach(var e in (season["episodes"] as JArray??new JArray()).Children<JObject>()) {
            if(!int.TryParse(season["season"]?.ToString(),out int s)||!int.TryParse(e["episode"]?.ToString(),out int ep)||s<0||ep<1)continue;
            rows.Add(new JObject{["season"]=s,["episode"]=ep,["title"]=(string)e["title"]??("Сезон "+s+" · серия "+ep)});
        }
        return new {items=rows};
    });
    [HttpPost,AllowAnonymous,Route("home/lift/options")]
    public Task<ActionResult> Options()=>Run(async()=> {
        var b=await Body();return await LiftRuntime.Service.Resolve(Target(b),null,null,HttpContext.RequestAborted);
    });
    [HttpPost,AllowAnonymous,Route("home/lift/resolve")]
    public Task<ActionResult> Resolve()=>Run(async()=> {
        var b=await Body();string voice=(string)b["voice_name"],revision=(string)b["binding_revision"];
        if(string.IsNullOrWhiteSpace(voice)||string.IsNullOrWhiteSpace(revision))throw new ArgumentException("selection_required");
        return await LiftRuntime.Service.Resolve(Target(b),voice,revision,HttpContext.RequestAborted);
    });
    [HttpPost,AllowAnonymous,Route("home/lift/position-context")]
    public Task<ActionResult> PositionContext()=>Run(async()=> {
        var b=await Body();var t=Target(b);await LiftService.Require(t);
        return LiftService.PositionContext(t,(string)b["binding_revision"]);
    });
    [HttpGet,AllowAnonymous,Route("home/lift/item")]
    public Task<ActionResult> Item(string media,long tmdb)=>Run(async()=> {
        var sample=LiftTarget.Parse(media,tmdb,media=="tv"?1:0,media=="tv"?1:0);
        await LiftService.Require(sample);
        var rows=HomeDb.With(c=>HomeDb.Query(c,"SELECT external_id,mapping FROM content_refs WHERE provider='lift' AND ref_kind='episode' AND scope='approved-v1' AND content_id=@c",("@c","tmdb:"+media+":"+tmdb)));
        return new{items=new JArray(rows.Select(r=>new JObject{["key"]=(string)r[0],["binding"]=JObject.Parse((string)r[1])}))};
    });
}
