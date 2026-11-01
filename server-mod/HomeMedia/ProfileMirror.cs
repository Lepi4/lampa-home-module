using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Shared cloud contains a union of local owners. It never supplies personal progress.
public static class ProfileMirror
{
    public static string Last="not started";
    public static int MissingMappings,SnapshotItems;
    static readonly SemaphoreSlim Gate=new(1,1);
    internal sealed class Transport
    {
        public Func<Task<string>> Token=()=>KpStore.Token(KpStore.DefaultProfile);
        public Func<string> Account=()=>KpStore.AcctGen(KpStore.DefaultProfile)??"default";
        public Func<HashSet<string>> Selected=KpImport.Selected;
        public Action<IEnumerable<string>> Select=KpImport.SetSelected;
        public Func<string,string,Task<JObject>> Read=Get;
        public Func<JObject,string,Task<JObject>> Map=(item,token)=>KpMap.ToTmdbCard(item,token);
        public Func<string,long,Task<long>> Lookup=(media,id)=>KpMap.LookupKp(media,id,KpStore.DefaultProfile);
        public Func<string,Dictionary<string,string>,string,Task<(int code,string body)>> Form=(route,body,token)=>KpStore.PostForm(KpStore.Api+"/v1/"+route,body,token);
        public Func<string,object,string,Task<(int code,string body)>> Json=(route,body,token)=>KpStore.PostJson(KpStore.Api+"/v1/"+route,body,token);
    }
    static async Task<JObject> Get(string token,string route){var (code,body)=await KpStore.Send(HttpMethod.Get,KpStore.Api+"/v1/"+route,token);if(code!=200)throw new InvalidOperationException("cloud_read_"+code);var result=JObject.Parse(body);if((int?)result["status"] is int status&&status!=200)throw new InvalidOperationException("cloud_status_"+status);return result;}
    static async Task<List<JObject>> Pages(Transport io,string token,string route)
    {
        var rows=new List<JObject>();var seen=new HashSet<long>();int? expected=null;
        for(int page=1;page<=100;page++){
            var d=await io.Read(token,route+(route.Contains('?')?"&":"?")+"perpage=100&page="+page);
            var list=d["items"] as JArray??throw new InvalidOperationException("cloud_items_missing");
            if(list.Any(x=>x is not JObject||(long?)x["id"]==null||(long?)x["id"]<=0))throw new InvalidOperationException("cloud_item_invalid");
            int total=(int?)d["pagination"]?["total"]??(page==1?1:0),current=(int?)d["pagination"]?["current"]??page;
            // Some provider responses encode an empty catalog as zero pages.
            if(page==1&&list.Count==0&&total==0&&(current==0||current==1)){total=1;current=1;}
            if(total<1||total>100||current!=page||(expected.HasValue&&expected!=total))throw new InvalidOperationException("incomplete_cloud_snapshot");
            if(list.Any(x=>!seen.Add((long)x["id"])))throw new InvalidOperationException("cloud_duplicate_page_items");
            expected=total;rows.AddRange(list.Children<JObject>());
            if(page>=total)return rows;
            if(list.Count==0)throw new InvalidOperationException("incomplete_cloud_snapshot");
        }
        throw new InvalidOperationException("cloud_pagination_limit");
    }
    static string Collection(string dest)=>dest=="watching"||dest=="bookmark:my"?"my":dest=="bookmark:favorite"?"favorite":"later";
    static string Audit(JObject snapshot)
    {
        string dir=Path.Combine(Directory.GetCurrentDirectory(),"database","kp_mirror_audit");Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".jsonl");
        File.WriteAllText(path,snapshot.ToString(Newtonsoft.Json.Formatting.None)+Environment.NewLine);
        foreach(var old in Directory.GetFiles(dir,"*.jsonl").OrderByDescending(x=>x).Skip(200))File.Delete(old);
        return path;
    }
    static void AuditCommand(string path,string dest,string key,long kp,bool before,bool want,IEnumerable<long> folders)
        =>File.AppendAllText(path,new JObject{["at"]=HomeDb.Now(),["destination"]=dest,["content_id"]=key,["kp"]=kp,["before"]=before,["want"]=want,["folders"]=new JArray(folders),["phase"]="command_intent"}.ToString(Newtonsoft.Json.Formatting.None)+Environment.NewLine);
    public static async Task<string> Run(bool dry,bool importOnly=false)
    {
        if(Profiles.IsTest&&!dry)return Last="test instance: cloud writes disabled";
        return await RunCore(new Transport(),dry,importOnly);
    }
    // Injectable transport is internal to this assembly; tests never contact the real provider.
    internal static async Task<string> RunCore(Transport io,bool dry=false,bool importOnly=false)
    {
        await Gate.WaitAsync();try{return await RunLocked(io,dry,importOnly);}finally{Gate.Release();}
    }
    static async Task<string> RunLocked(Transport io,bool dry,bool importOnly)
    {
        string account=io.Account();
        string token=await io.Token();if(token==null)return Last="cloud unavailable; local profiles continue working";
        if(!dry)HomeDb.With(c=>{using var tx=c.BeginTransaction();foreach(var r in Profiles.Rows(c,"SELECT DISTINCT content_id,collection FROM memberships")){var a=((string)r[0]).Split(':');if(a.Length!=3)continue;string dest=ProfileLibrary.Destination(a[1],(string)r[1]);if(dest!=null)ProfileLibrary.Recompute(c,(string)r[0],dest);}tx.Commit();});
        var before=HomeDb.With(c=>Profiles.Rows(c,"SELECT destination,content_id,revision,confirmed_revision,observed,desired,remote_id FROM kp_mirror_state WHERE account='shared'").ToDictionary(r=>(string)r[0]+"|"+(string)r[1],r=>r));
        MissingMappings=0;SnapshotItems=0;var rawSnapshot=new JObject();
        var folders=await Pages(io,token,"bookmarks");
        var selected=io.Selected();
        var destinations=new Dictionary<string,List<long>>{
            ["watching"]=new(),["bookmark:my"]=folders.Where(f=>selected.Contains((string)f["id"])).Select(f=>(long)f["id"]).ToList(),
            ["bookmark:favorite"]=folders.Where(f=>(string)f["title"]=="Избранное (Lampa)").Select(f=>(long)f["id"]).ToList(),
            ["bookmark:later"]=folders.Where(f=>(string)f["title"]=="Позже (Lampa)").Select(f=>(long)f["id"]).ToList()};
        var remote=new Dictionary<string,Dictionary<string,(long kp,JObject card,List<long> folders)>>();
        // Finish every requested page before importing or issuing any command.
        foreach(var dest in destinations){var map=new Dictionary<string,(long,JObject,List<long>)>();
            var source=new List<(JObject item,long folder)>();
            if(dest.Key=="watching")source.AddRange((await Pages(io,token,"watching/serials?subscribed=1")).Select(x=>(x,0L)));
            else foreach(long folder in dest.Value)source.AddRange((await Pages(io,token,"bookmarks/"+folder)).Select(x=>(x,folder)));
            rawSnapshot[dest.Key]=new JArray(source.Select(x=>new JObject{["kp"]=x.item["id"],["folder"]=x.folder}));
            foreach(var entry in source){SnapshotItems++;var card=await io.Map(entry.item,token);if(card==null){MissingMappings++;continue;}string key=ProfileLibrary.Key(CardKind.Media(card),(long)card["id"]);
                if(!map.TryGetValue(key,out var hit))map[key]=((long)entry.item["id"],card,new List<long>{entry.folder});else hit.Item3.Add(entry.folder);
            }remote[dest.Key]=map;
        }
        // KinoPub's serial list may omit a subscribed series (e.g. nothing new to watch).
        // Check known missing titles individually, including previously removed titles,
        // before interpreting absence or confirming an unsubscribe.
        var verified=new JArray();
        foreach(var old in before.Values.Where(r=>(string)r[0]=="watching")){
            string key=(string)old[1];if(remote["watching"].ContainsKey(key))continue;
            var identity=key.Split(':');long tmdb=long.Parse(identity[2]);
            long kp=long.TryParse(old[6] as string,out long stored)?stored:await io.Lookup(identity[1],tmdb);
            if(kp<=0)continue;
            var full=await io.Read(token,"items/"+kp);
            if(full["item"]?["subscribed"]?.Type!=JTokenType.Boolean)throw new InvalidOperationException("cloud_subscription_unknown");
            bool on=(bool)full["item"]["subscribed"];
            verified.Add(new JObject{["kp"]=kp,["content_id"]=key,["subscribed"]=on});
            if(on)remote["watching"][key]=(kp,new JObject{["id"]=tmdb,["media_type"]="tv"},new List<long>{0});
        }
        rawSnapshot["watching_verified"]=verified;
        if(importOnly&&!dry&&MissingMappings>0)throw new InvalidOperationException("unmapped_cloud_items_"+MissingMappings);
        if(io.Account()!=account)throw new InvalidOperationException("cloud_account_changed");
        string audit=dry?null:Audit(new JObject{["phase"]="snapshot",["at"]=HomeDb.Now(),["account_generation"]=account,["selected_folders"]=new JArray(selected),["remote"]=rawSnapshot,["missing_mappings"]=MissingMappings});
        int imported=0,sent=0,removed=0;
        if(!dry){
            var p=Profiles.Get(Profiles.MainId);if(p==null||(bool)p["is_child"]||(bool)p["archived"])throw new InvalidOperationException("adult_import_profile_required");
            HomeDb.With(c=>{using var tx=c.BeginTransaction();
                foreach(var dest in remote){
                    string scopeKey="mirror_scope_v2:"+dest.Key;
                    string scope=account+"|"+(dest.Key=="bookmark:my"?string.Join(",",selected.OrderBy(x=>x)):dest.Key);
                    var prior=Profiles.Rows(c,"SELECT v FROM kv WHERE k=@k",("@k",scopeKey)).FirstOrDefault();
                    bool sameScope=prior!=null&&(string)prior[0]==scope;
                    if(!sameScope){
                        Profiles.Exec(c,"UPDATE kp_mirror_state SET observed=NULL,confirmed_revision=0,retry_at=0 WHERE account='shared' AND destination=@d",("@d",dest.Key));
                        Profiles.Exec(c,"DELETE FROM kv WHERE k LIKE @prefix",("@prefix","mirror_missing:"+dest.Key+"|%"));
                        Profiles.Exec(c,"INSERT INTO kv(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v",("@k",scopeKey),("@v",scope));
                    }
                    foreach(var item in dest.Value){
                        Profiles.Exec(c,"UPDATE kp_mirror_state SET remote_id=@kp WHERE account='shared' AND destination=@d AND content_id=@k",("@kp",item.Value.kp.ToString()),("@d",dest.Key),("@k",item.Key));
                        string probe="mirror_missing:"+dest.Key+"|"+item.Key;Profiles.Exec(c,"DELETE FROM kv WHERE k=@k",("@k",probe));
                        var old=before.GetValueOrDefault(dest.Key+"|"+item.Key);
                        bool known=Profiles.Rows(c,"SELECT 1 FROM memberships WHERE content_id=@k AND collection=@c LIMIT 1",("@k",item.Key),("@c",Collection(dest.Key))).Count>0;
                        // A pending local add/remove is ours, not a cloud-origin addition.
                        bool externalAdd=sameScope&&old!=null&&old[4] is not DBNull&&Convert.ToInt64(old[4])==0&&Convert.ToInt64(old[5])==0&&Convert.ToInt64(old[2])==Convert.ToInt64(old[3]);
                        bool unchanged=old==null?Profiles.Rows(c,"SELECT 1 FROM kp_mirror_state WHERE account='shared' AND destination=@d AND content_id=@k",("@d",dest.Key),("@k",item.Key)).Count==0:Current(dest.Key,item.Key,Convert.ToInt64(old[2]),Convert.ToInt64(old[5])!=0);
                        if(unchanged&&(!known||externalAdd)){
                            ProfileLibrary.CloudMembership(c,Profiles.MainId,item.Key,Collection(dest.Key),true,item.Value.card);imported++;
                        }
                    }
                    if(importOnly||!sameScope)continue;
                    foreach(var old in before.Values.Where(r=>(string)r[0]==dest.Key)){
                        string key=(string)old[1],probe="mirror_missing:"+dest.Key+"|"+key;long rev=Convert.ToInt64(old[2]);
                        bool confirmed=old[4] is not DBNull&&Convert.ToInt64(old[4])==1&&Convert.ToInt64(old[5])==1&&rev==Convert.ToInt64(old[3]);
                        if(dest.Value.ContainsKey(key)||!confirmed||MissingMappings>0||!Current(dest.Key,key,rev,true)){
                            Profiles.Exec(c,"DELETE FROM kv WHERE k=@k",("@k",probe));continue;
                        }
                        var absence=Profiles.Rows(c,"SELECT v FROM kv WHERE k=@k",("@k",probe)).FirstOrDefault();
                        if(absence==null||(string)absence[0]!=rev.ToString()){
                            Profiles.Exec(c,"INSERT INTO kv(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v",("@k",probe),("@v",rev.ToString()));continue;
                        }
                        foreach(var owner in Profiles.Rows(c,"SELECT id FROM user_profiles WHERE archived=0"))ProfileLibrary.CloudMembership(c,(string)owner[0],key,Collection(dest.Key),false);
                        // Finish even if all owners had already been inactive.
                        ProfileLibrary.Recompute(c,key,dest.Key);
                        Profiles.Exec(c,"UPDATE kp_mirror_state SET observed=0,confirmed_revision=revision,attempts=0,retry_at=0 WHERE account='shared' AND destination=@d AND content_id=@k",("@d",dest.Key),("@k",key));
                        Profiles.Exec(c,"DELETE FROM kv WHERE k=@k",("@k",probe));removed++;
                    }
                }
                tx.Commit();
            });
            HomeDb.Set("user_cloud_import_complete","1");
        }
        if(importOnly)return Last=$"cloud membership import complete; imported={imported}; no remote commands";
        var jobs=HomeDb.With(c=>Profiles.Rows(c,"SELECT destination,content_id,desired,revision,retry_at FROM kp_mirror_state WHERE account='shared' AND managed=1 ORDER BY retry_at LIMIT 300"));
        foreach(var row in jobs){string dest=(string)row[0],key=(string)row[1];bool want=Convert.ToInt64(row[2])!=0;long revision=Convert.ToInt64(row[3]);if(!remote.TryGetValue(dest,out var snapshot))continue;
            bool on=snapshot.TryGetValue(key,out var found);
            if(on==want){if(!dry)Confirm(dest,key,revision,want);continue;}
            // A previously confirmed presence disappeared: wait for external deletion
            // reconciliation, never race it by adding the title back ourselves.
            var previous=before.GetValueOrDefault(dest+"|"+key);
            if(want&&!on&&previous!=null&&previous[4] is not DBNull&&Convert.ToInt64(previous[4])==1&&Convert.ToInt64(previous[2])==revision&&Convert.ToInt64(previous[3])==revision)continue;
            if(dry||Convert.ToInt64(row[4] is DBNull?0:row[4])>HomeDb.Now())continue;
            var identity=key.Split(':');long tmdb=long.Parse(identity[2]);long kp=on?found.kp:await io.Lookup(identity[1],tmdb);
            if(kp<=0){Retry(dest,key,revision);continue;}
            if(!Current(dest,key,revision,want)||io.Account()!=account)continue;
            bool ok=false;
            try{
                if(dest=="watching"){
                    // toggle is used only after a fresh read; a lost response is reconciled next pass.
                    var full=await io.Read(token,"items/"+kp);if(full["item"]?["subscribed"]?.Type!=JTokenType.Boolean)throw new InvalidOperationException("cloud_subscription_unknown");bool actual=(bool)full["item"]["subscribed"];
                    if(actual==want)ok=true;
                    else if(Current(dest,key,revision,want)&&io.Account()==account){AuditCommand(audit,dest,key,kp,actual,want,Array.Empty<long>());var result=await io.Read(token,"watching/togglewatchlist?id="+kp);ok=(int?)result["status"]==200;}
                }else{
                    var target=on?found.folders:destinations[dest];
                    if(want&&target.Count==0){string title=dest=="bookmark:my"?"Мои фильмы (Lampa)":dest=="bookmark:favorite"?"Избранное (Lampa)":"Позже (Lampa)";
                        var(c,_)=await io.Form("bookmarks/create",new(){["title"]=title},token);
                        if(c!=200)throw new Exception("folder_create");var fresh=await Pages(io,token,"bookmarks");long fid=(long)(fresh.First(f=>(string)f["title"]==title)["id"]);target.Add(fid);destinations[dest]=target;
                        if(dest=="bookmark:my")io.Select(selected.Concat(new[]{fid.ToString()}));
                    }
                    ok=true;foreach(long folder in (want?target.Take(1):target)){
                        if(!Current(dest,key,revision,want)||io.Account()!=account){ok=false;break;}
                        AuditCommand(audit,dest,key,kp,on,want,new[]{folder});
                        var(c,_)=want?await io.Form("bookmarks/add",new(){["item"]=kp.ToString(),["folder"]=folder.ToString()},token):await io.Json("bookmarks/remove-item",new{item=kp,folder},token);ok&=c==200;
                    }
                }
            }catch{ok=false;}
            // Only the next complete read confirms remote state, never an HTTP 200 alone.
            if(ok&&Current(dest,key,revision,want))sent++;
            Retry(dest,key,revision);
        }
        return Last=$"local owners mirrored; imported={imported}; removed={removed}; commands={sent}; dry={dry}";
    }
    static bool Current(string dest,string key,long rev,bool want)=>HomeDb.With(c=>Profiles.Rows(c,"SELECT 1 FROM kp_mirror_state WHERE account='shared' AND destination=@d AND content_id=@k AND revision=@r AND desired=@v",("@d",dest),("@k",key),("@r",rev),("@v",want?1:0)).Count>0);
    static void Confirm(string dest,string key,long rev,bool on)=>HomeDb.With(c=>Profiles.Exec(c,"UPDATE kp_mirror_state SET observed=@v,confirmed_revision=@r,attempts=0,retry_at=0 WHERE account='shared' AND destination=@d AND content_id=@k AND revision=@r",("@d",dest),("@k",key),("@r",rev),("@v",on?1:0)));
    static void Retry(string dest,string key,long rev)=>HomeDb.With(c=>Profiles.Exec(c,"UPDATE kp_mirror_state SET attempts=attempts+1,retry_at=@at WHERE account='shared' AND destination=@d AND content_id=@k AND revision=@r",("@d",dest),("@k",key),("@r",rev),("@at",HomeDb.Now()+240_000)));
}
