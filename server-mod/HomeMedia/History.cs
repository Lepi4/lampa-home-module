using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

public static class HistoryStore
{
    public static void Ensure(SqliteConnection c)
    {
        Profiles.Exec(c,@"CREATE TABLE IF NOT EXISTS play_history(
profile_id TEXT NOT NULL,identity TEXT NOT NULL,card TEXT NOT NULL,provider TEXT NOT NULL,
title TEXT NOT NULL DEFAULT '',at INTEGER NOT NULL,hidden_at INTEGER NOT NULL DEFAULT 0,
rev INTEGER NOT NULL DEFAULT 0,hidden_rev INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(profile_id,identity));
CREATE INDEX IF NOT EXISTS ix_play_history_at ON play_history(profile_id,at DESC);");
        // Compatibility with candidate databases created before the revision field was added.
        if(!Profiles.Rows(c,"PRAGMA table_info(play_history)").Any(r=>(string)r[1]=="rev"))
        {
            Profiles.Exec(c,"ALTER TABLE play_history ADD COLUMN rev INTEGER NOT NULL DEFAULT 0");
            Profiles.Exec(c,"ALTER TABLE play_history ADD COLUMN hidden_rev INTEGER NOT NULL DEFAULT 0");
            Profiles.Exec(c,@"UPDATE play_history SET rev=COALESCE((SELECT s FROM timeline WHERE timeline.profile_id=play_history.profile_id AND timeline.id=play_history.identity),1)");
            Profiles.Exec(c,"UPDATE play_history SET hidden_rev=CASE WHEN hidden_at>0 THEN rev ELSE 0 END");
        }
        if(Profiles.Rows(c,"SELECT 1 FROM kv WHERE k='play_history_seeded'").Count!=0)return;
        // One-time preservation of the old history. Future resets do not erase it.
        Profiles.Exec(c,@"INSERT OR IGNORE INTO play_history(profile_id,identity,card,provider,at,rev)
SELECT profile_id,id,c,'legacy',u,s FROM timeline WHERE id IS NOT NULL AND c IS NOT NULL
AND c<>'' AND t>=10 AND COALESCE(op,'')<>'reset' AND COALESCE(event,'') NOT LIKE '9:%' AND COALESCE(event,'') NOT LIKE 'manual:%'");
        Profiles.Exec(c,"INSERT INTO kv(k,v) VALUES('play_history_seeded','1')");
    }
    public static void Record(SqliteConnection c,TlV2.Row n)
    {
        // A manual checkmark is not a playback. Resume metadata identifies an actual player report.
        if(n.Id==null||string.IsNullOrEmpty(n.C)||n.Resume==null||n.T<10||n.Op=="reset"||((n.Event??"").StartsWith("9:")||(n.Event??"").StartsWith("manual:")))return;
        string source=n.C.StartsWith("yt_")?"youtube":(string)n.Resume["src"]??"local";
        HomeDb.Exec(c,@"INSERT INTO play_history(profile_id,identity,card,provider,title,at,rev)
VALUES(@profile,@id,@card,@src,@title,@at,@rev) ON CONFLICT(profile_id,identity) DO UPDATE SET
provider=CASE WHEN excluded.at>=play_history.at THEN excluded.provider ELSE play_history.provider END,
title=CASE WHEN excluded.at>=play_history.at AND excluded.title<>'' THEN excluded.title ELSE play_history.title END,
at=MAX(play_history.at,excluded.at),rev=MAX(play_history.rev,excluded.rev)",("@id",n.Id),("@card",n.C),("@src",source),("@title",((string)n.Resume["title"]??"").Substring(0,Math.Min(300,((string)n.Resume["title"]??"").Length))),("@at",n.U),("@rev",n.S));
    }
    public static async Task<(JArray results,int pages)> List(int page,int limit=24)
    {
        bool youtube=ProfilePolicy.SourceEnabled("youtube");
        var grouped=HomeDb.With(c=>HomeDb.Query(c,@"SELECT identity,card,provider,title,at FROM play_history
WHERE profile_id=@profile AND rev>hidden_rev ORDER BY at DESC,identity DESC"))
            .Where(r=>youtube||!((string)r[1]).StartsWith("yt_"))
            .GroupBy(r=>(string)r[1]).Select(g=>g.First()).ToList();
        var cards=new JArray();
        int skip=(Math.Max(1,page)-1)*limit, kept=0;
        bool more=false;
        foreach(var row in grouped)
        {
            string identity=(string)row[0],card=(string)row[1];JObject value=null;
            if(card.StartsWith("yt_")&&YtStore.ValidId(card.Substring(3)))
            {
                string id=card.Substring(3);var meta=YtStore.Cached("v3:"+id,365*86400) as JObject;
                value=new JObject{["id"]=id,["video_id"]=id,["provider"]="youtube",["title"]=meta?["title"]??(string)row[3],
                    ["channel_id"]=meta?["channel_id"],["age_limit"]=meta?["age_limit"],["thumb"]="https://i.ytimg.com/vi/"+id+"/hqdefault.jpg"};
            }
            else
            {
                var match=Regex.Match(card,@"^(\d+)_(tv|movie)$");
                if(match.Success)value=await KpMap.TmdbCard(match.Groups[2].Value,long.Parse(match.Groups[1].Value));
            }
            if(value==null||!await ProfilePolicy.Allows(value))continue;
            kept++;
            if(kept<=skip)continue;
            if(cards.Count>=limit){more=true;break;}
            value=(JObject)value.DeepClone();value["history_card"]=card;value["history_identity"]=identity;value["watch_at"]=Convert.ToInt64(row[4]);
            var episode=Regex.Match(identity??"",@"^tv-\d+-s(\d+)e(\d+)$");
            if(episode.Success)value["history_episode"]=$"Сезон {episode.Groups[1].Value} · серия {episode.Groups[2].Value}";
            cards.Add(value);
        }
        int pages=more?page+1:Math.Max(1,(int)Math.Ceiling(kept/(double)limit));
        return(cards,pages);
    }
    public static void Hide(string card)=>HomeDb.With(c=>HomeDb.Exec(c,
        "UPDATE play_history SET hidden_at=@at,hidden_rev=@rev WHERE profile_id=@profile AND card=@card",("@at",HomeDb.Now()),("@rev",TlV2.MaxS(c)),("@card",card)));
    public static void Clear()=>HomeDb.With(c=>HomeDb.Exec(c,
        "UPDATE play_history SET hidden_at=@at,hidden_rev=@rev WHERE profile_id=@profile",("@at",HomeDb.Now()),("@rev",TlV2.MaxS(c))));
}

[ProfileResult]
public class HistoryController:BaseController
{
    static ActionResult Json(object value,int code=200)=>new ContentResult{Content=JsonConvert.SerializeObject(value),ContentType="application/json; charset=utf-8",StatusCode=code};
    [HttpGet,AllowAnonymous,Route("home/history")]
    public async Task<ActionResult> List(int page=1)
    {
        if(page<1||page>10000)return Json(new{error="bad_page"},400);
        var result=await HistoryStore.List(page);
        return Json(new{results=result.results,page,total_pages=result.pages});
    }
    [HttpPost,AllowAnonymous,Route("home/history/hide")]
    public async Task<ActionResult> Hide()
    {
        if(!Guard.Ok(Request))return Json(new{error="forbidden"},403);
        using var reader=new System.IO.StreamReader(Request.Body);string body=await reader.ReadToEndAsync();
        if(body.Length>1000)return Json(new{error="too_big"},413);
        string card;try{card=(string)JObject.Parse(body)["card"];}catch{return Json(new{error="bad_json"},400);}
        if(!Regex.IsMatch(card??"",@"^(\d+_(movie|tv)|yt_[A-Za-z0-9_-]{11})$"))return Json(new{error="bad_card"},400);
        HistoryStore.Hide(card);return Json(new{ok=true});
    }
    [HttpPost,AllowAnonymous,Route("home/history/clear")]
    public ActionResult Clear(){if(!Guard.Ok(Request))return Json(new{error="forbidden"},403);HistoryStore.Clear();return Json(new{ok=true});}
}
