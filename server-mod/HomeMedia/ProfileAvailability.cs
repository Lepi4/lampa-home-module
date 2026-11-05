using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace HomeMedia;
public static class ProfileAvailability
{
    static readonly ConcurrentDictionary<string,(DateTime at,HashSet<(int,int)> set)> cache=new();
    static readonly SemaphoreSlim gate=new(6);
    static readonly ConcurrentDictionary<string,Lazy<Task<HashSet<(int,int)>>>> pending=new();
    public static async Task<HashSet<(int,int)>> Available(long kp,string token,string account)
    {
        string key=account+":"+kp;
        if(cache.TryGetValue(key,out var hit)&&DateTime.UtcNow-hit.at<TimeSpan.FromMinutes(5))return hit.set;
        var work=pending.GetOrAdd(key,_=>new Lazy<Task<HashSet<(int,int)>>>(()=>Fetch(kp,token,account)));
        try{return await work.Value;}
        finally{if(pending.TryGetValue(key,out var current)&&ReferenceEquals(current,work))pending.TryRemove(key,out _);}
    }
    static async Task<HashSet<(int,int)>> Fetch(long kp,string token,string account)
    {
        string key=account+":"+kp;if(cache.TryGetValue(key,out var old)&&DateTime.UtcNow-old.at<TimeSpan.FromMinutes(5))return old.set;
        await gate.WaitAsync();try{
            if(cache.TryGetValue(key,out old)&&DateTime.UtcNow-old.at<TimeSpan.FromMinutes(5))return old.set;
            var(code,body)=await KpStore.Send(HttpMethod.Get,KpStore.Api+"/v1/items/"+kp,token);if(code!=200)return cache.TryGetValue(key,out old)?old.set:null;
            var item=JObject.Parse(body)["item"];var set=new HashSet<(int,int)>();
            foreach(var season in (item?["seasons"] as JArray??new JArray()).Children<JObject>())foreach(var ep in (season["episodes"] as JArray??new JArray()).Children<JObject>()){
                // These are provider-available files, independent of its shared watched flags.
                if(ep["files"] is JArray files&&files.Count==0)continue;
                int s=(int?)season["number"]??0,e=(int?)ep["number"]??0;if(s>0&&e>0)set.Add((s,e));
            }
            cache[key]=(DateTime.UtcNow,set);return set;
        }catch{return cache.TryGetValue(key,out old)?old.set:null;}finally{gate.Release();}
    }
    public static async Task EnrichCached(JArray cards,string account)
    {
        if(!Profiles.Enabled)return;
        var watched=await KpUnified.WatchedSets();
        foreach(var card in cards.Children<JObject>().Where(c=>CardKind.Media(c)=="tv")){
            // A source card's shared cloud counter is not this viewer's progress.
            card.Remove("kp_new");
            long id=(long?)card["id"]??0,kp=(long?)card["kp_id"]??KpMap.CachedKp("tv",id);
            if(kp<=0||!cache.TryGetValue(account+":"+kp,out var hit))continue;
            watched.TryGetValue(id,out var seen);
            card["kp_new"]=hit.set.Count(x=>seen==null||!seen.Contains(x));
            await SuppressSubtitlesOnlyNew(card,hit.set,seen);
        }
    }
    public static async Task Enrich(JArray cards,string token,string account)
    {
        if(!Profiles.Enabled||string.IsNullOrEmpty(token))return;var watched=await KpUnified.WatchedSets();
        await Task.WhenAll(cards.Children<JObject>().Where(c=>CardKind.Media(c)=="tv").Select(async card=>{
            long id=(long?)card["id"]??0,kp=(long?)card["kp_id"]??KpMap.CachedKp("tv",id);if(kp<=0)return;
            var available=await Available(kp,token,account);if(available==null)return;
            watched.TryGetValue(id,out var seen);card["kp_new"]=available.Count(x=>seen==null||!seen.Contains(x));
            await SuppressSubtitlesOnlyNew(card,available,seen);
        }));
    }

    // If the newest unseen episode exists only as СТ/Sub, do not trigger «новая серия»
    // in watching/watching_anime/watching_cartoons. Unknown source => keep the badge.
    static async Task SuppressSubtitlesOnlyNew(JObject card,HashSet<(int,int)> available,HashSet<(int,int)> seen)
    {
        if(ReleasePolicy.AllowSubtitlesOnly)return;
        var unseen=available.Where(e=>seen==null||!seen.Contains(e)).ToList();
        if(unseen.Count==0){card["kp_new"]=0;return;}
        long id=(long?)card["id"]??0;
        string title=(string)(card["original_name"]??card["name"])??"";
        int eligible=0;
        foreach(var ep in unseen){
            bool? ru=ReleasePolicy.EpisodeRu("tv",id,ep.Item1,ep.Item2);
            if(ru==null){_=ReleaseIndexer.Ensure("tv",id,title);eligible++;continue;}
            if(ru!=false)eligible++;
        }
        card["kp_new"]=eligible;
    }
}
