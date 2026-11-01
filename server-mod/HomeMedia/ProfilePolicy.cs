using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

public static class ProfilePolicy
{
    static readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(8)};
    static readonly SemaphoreSlim gate=new(6);
    static readonly ConcurrentDictionary<string,Task<JObject>> pending=new();
    public static int? Age(string value)
    {
        string r=(value??"").Trim().ToUpperInvariant().Replace(" ","");
        return r switch { "G" or "TV-Y" or "TV-G" or "G_ALLAGES"=>0,
            "PG" or "PG_CHILDREN"=>6,
            "TV-Y7"=>7,
            "TV-PG"=>12,
            "PG-13" or "PG_13" or "PG13"=>13,
            "TV-14"=>14,
            "R-17" or "R17" or "R" or "TV-MA"=>17,
            "NC-17" or "R+" or "RX" or "R_PLUS"=>18,
            "0" or "0+"=>0,"6" or "6+"=>6,"12" or "12+"=>12,"13" or "13+"=>13,"14" or "14+"=>14,"16" or "16+"=>16,"18" or "18+"=>18,_=>null};
    }
    public static int? MetadataAge(JObject card)
    {
        if((bool?)card["adult"]==true)return 18;
        int? best=null;
        foreach(string field in new[]{"home_min_age","age_limit","age","age_rating","mpaa","rating","certification"}){
            var v=card[field];if(v==null||v is JContainer)continue;int? a=Age(v.ToString());if(a!=null)best=Math.Max(best??0,a.Value);
        }
        foreach(string section in new[]{"release_dates","content_ratings"}){
            var markets=(card[section]?["results"] as JArray)?.Children<JObject>().ToList();if(markets==null)continue;
            foreach(var chosen in markets.Where(x=>(string)x["iso_3166_1"]=="RU"||(string)x["iso_3166_1"]=="US")){
                var values=section=="release_dates"?chosen.SelectTokens("release_dates[*].certification"):new[]{chosen["rating"]}.Where(x=>x!=null);
                foreach(var r in values){int? a=Age(r.ToString());if(a!=null)best=Math.Max(best??0,a.Value);}
            }
        }
        return best;
    }
    public static async Task<JObject> Metadata(string media,long id)
    {
        string key=ProfileLibrary.Key(media,id);
        var row=HomeDb.With(c=>Profiles.Rows(c,"SELECT j,at FROM content_items WHERE id=@k",("@k",key))).FirstOrDefault();
        if(row?[0] is string json && row[1] is not DBNull && HomeDb.Now()-Convert.ToInt64(row[1])<24L*3600_000){try{var cached=JObject.Parse(json);cached.Remove("home_min_age");int? age=MetadataAge(cached);if(age!=null)cached["home_min_age"]=age;return cached;}catch{}}
        var task=pending.GetOrAdd(key,_=>Fetch(media,id,key));
        try{return await task;}finally{if(task.IsCompleted)pending.TryRemove(key,out _);}
    }
    static async Task<JObject> Fetch(string media,long id,string key)
    {
        await gate.WaitAsync();
        try{
            string append=media=="tv"?"content_ratings":"release_dates";
            string api=(string)KpStore.Load()["tmdb_key"]??"4ef0d7355d9ffb5151e987764708ce96";
            var j=JObject.Parse(await http.GetStringAsync($"http://127.0.0.1:9118/tmdb/api/3/{media}/{id}?language=ru&append_to_response={append}&api_key={Uri.EscapeDataString(api)}"));
            if((long?)j["id"]!=id)return null;
            j["media_type"]=media;int? age=MetadataAge(j);if(age!=null)j["home_min_age"]=age;
            HomeDb.With(c=>Profiles.Exec(c,"INSERT INTO content_items(id,kind,min_age,j,at) VALUES(@k,@kind,@age,@j,@at) ON CONFLICT(id) DO UPDATE SET min_age=@age,j=@j,at=@at",("@k",key),("@kind",CardKind.Kind(j)),("@age",age),("@j",j.ToString(Formatting.None)),("@at",HomeDb.Now())));
            return j;
        }catch{return null;}finally{gate.Release();}
    }
    public static string Rule(string kind,string subject)=>HomeDb.With(c=>Profiles.Rows(c,"SELECT decision FROM profile_rules WHERE profile_id=@p AND kind=@k AND subject=@s",("@p",Profiles.CurrentId),("@k",kind),("@s",subject))).FirstOrDefault()?[0] as string;
    public static bool SourceEnabled(string source)
    {
        if (HomeDb.Get("module_" + source) == "0") return false;
        if (!Profiles.Enabled) return source != "vk";
        var user = Profiles.Current;
        if (user == null) return false;
        var settings = Profiles.Get(user.Id)?["settings"];
        if (source == "youtube" && (string)settings?["youtube_mode"] == "off") return false;
        return (bool?)settings?["sources"]?[source] ?? source != "vk";
    }
    public static async Task<bool> Allows(JObject card)
    {
        var u=Profiles.Current;
        bool yt=card["youtube"]!=null||card["video_id"]!=null||card["channel_id"]!=null||((string)card["id"]??"").StartsWith("UC")||((string)card["thumb"]??"").Contains("ytimg.com/");
        if(yt&&!SourceEnabled("youtube"))return false;
        if(!Profiles.Enabled||u?.Child!=true)return true;
        if(yt){
            var cfg=(JObject)Profiles.Get(u.Id)["settings"];string mode=(string)cfg["youtube_mode"]??"normal";
            if(mode=="off")return false;
            string channel=(string)card["channel_id"]??(((string)card["id"]??"").StartsWith("UC")?(string)card["id"]:"");string video=(string)card["video_id"]??(string)card["id"];
            if(Rule("youtube_video",video)=="deny"||Rule("youtube_channel",channel)=="deny")return false;
            int? age=MetadataAge(card);if(age>u.MaxAge)return false;
            if(mode!="channels")return true;
            if(Rule("youtube_channel",channel)=="allow")return true;
            return YtStore.ChannelIn((string)cfg["youtube_profile"],channel);
        }
        string media=CardKind.Media(card);long id=long.TryParse(card["id"]?.ToString(),out long n)?n:0;
        string key=id>0?ProfileLibrary.Key(media,id):(string)card["home_content_id"];
        string rule=key==null?null:Rule("content",key);if(rule!=null)return rule=="allow";
        int? a=MetadataAge(card);
        if(id>0){var full=await Metadata(media,id);if(full!=null){int? extra=MetadataAge(full);if(extra!=null)a=Math.Max(a??0,extra.Value);}}
        // Unknown film/anime/DLNA metadata needs one parental exception; never guess from the genre.
        return a!=null&&a<=u.MaxAge;
    }
    public static async Task<JToken> Filter(JToken value)
    {
        if(value is JArray array){
            var tasks=array.Select(async item=>{
                bool card=item is JObject c&&(c["video_id"]!=null||c["channel_id"]!=null||c["youtube"]!=null||((string)c["id"]??"").StartsWith("UC")||((string)c["thumb"]??"").Contains("ytimg.com/")||((c["poster_path"]!=null||c["first_air_date"]!=null||c["release_date"]!=null||c["media_type"]!=null||c["overview"]!=null)&&long.TryParse(c["id"]?.ToString(),out long n)&&n>0));
                if(card){var content=(JObject)item;return await Allows(content)?(JToken)CardKind.Annotate(content):null;}
                return await Filter(item);
            }).ToArray();
            return new JArray((await Task.WhenAll(tasks)).Where(x=>x!=null));
        }
        if(value is JObject o){foreach(var p in o.Properties().ToList())if(p.Value is JContainer)p.Value=await Filter(p.Value);}
        return value;
    }
}

// Runs on JSON DTOs only. Streams, HLS, images and video bodies are never buffered.
[AttributeUsage(AttributeTargets.Class)]
public sealed class ProfileResultAttribute : Attribute,IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context,ResultExecutionDelegate next)
    {
        if(Profiles.Enabled){
            if(context.Result is ContentResult cr&&cr.ContentType?.StartsWith("application/json")==true&&!string.IsNullOrEmpty(cr.Content)){
                try{var j=JToken.Parse(cr.Content);
                    j=await ProfilePolicy.Filter(j);cr.Content=j.ToString(Formatting.None);}catch(JsonException){}
            }else if(context.Result is ObjectResult result&&result.Value!=null){result.Value=await ProfilePolicy.Filter(JToken.FromObject(result.Value));}
        }
        await next();
    }
}
