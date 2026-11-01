using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace HomeMedia;
public static class AppUpdates
{
    public static readonly string DirectoryPath=Path.Combine(Directory.GetCurrentDirectory(),"releases","android");
    public static bool ValidName(string file)=>file!=null&&Regex.IsMatch(file,@"\ALMPC-[a-zA-Z0-9.-]+-\d+\.apk\z")&&Path.GetFileName(file)==file;
    public static JObject Latest()
    {
        try {
            var j=JObject.Parse(File.ReadAllText(Path.Combine(DirectoryPath,"latest.json")));
            string file=(string)j["file"];
            if(!ValidName(file)||(string)j["package"]!="app.lampac.home.mc"||((int?)j["version_code"]??0)<=0||!Regex.IsMatch((string)j["sha256"]??"",@"\A[0-9a-f]{64}\z"))return null;
            var info=new FileInfo(Path.Combine(DirectoryPath,file));
            if(!info.Exists||info.Length!=(long?)j["bytes"])return null;
            return j;
        }catch{return null;}
    }
    public static void Cleanup()
    {
        // Latest stays available even to a device that was offline for months.
        var latest=Latest();if(latest==null)return;
        try {foreach(var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.apk")){
            if(!ValidName(file.Name)||file.Name==(string)latest["file"]||DateTime.UtcNow-file.LastWriteTimeUtc<TimeSpan.FromDays(14))continue;
            try{file.Delete();}catch{}
        }}catch{}
    }
}
[AllowAnonymous]
public class AppUpdatesController : Controller
{
    [HttpGet,Route("home/app-updates/latest")]
    public ActionResult Latest()
    {
        Response.Headers["Cache-Control"]="no-store";
        var j=AppUpdates.Latest();
        if(j==null)return Json(new{available=false});
        j["available"]=true;return Content(j.ToString(),"application/json");
    }
    [HttpGet,Route("home/app-updates/apk/{file}")]
    public ActionResult Apk(string file)
    {
        if(!AppUpdates.ValidName(file))return NotFound();
        string path=Path.Combine(AppUpdates.DirectoryPath,file);
        if(!System.IO.File.Exists(path))return NotFound();
        Response.Headers["Cache-Control"]="public,max-age=86400,immutable";
        return new FileStreamResult(System.IO.File.OpenRead(path),"application/vnd.android.package-archive"){EnableRangeProcessing=true,FileDownloadName=file};
    }
}
