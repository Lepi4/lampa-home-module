// Added to the manifest only in the isolated network-proxy contract runner.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
namespace HomeMedia;
public class ProxyFixtureController : BaseController
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    [HttpGet,AllowAnonymous,Route("home/admin/proxy-fixture")]
    public ActionResult Run(bool drift=false)
    {
        if (!Profiles.IsTest || Profiles.FromRequest(HttpContext)?.Admin != true) return NotFound();
        var fixture = new JObject { ["connections"] = new JArray(new JObject { ["id"] = "secret", ["name"] = "Unit", ["url"] = "socks5://fixture-user:p%40ss%3Aword@127.0.0.1:7893" }),
            ["routes"] = JObject.FromObject(NetworkProxy.Services.ToDictionary(s => s, s => "secret")) };
        var submitted = (JObject)fixture.DeepClone(); submitted["connections"][0]["url"] = "socks5://127.0.0.1:7893";
        var checkedConfig = NetworkProxy.Validate(submitted, fixture);
        string url = NetworkProxy.Resolve(checkedConfig, "kinopub");
        var credentials = NetworkProxy.Web(url).Credentials.GetCredential(new Uri("http://unit.invalid"), "Basic");
        Check(credentials.UserName == "fixture-user" && credentials.Password == "p@ss:word", "existing auth preserved without double escaping");
        Check(!NetworkProxy.Display(url).Contains("fixture-user") && !NetworkProxy.Display(url).Contains("word"), "display redacted");
        submitted["connections"][0]["password"] = "new:/@ pass";
        checkedConfig = NetworkProxy.Validate(submitted, fixture);
        credentials = NetworkProxy.Web(NetworkProxy.Resolve(checkedConfig, "kinopub")).Credentials.GetCredential(new Uri("http://unit.invalid"), "Basic");
        Check(credentials.UserName == "fixture-user" && credentials.Password == "new:/@ pass", "new auth round trip");
        submitted["connections"][0]["clear_auth"] = true;
        checkedConfig = NetworkProxy.Validate(submitted, fixture);
        Check(NetworkProxy.Web(NetworkProxy.Resolve(checkedConfig,"kinopub")).Credentials == null, "clear auth");
        submitted["routes"]["youtube"] = "direct";
        Check(NetworkProxy.Resolve(NetworkProxy.Validate(submitted, fixture), "youtube") == "", "direct overrides auth route");
        string probe=Path.Combine(Path.GetTempPath(),"lmpc-proxy-argv-"+Guid.NewGuid().ToString("N"));
        try {
            System.IO.File.WriteAllText(probe,"#!/usr/bin/python3\nimport json,sys\nprint(json.dumps(sys.argv[1:]))\n");
            System.IO.File.SetUnixFileMode(probe,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var extracted=YoutubeProcess.Run(probe,"",2,default,"--version").GetAwaiter().GetResult();
            var args=JArray.Parse(extracted.Output);int index=args.ToList().FindIndex(x=>(string)x=="--proxy");
            Check(index>=0&&(string)args[index+1]=="","yt-dlp explicit direct mode");
        } finally { if(System.IO.File.Exists(probe))System.IO.File.Delete(probe); }
        var active = NetworkProxy.Load();
        Check(YtStore.Proxy == NetworkProxy.Resolve(active, "youtube"), "youtube actual route");
        string initPath=Path.Combine(Directory.GetCurrentDirectory(), "init.conf");
        if(drift){var changed=JObject.Parse(System.IO.File.ReadAllText(initPath));changed["tmdb"]["proxyapi"]=new JObject{["useproxy"]=true,["proxy"]=new JObject{["list"]=new JArray("socks5://127.0.0.1:9")}};System.IO.File.WriteAllText(initPath,changed.ToString());}
        var init = JObject.Parse(System.IO.File.ReadAllText(initPath));
        // The direct path explicitly disables environmental proxies; only the configured route is used.
        KpStore.Client();
        var searchClient = (System.Net.Http.HttpClient)typeof(KpSearchController).GetMethod("Client",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        var handlerField=typeof(System.Net.Http.HttpMessageInvoker).GetField("_handler",BindingFlags.Instance|BindingFlags.NonPublic);
        foreach (var pair in new[] { ("kinopub",KpStore.Client()), ("jacred",searchClient),
            ("youtube",(System.Net.Http.HttpClient)typeof(YoutubeController).GetMethod("Client",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)) }) {
            var handler=(System.Net.Http.SocketsHttpHandler)handlerField.GetValue(pair.Item2);
            Check(handler.UseProxy==(NetworkProxy.Resolve(active,pair.Item1).Length>0),pair.Item1+" handler follows route");
        }
        var tmdbType=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("TmdbProxy.ModInit")).FirstOrDefault(t=>t!=null);
        object runtime=tmdbType?.GetField("conf")?.GetValue(null);
        object api=runtime?.GetType().GetProperty("proxyapi")?.GetValue(runtime);
        bool nativeEnabled=api?.GetType().GetProperty("useproxy")?.GetValue(api) is true;
        return Json(new { ok=true, assertions=7, routes=active["routes"], youtube_direct=YtStore.Proxy.Length==0,
            native_tmdb_loaded=runtime!=null, native_tmdb_enabled=nativeEnabled,
            tmdb_api_enabled=(bool?)init["tmdb"]?["proxyapi"]?["useproxy"]??false,
            tmdb_images_enabled=(bool?)init["tmdb"]?["proxyimg"]?["useproxy"]??false,
            native_sentinel=(string)init["tmdb"]?["sentinel"],
            same_search_client=ReferenceEquals(searchClient,typeof(KpSearchController).GetMethod("Client",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)) });
    }
}
