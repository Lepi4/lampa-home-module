using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

// The web process never runs root commands. A separately installed, fixed-target
// worker consumes this spool and publishes results even while Lampac restarts.
public class ServiceControl : BaseController
{
    static readonly string Spool = Environment.GetEnvironmentVariable("HOME_SERVICE_SPOOL") ?? Path.Combine(Directory.GetCurrentDirectory(),"database","service-control");
    static readonly object Gate = new();
    static readonly string[] Components={"lampac","parsers","homemedia","channel-manager","tvheadend","torserver","yt-dlp"};
    static readonly string[] Actions={"check","prepare","start","stop","update","rollback"};
    bool Admin()=>Guard.Ok(Request)&&Profiles.FromRequest(HttpContext)?.Admin==true;
    static ActionResult J(object o,int status=200)=>new ContentResult{Content=JsonConvert.SerializeObject(o),ContentType="application/json; charset=utf-8",StatusCode=status};
    static JObject Read(string name){try{return JObject.Parse(System.IO.File.ReadAllText(Path.Combine(Spool,name)));}catch{return new JObject();}}

    [HttpGet,AllowAnonymous,Route("home/health")]
    public ActionResult Health()
    {
        try { HomeDb.With(c=>HomeDb.Query(c,"SELECT 1"));return J(new{ok=true,module="HomeMedia",profiles=Profiles.Enabled}); }
        catch { return J(new{ok=false},503); }
    }
    [HttpGet,AllowAnonymous,Route("home/admin/services")]
    public ActionResult State()
    {
        if(!Admin())return J(new{error="admin_required"},403);
        var state=Read("state.json");
        state["worker_online"]=HomeDb.Now()-(long?)state["at"]<90000;
        var jobs=new JArray();
        if(Directory.Exists(Spool))foreach(var p in Directory.GetFiles(Spool,"*.result").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Take(12))try{jobs.Add(JObject.Parse(System.IO.File.ReadAllText(p)));}catch{}
        state["jobs"]=jobs;
        state["pending"]=Directory.Exists(Spool)?Directory.GetFiles(Spool,"*.request").Length+Directory.GetFiles(Spool,"*.running").Length:0;
        return J(state);
    }
    [HttpPost,AllowAnonymous,Route("home/admin/services/job")]
    public async Task<ActionResult> Queue()
    {
        if(!Admin())return J(new{error="admin_required"},403);
        using var reader=new StreamReader(Request.Body);var raw=await reader.ReadToEndAsync();
        if(raw.Length>4096)return J(new{error="body_too_large"},400);
        JObject b;try{b=JObject.Parse(raw);}catch{return J(new{error="invalid_json"},400);}
        string component=(string)b["component"],action=(string)b["action"];
        if(!Components.Contains(component)||!Actions.Contains(action))return J(new{error="invalid_action"},400);
        lock(Gate)
        {
            var state=Read("state.json");
            if(HomeDb.Now()-(long?)state["at"]>=90000||state["at"]==null)return J(new{error="worker_offline"},503);
            var target=(state["services"] as JArray)?.Children<JObject>().FirstOrDefault(x=>(string)x["id"]==component);
            if(target==null||(target["actions"] as JArray)?.Any(x=>(string)x==action)!=true)return J(new{error="action_unavailable"},409);
            Directory.CreateDirectory(Spool);
            if(Directory.GetFiles(Spool,"*.request").Length+Directory.GetFiles(Spool,"*.running").Length>0)return J(new{error="job_in_progress"},409);
            var id=Guid.NewGuid().ToString("N");
            var job=new JObject{["id"]=id,["component"]=component,["action"]=action,["at"]=HomeDb.Now(),["actor"]=Profiles.FromRequest(HttpContext)?.Id};
            var tmp=Path.Combine(Spool,id+".tmp");System.IO.File.WriteAllText(tmp,job.ToString(Formatting.None));System.IO.File.Move(tmp,Path.Combine(Spool,id+".request"));
            return J(new{id,queued=true},202);
        }
    }

    [HttpPost,AllowAnonymous,Route("home/admin/services/automation")]
    public async Task<ActionResult> Automation()
    {
        if(!Admin())return J(new{error="admin_required"},403);
        using var reader=new StreamReader(Request.Body);string raw=await reader.ReadToEndAsync();
        if(raw.Length>1024)return J(new{error="body_too_large"},400);
        JObject body;try{body=JObject.Parse(raw);}catch{return J(new{error="invalid_json"},400);}
        string component=(string)body["component"],mode=(string)body["mode"];
        if(!Components.Contains(component)||!new[]{"off","check","prepare"}.Contains(mode))return J(new{error="invalid_policy"},400);
        lock(Gate){
            var state=Read("state.json");
            var target=(state["services"] as JArray)?.Children<JObject>().FirstOrDefault(x=>(string)x["id"]==component);
            if(state["at"]==null||HomeDb.Now()-(long?)state["at"]>=90000)return J(new{error="worker_offline"},503);
            if((bool?)target?["automation_available"]!=true)return J(new{error="automation_unavailable"},409);
            Directory.CreateDirectory(Spool);var policy=Read("automation.json");policy[component]=mode;
            string path=Path.Combine(Spool,"automation.json"),tmp=path+".tmp";
            System.IO.File.WriteAllText(tmp,policy.ToString(Formatting.None));System.IO.File.Move(tmp,path,true);
            return J(new{ok=true,component,mode});
        }
    }

    [HttpPost,AllowAnonymous,Route("home/admin/services/package"),RequestSizeLimit(536870912)]
    public async Task<ActionResult> Upload(string component)
    {
        if(!Admin())return J(new{error="admin_required"},403);
        if(component!="homemedia"&&component!="channel-manager")return J(new{error="package_upload_unavailable"},400);
        string id=Guid.NewGuid().ToString("N");Directory.CreateDirectory(Spool);
        string temporary=Path.Combine(Spool,id+".upload.part"),uploaded=Path.Combine(Spool,id+".upload");
        try {
            long size=0;byte[] buffer=new byte[81920];
            using(var file=System.IO.File.Create(temporary)){
                int n;while((n=await Request.Body.ReadAsync(buffer,0,buffer.Length,HttpContext.RequestAborted))>0){
                    size+=n;if(size>536870912)return J(new{error="package_too_large"},413);
                    await file.WriteAsync(buffer,0,n,HttpContext.RequestAborted);
                }
            }
            if(size==0)return J(new{error="empty_package"},400);
            lock(Gate){
                var state=Read("state.json");
                var target=(state["services"] as JArray)?.Children<JObject>().FirstOrDefault(x=>(string)x["id"]==component);
                if(HomeDb.Now()-(long?)state["at"]>=90000||target==null||(target["actions"] as JArray)?.Any(x=>(string)x=="package")!=true)return J(new{error="package_upload_unavailable"},409);
                if(Directory.GetFiles(Spool,"*.request").Length+Directory.GetFiles(Spool,"*.running").Length>0)return J(new{error="job_in_progress"},409);
                System.IO.File.Move(temporary,uploaded);
                var job=new JObject{["id"]=id,["component"]=component,["action"]="package",["at"]=HomeDb.Now(),["actor"]=Profiles.FromRequest(HttpContext)?.Id};
                string next=Path.Combine(Spool,id+".tmp");System.IO.File.WriteAllText(next,job.ToString(Formatting.None));System.IO.File.Move(next,Path.Combine(Spool,id+".request"));
                return J(new{id,queued=true},202);
            }
        } finally {if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary);}
    }
}
