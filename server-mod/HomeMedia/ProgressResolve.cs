using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

public class ProgressResolveController : BaseController
{
    static ActionResult J(object data,int code=200) => new ContentResult {
        Content=JsonConvert.SerializeObject(data),ContentType="application/json; charset=utf-8",StatusCode=code
    };

    [HttpPost,AllowAnonymous,Route("home/tl/resolve")]
    public async Task<ActionResult> Resolve()
    {
        if(Profiles.Enabled && Profiles.Current==null) return J(new {error="session_required"},401);
        using var reader=new StreamReader(Request.Body);
        char[] buffer=new char[2049]; int count=0;
        while(count<buffer.Length) {int got=await reader.ReadAsync(buffer,count,buffer.Length-count);if(got==0)break;count+=got;}
        if(count>2048)return J(new {error="payload_too_large"},413);
        JObject b;
        try { b=JObject.Parse(new string(buffer,0,count)); } catch(JsonException){return J(new {error="bad_json"},400);}
        string id=b["id"]?.Type==JTokenType.String?(string)b["id"]:null;
        string hash=b["h"]?.Type==JTokenType.String?(string)b["h"]:null;
        string card=b["c"]?.Type==JTokenType.String?(string)b["c"]:null;
        if(!Regex.IsMatch(hash??"",@"^-?[0-9]{1,15}$") || hash=="0" || !Regex.IsMatch(card??"",@"^[0-9]{1,12}_(tv|movie)$")) return J(new {error="bad_target"},400);
        if(TlV2.CardFromIdentity(id)!=card && id!="tl-"+hash) return J(new {error="identity_card_mismatch"},400);
        return HomeDb.With(c => {
            var row=TlV2.Resolve(c,id,hash,card);
            return J(new {profile_id=Profiles.CurrentId,now=HomeDb.Now(),epoch=TlV2.Epoch,row=row==null?null:TlV2.ToWire(row)});
        });
    }
}
