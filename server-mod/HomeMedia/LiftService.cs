using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace HomeMedia;

public sealed class LiftService
{
    readonly LiftApiClient client;
    readonly string mediaUserAgent;
    public LiftService(LiftApiClient client,string mediaUserAgent){this.client=client;this.mediaUserAgent=mediaUserAgent;}
    public static async Task Require(LiftTarget target)
    {
        if(Profiles.Current==null)throw new UnauthorizedAccessException("session_required");
        if(HomeDb.Get("module_lift")!="1" || !ProfilePolicy.SourceEnabled("lift"))throw new InvalidOperationException("source_disabled");
        var card=await KpMap.TmdbCard(target.Media,target.Tmdb);
        if(card==null || !await ProfilePolicy.Allows(card))throw new UnauthorizedAccessException("parental_blocked");
    }
    public static int DeviceHeight()=>HomeDb.With(c=> {
        var rows=HomeDb.Query(c,"SELECT cfg FROM devices WHERE id=@d",("@d",Profiles.Current.Device));
        var cfg=rows.Count==0?new JObject():JObject.Parse((string)rows[0][0]??"{}");
        // General VOD quality belongs to this device. playback.max_height is a torrent filter.
        return int.TryParse(cfg["maxh"]?.ToString(),out int legacy) && legacy is 0 or 720 or 1080 or 2160?legacy:1080;
    });
    public async Task<JObject> Resolve(LiftTarget target,string voiceName,string expectedRevision,CancellationToken ct)
    {
        await Require(target);ct.ThrowIfCancellationRequested();
        var binding=LiftBindings.Get(target)??throw new InvalidOperationException("lift_binding_required");
        if(!string.IsNullOrEmpty(expectedRevision)&&expectedRevision!=(string)binding["revision"])
            throw new InvalidOperationException("lift_binding_changed");
        var playlist=await client.Playlist((long)binding["item"],ct);
        var entry=LiftPlaylist.Select(playlist,(bool)binding["movie"],(int)binding["provider_season"],(int)binding["provider_episode"]);
        var uri=LiftVp9Policy.Manifest(entry);client.CheckMedia(uri.AbsoluteUri);
        var xml=await client.Mpd(uri.AbsoluteUri,ct);
        var d=LiftVp9Policy.Describe(entry,xml,DeviceHeight());
        if((d["voices"] as JArray)?.Count==0)throw new InvalidDataException("lift_audio_mapping_unverified");
        if(voiceName!=null) {
            var hits=((JArray)d["voices"]).Children<JObject>().Where(v=>(string)v["name"]==voiceName).ToArray();
            if(hits.Length!=1)throw new InvalidDataException("lift_voice_unavailable_or_ambiguous");
            d["selected_voice"]=hits[0].DeepClone();
        }
        d["identity"]=target.Identity;d["card"]=target.Card;d["hash"]=LiftBindings.ProgressHash(target);
        d["profile_id"]=Profiles.CurrentId;d["item"]=binding["item"].DeepClone();
        d["edition"]=binding["edition"].DeepClone();d["binding_revision"]=binding["revision"].DeepClone();
        d["season"]=target.Season;d["episode"]=target.Episode;d["title"]=entry["title"]??playlist["title"];
        d["headers"]=new JObject{["User-Agent"]=mediaUserAgent};d["now"]=HomeDb.Now();
        // Resolving options is not a playback URL lease. Resolve again after the voice choice.
        if(voiceName==null)d.Remove("video");
        return d;
    }
    public static JObject PositionContext(LiftTarget target,string revision)
    {
        var b=LiftBindings.Get(target);
        if(b==null || (string)b["revision"]!=revision)throw new InvalidOperationException("lift_binding_changed");
        var old=LastPlayStore.GetForRead(target.Card);
        bool same=old!=null && (string)old["id"]==target.Identity && (string)old["src"]=="lift"
            && (string)old["edition"]==(string)b["edition"] && (string)old["item"]==b["item"].ToString();
        return new JObject{["compatible"]=same,["edition"]=b["edition"].DeepClone()};
    }
}
