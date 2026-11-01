using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HomeMedia;

public static class ProfileLibrary
{
    public static string Key(string media,long id)=>"tmdb:"+media+":"+id;
    public static string Destination(string media,string collection)=>collection=="my"?(media=="tv"?"watching":"bookmark:my"):collection=="favorite"?"bookmark:favorite":collection=="later"?"bookmark:later":null;
    public static JArray My()=>HomeDb.With(c=>new JArray(Profiles.Rows(c,"SELECT content_id,at FROM memberships WHERE profile_id=@p AND collection='my' AND active=1",("@p",Profiles.CurrentId)).Select(r=> {
        var a=((string)r[0]).Split(':');return a.Length==3&&long.TryParse(a[2],out long id)?new JObject { ["media"]=a[1],["tmdb"]=id,["at"]=Convert.ToInt64(r[1]) }:null;
    }).Where(r=>r!=null)));
    public static bool Has(string media,long id,string collection="my")=>HomeDb.With(c=>Profiles.Rows(c,"SELECT 1 FROM memberships WHERE profile_id=@p AND content_id=@k AND collection=@c AND active=1",("@p",Profiles.CurrentId),("@k",Key(media,id)),("@c",collection)).Count>0);
    public static bool Known(string media,long id)=>HomeDb.With(c=>Profiles.Rows(c,"SELECT 1 FROM memberships WHERE content_id=@k LIMIT 1",("@k",Key(media,id))).Count>0);
    public static bool Set(string media,long id,bool on,string collection="my",string origin="local",string eventId=null)
    {
        return HomeDb.With(c=>{using var tx=c.BeginTransaction();bool changed=SetInTransaction(c,Profiles.CurrentId,media,id,on,collection,origin,eventId);tx.Commit();return changed;});
    }
    internal static bool SetInTransaction(SqliteConnection c,string owner,string media,long id,bool on,string collection="my",string origin="local",string eventId=null)
    {
        if((media!="tv"&&media!="movie")||id<=0)throw new ArgumentException("bad_content");
        string k=Key(media,id),dest=Destination(media,collection);
        var old=Profiles.Rows(c,"SELECT active FROM memberships WHERE profile_id=@p AND content_id=@k AND collection=@c",("@p",owner),("@k",k),("@c",collection)).FirstOrDefault();
        if(old!=null&&(Convert.ToInt64(old[0])!=0)==on)return false;
        Profiles.Exec(c,"INSERT OR IGNORE INTO content_items(id,kind) VALUES(@k,@kind)",("@k",k),("@kind",media=="tv"?"series":"movie"));
        Profiles.Exec(c,@"INSERT INTO memberships(profile_id,content_id,collection,active,origin,at,event,revision) VALUES(@p,@k,@c,@on,@origin,@at,@event,1)
ON CONFLICT(profile_id,content_id,collection) DO UPDATE SET active=@on,origin=@origin,at=@at,event=@event,revision=memberships.revision+1",("@p",owner),("@k",k),("@c",collection),("@on",on?1:0),("@origin",origin),("@at",HomeDb.Now()),("@event",eventId??Guid.NewGuid().ToString("N")));
        if(dest!=null)Recompute(c,k,dest,true);return true;
    }
    public static void Recompute(SqliteConnection c,string key,string destination,bool action=false)
    {
        string collection=destination=="watching"||destination=="bookmark:my"?"my":destination=="bookmark:favorite"?"favorite":"later";
        bool on=Profiles.Rows(c,"SELECT 1 FROM memberships m JOIN user_profiles p ON p.id=m.profile_id WHERE m.content_id=@k AND m.collection=@c AND m.active=1 AND p.archived=0",("@k",key),("@c",collection)).Count>0;
        Profiles.Exec(c,@"INSERT INTO kp_mirror_state(account,destination,content_id,desired,revision,managed,retry_at) VALUES('shared',@d,@k,@on,1,1,0)
ON CONFLICT(account,destination,content_id) DO UPDATE SET desired=@on,revision=kp_mirror_state.revision+CASE WHEN kp_mirror_state.desired<>@on OR @action=1 THEN 1 ELSE 0 END,managed=1,retry_at=CASE WHEN kp_mirror_state.desired<>@on OR @action=1 THEN 0 ELSE kp_mirror_state.retry_at END",("@d",destination),("@k",key),("@on",on?1:0),("@action",action?1:0));
    }
    // Remote list changes update memberships and bookmark deltas atomically. Never touch progress/history.
    internal static void CloudMembership(SqliteConnection c,string owner,string key,string collection,bool on,JObject card=null)
    {
        var parts=key.Split(':');string media=parts[1];long id=long.Parse(parts[2]);
        SetInTransaction(c,owner,media,id,on,collection,"cloud");
        long now=HomeDb.Now();
        if(collection=="my"){
            string hidden="w_"+id+"_"+media;
            if(on)Profiles.Exec(c,"DELETE FROM hidden WHERE profile_id=@p AND k=@k",("@p",owner),("@k",hidden));
            else {
                var latest=Profiles.Rows(c,"SELECT COALESCE(MAX(u),0) FROM timeline WHERE profile_id=@p AND c=@card",("@p",owner),("@card",id+"_"+media));
                long barrier=Math.Max(now,Convert.ToInt64(latest[0][0])+1);
                Profiles.Exec(c,"INSERT INTO hidden(profile_id,k,at) VALUES(@p,@k,@u) ON CONFLICT(profile_id,k) DO UPDATE SET at=MAX(hidden.at,excluded.at)",("@p",owner),("@k",hidden),("@u",barrier));
            }
        }else if(collection=="favorite"||collection=="later"){
            string cat=collection=="favorite"?"book":"wath",k=media+":"+id;
            var old=Profiles.Rows(c,"SELECT u,card FROM bookmarks WHERE profile_id=@p AND cat=@cat AND k=@k",("@p",owner),("@cat",cat),("@k",k)).FirstOrDefault();
            long u=Math.Max(now,old==null?0:Convert.ToInt64(old[0])+1);
            long s=Math.Max(now,Convert.ToInt64(Profiles.Rows(c,"SELECT COALESCE(MAX(s),0) FROM bookmarks WHERE profile_id=@p",("@p",owner))[0][0])+1);
            string json=card?.ToString(Newtonsoft.Json.Formatting.None)??old?[1] as string??"{}";
            Profiles.Exec(c,"INSERT INTO bookmarks(profile_id,cat,k,card,u,s,deleted) VALUES(@p,@cat,@k,@j,@u,@s,@deleted) ON CONFLICT(profile_id,cat,k) DO UPDATE SET card=@j,u=@u,s=@s,deleted=@deleted",("@p",owner),("@cat",cat),("@k",k),("@j",json),("@u",u),("@s",s),("@deleted",on?0:1));
        }
    }
    // Legacy rows are attributed once to their previous shared owner. Tombstones survive every restart.
    public static void ImportLegacy(SqliteConnection c)
    {
        if(Profiles.Rows(c,"SELECT 1 FROM kv WHERE k='user_members_imported'").Count>0)return;
        foreach(var r in Profiles.Rows(c,"SELECT profile_id,media,tmdb,at FROM watchlist"))
        {
            string k=Key((string)r[1],Convert.ToInt64(r[2]));
            Profiles.Exec(c,"INSERT OR IGNORE INTO content_items(id,kind) VALUES(@k,@kind)",("@k",k),("@kind",(string)r[1]=="tv"?"series":"movie"));
            Profiles.Exec(c,"INSERT OR IGNORE INTO memberships(profile_id,content_id,collection,active,origin,at,event,revision) VALUES(@p,@k,'my',1,'legacy',@at,'legacy',1)",("@p",(string)r[0]),("@k",k),("@at",Convert.ToInt64(r[3])*1000));
        }
        foreach(var r in Profiles.Rows(c,"SELECT profile_id,cat,card,deleted,u FROM bookmarks"))
        {
            try {
                var card=JObject.Parse((string)r[2]);long id=(long?)card["id"]??0;if(id<=0)continue;
                string media=(string)card["media_type"]??(card["name"]!=null?"tv":"movie");if(media!="tv"&&media!="movie")continue;
                string category=(string)r[1],collection=category=="book"?"favorite":category=="wath"?"later":"lampa:"+category,k=Key(media,id);
                Profiles.Exec(c,"INSERT OR IGNORE INTO content_items(id,kind,j) VALUES(@k,@kind,@j)",("@k",k),("@kind",media=="tv"?"series":"movie"),("@j",(string)r[2]));
                Profiles.Exec(c,"INSERT OR IGNORE INTO memberships(profile_id,content_id,collection,active,origin,at,event,revision) VALUES(@p,@k,@c,@on,'legacy',@at,'legacy',1)",("@p",(string)r[0]),("@k",k),("@c",collection),("@on",Convert.ToInt64(r[3])==0?1:0),("@at",Convert.ToInt64(r[4])));
            }catch(Newtonsoft.Json.JsonException) { }
        }
        Profiles.Exec(c,"INSERT INTO kv(k,v) VALUES('user_members_imported','1')");
    }
}
