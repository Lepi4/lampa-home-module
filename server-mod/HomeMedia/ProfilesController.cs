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
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

public class ProfilesController : BaseController
{
    static ActionResult J(object value,int status=200)=>new ContentResult { Content=JsonConvert.SerializeObject(value),ContentType="application/json; charset=utf-8",StatusCode=status };
    async Task<JObject> Body(){using var r=new StreamReader(Request.Body);var s=await r.ReadToEndAsync();if(s.Length>65536)throw new ArgumentException("body_too_large");return string.IsNullOrWhiteSpace(s)?new JObject():JObject.Parse(s);}
    Profiles.Scope Viewer()=>Profiles.FromRequest(HttpContext);
    bool Admin()=>Guard.Ok(Request)&&Viewer()?.Admin==true;
    static bool IdOk(string s)=>Regex.IsMatch(s??"",@"^[A-Za-z0-9_-]{1,80}$");
    static object Session((string token,Profiles.Scope scope) s)=>new { token=s.token,profile=Profiles.Public(Profiles.Get(s.scope.Id)),expires_in=86400,server_id=HomeDb.Get("user_server_id"),profiles_enabled=Profiles.Enabled };
    static JObject BootProfile(string dev)
    {
        string id=null;
        if(DeviceStore.ValidId(dev))foreach(var row in DeviceStore.List().Children<JObject>())if((string)row["id"]==dev){id=(string)row["cfg"]?["viewer"];break;}
        var chosen=string.IsNullOrEmpty(id)?null:Profiles.Get(id);
        return chosen!=null&&!(bool)chosen["archived"]?chosen:Profiles.Get(Profiles.DefaultId);
    }
    static readonly HashSet<string> RowsAllowed=new(){"continue","history","watching","watching_anime","watching_cartoons","my_movies","my_cartoons","new_movie","new_serial","top_new","anime","ru_serial","ru_movie","new_cartoon","new_cartoon_series","youtube"};

    [HttpGet,AllowAnonymous,Route("home/profiles")]
    public ActionResult List()=>J(new {enabled=Profiles.Enabled,test_instance=Profiles.IsTest,profiles=new JArray(Profiles.List().Children<JObject>().Select(p=>new JObject { ["id"]=p["id"],["name"]=p["name"],["is_child"]=p["is_child"],["has_pin"]=p["has_pin"] }))});
    [HttpGet,AllowAnonymous,Route("home/session")]
    public ActionResult Current(){var u=Viewer();return u==null?J(new {error="session_required"},401):J(new {profile=Profiles.Public(Profiles.Get(u.Id)),profiles_enabled=Profiles.Enabled});}

    [HttpPost,AllowAnonymous,Route("home/session/renew")]
    public async Task<ActionResult> Renew()
    {
        if(!Guard.Ok(Request))return J(new{error="forbidden"},403);
        var body=await Body();string token=Request.Headers["X-Home-Session"].FirstOrDefault();
        var user=Profiles.Renew(token,(string)body["boot"]);
        return user==null?J(new{error="session_required"},401):J(Session((token,user)));
    }
    [HttpPost,AllowAnonymous,Route("home/session/bootstrap"),Route("home/session/login")]
    public async Task<ActionResult> Bootstrap()
    {
        if(!Guard.Ok(Request))return J(new {error="forbidden"},403);
        try {
            var b=await Body();string dev=(string)b["device"],boot=(string)b["boot"];
            if(!IdOk(dev)||!IdOk(boot))return J(new {error="device_and_boot_required"},400);
            var p=Request.Path.Value.EndsWith("/login",StringComparison.Ordinal)?Profiles.Get((string)b["profile_id"]):BootProfile(dev);
            if(p==null||(bool)p["archived"])return J(new {error="invalid_profile"},404);
            if(!string.IsNullOrEmpty((string)p["pin_hash"])&&(b["pin"]==null||!Profiles.CheckPin(p,(string)b["pin"],dev,HttpContext.Connection.RemoteIpAddress?.ToString())))return J(new {locked=true,profile=new {id=(string)p["id"],name=(string)p["name"]}});
            return J(Session(Profiles.CreateSession((string)p["id"],dev,boot)));
        }catch(Exception e){return J(new {error=e.Message},400);}
    }
    [HttpPost,AllowAnonymous,Route("home/session/switch")]
    public async Task<ActionResult> Switch()
    {
        var from=Viewer();if(from==null)return J(new {error="session_required"},401);
        try {
            var b=await Body();var p=Profiles.Get((string)b["profile_id"]);
            if(p==null||(bool)p["archived"])return J(new {error="unknown_profile"},404);
            if(!(bool)p["is_child"]&&!string.IsNullOrEmpty((string)p["pin_hash"])&&!Profiles.CheckPin(p,(string)b["pin"],from.Device,HttpContext.Connection.RemoteIpAddress?.ToString()))return J(new {error="pin_required_or_invalid"},403);
            return J(Session(Profiles.CreateSession((string)p["id"],from.Device,from.Boot,from.TokenHash)));
        }catch(Exception e){return J(new {error=e.Message},409);}
    }
    [HttpPost,AllowAnonymous,Route("home/play/grant")]
    public async Task<ActionResult> Grant()
    {
        var user=Viewer();if(user==null)return J(new {error="session_required"},401);
        var b=await Body();var ids=b["identities"] as JArray;
        if(ids==null||ids.Count==0||ids.Count>300||ids.Any(x=>x.Type!=JTokenType.String||x.ToString().Length>180))return J(new {error="bad_identities"},400);
        string card=(string)b["card"];var m=Regex.Match(card??"",@"^(\d+)_(tv|movie)$");
        bool youtube=card?.StartsWith("yt_")==true&&YtStore.ValidId(card.Substring(3));
        if(youtube&&!ProfilePolicy.SourceEnabled("youtube"))return J(new{error="source_disabled",source="youtube"},403);
        if(youtube&&ids.Any(x=>x.ToString()!="yt-"+card.Substring(3)))return J(new{error="identity_card_mismatch"},400);
        if(user.Child&&youtube){var info=YtStore.Cached("v3:"+card.Substring(3),4*3600) as JObject;if(info==null||!await ProfilePolicy.Allows(info))return J(new{error="parental_youtube_blocked"},403);}
        if(user.Child&&!youtube){
            if(!m.Success)return J(new {error="parental_metadata_required"},403);
            long tmdb=long.Parse(m.Groups[1].Value);string media=m.Groups[2].Value;
            if(!await ProfilePolicy.Allows(new JObject{["id"]=tmdb,["media_type"]=media}))return J(new {error="parental_blocked"},403);
        }
        if(m.Success){string root=m.Groups[2].Value+"-"+m.Groups[1].Value;
            if(ids.Any(x=>x.ToString()!=root&&!Regex.IsMatch(x.ToString(),"^"+Regex.Escape(root)+@"-s\d+e\d+$")))return J(new {error="identity_card_mismatch"},400);
        }
        return J(new {grant=Profiles.IssueProgressGrant(user,ids,card),profile_id=user.Id});
    }
    [HttpPost,AllowAnonymous,Route("home/session/logout")]
    public ActionResult Logout(){var u=Viewer();if(u!=null)HomeDb.With(c=>Profiles.Exec(c,"UPDATE user_sessions SET revoked=1 WHERE token_hash=@h",("@h",u.TokenHash)));return J(new {ok=true});}

    [HttpPost,AllowAnonymous,Route("home/admin/login")]
    public async Task<ActionResult> Login()
    {
        if(!Guard.Ok(Request))return J(new {error="forbidden"},403);
        try {
            var b=await Body();var p=Profiles.Get((string)b["profile_id"]??Profiles.MainId);
            if(p==null||!(bool)p["is_admin"]||(bool)p["is_child"]||(bool)p["archived"])return J(new {error="admin_required"},403);
            string dev=(string)b["device"]??"web_admin",boot=(string)b["boot"]??Guid.NewGuid().ToString("N");
            if(!IdOk(dev)||!IdOk(boot)||!Profiles.CheckPin(p,(string)b["pin"],dev,HttpContext.Connection.RemoteIpAddress?.ToString()))return J(new {error="pin_required_or_invalid"},403);
            var session=Profiles.CreateSession((string)p["id"],dev,boot);
            Response.Cookies.Append("home_session",session.token,new CookieOptions{HttpOnly=true,IsEssential=true,SameSite=SameSiteMode.Lax,Path="/",MaxAge=TimeSpan.FromHours(24)});
            return J(Session(session));
        }catch(Exception e){return J(new {error=e.Message},400);}
    }
    [HttpGet,AllowAnonymous,Route("home/catalog/{**path}")]
    public async Task<ActionResult> Catalog(string path)
    {
        var user=Viewer();if(user==null)return J(new{error="session_required"},401);
        if(string.IsNullOrEmpty(path)||path.Contains("..")||!Regex.IsMatch(path,@"^(movie|tv|search|discover|trending|person|genre|configuration|find)/?[A-Za-z0-9_/-]*$"))return J(new{error="bad_catalog_path"},400);
        try{
            var result=await KpMap.TmdbRaw(path+Request.QueryString.Value);
            var detail=Regex.Match(path,@"^(tv|movie)/(\d+)$");
            if(detail.Success){result["media_type"]=detail.Groups[1].Value;if(!await ProfilePolicy.Allows(result))return J(new{error="parental_blocked"},403);result=CardKind.Annotate(result);}
            return J(await ProfilePolicy.Filter(result));
        }catch{return J(new{error="catalog_unavailable"},502);}
    }
    [HttpGet,HttpPost,AllowAnonymous,Route("home/blocked-external")]
    public ActionResult BlockedExternal()=>J(new{error="external_catalog_blocked"},403);
    [HttpGet,AllowAnonymous,Route("home/content-kind")]
    public ActionResult ContentKind(string media,long id)
    {
        if(Viewer()==null)return J(new{error="session_required"},401);
        var row=HomeDb.With(c=>Profiles.Rows(c,"SELECT override_kind FROM content_items WHERE id=@k",("@k",ProfileLibrary.Key(media,id)))).FirstOrDefault();
        return J(new{kind=row?[0] as string});
    }
    [HttpGet,AllowAnonymous,Route("home/youtube/progress")]
    public ActionResult YoutubeProgress(string id)
    {
        if(Viewer()==null)return J(new{error="session_required"},401);
        if(!YtStore.ValidId(id))return J(new{error="bad_id"},400);
        var row=HomeDb.With(c=>HomeDb.Query(c,"SELECT t,d,p,op FROM timeline WHERE profile_id=@profile AND id=@id",("@id","yt-"+id))).FirstOrDefault();
        return row==null?J(new{position=0}):J(new{position=(string)(row[3] is DBNull?null:row[3])=="completed"?0:Convert.ToInt64(row[0]),duration=Convert.ToInt64(row[1]),percent=Convert.ToDouble(row[2])});
    }
    [HttpGet,AllowAnonymous,Route("home/auto/episode")]
    public async Task<ActionResult> AutoEpisode(long tmdb)
    {
        if(Viewer()==null)return J(new{error="session_required"},401);
        var partial=await KpUnified.Progress("tv",tmdb);if((int?)partial?["season"]>0&&(int?)partial?["episode"]>0)return J(partial);
        var counts=await KpUnified.AiredCounts(tmdb);if(counts==null)return J(new{manual=true});
        var watched=await KpUnified.WatchedSets();watched.TryGetValue(tmdb,out var seen);
        foreach(var season in counts.OrderBy(x=>x.Key))for(int ep=1;ep<=season.Value;ep++)if(seen==null||!seen.Contains((season.Key,ep)))return J(new{season=season.Key,episode=ep});
        return J(new{manual=true});
    }
    [HttpPost,AllowAnonymous,Route("home/play/authorize")]
    public async Task<ActionResult> AuthorizePlay()
    {
        var user=Viewer();if(user==null)return J(new{error="session_required"},401);var b=await Body();
        if(!string.IsNullOrEmpty((string)b["youtube_id"])&&!ProfilePolicy.SourceEnabled("youtube"))return J(new{error="source_disabled",source="youtube"},403);
        if(!user.Child)return J(new{ok=true});
        string card=(string)b["card"],yt=(string)b["youtube_id"],tv=(string)b["tv_channel"];
        // Live TV uses the same Channel Manager lineup for every viewer.
        // Old tv_channel rules are intentionally ignored.
        if(!string.IsNullOrEmpty(tv))return J(new{ok=true});
        if(!string.IsNullOrEmpty(yt)){
            var info=YtStore.Cached("v3:"+yt,4*3600) as JObject;
            return info!=null&&await ProfilePolicy.Allows(info)?J(new{ok=true}):J(new{error="parental_youtube_blocked"},403);
        }
        var m=Regex.Match(card??"",@"^(\d+)_(tv|movie)$");
        if(!m.Success)return J(new{error="parental_metadata_required"},403);
        var c=new JObject{["id"]=long.Parse(m.Groups[1].Value),["media_type"]=m.Groups[2].Value};
        return await ProfilePolicy.Allows(c)?J(new{ok=true}):J(new{error="parental_blocked"},403);
    }
    [HttpGet,AllowAnonymous,Route("home/ops/profiles/selftest")]
    public ActionResult PolicySelftest()
    {
        if(!Profiles.IsTest)return J(new{error="not_found"},404);
        if(!Admin())return J(new{error="admin_required"},403);
        var inputs=new(string rating,int? expected)[]{("G",0),("PG",6),("TV-PG",12),("PG-13",13),("pg_13",13),("TV-Y7",7),("TV-14",14),("R",17),("TV-MA",17),("r_plus",18),("NC-17",18),("r",17),("16+",16),("unknown",null)};
        var checks=inputs.Select(x=>new{name=x.rating,expected=x.expected,actual=ProfilePolicy.Age(x.rating),passed=ProfilePolicy.Age(x.rating)==x.expected}).ToList();
        bool noRating=ProfilePolicy.MetadataAge(new JObject{["adult"]=false,["vote_average"]=8.2})==null;
        return J(new{checks,unknown_is_not_zero=noRating,passed=checks.All(x=>x.passed)&&noRating});
    }
    [HttpGet,AllowAnonymous,Route("home/ops/catalog/selftest")]
    public async Task<ActionResult> CatalogSelftest()
    {
        if(!Profiles.IsTest)return J(new{error="not_found"},404);
        if(!Admin())return J(new{error="admin_required"},403);
        int reads=0;string key="selftest:"+Guid.NewGuid();
        Task<(JArray results,int pages)> Load(int page){
            reads++;var ids=page==1?Array.Empty<int>():page==2?Enumerable.Range(1,12).Concat(new[]{1}).ToArray():page==3?Enumerable.Range(13,28).Concat(new[]{12}).ToArray():Enumerable.Range(41,15).ToArray();
            return Task.FromResult((new JArray(ids.Select(id=>new JObject{["id"]=id,["media_type"]="movie"})),4));
        }
        Task Verify(JArray items){foreach(var c in items.Children<JObject>().Where(c=>(int)c["id"]==1).ToArray())c.Remove();return Task.CompletedTask;}
        var second=await ProfileCatalogPages.Get(key,2,Load,Verify);
        var first=await ProfileCatalogPages.Get(key,1,Load,Verify);
        var third=await ProfileCatalogPages.Get(key,3,Load,Verify);
        var end=await ProfileCatalogPages.Get(key,4,Load,Verify);
        var ids=first.results.Concat(second.results).Concat(third.results).Select(c=>(int)c["id"]).ToArray();
        var checks=new[]{
            new{name="filled_after_empty_source_page",passed=first.results.Count==20},
            new{name="second_requested_first",passed=second.results.Count==20&&(int)second.results[0]["id"]==22},
            new{name="dedup_and_no_gaps",passed=ids.SequenceEqual(Enumerable.Range(2,54))},
            new{name="short_final_page",passed=third.results.Count==14&&!third.more&&third.pages==3},
            new{name="end_has_no_phantom_page",passed=end.results.Count==0&&!end.more&&end.pages==3},
            new{name="cached_pages_do_not_reload",passed=reads==4}
        };
        return J(new{checks,passed=checks.All(c=>c.passed)});
    }
    [HttpGet,AllowAnonymous,Route("lampa")]
    public async Task<ActionResult> WebLampa()
    {
        if(!Profiles.Enabled)return Redirect("/");
        var viewer=Viewer();string token=Request.Headers["X-Home-Session"].FirstOrDefault()??Request.Cookies["home_session"];
        if(viewer==null){var p=Profiles.Get(Profiles.DefaultId);if(!string.IsNullOrEmpty((string)p?["pin_hash"]))return Redirect("/profiles");
            string device=Request.Cookies["home_web_device"]??"web_"+Guid.NewGuid().ToString("N");var session=Profiles.CreateSession(Profiles.DefaultId,device,Guid.NewGuid().ToString("N"));viewer=session.scope;token=session.token;
            Response.Cookies.Append("home_web_device",device,new CookieOptions{HttpOnly=true,IsEssential=true,SameSite=SameSiteMode.Lax,Path="/"});Response.Cookies.Append("home_session",token,new CookieOptions{HttpOnly=true,IsEssential=true,SameSite=SameSiteMode.Lax,Path="/"});}
        string address=Request.Scheme+"://"+Request.Host;
        using var http=new System.Net.Http.HttpClient{Timeout=TimeSpan.FromSeconds(15)};using var request=new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,"http://127.0.0.1:9118/"+Request.QueryString.Value);
        request.Headers.Host=Request.Host.Value;request.Headers.Add("X-Home-Session",token);request.Headers.Add("X-Home-Web-Render","1");
        using var response=await http.SendAsync(request);response.EnsureSuccessStatusCode();string html=await response.Content.ReadAsStringAsync();
        var scope=new JObject{["ready"]=true,["profiles_enabled"]=true,["token"]=token,["profile"]=Profiles.Public(Profiles.Get(viewer.Id)),["server_id"]=HomeDb.Get("user_server_id"),["server_base"]=address};
        string script="<script>window.__home_scope="+scope.ToString(Formatting.None).Replace("<","\\u003c")+";"+System.IO.File.ReadAllText(System.IO.Path.Combine(ModInit.modpath,"profiles-web.js"))+"</script>";
        var head=Regex.Match(html,"<head[^>]*>",RegexOptions.IgnoreCase);html=head.Success?html.Insert(head.Index+head.Length,script):script+html;
        Response.Headers["Cache-Control"]="no-store";return new ContentResult{Content=html,ContentType="text/html; charset=utf-8"};
    }
    [HttpGet,AllowAnonymous,Route("profiles")]
    public ActionResult Panel()=>new ContentResult{Content=System.IO.File.ReadAllText(System.IO.Path.Combine(ModInit.modpath,"profiles.html")),ContentType="text/html; charset=utf-8"};
    [HttpGet,HttpPost,AllowAnonymous,Route("home/admin/profile-rules")]
    public async Task<ActionResult> Rules(string profile_id)
    {
        if(!Admin())return J(new{error="admin_required"},403);
        if(Profiles.Get(profile_id)==null)return J(new{error="unknown_profile"},404);
        if(!HttpMethods.IsGet(Request.Method)){
            var b=await Body();string kind=(string)b["kind"],subject=(string)b["subject"],decision=(string)b["decision"];
            if(!new[]{"content","youtube_channel","youtube_video"}.Contains(kind)||string.IsNullOrWhiteSpace(subject)||subject.Length>240||!new[]{"allow","deny","inherit"}.Contains(decision))return J(new{error="bad_rule"},400);
            HomeDb.With(c=>{using var tx=c.BeginTransaction();if(decision=="inherit")Profiles.Exec(c,"DELETE FROM profile_rules WHERE profile_id=@p AND kind=@k AND subject=@s",("@p",profile_id),("@k",kind),("@s",subject));
                else Profiles.Exec(c,"INSERT INTO profile_rules(profile_id,kind,subject,decision,at) VALUES(@p,@k,@s,@d,@at) ON CONFLICT(profile_id,kind,subject) DO UPDATE SET decision=@d,at=@at",("@p",profile_id),("@k",kind),("@s",subject),("@d",decision),("@at",HomeDb.Now()));
                Profiles.Exec(c,"UPDATE user_profiles SET policy_rev=policy_rev+1 WHERE id=@p",("@p",profile_id));tx.Commit();});
        }
        return J(new{rules=HomeDb.With(c=>Profiles.Rows(c,"SELECT kind,subject,decision FROM profile_rules WHERE profile_id=@p AND kind<>'tv_channel'",("@p",profile_id))).Select(r=>new{kind=(string)r[0],subject=(string)r[1],decision=(string)r[2]})});
    }
    [HttpPost,AllowAnonymous,Route("home/admin/content-kind")]
    public async Task<ActionResult> Classify()
    {
        if(!Admin())return J(new{error="admin_required"},403);var b=await Body();string key=(string)b["content_id"],kind=(string)b["kind"];
        if(!Regex.IsMatch(key??"",@"^tmdb:(movie|tv):\d+$")||!new[]{"movie","series","cartoon_movie","cartoon_series","anime_movie","anime_series","inherit"}.Contains(kind))return J(new{error="bad_content_kind"},400);
        HomeDb.With(c=>Profiles.Exec(c,"INSERT INTO content_items(id,override_kind) VALUES(@k,@v) ON CONFLICT(id) DO UPDATE SET override_kind=@v,meta_rev=meta_rev+1",("@k",key),("@v",kind=="inherit"?null:kind)));
        return J(new{ok=true});
    }
    [HttpGet,HttpPost,AllowAnonymous,Route("home/profiles/me/settings")]
    public async Task<ActionResult> MySettings()
    {
        var user=Viewer();if(user==null)return J(new{error="session_required"},401);
        if(!HttpMethods.IsGet(Request.Method)){
            var b=await Body();var cfg=(JObject)Profiles.Get(user.Id)["settings"];
            if(b["youtube_profile"]!=null){
                string pack=(string)b["youtube_profile"];
                if(!YtStore.CanAssign(user.Id,pack))return J(new{error="bad_youtube_profile"},400);
                // Ребёнок не может назначить себе другой набор каналов: иначе через youtube_profile
                // он расширяет свой allowlist чужим глобальным паком. Смена набора — только админ.
                if(user.Child&&!string.Equals(pack,(string)cfg["youtube_profile"],System.StringComparison.Ordinal))return J(new{error="adult_required"},403);
                cfg["youtube_profile"]=pack;
            }
            if(!user.Child){
                foreach(string key in new[]{"torrent_auto","torrent_max_height","torrent_min_seeds","torrent_allow_hevc","torrent_allow_hdr"})if(b[key]!=null)cfg[key]=b[key];
            }else if(b["youtube_profile"]==null||b.Properties().Any(p=>p.Name!="youtube_profile"))return J(new{error="adult_required"},403);
            HomeDb.With(c=>Profiles.Exec(c,"UPDATE user_profiles SET settings=@j WHERE id=@p",("@j",cfg.ToString(Formatting.None)),("@p",user.Id)));
        }
        return J(new{settings=Profiles.Get(user.Id)["settings"]});
    }
    [HttpPost,AllowAnonymous,Route("home/admin/activate-profiles")]
    public async Task<ActionResult> ActivateProfiles()
    {
        if(!Admin())return J(new{error="admin_required"},403);
        if(Profiles.Enabled)return J(new{ok=true,enabled=true});
        try{
            HomeDb.Set("user_cloud_import_complete","0");
            await ProfileMirror.Run(false,true);
            if(HomeDb.Get("user_cloud_import_complete")!="1")return J(new{error="complete_cloud_import_required",message=ProfileMirror.Last},503);
            Profiles.Activate();return J(new{ok=true,enabled=true,restart_clients=true});
        }catch(Exception e){return J(new{error="profile_activation_failed",message=e.GetType().Name},503);}
    }
    [HttpGet,AllowAnonymous,Route("home/admin/mirror/preflight")]
    public async Task<ActionResult> MirrorPreflight()
    {
        if(!Admin())return J(new{error="admin_required"},403);
        try{string result=await ProfileMirror.Run(true,true);return J(new{result,missing=ProfileMirror.MissingMappings,items=ProfileMirror.SnapshotItems});}
        catch(Exception e){return J(new{error="preflight_failed",message=e.Message},503);}
    }
    [HttpGet,AllowAnonymous,Route("home/admin/mirror")]
    public ActionResult MirrorStatus()
    {
        if(!Admin())return J(new {error="admin_required"},403);
        var rows=HomeDb.With(c=>Profiles.Rows(c,"SELECT destination,content_id,desired,observed,revision,confirmed_revision,managed,retry_at FROM kp_mirror_state ORDER BY destination,content_id"));
        return J(new {states=new JArray(rows.Select(r=>new JObject { ["destination"]=(string)r[0],["content_id"]=(string)r[1],["desired"]=Convert.ToInt64(r[2])!=0,["observed"]=r[3] is DBNull?null:new JValue(Convert.ToInt64(r[3])!=0),["revision"]=Convert.ToInt64(r[4]),["confirmed_revision"]=Convert.ToInt64(r[5]),["managed"]=Convert.ToInt64(r[6])!=0 }))});
    }
    [HttpGet,AllowAnonymous,Route("home/admin/profiles")]
    public ActionResult AdminList()=>Admin()?J(new {enabled=Profiles.Enabled,test_instance=Profiles.IsTest,profiles=Profiles.List(),default_profile=Profiles.DefaultId,import_profile=Profiles.ImportId}):J(new {error="admin_required"},403);
    [HttpPost,HttpPatch,AllowAnonymous,Route("home/admin/profiles")]
    public async Task<ActionResult> SaveProfile()
    {
        if(!Admin())return J(new {error="admin_required"},403);
        try {
            var b=await Body();string id=(string)b["id"]??"u_"+Guid.NewGuid().ToString("N"),name=((string)b["name"]??"").Trim();
            if(!IdOk(id)||name.Length<1||name.Length>48)return J(new {error="invalid_name_or_id"},400);
            var old=Profiles.Get(id);bool child=(bool?)b["is_child"]??(bool?)old?["is_child"]??false;
            int age=child?((int?)b["max_age"]??(int?)old?["max_age"]??6):18;
            if(child&&!new[]{6,12,13,16}.Contains(age))return J(new {error="invalid_age_preset"},400);
            bool admin=!child&&((bool?)b["is_admin"]??(bool?)old?["is_admin"]??false);
            if(id==Profiles.MainId&&(!admin||child))return J(new {error="primary_admin_required"},400);
            string pin=child?null:(b.Property("pin")!=null?Profiles.PinHash((string)b["pin"]):(string)old?["pin_hash"]);
            var cfg=(JObject)(b["settings"]??old?["settings"]??new JObject());
            if(cfg["sources"]!=null&&cfg["sources"] is not JObject)return J(new{error="bad_sources"},400);
            if(cfg["sources"] is JObject sources)foreach(string key in new[]{"youtube","vk"})if(sources[key]!=null&&sources[key].Type!=JTokenType.Boolean)return J(new{error="bad_source_flag"},400);
            if(cfg["torrent_auto"]==null)cfg["torrent_auto"]=false;
            if(cfg["youtube_mode"]==null)cfg["youtube_mode"]=child&&age==6?"channels":"normal";
            if(!new[]{"normal","channels","off"}.Contains((string)cfg["youtube_mode"]))return J(new {error="bad_youtube_mode"},400);
            string ytPack=(string)cfg["youtube_profile"];
            if(string.IsNullOrEmpty(ytPack))cfg.Remove("youtube_profile");
            else if(!YtStore.CanAssign(id,ytPack))return J(new{error="bad_youtube_profile"},400);
            HomeDb.With(c=>Profiles.Exec(c,@"INSERT INTO user_profiles(id,name,is_child,max_age,pin,is_admin,settings) VALUES(@id,@n,@c,@a,@p,@ad,@j)
ON CONFLICT(id) DO UPDATE SET name=@n,is_child=@c,max_age=@a,pin=@p,is_admin=@ad,settings=@j,policy_rev=user_profiles.policy_rev+1",("@id",id),("@n",name),("@c",child?1:0),("@a",age),("@p",pin),("@ad",admin?1:0),("@j",cfg.ToString(Formatting.None))));
            if(old!=null&&((string)old["pin_hash"]!=pin||(bool)old["is_child"]!=child||(bool)old["is_admin"]!=admin))HomeDb.With(c=>Profiles.Exec(c,"UPDATE user_sessions SET revoked=1 WHERE profile_id=@p AND token_hash<>@actor",("@p",id),("@actor",Viewer()?.TokenHash??"")));
            return J(new {profile=Profiles.Public(Profiles.Get(id))});
        }catch(Exception e){return J(new {error=e.Message},400);}
    }
    [HttpDelete,HttpPost,AllowAnonymous,Route("home/admin/profiles/{id}"),Route("home/admin/profiles/{id}/archive")]
    public ActionResult Archive(string id)
    {
        if(!Admin())return J(new {error="admin_required"},403);
        if(id==Profiles.MainId||id==Profiles.DefaultId||id==Profiles.ImportId)return J(new {error="profile_in_use"},409);
        HomeDb.With(c=>{using var tx=c.BeginTransaction();Profiles.Exec(c,"UPDATE user_profiles SET archived=1,policy_rev=policy_rev+1 WHERE id=@id",("@id",id));Profiles.Exec(c,"UPDATE user_sessions SET revoked=1 WHERE profile_id=@id",("@id",id));
            foreach(var r in Profiles.Rows(c,"SELECT content_id,collection FROM memberships WHERE profile_id=@id",("@id",id))){var a=((string)r[0]).Split(':');if(a.Length==3){string d=ProfileLibrary.Destination(a[1],(string)r[1]);if(d!=null)ProfileLibrary.Recompute(c,(string)r[0],d,true);}}tx.Commit();});
        return J(new {ok=true});
    }
    [HttpPut,HttpPost,AllowAnonymous,Route("home/admin/default-profile")]
    public async Task<ActionResult> Default()
    {
        if(!Admin())return J(new {error="admin_required"},403);
        var b=await Body();string id=(string)b["profile_id"];var p=Profiles.Get(id);if(p==null||(bool)p["archived"])return J(new {error="unknown_profile"},400);
        HomeDb.Set("user_default_profile",id);return J(new {ok=true});
    }
    [HttpGet,HttpPost,AllowAnonymous,Route("home/admin/profile-rows")]
    public async Task<ActionResult> AdminRows(string profile_id)
    {
        if(!Admin())return J(new{error="admin_required"},403);var p=Profiles.Get(profile_id);if(p==null)return J(new{error="unknown_profile"},404);
        using var scope=Profiles.Enter(new Profiles.Scope(profile_id,"admin","admin",null,false,(bool)p["is_child"],(int)p["max_age"],(long)p["policy_rev"]));
        return await RowsFor(profile_id,true);
    }
    [HttpGet,HttpPut,HttpPost,AllowAnonymous,Route("home/profiles/me/rows")]
    public async Task<ActionResult> Rows()
    {
        var u=Viewer();if(u==null)return J(new {error="session_required"},401);
        return await RowsFor(u.Id);
    }
    async Task<ActionResult> RowsFor(string profileId,bool defaults=false)
    {
        if(!HttpMethods.IsGet(Request.Method)) {
            var b=await Body();if(b["rows"] is not JArray list||list.Count>RowsAllowed.Count)return J(new {error="bad_rows"},400);
            var ids=new HashSet<string>();foreach(var row in list.Children<JObject>())if(!RowsAllowed.Contains((string)row["id"]??"")||!ids.Add((string)row["id"]))return J(new {error="unknown_or_duplicate_row"},400);
            HomeDb.With(c=>{using var tx=c.BeginTransaction();int n=0;foreach(var row in list.Children<JObject>())Profiles.Exec(c,"INSERT INTO profile_rows(profile_id,row_id,enabled,sort_order) VALUES(@p,@r,@e,@o) ON CONFLICT(profile_id,row_id) DO UPDATE SET enabled=@e,sort_order=@o",("@p",profileId),("@r",(string)row["id"]),("@e",(bool?)row["enabled"]==false?0:1),("@o",(int?)row["order"]??n++));tx.Commit();});
        }
        var rows=HomeDb.With(c=>Profiles.Rows(c,"SELECT row_id,enabled,sort_order FROM profile_rows WHERE profile_id=@p ORDER BY sort_order,row_id",("@p",profileId)));
        if(!defaults)return J(new {rows=new JArray(rows.Select(r=>new JObject { ["id"]=(string)r[0],["enabled"]=Convert.ToInt64(r[1])!=0,["order"]=Convert.ToInt32(r[2]) }))});
        var configured=rows.ToDictionary(r=>(string)r[0]);
        return J(new {rows=new JArray(RowsAllowed.Select((id,i)=>configured.TryGetValue(id,out var r)?new JObject{["id"]=id,["enabled"]=Convert.ToInt64(r[1])!=0,["order"]=Convert.ToInt32(r[2])}:new JObject{["id"]=id,["enabled"]=true,["order"]=100+i}).OrderBy(r=>(int)r["order"]))});
    }
    [HttpGet,HttpPut,HttpPost,AllowAnonymous,Route("home/view/preferences")]
    public async Task<ActionResult> Preferences(string card=null)
    {
        var u=Viewer();if(u==null)return J(new {error="session_required"},401);
        if(string.IsNullOrEmpty(card)||card.Length>200)return J(new {error="invalid_card"},400);
        if(!HttpMethods.IsGet(Request.Method)){
            var body=await Body();if(body.ToString().Length>8000)return J(new {error="preferences_too_large"},400);
            HomeDb.With(c=>{using var tx=c.BeginTransaction();var raw=Profiles.Rows(c,"SELECT j FROM view_preferences WHERE profile_id=@p AND content_id=@k",("@p",u.Id),("@k",card)).FirstOrDefault()?[0] as string;
                var merged=raw==null?new JObject():JObject.Parse(raw);merged.Merge(body,new JsonMergeSettings{MergeArrayHandling=MergeArrayHandling.Replace,MergeNullValueHandling=MergeNullValueHandling.Merge});
                Profiles.Exec(c,"INSERT INTO view_preferences(profile_id,content_id,j,at) VALUES(@p,@k,@j,@a) ON CONFLICT(profile_id,content_id) DO UPDATE SET j=@j,at=@a",("@p",u.Id),("@k",card),("@j",merged.ToString(Formatting.None)),("@a",HomeDb.Now()));tx.Commit();});
        }
        var r=HomeDb.With(c=>Profiles.Rows(c,"SELECT j FROM view_preferences WHERE profile_id=@p AND content_id=@k",("@p",u.Id),("@k",card))).FirstOrDefault();
        return J(new {preferences=r==null?new JObject():JObject.Parse((string)r[0])});
    }
}
