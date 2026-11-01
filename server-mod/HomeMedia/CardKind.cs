using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace HomeMedia;

public static class CardKind
{
    public static string Media(JObject c)=>(string)c?["media_type"]??(c?["name"]!=null||c?["first_air_date"]!=null?"tv":"movie");
    public static string Kind(JObject c)
    {
        if(c==null)return null;
        string media=Media(c);long id=long.TryParse(c["id"]?.ToString(),out long parsed)?parsed:0;
        if(id>0){var r=HomeDb.With(db=>Profiles.Rows(db,"SELECT override_kind FROM content_items WHERE id=@k",("@k",ProfileLibrary.Key(media,id)))).FirstOrDefault();if(r?[0] is string manual&&!string.IsNullOrEmpty(manual))return manual;}
        bool anim=(c["genre_ids"] as JArray)?.Any(g=>(int?)g==16)==true||(c["genres"] as JArray)?.Any(g=>(int?)g?["id"]==16)==true;
        bool anime=anim&&((string)c["original_language"]=="ja"||(c["origin_country"] as JArray)?.Any(x=>(string)x=="JP")==true);
        return anime?(media=="tv"?"anime_series":"anime_movie"):anim?(media=="tv"?"cartoon_series":"cartoon_movie"):(media=="tv"?"series":"movie");
    }
    public static bool IsAnime(JObject c)=>Kind(c)?.StartsWith("anime_",StringComparison.Ordinal)==true;
    public static JObject Annotate(JObject c)
    {
        if(c==null)return null;var clone=(JObject)c.DeepClone();clone["home_kind"]=Kind(clone);return clone;
    }
    public static object Payload(object value)
    {
        if(value==null||value is string)return value;
        var j=JToken.FromObject(value);
        if(j is JObject o&&o["results"] is JArray rows)
            o["results"]=new JArray(rows.Select(x=>x is JObject card?Annotate(card):(JToken)x.DeepClone()));
        return j;
    }
}
