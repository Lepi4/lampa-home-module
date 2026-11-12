using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace HomeMedia;

// Lift transport, explicit content bindings, and canonical progress identity.
public sealed record LiftTarget(string Media,long Tmdb,int Season,int Episode)
{
    public static LiftTarget Parse(string media,long tmdb,int season,int episode)
    {
        if(media is not ("movie" or "tv") || tmdb<=0 || tmdb>999999999999L)
            throw new ArgumentException("lift_bad_target");
        if(media=="movie" && (season!=0 || episode!=0))throw new ArgumentException("lift_movie_has_no_episode");
        if(media=="tv" && (season<0 || season>999 || episode<1 || episode>9999))
            throw new ArgumentException("lift_bad_episode");
        return new(media,tmdb,season,episode);
    }
    public string Identity => Media=="movie"?"movie-"+Tmdb:$"tv-{Tmdb}-s{Season}e{Episode}";
    public string Card => Tmdb+"_"+Media;
    public string Key => $"{Media}:{Tmdb}:{Season}:{Episode}";
}

public static class LiftPlaylist
{
    public static JObject Select(JObject playlist,bool movie,int season,int episode)
    {
        if(movie) {
            if(playlist["streams"] is not JObject)throw new InvalidDataException("lift_not_a_movie");
            return (JObject)playlist.DeepClone();
        }
        var candidates=(playlist["seasons"] as JArray ?? new JArray())
            .Children<JObject>().Where(s=>(int?)s["season"]==season)
            .SelectMany(s=>(s["episodes"] as JArray ?? new JArray()).Children<JObject>())
            .Where(e=>int.TryParse(e["episode"]?.ToString(),out int n)&&n==episode).ToArray();
        if(candidates.Length!=1)throw new InvalidDataException("lift_episode_missing_or_ambiguous");
        return (JObject)candidates[0].DeepClone();
    }
}

public static class LiftBindings
{
    const string Scope="approved-v1";
    // Caller must enforce Admin + !Child before Put, and policy before Get.
    public static JObject Get(LiftTarget t)=>HomeDb.With(c=> {
        var q=HomeDb.Query(c,"SELECT mapping FROM content_refs WHERE provider='lift' AND ref_kind='episode' AND scope=@s AND external_id=@k",
            ("@s",Scope),("@k",t.Key));
        return q.Count==0?null:JObject.Parse((string)q[0][0]);
    });
    public static JObject Put(LiftTarget t,long item,bool movie,int providerSeason,int providerEpisode)
    {
        if(item<=0 || (t.Media=="movie")!=movie || (movie&&(providerSeason!=0||providerEpisode!=0)) || providerSeason<0 || providerSeason>999
            || providerEpisode<0 || providerEpisode>9999 || (!movie && providerEpisode==0))
            throw new ArgumentException("lift_bad_binding");
        var mapping=new JObject {
            ["item"]=item,["movie"]=movie,["provider_season"]=providerSeason,["provider_episode"]=providerEpisode,
            ["revision"]=Guid.NewGuid().ToString("N"),["edition"]=Guid.NewGuid().ToString("N"),
            ["approved_by"]=Profiles.CurrentId,["approved_at"]=HomeDb.Now()
        };
        HomeDb.With(c=>HomeDb.Exec(c,@"INSERT INTO content_refs(provider,ref_kind,scope,external_id,content_id,mapping)
VALUES('lift','episode',@s,@k,@c,@m) ON CONFLICT(provider,ref_kind,scope,external_id) DO UPDATE SET content_id=@c,mapping=@m",
            ("@s",Scope),("@k",t.Key),("@c","tmdb:"+t.Media+":"+t.Tmdb),("@m",mapping.ToString(Newtonsoft.Json.Formatting.None))));
        return mapping;
    }
    public static string ProgressHash(LiftTarget t)=>HomeDb.With(c=> {
        var q=HomeDb.Query(c,"SELECT h FROM timeline WHERE profile_id=@profile AND id=@id LIMIT 1",("@id",t.Identity));
        if(q.Count>0)return (string)q[0][0];
        // A transport hash only: canonical episode identity remains the existing TMDB identity.
        for(int salt=0;salt<10;salt++) {
            var bytes=SHA256.HashData(Encoding.UTF8.GetBytes("lift:"+t.Identity+":"+salt));
            ulong n=0;for(int i=0;i<8;i++)n=(n<<8)|bytes[i];
            string h=(n%999999999999999UL+1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var old=TlV2.Get(c,h);
            if(old==null || old.Id==t.Identity)return h;
        }
        throw new InvalidOperationException("lift_progress_hash_conflict");
    });
}

public sealed class LiftApiClient : IDisposable
{
    readonly HttpClient http;
    readonly Uri[] api;
    readonly HashSet<string> cdn;
    readonly string basic,agent;
    const int MaxJson=2*1024*1024;
    public LiftApiClient(IEnumerable<Uri> apiRoots,IEnumerable<string> cdnHosts,string basicBase64,string userAgent,HttpMessageHandler handler=null)
    {
        api=apiRoots.Take(2).ToArray();
        if(api.Length==0 || api.Any(u=>u.Scheme!="https"||u.AbsolutePath!="/"||u.Query!=""||u.UserInfo!=""))
            throw new ArgumentException("lift_bad_api_configuration");
        cdn=new HashSet<string>(cdnHosts,StringComparer.OrdinalIgnoreCase);
        if(cdn.Count==0 || string.IsNullOrWhiteSpace(userAgent))throw new ArgumentException("lift_missing_cdn_or_agent");
        try {Convert.FromBase64String(basicBase64);}catch{throw new ArgumentException("lift_missing_credentials");}
        basic=basicBase64;agent=userAgent;
        http=new HttpClient(handler??new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false}) {Timeout=TimeSpan.FromSeconds(12)};
    }
    public void Dispose()=>http.Dispose();
    public Uri CheckMedia(string raw)
    {
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo!=""||u.Port!=443||!cdn.Contains(u.IdnHost))
            throw new InvalidDataException("lift_unapproved_cdn");
        return u;
    }
    static async Task<string> ReadBounded(HttpResponseMessage response,int limit,CancellationToken ct)
    {
        if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("lift_response_too_large");
        using var input=await response.Content.ReadAsStreamAsync(ct);
        using var output=new MemoryStream();byte[] buffer=new byte[8192];
        while(true) {int n=await input.ReadAsync(buffer,ct);if(n==0)break;if(output.Length+n>limit)throw new InvalidDataException("lift_response_too_large");await output.WriteAsync(buffer.AsMemory(0,n),ct);}
        return Encoding.UTF8.GetString(output.ToArray());
    }
    async Task<JObject> Api(string path,CancellationToken ct)
    {
        foreach(var root in api) {
            try {
                using var req=new HttpRequestMessage(HttpMethod.Get,new Uri(root,path));
                req.Headers.Authorization=new AuthenticationHeaderValue("Basic",basic);
                req.Headers.TryAddWithoutValidation("User-Agent",agent);
                using var r=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
                if(r.StatusCode==HttpStatusCode.Unauthorized||r.StatusCode==HttpStatusCode.Forbidden)
                    throw new InvalidDataException("lift_api_credentials_rejected");
                if((int)r.StatusCode==429)throw new InvalidDataException("lift_rate_limited");
                if((int)r.StatusCode>=500)continue;
                if(!r.IsSuccessStatusCode)throw new InvalidDataException("lift_api_http_"+(int)r.StatusCode);
                var json=JObject.Parse(await ReadBounded(r,MaxJson,ct));
                if(json["error"]!=null && json["error"].Type!=JTokenType.Null)
                    throw new InvalidDataException("lift_api_error");
                return json;
            } catch(HttpRequestException) { }
              catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
        }
        throw new HttpRequestException("lift_api_unavailable");
    }
    public Task<JObject> Search(string query,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query)||query.Length>160)throw new ArgumentException("lift_bad_query");
        return Api("search?q="+Uri.EscapeDataString(query),ct);
    }
    public Task<JObject> Playlist(long item,CancellationToken ct)
    {
        if(item<=0)throw new ArgumentException("lift_bad_item");
        return Api("playlist?id="+item,ct);
    }
    public async Task<string> Mpd(string url,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,CheckMedia(url));
        req.Headers.TryAddWithoutValidation("User-Agent",agent); // No API or Home credentials on CDN.
        using var r=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!r.IsSuccessStatusCode)throw new InvalidDataException("lift_manifest_unavailable");
        var xml=await ReadBounded(r,MaxJson,ct);
        ValidateManifestUrls(xml,req.RequestUri);
        return xml;
    }
    public void ValidateManifestUrls(string xml,Uri manifest)
    {
        using var text=new StringReader(xml);
        using var reader=XmlReader.Create(text,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxJson});
        var doc=XDocument.Load(reader);
        if(doc.Root?.Name.LocalName!="MPD" || (string)doc.Root.Attribute("type")=="dynamic"
            || doc.Descendants().Count(x=>x.Name.LocalName=="Period")!=1
            || doc.Descendants().Any(x=>x.Name.LocalName=="ContentProtection"))
            throw new InvalidDataException("lift_mpd_layout_unsupported");
        foreach(var a in doc.Descendants().Attributes())
            if(a.Name.NamespaceName=="http://www.w3.org/1999/xlink")throw new InvalidDataException("lift_mpd_xlink_unsupported");
        void Walk(XElement node,Uri parent)
        {
            var bases=node.Elements().Where(x=>x.Name.LocalName=="BaseURL").ToArray();
            if(bases.Length>1)throw new InvalidDataException("lift_multiple_baseurls_unsupported");
            Uri current=bases.Length==0?parent:CheckMedia(new Uri(parent,bases[0].Value.Trim()).AbsoluteUri);
            foreach(var a in node.Attributes())
                if(a.Name.LocalName is "sourceURL" or "media" or "index" or "initialization")
                    CheckMedia(new Uri(current,a.Value).AbsoluteUri);
            foreach(var child in node.Elements().Where(x=>x.Name.LocalName!="BaseURL")) {
                if(child.Name.LocalName is "Location" or "PatchLocation" or "UTCTiming")
                    throw new InvalidDataException("lift_mpd_external_control_unsupported");
                Walk(child,current);
            }
        }
        Walk(doc.Root,CheckMedia(manifest.AbsoluteUri));
    }
}
