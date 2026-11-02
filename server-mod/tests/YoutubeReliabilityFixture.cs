// Loaded ONLY by the isolated compatibility runner, never by the production manifest.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json.Linq;
using Shared;
namespace HomeMedia;
public class YoutubeReliabilityFixtureController : BaseController
{
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static DefaultHttpContext Context(string range = null) {
        var c = new DefaultHttpContext(); c.Response.Body = new MemoryStream();
        if(range != null)c.Request.Headers["Range"] = range; return c;
    }
    static readonly byte[] Data = Enumerable.Range(0,37).Select(x=>(byte)x).ToArray();
    static HttpResponseMessage Block(long from, long to, bool shortBody = false) {
        to = Math.Min(to, Data.Length - 1);
        if(from >= Data.Length) { var fail=new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable){Content=new ByteArrayContent(Array.Empty<byte>())};fail.Content.Headers.ContentRange=new ContentRangeHeaderValue(Data.Length);return fail; }
        var r = new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(Data.Skip((int)from).Take((int)(to-from+1)-(shortBody?2:0)).ToArray())};
        r.Content.Headers.ContentRange = new ContentRangeHeaderValue(from,to,Data.Length);return r;
    }
    static byte[] Body(DefaultHttpContext c)=>( (MemoryStream)c.Response.Body).ToArray();
    [HttpGet,AllowAnonymous,Route("yt/reliability-fixture")]
    public async Task<ActionResult> Run() {
        if(Environment.GetEnvironmentVariable("HOME_PROFILE_TEST")!="1")return NotFound();
        var passed=new List<string>();
        async Task Test(string name,Func<Task> test){await test();passed.Add(name);}
        var uri=new Uri("https://unit.googlevideo.com/videoplayback");
        await Test("multi-block relay",async()=>{var c=Context("bytes=3-29");await new YoutubeRelay((u,a,b,t)=>Task.FromResult(Block(a,b)),chunk:8).Send(c,uri);Check(c.Response.StatusCode==206 && Body(c).SequenceEqual(Data.Skip(3).Take(27)),"body");Check(c.Response.Headers["Content-Range"]=="bytes 3-29/37","headers");});
        await Test("full response",async()=>{var c=Context();await new YoutubeRelay((u,a,b,t)=>Task.FromResult(Block(a,b)),chunk:8).Send(c,uri);Check(c.Response.StatusCode==200 && c.Response.ContentLength==37 && Body(c).SequenceEqual(Data),"body");});
        await Test("suffix range",async()=>{var c=Context("bytes=-7");await new YoutubeRelay((u,a,b,t)=>Task.FromResult(Block(a,b)),chunk:8).Send(c,uri);Check(c.Response.StatusCode==206 && Body(c).SequenceEqual(Data.Skip(30)),"suffix");});
        await Test("open range",async()=>{var c=Context("bytes=31-");await new YoutubeRelay((u,a,b,t)=>Task.FromResult(Block(a,b)),chunk:8).Send(c,uri);Check(Body(c).SequenceEqual(Data.Skip(31)),"open");});
        foreach(var bad in new[]{"bytes=100-50","bytes=999999999999999999999999999999999-","bytes=-0","bytes=-","bytes=0-1,4-5"})await Test("reject "+bad,async()=>{var c=Context(bad);await new YoutubeRelay((u,a,b,t)=>throw new Exception("must not fetch")).Send(c,uri);Check(c.Response.StatusCode==416 && Body(c).Length==0,"416");});
        await Test("unsatisfiable",async()=>{var c=Context("bytes=99-100");await new YoutubeRelay((u,a,b,t)=>Task.FromResult(Block(a,b))).Send(c,uri);Check(c.Response.StatusCode==416 && c.Response.Headers["Content-Range"]=="bytes */37","416 upstream");});
        await Test("renew second block after 403",async()=>{int renews=0;var c=Context();await new YoutubeRelay((u,a,b,t)=>Task.FromResult(a>=8 && u==uri?new HttpResponseMessage(HttpStatusCode.Forbidden):Block(a,b)),t=>{renews++;return Task.FromResult(new Uri("https://fresh.googlevideo.com/videoplayback"));},8).Send(c,uri);Check(renews==1 && Body(c).SequenceEqual(Data),"renew");});
        await Test("503 body never copied",async()=>{int failures=0;var c=Context();await new YoutubeRelay((u,a,b,t)=>Task.FromResult(a==8 && failures++==0?new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("ERROR HTML")}:Block(a,b)),chunk:8).Send(c,uri);Check(Body(c).SequenceEqual(Data),"503 corruption");});
        await Test("truncated block resumes exact offset",async()=>{bool once=true;var c=Context();await new YoutubeRelay((u,a,b,t)=>{bool cut=once;once=false;return Task.FromResult(Block(a,b,cut));},chunk:8).Send(c,uri);Check(Body(c).SequenceEqual(Data),"truncation");});
        await Test("permanent second-block failure is bounded",async()=>{int n=0;var c=Context();await new YoutubeRelay((u,a,b,t)=>{n++;return Task.FromResult(a==0?Block(a,b):new HttpResponseMessage(HttpStatusCode.BadGateway){Content=new StringContent("ERROR")});},chunk:8).Send(c,uri);Check(n==4 && Body(c).SequenceEqual(Data.Take(8)),"bounded");});
        await Test("reject invalid upstream range",async()=>{int n=0;var c=Context();await new YoutubeRelay((u,a,b,t)=>{n++;return Task.FromResult(Block(a+1,b));},chunk:8).Send(c,uri);Check(n==3 && c.Response.StatusCode==502 && Body(c).Length==0,"invalid range");});
        await Test("cancelled relay",async()=>{var c=Context();using var stop=new CancellationTokenSource();stop.Cancel();c.RequestAborted=stop.Token;await new YoutubeRelay((u,a,b,t)=>throw new Exception("cancelled fetch")).Send(c,uri);Check(Body(c).Length==0,"cancel");});
        await Test("cache expiry and route",()=>{long now=1000;var d=new JObject{["proxy_stamp"]="one",["formats"]=new JArray(new JObject{["url"]="https://test?expire=1200"})};Check(YoutubeLinks.Fresh(d,"one",now),"fresh");Check(!YoutubeLinks.Fresh(d,"two",now),"route");Check(!YoutubeLinks.Fresh(d,"one",1100),"expiry margin");return Task.CompletedTask;});
        await Test("duplicate callers and individual cancellation",async()=>{var pool=new YoutubeFlights<int>();int calls=0;var ready=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);using var stop=new CancellationTokenSource();var a=pool.Get("a",t=>{calls++;return ready.Task;},stop.Token);var b=pool.Get("a",t=>throw new Exception("duplicate"),default);stop.Cancel();try{await a;throw new Exception("not cancelled");}catch(OperationCanceledException){}ready.SetResult(7);Check(await b==7 && calls==1,"shared");});
        await Test("last caller stops extraction",async()=>{var pool=new YoutubeFlights<int>();using var stop=new CancellationTokenSource();CancellationToken work=default;var a=pool.Get("a",async t=>{work=t;await Task.Delay(10000,t);return 1;},stop.Token);stop.Cancel();try{await a;}catch(OperationCanceledException){}Check(work.IsCancellationRequested,"orphan");});
        await Test("safe failure classes",()=>{Check(YoutubeFailure.Classify("Sign in to confirm you're not a bot")=="verification_required","bot");Check(YoutubeFailure.Classify("HTTP 429")=="rate_limited","429");Check(YoutubeFailure.Classify("Private video")=="unavailable","private");Check(YoutubeFailure.Classify("Connection timed out")=="network_error","network");return Task.CompletedTask;});
        return Json(new{ok=true,passed});
    }
}
