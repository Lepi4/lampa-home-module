using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace HomeMedia;

// Authentication, mapping and policy checks belong to the caller.
public static class LiftVp9Policy
{
    public static Uri Manifest(JObject item)
    {
        string raw=item?["streams"]?["dash_vp9"]?.Type==JTokenType.String
            ?(string)item["streams"]["dash_vp9"]:null;
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri) || uri.Scheme!="https"
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("lift_vp9_unavailable");
        return uri; // No fallback to dash_av1 or hls.
    }

    static int Number(XElement node,string name,int fallback=0)
        =>int.TryParse((string)node.Attribute(name),out var n)?n:fallback;
    static string Attr(XElement rep,XElement adaptation,string name)
        =>(string)rep.Attribute(name)??(string)adaptation.Attribute(name);

    public static JObject Describe(JObject item,string xml,int maxHeight)
    {
        var uri=Manifest(item);
        if(maxHeight<0 || maxHeight>4320)throw new ArgumentOutOfRangeException(nameof(maxHeight));
        var settings=new XmlReaderSettings {
            DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,
            MaxCharactersInDocument=2*1024*1024
        };
        using var text=new StringReader(xml);
        using var reader=XmlReader.Create(text,settings);
        var doc=XDocument.Load(reader);
        if(doc.Root?.Name.LocalName!="MPD")throw new InvalidDataException("lift_not_mpd");
        var videos=new JArray();
        var audioIds=doc.Descendants().Where(x=>x.Name.LocalName=="AdaptationSet")
            .Where(x=>(string)x.Attribute("contentType")=="audio"
                || ((string)x.Attribute("mimeType")??"").StartsWith("audio/",StringComparison.Ordinal))
            .SelectMany(a=>a.Elements().Where(r=>r.Name.LocalName=="Representation"))
            .Select(r=>(string)r.Attribute("id")).Where(x=>x!=null).ToArray();
        foreach(var a in doc.Descendants().Where(x=>x.Name.LocalName=="AdaptationSet"))
        foreach(var r in a.Elements().Where(x=>x.Name.LocalName=="Representation")) {
            string codec=Attr(r,a,"codecs")??"";
            if(!codec.StartsWith("vp09",StringComparison.OrdinalIgnoreCase)
                && !codec.StartsWith("vp9",StringComparison.OrdinalIgnoreCase))continue;
            if(!int.TryParse(Attr(r,a,"width"),out int width)
                || !int.TryParse(Attr(r,a,"height"),out int height)
                || width<=0 || height<=0)continue;
            // Same landscape quality-class rule as the current HomePlayerActivity.
            if(maxHeight>0 && width>(long)maxHeight*16/9+8)continue;
            videos.Add(new JObject {
                ["id"]=(string)r.Attribute("id"),["width"]=width,["height"]=height,
                ["codecs"]=codec,["frame_rate"]=Attr(r,a,"frameRate"),
                ["bandwidth"]=long.TryParse(Attr(r,a,"bandwidth"),out long b)?b:0
            });
        }
        if(videos.Count==0)throw new InvalidDataException("lift_no_vp9_within_device_limit");
        var voices=new JArray();
        var audio=item["audio"] as JObject;
        if(audio!=null) {
            var names=audio["names"] as JArray;
            var order=audio["order"] as JArray;
            var visible=audio["visible"] as JArray;
            if(names==null || order==null || names.Count!=order.Count)
                throw new InvalidDataException("lift_invalid_audio_metadata");
            for(int i=0;i<names.Count;i++) {
                if(names[i].Type!=JTokenType.String || order[i].Type!=JTokenType.Integer)
                    throw new InvalidDataException("lift_invalid_audio_metadata");
                if(visible!=null && i<visible.Count && visible[i].Type==JTokenType.Boolean && !(bool)visible[i])continue;
                string id=order[i].ToString();
                if(audioIds.Count(x=>x==id)!=1)
                    throw new InvalidDataException("lift_audio_mapping_unverified");
                if(voices.Children<JObject>().Any(x=>(string)x["representation_id"]==id))
                    throw new InvalidDataException("lift_duplicate_audio_order");
                voices.Add(new JObject {
                    ["name"]=(string)names[i],["representation_id"]=id
                });
            }
        }
        return new JObject {
            ["source"]="lift",["protocol"]="dash",["codec"]="vp9",
            ["mime"]="application/dash+xml",["video"]=uri.AbsoluteUri,
            ["representations"]=videos,["voices"]=voices
        };
    }
}
