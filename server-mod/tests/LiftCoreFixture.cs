using HomeMedia;using Newtonsoft.Json.Linq;using System.Net;using System.Net.Http;using System.Text;
class LiftFake:HttpMessageHandler {public List<HttpRequestMessage> Requests=new(); public Queue<HttpResponseMessage> Responses=new();protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c){Requests.Add(r);return Task.FromResult(Responses.Dequeue());}}
class LiftCoreFixture {
static int n;static void T(string name,bool ok){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);n++;}
static HttpResponseMessage R(HttpStatusCode c,string text)=>new(c){Content=new StringContent(text)};
static async Task Main(){
var fixture=Path.Combine(AppContext.BaseDirectory,"lift-core-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);Directory.SetCurrentDirectory(fixture);
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
var entry=JObject.Parse("{streams:{dash_vp9:'https://media.example/manifest.mpd'},audio:{names:['Voice'],order:[0]}}");
string M(string reps)=>"<MPD><Period><AdaptationSet contentType='video' mimeType='video/webm' codecs='vp09.00.40.08'>"+reps+"</AdaptationSet><AdaptationSet contentType='audio'><Representation id='0'/></AdaptationSet></Period></MPD>";
JObject Describe(string reps)=>LiftVp9Policy.Describe(entry,M(reps));
string V(string id,int w,int h,long b=1)=>$"<Representation id='{id}' width='{w}' height='{h}' bandwidth='{b}'/>";
var fixedQuality=Describe(V("sd",864,486)+V("hd",1280,720)+V("fhd",1920,1080)+V("uhd",3840,2160));
T("exactly one 1080 track reaches the player",((JArray)fixedQuality["representations"]).Count==1&&(string)fixedQuality["representations"][0]["id"]=="fhd");
T("descriptor explicitly fixes the 1080 policy",(int)fixedQuality["quality_height"]==1080&&(bool)fixedQuality["quality_fixed"]);
bad=false;try{Describe(V("hd",1280,720));}catch(InvalidDataException e){bad=e.Message=="lift_1080_unavailable";}T("720 cannot silently substitute for missing 1080",bad);
bad=false;try{Describe(V("uhd",3840,2160));}catch(InvalidDataException e){bad=e.Message=="lift_1080_unavailable";}T("4K cannot substitute for fixed 1080",bad);
T("cropped cinema keeps the 1080 quality class",(int)Describe(V("crop",1920,804))["representations"][0]["height"]==804);
T("one highest bitrate 1080 representation is fixed",(string)Describe(V("low",1920,1080,1000)+V("high",1920,1080,2000))["representations"][0]["id"]=="high");
T("equal bitrate selection is deterministic",(string)Describe(V("z",1920,1080,1000)+V("a",1920,1080,1000))["representations"][0]["id"]=="a");
bad=false;try{Describe(V("duplicate",1280,720)+V("duplicate",1920,1080));}catch(InvalidDataException e){bad=e.Message=="lift_video_mapping_ambiguous";}T("ambiguous representation ID cannot unlock another resolution",bad);
bad=false;try{LiftVp9Policy.Describe(entry,M(V("av1",1920,1080)).Replace("vp09.00.40.08","av01.0.08M.08"));}catch(InvalidDataException e){bad=e.Message=="lift_1080_unavailable";}T("AV1 is not a substitute for 1080 VP9",bad);
Console.WriteLine("TOTAL "+n);
}}
