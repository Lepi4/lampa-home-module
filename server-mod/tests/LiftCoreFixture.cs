using HomeMedia;using Newtonsoft.Json.Linq;using System.Net;using System.Net.Http;using System.Text;
class LiftFake:HttpMessageHandler {public List<HttpRequestMessage> Requests=new(); public Queue<HttpResponseMessage> Responses=new();protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c){Requests.Add(r);return Task.FromResult(Responses.Dequeue());}}
class LiftCoreFixture {
static int n;static void T(string name,bool ok){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);n++;}
static HttpResponseMessage R(HttpStatusCode c,string text)=>new(c){Content=new StringContent(text)};
static async Task Main(){
var t=LiftTarget.Parse("tv",42,1,2);T("existing TMDB identity",t.Identity=="tv-42-s1e2"&&t.Card=="42_tv");
bool bad=false;try{LiftTarget.Parse("movie",42,1,2);}catch(ArgumentException){bad=true;}T("movie cannot become an episode",bad);
var p=JObject.Parse("{seasons:[{season:2,episodes:[{episode:1,title:'wrong'}]},{season:1,episodes:[{episode:2,title:'right'}]}]}");
T("select season number instead of array index",(string)LiftPlaylist.Select(p,false,1,2)["title"]=="right");
((JArray)p["seasons"]).Add(p["seasons"][1].DeepClone());bad=false;try{LiftPlaylist.Select(p,false,1,2);}catch(System.IO.InvalidDataException){bad=true;}T("ambiguous episode rejected",bad);
var f=new LiftFake();f.Responses.Enqueue(R(HttpStatusCode.InternalServerError,"error"));f.Responses.Enqueue(R(HttpStatusCode.OK,"{items:[]}"));
using var client=new LiftApiClient(new[]{new Uri("https://api-a.example/"),new Uri("https://api-b.example/")},new[]{"media.example"},Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:fixture")),"fixture-agent",f);
await client.Search("title",default);T("failover after 500",f.Requests.Count==2&&f.Requests[1].RequestUri.Host=="api-b.example");
T("API Basic is request scoped",f.Requests[0].Headers.Authorization?.Scheme=="Basic");
f.Responses.Enqueue(R(HttpStatusCode.OK,"<MPD><Period/></MPD>"));await client.Mpd("https://media.example/manifest.mpd",default);T("CDN gets no Basic credentials",f.Requests.Last().Headers.Authorization==null);
bad=false;try{client.CheckMedia("https://other.example/a");}catch(System.IO.InvalidDataException){bad=true;}T("unapproved CDN is rejected",bad);
f.Responses.Enqueue(R(HttpStatusCode.Redirect,""));bad=false;try{await client.Mpd("https://media.example/a",default);}catch(System.IO.InvalidDataException){bad=true;}T("redirect is not accepted as MPD",bad);
f.Responses.Enqueue(R(HttpStatusCode.OK,new string('x',2*1024*1024+1)));bad=false;try{await client.Mpd("https://media.example/a",default);}catch(System.IO.InvalidDataException){bad=true;}T("oversize manifest rejected",bad);
Environment.SetEnvironmentVariable("HOME_PROFILES_ENABLED","1");HomeDb.With(c=>{});Profiles.Activate();using var own=Profiles.Enter(new Profiles.Scope("u_default","fixture","fixture",null,true,false,18,1));
var binding=LiftBindings.Put(t,123,false,4,5);var loaded=LiftBindings.Get(t);T("approved mapping contains no playback URL",(long)loaded["item"]==123&&(int)loaded["provider_season"]==4&&loaded["url"]==null);
var hash=LiftBindings.ProgressHash(t);T("new hash fits server decimal contract",System.Text.RegularExpressions.Regex.IsMatch(hash,"^[1-9][0-9]{0,14}$"));
client.ValidateManifestUrls("<MPD><BaseURL>https://media.example/base/</BaseURL><Period><AdaptationSet><Representation><SegmentTemplate media='segment-$Number$.webm'/></Representation></AdaptationSet></Period></MPD>",new Uri("https://media.example/a.mpd"));T("relative DASH segment URLs retain approved origin",true);
bad=false;try{client.ValidateManifestUrls("<MPD><Period><BaseURL>https://other.example/</BaseURL></Period></MPD>",new Uri("https://media.example/a.mpd"));}catch(System.IO.InvalidDataException){bad=true;}T("unapproved nested BaseURL rejected",bad);
bad=false;try{client.ValidateManifestUrls("<MPD><Period><ContentProtection/></Period></MPD>",new Uri("https://media.example/a.mpd"));}catch(System.IO.InvalidDataException){bad=true;}T("unsupported protected MPD rejected",bad);
HomeDb.With(c=>HomeDb.Exec(c,"INSERT OR REPLACE INTO devices(id,cfg) VALUES('fixture','{\"maxh\":1080,\"playback\":{\"max_height\":2160}}')"));
T("Lift uses device VOD cap instead of torrent filter",LiftService.DeviceHeight()==1080);
HomeDb.With(c=>HomeDb.Exec(c,"UPDATE devices SET cfg='{\"maxh\":0,\"playback\":{\"max_height\":720}}' WHERE id='fixture'"));
T("unlimited VOD does not inherit torrent filter",LiftService.DeviceHeight()==0);
Console.WriteLine("TOTAL "+n);
}}
