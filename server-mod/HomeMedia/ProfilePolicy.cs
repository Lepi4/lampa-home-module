using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace HomeMedia;

public static class ProfilePolicy
{
    public const int AgeClassifierVersion = 1;
    public static readonly long FactTtlMs = 24L * 3600_000;
    public static readonly long AbsenceTtlMs = 6L * 3600_000;

    public enum AgeDecision { Allow, Deny, Pending }

    static readonly Channel<string> ageQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(6) { FullMode = BoundedChannelFullMode.DropWrite });
    static readonly ConcurrentDictionary<string, byte> ageQueued = new();
    static readonly ConcurrentDictionary<string, long> ageBackoffUntil = new();
    static readonly ConcurrentDictionary<string, int> ageFailCount = new();

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

    public static void EnsureAgeColumns(SqliteConnection c)
    {
        var names = Profiles.Rows(c, "SELECT name FROM pragma_table_info('content_items')").Select(r => (string)r[0]).ToHashSet();
        if (!names.Contains("age_ver")) Profiles.Exec(c, "ALTER TABLE content_items ADD COLUMN age_ver INTEGER");
        if (!names.Contains("age_checked_at")) Profiles.Exec(c, "ALTER TABLE content_items ADD COLUMN age_checked_at INTEGER");
    }

    sealed record AgeFact(int? Age, int Ver, long CheckedAt);

    static AgeFact ReadFact(string key)
    {
        if (string.IsNullOrEmpty(key)) return new AgeFact(null, 0, 0);
        var row = HomeDb.With(c => Profiles.Rows(c, "SELECT min_age,j,age_ver,age_checked_at FROM content_items WHERE id=@k", ("@k", key))).FirstOrDefault();
        if (row == null) return new AgeFact(null, 0, 0);
        int? minAge = row[0] is DBNull or null ? null : Convert.ToInt32(row[0]);
        string json = row[1] as string;
        int ageVer = row[2] is DBNull or null ? 0 : Convert.ToInt32(row[2]);
        long checkedAt = row[3] is DBNull or null ? 0 : Convert.ToInt64(row[3]);
        // One-time backfill from stored JSON when age columns were missing (no network; does not change at)
        if (ageVer == 0 && !string.IsNullOrEmpty(json))
        {
            try
            {
                var jo = JObject.Parse(json);
                int? fromJ = MetadataAge(jo);
                long now = HomeDb.Now();
                HomeDb.With(c => Profiles.Exec(c, "UPDATE content_items SET min_age=@a,age_ver=@av,age_checked_at=@ac WHERE id=@k AND (age_ver IS NULL OR age_ver=0)",
                    ("@a", fromJ), ("@av", AgeClassifierVersion), ("@ac", now), ("@k", key)));
                minAge = fromJ;
                ageVer = AgeClassifierVersion;
                checkedAt = now;
            }
            catch { }
        }
        return new AgeFact(minAge, ageVer, checkedAt);
    }

    static bool IsYoutubeCard(JObject card) =>
        card["youtube"] != null || card["video_id"] != null || card["channel_id"] != null
        || (((string)card["id"] ?? "").StartsWith("UC")) || (((string)card["thumb"] ?? "").Contains("ytimg.com/"));

    public static bool YoutubeAllows(JObject card)
    {
        var u = Profiles.Current;
        if (!IsYoutubeCard(card)) return true;
        if (!SourceEnabled("youtube")) return false;
        if (!Profiles.Enabled || u == null) return true;
        var cfg = (JObject)Profiles.Get(u.Id)["settings"];
        string mode = (string)cfg["youtube_mode"] ?? "normal";
        if (mode == "off") return false;
        string channel = (string)card["channel_id"] ?? (((string)card["id"] ?? "").StartsWith("UC") ? (string)card["id"] : "");
        string video = (string)card["video_id"] ?? (string)card["id"];
        if (Rule("youtube_video", video) == "deny" || Rule("youtube_channel", channel) == "deny") return false;
        int? age = MetadataAge(card);
        if (age > u.MaxAge) return false;
        if (mode != "channels") return true;
        if (Rule("youtube_channel", channel) == "allow") return true;
        return YtStore.ChannelIn((string)cfg["youtube_profile"], channel);
    }

    public static AgeDecision Decide(string parentRule, int? age, int ageVer, long checkedAt, int maxAge, long now)
    {
        if (parentRule == "deny") return AgeDecision.Deny;
        if (parentRule == "allow") return AgeDecision.Allow;
        bool fresh = checkedAt > 0 && checkedAt <= now;
        if (age != null && ageVer == AgeClassifierVersion && fresh && now - checkedAt < FactTtlMs)
            return age <= maxAge ? AgeDecision.Allow : AgeDecision.Deny;
        if (age == null && ageVer == AgeClassifierVersion && fresh && now - checkedAt < AbsenceTtlMs)
            return AgeDecision.Deny;
        return AgeDecision.Pending;
    }

    public static AgeDecision Classify(JObject card, bool enqueue)
    {
        var user = Profiles.Current;
        if (!Profiles.Enabled || user?.Child != true) return AgeDecision.Allow;
        if (IsYoutubeCard(card)) return YoutubeAllows(card) ? AgeDecision.Allow : AgeDecision.Deny;
        string media = CardKind.Media(card);
        long id = long.TryParse(card["id"]?.ToString(), out long n) ? n : 0;
        string key = id > 0 ? ProfileLibrary.Key(media, id) : (string)card["home_content_id"];
        string rule = key == null ? null : Rule("content", key);
        var fact = ReadFact(key);
        var decision = Decide(rule, fact.Age, fact.Ver, fact.CheckedAt, user.MaxAge, HomeDb.Now());
        if (decision == AgeDecision.Pending && enqueue && id > 0 && !string.IsNullOrEmpty(key) && !AgeCooling(key))
        {
            string slot = media + ":" + id;
            if (ageQueued.TryAdd(slot, 0) && !ageQueue.Writer.TryWrite(slot))
                ageQueued.TryRemove(slot, out _);
        }
        return decision;
    }

    static bool AgeCooling(string key) =>
        ageBackoffUntil.TryGetValue(key, out long until) && HomeDb.Now() < until;

    static void NoteAgeBackoff(string key)
    {
        int n = ageFailCount.AddOrUpdate(key, 1, (_, c) => Math.Min(c + 1, 8));
        long wait = Math.Min(8L * 60_000, 30_000L << Math.Min(n - 1, 4));
        ageBackoffUntil[key] = HomeDb.Now() + wait;
    }

    static void ClearAgeBackoff(string key)
    {
        ageFailCount.TryRemove(key, out _);
        ageBackoffUntil.TryRemove(key, out _);
    }

    public static async Task RunAgeQueue(System.Threading.CancellationToken ct = default)
    {
        await foreach (var item in ageQueue.Reader.ReadAllAsync(ct))
        {
            var parts = item.Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[1], out long id)) { ageQueued.TryRemove(item, out _); continue; }
            try { await Metadata(parts[0], id); } catch { }
            finally { ageQueued.TryRemove(item, out _); }
        }
    }

    public static async Task<JObject> Metadata(string media,long id)
    {
        string key=ProfileLibrary.Key(media,id);
        var row=HomeDb.With(c=>Profiles.Rows(c,"SELECT j,at FROM content_items WHERE id=@k",("@k",key))).FirstOrDefault();
        if(row?[0] is string json && row[1] is not DBNull && HomeDb.Now()-Convert.ToInt64(row[1])<24L*3600_000){try{var cached=JObject.Parse(json);cached.Remove("home_min_age");int? age=MetadataAge(cached);if(age!=null)cached["home_min_age"]=age;return cached;}catch{}}
        if (AgeCooling(key)) return null;
        var task=pending.GetOrAdd(key,_=>Fetch(media,id,key));
        try{return await task;}finally{if(task.IsCompleted)pending.TryRemove(key,out _);}
    }
    static async Task<JObject> Fetch(string media,long id,string key)
    {
        using var work = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool entered = false;
        try
        {
            await gate.WaitAsync(work.Token);
            entered = true;
            string append=media=="tv"?"content_ratings":"release_dates";
            string api=(string)KpStore.Load()["tmdb_key"]??"4ef0d7355d9ffb5151e987764708ce96";
            var j=JObject.Parse(await http.GetStringAsync($"http://127.0.0.1:9118/tmdb/api/3/{media}/{id}?language=ru&append_to_response={append}&api_key={Uri.EscapeDataString(api)}", work.Token));
            if((long?)j["id"]!=id){ NoteAgeBackoff(key); return null; }
            j["media_type"]=media;int? age=MetadataAge(j);if(age!=null)j["home_min_age"]=age;
            long now = HomeDb.Now();
            HomeDb.With(c=>Profiles.Exec(c,"INSERT INTO content_items(id,kind,min_age,j,at,age_ver,age_checked_at) VALUES(@k,@kind,@age,@j,@at,@av,@ac) ON CONFLICT(id) DO UPDATE SET min_age=@age,j=@j,at=@at,age_ver=@av,age_checked_at=@ac",
                ("@k",key),("@kind",CardKind.Kind(j)),("@age",age),("@j",j.ToString(Formatting.None)),("@at",now),("@av",AgeClassifierVersion),("@ac",now)));
            ClearAgeBackoff(key);
            return j;
        }
        catch
        {
            // Temporary failure stays in memory. age_checked_at is only for a finished classification.
            NoteAgeBackoff(key);
            return null;
        }
        finally { if (entered) gate.Release(); }
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
        if (IsYoutubeCard(card)) return YoutubeAllows(card);
        var decision = Classify(card, enqueue: false);
        if (decision == AgeDecision.Allow) return true;
        if (decision == AgeDecision.Deny) return false;
        string media = CardKind.Media(card);
        if (!long.TryParse(card["id"]?.ToString(), out long id) || id <= 0) return false;
        await Metadata(media, id);
        return Classify(card, enqueue: false) == AgeDecision.Allow;
    }
    public static async Task<JToken> Filter(JToken value)
    {
        if(value is JArray array){
            var tasks=array.Select(async item=>{
                bool card=item is JObject c&&(c["video_id"]!=null||c["channel_id"]!=null||c["youtube"]!=null||((string)c["id"]??"").StartsWith("UC")||((string)c["thumb"]??"").Contains("ytimg.com/")||((c["poster_path"]!=null||c["first_air_date"]!=null||c["release_date"]!=null||c["media_type"]!=null||c["overview"]!=null)&&long.TryParse(c["id"]?.ToString(),out long n)&&n>0));
                if(card){var content=(JObject)item;
                    if(IsYoutubeCard(content))return YoutubeAllows(content)?(JToken)CardKind.Annotate(content):null;
                    return Classify(content,enqueue:false)==AgeDecision.Allow?(JToken)CardKind.Annotate(content):null;}
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
        int code = (context.Result as ContentResult)?.StatusCode
            ?? (context.Result as ObjectResult)?.StatusCode
            ?? 200;
        if (code < 200 || code > 299) { await next(); return; }
        if(Profiles.Enabled){
            if(context.Result is ContentResult cr&&cr.ContentType?.StartsWith("application/json")==true&&!string.IsNullOrEmpty(cr.Content)){
                try{var j=JToken.Parse(cr.Content);
                    j=await ProfilePolicy.Filter(j);cr.Content=j.ToString(Formatting.None);}catch(JsonException){}
            }else if(context.Result is ObjectResult result&&result.Value!=null){result.Value=await ProfilePolicy.Filter(JToken.FromObject(result.Value));}
        }
        await next();
    }
}
