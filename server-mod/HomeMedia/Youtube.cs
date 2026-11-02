using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// YouTube внутри Lampac: поиск, трейлеры карточек, выбор потока нужного качества, пропуск рекламных вставок (SponsorBlock).
// Потоки добывает yt-dlp (/opt/lampac/yt), весь трафик к YouTube идёт через прокси из настройки yt_proxy (по умолчанию mihomo на CT109).
// Устройства к googlevideo напрямую не ходят: видео и звук отдаёт наш /yt/p с поддержкой Range.
public static class YtStore
{
    public static readonly string Bin = Environment.GetEnvironmentVariable("HOME_YTDLP") is { Length: > 0 } yb ? yb : "/opt/lampac/yt/yt-dlp";
    static bool ready;

    public static string Proxy
    {
        get { var p = HomeDb.Get("yt_proxy"); return NetworkProxy.Route("youtube", string.IsNullOrWhiteSpace(p) ? Environment.GetEnvironmentVariable("HOME_YT_PROXY") : p); }
    }

    static void Ensure()
    {
        if (ready) return;
        HomeDb.With(c => HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS yt_cache (k TEXT PRIMARY KEY, j TEXT, at INTEGER)"));
        ready = true;
    }

    public static JToken Cached(string k, int freshSec)
    {
        Ensure();
        return HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT j, at FROM yt_cache WHERE k=@k", ("@k", k));
            if (q.Count == 0 || HomeDb.Now() / 1000 - Convert.ToInt64(q[0][1]) > freshSec) return null;
            try { return JToken.Parse((string)q[0][0]); } catch { return null; }
        });
    }

    public static void Put(string k, JToken v)
    {
        Ensure();
        HomeDb.With(c => HomeDb.Exec(c, "INSERT INTO yt_cache(k,j,at) VALUES(@k,@j,@a) ON CONFLICT(k) DO UPDATE SET j=@j, at=@a",
            ("@k", k), ("@j", v.ToString(Formatting.None)), ("@a", HomeDb.Now() / 1000)));
    }

    // Shared, bounded process slots; detailed failures remain available to video resolution.
    public static async Task<string> Run(int timeoutSec, params string[] args)
        => (await YoutubeProcess.Run(Bin, Proxy, timeoutSec, CancellationToken.None, args)).Output;

    public static JObject Card(JObject e)
    {
        string id = (string)e["id"];
        return new JObject
        {
            ["id"] = id,
            ["title"] = (string)e["title"],
            ["channel"] = (string)(e["channel"] ?? e["uploader"]),
            ["channel_id"] = (string)e["channel_id"], ["video_id"] = id, ["age_limit"] = e["age_limit"],
            ["duration"] = (double?)e["duration"] ?? 0,
            ["views"] = (long?)e["view_count"] ?? 0,
            ["published"] = ((long?)e["timestamp"] ?? (long?)e["release_timestamp"]) is long ts ? DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-ddTHH:mm:ssZ") : null,
            ["thumb"] = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg"
        };
    }

    // страницы YouTube (с подписью «N дн. назад»): сервер обычного JSON-списка даты не отдаёт
    static HttpClient pageClient;
    static string pageFor;
    public static async Task<string> PageHtml(string url)
    {
        try
        {
            string p = Proxy;
            if (pageClient == null || pageFor != p)
            {
                pageClient = new HttpClient(new SocketsHttpHandler { Proxy = NetworkProxy.Web(p), UseProxy = !string.IsNullOrWhiteSpace(p), ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(20) };
                pageFor = p;
            }
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9");
            req.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+1; SOCS=CAI");
            using var r = await pageClient.SendAsync(req);
            return await r.Content.ReadAsStringAsync();
        }
        catch { return null; }
    }

    // id видео -> «4 дн. назад» (страница канала и результаты поиска)
    public static Dictionary<string, string> ParseAgo(string html)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(html)) return d;
        var ms = Regex.Matches(html, "\"contentId\":\"([\\w-]{11})\",\"contentType\":\"LOCKUP_CONTENT_TYPE_VIDEO\"");
        foreach (Match m in ms)
        {
            // подпись «4 дн. назад» лежит в разметке карточки ДО contentId (в пределах ~5 КБ)
            int from = Math.Max(0, m.Index - 5500);
            string seg = html.Substring(from, m.Index - from);
            var all = Regex.Matches(seg, "\"([^\"]{1,25}назад)\"");
            if (all.Count > 0 && !d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = all[all.Count - 1].Groups[1].Value;
        }
        var vs = Regex.Matches(html, "\"videoRenderer\":\\{\"videoId\":\"([\\w-]{11})\"");
        foreach (Match m in vs)
        {
            string seg = html.Substring(m.Index, Math.Min(6000, html.Length - m.Index));
            var a = Regex.Match(seg, "\"publishedTimeText\":\\{\"simpleText\":\"([^\"]+)\"");
            if (a.Success && !d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = a.Groups[1].Value;
        }
        return d;
    }

    // поиск каналов: страница результатов YouTube с фильтром «Каналы»
    public static async Task<JArray> SearchChannels(string q, int n)
    {
        string key = "sc:" + n + ":" + q.ToLowerInvariant();
        if (Cached(key, 3600) is JArray c) return c;
        var o = await Run(40, "--flat-playlist", "-J", "--playlist-end", n.ToString(), "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q) + "&sp=EgIQAg%253D%253D");
        if (o == null) return Cached(key, 86400) as JArray ?? new JArray();
        var arr = new JArray();
        foreach (var e in ((JArray)JObject.Parse(o)["entries"] ?? new JArray()).Children<JObject>())
        {
            string id = (string)e["channel_id"] ?? (string)e["id"];
            if (string.IsNullOrEmpty(id) || !id.StartsWith("UC")) continue;
            string thumb = null;
            foreach (var t in ((JArray)e["thumbnails"] ?? new JArray()).Children<JObject>()) { thumb = (string)t["url"]; if (thumb != null && thumb.StartsWith("//")) thumb = "https:" + thumb; }
            arr.Add(new JObject { ["id"] = id, ["title"] = (string)e["channel"] ?? (string)e["uploader"] ?? (string)e["title"], ["followers"] = (long?)e["channel_follower_count"] ?? 0, ["thumb"] = thumb ?? "", ["description"] = (string)e["description"] ?? "" });
        }
        Put(key, arr);
        return arr;
    }

    public static async Task<JArray> Search(string q, int n)
    {
        string key = "s2:" + n + ":" + q.ToLowerInvariant();
        if (Cached(key, 1800) is JArray c) return c;
        var o = await Run(40, "--flat-playlist", "-J", $"ytsearch{n}:{q}");
        if (o == null) return Cached(key, 86400 * 3) as JArray ?? new JArray();
        var arr = new JArray(((JArray)JObject.Parse(o)["entries"] ?? new JArray()).Children<JObject>().Select(Card));
        try
        {
            var ago = ParseAgo(await PageHtml("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q)));
            foreach (var v in arr.Children<JObject>()) if (ago.TryGetValue((string)v["id"], out var t)) v["ago"] = t;
        }
        catch { }
        Put(key, arr);
        return arr;
    }

    public static async Task<JObject> SearchPage(string query,int size,int page)
    {
        int start=(page-1)*size+1,end=page*size+1;
        string key=$"search_page_v1:{size}:{page}:{query.ToLowerInvariant()}";
        if(Cached(key,1800) is JObject cached)return (JObject)cached.DeepClone();
        var output=await Run(60,"--flat-playlist","-J","--playlist-items",$"{start}-{end}",$"ytsearch{end}:{query}");
        if(output==null)throw new HttpRequestException("youtube_search_unavailable");
        var raw=JObject.Parse(output)["entries"] as JArray??new JArray();
        var result=new JObject{["results"]=new JArray(raw.Children<JObject>().Take(size).Select(Card)),["page"]=page,["has_more"]=raw.Count>size};
        Put(key,result);return (JObject)result.DeepClone();
    }

    // ролики канала (последние): ссылка /channel/<id>/videos
    public static async Task<JArray> Channel(string id, int n)
    {
        string key = "c:" + n + ":" + id;
        if (Cached(key, 1800) is JArray c) return c;
        var o = await Run(40, "--flat-playlist", "-J", "--playlist-end", n.ToString(), $"https://www.youtube.com/channel/{id}/videos");
        if (o == null) return Cached(key, 86400 * 3) as JArray ?? new JArray();
        var arr = new JArray(((JArray)JObject.Parse(o)["entries"] ?? new JArray()).Children<JObject>().Select(Card));
        Put(key, arr);
        return arr;
    }

    // ---- профили и подписки ----
    static bool subsReady;
    static void EnsureSubs()
    {
        if (subsReady) return;
        HomeDb.With(c =>
        {
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS yt_subs (profile TEXT, ch TEXT, title TEXT, thumb TEXT, at INTEGER, PRIMARY KEY(profile, ch))");
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS yt_profiles (name TEXT PRIMARY KEY, at INTEGER)");
        });
        subsReady = true;
    }

    public static string ForUser(string id)=>"usr_"+HomeMedia.Profiles.Hash(id).Substring(0,24);
    public static string UserProfile()
    {
        string selected=(string)HomeMedia.Profiles.Get(HomeMedia.Profiles.CurrentId)?["settings"]?["youtube_profile"];
        if(CanAssign(HomeMedia.Profiles.CurrentId,selected))return selected;
        string key=ForUser(HomeMedia.Profiles.CurrentId);EnsureSubs();
        if(HomeMedia.Profiles.CurrentId==HomeMedia.Profiles.MainId&&HomeDb.Get("user_youtube_imported")!="1"){
            HomeDb.With(c=>{using var tx=c.BeginTransaction();HomeDb.Exec(c,"INSERT OR IGNORE INTO yt_subs(profile,ch,title,thumb,at) SELECT @p,ch,title,thumb,at FROM yt_subs WHERE profile NOT LIKE 'usr_%'",("@p",key));tx.Commit();});
            HomeDb.Set("user_youtube_imported","1");
        }
        AddProfile(key);
        var current=HomeMedia.Profiles.Get(HomeMedia.Profiles.CurrentId);
        if((bool?)current?["is_child"]==true&&(string)current?["settings"]?["youtube_mode"]=="channels")HomeDb.With(c=>{
            HomeDb.Exec(c,"INSERT OR IGNORE INTO yt_subs(profile,ch,title,thumb,at) SELECT @key,subject,subject,'',@at FROM profile_rules WHERE profile_id=@owner AND kind='youtube_channel' AND decision='allow'",("@key",key),("@owner",HomeMedia.Profiles.CurrentId),("@at",HomeDb.Now()/1000));
            HomeDb.Exec(c,"DELETE FROM yt_subs WHERE profile=@key AND ch NOT IN(SELECT subject FROM profile_rules WHERE profile_id=@owner AND kind='youtube_channel' AND decision='allow')",("@key",key),("@owner",HomeMedia.Profiles.CurrentId));
        });
        return key;
    }
    public static readonly Regex profRx = new(@"^[^\s/\?&#<>""']{1,32}$");
    static string CleanPackName(string raw)
    {
        string s = Regex.Replace((raw ?? "").Trim(), @"\s+", " ");
        if(HomeMedia.Profiles.Enabled){
            if(s.Equals("main",StringComparison.OrdinalIgnoreCase)||s.Equals("Основной",StringComparison.OrdinalIgnoreCase)||s.Equals("я",StringComparison.OrdinalIgnoreCase))s="Я";
            if(s.Equals("дети",StringComparison.OrdinalIgnoreCase))s="дети";
        }
        if (s.Length < 1 || s.Length > 32 || s.IndexOfAny(new[] { '/', '?', '&', '#', '<', '>', '"', '\'', '\\', ':' }) >= 0) return null;
        return s;
    }
    public static string PackKey(string raw)
    {
        string name = CleanPackName(raw);
        return name == null || HomeMedia.Profiles.Current == null ? null : "pack:" + HomeMedia.Profiles.CurrentId + ":" + name;
    }
    public static bool ChildChannels()
    {
        var person = HomeMedia.Profiles.Get(HomeMedia.Profiles.CurrentId);
        return (bool?)person?["is_child"] == true && (string)person?["settings"]?["youtube_mode"] == "channels";
    }
    // Набор каналов принадлежит только текущему зрителю. Чужой ключ не открывается.
    public static bool KnownPack(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        EnsureSubs();
        return HomeDb.With(c => HomeDb.Query(c, "SELECT 1 FROM yt_profiles WHERE name=@n", ("@n", name)).Count > 0);
    }

    // Глобальное имя, личный набор этого профиля или его собственный pack:. Чужой usr_ не открывается.
    public static bool CanAssign(string lampaId, string name)
    {
        if (string.IsNullOrEmpty(lampaId) || !KnownPack(name)) return false;
        if (name == ForUser(lampaId)) return true;
        if (name.StartsWith("usr_", StringComparison.Ordinal)) return false;
        if (name.StartsWith("pack:", StringComparison.Ordinal)) return name.StartsWith("pack:" + lampaId + ":", StringComparison.Ordinal);
        return CleanPackName(name) == name;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, HashSet<string> ids)> packChannels = new();
    public static bool ChannelIn(string pack, string channel)
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(channel)) return false;
        if (!packChannels.TryGetValue(pack, out var hit) || DateTime.UtcNow - hit.at > TimeSpan.FromMinutes(2))
        {
            hit = (DateTime.UtcNow, new HashSet<string>(Subs(pack).Children<JObject>().Select(c => (string)c["id"])));
            packChannels[pack] = hit;
        }
        return hit.ids.Contains(channel);
    }

    // Набор каналов принадлежит профилю Lampa (Основной, Дети, жена), а не приставке.
    public static string ResolvePack(string requested)
    {
        if (!HomeMedia.Profiles.Enabled || HomeMedia.Profiles.Current == null) return string.IsNullOrEmpty(requested) ? "main" : requested;
        // Взрослая веб-панель перечисляет несколько глобальных наборов и
        // запрашивает каждый отдельно. Раньше параметр requested игнорировался,
        // поэтому счётчик «Дети» был 9, а отрисованные карточки приходили из «Я».
        // Детский сеанс не может открыть чужой набор.
        var current = HomeMedia.Profiles.Get(HomeMedia.Profiles.CurrentId);
        string own = UserProfile();
        if (!string.IsNullOrEmpty(requested) && CanAssign(HomeMedia.Profiles.CurrentId, requested))
        {
            bool child = (bool?)current?["is_child"] == true;
            string selected = (string)current?["settings"]?["youtube_profile"];
            if (!child || requested == selected || requested == own) return requested;
        }
        string chosen = (string)HomeMedia.Profiles.Get(HomeMedia.Profiles.CurrentId)?["settings"]?["youtube_profile"];
        if (CanAssign(HomeMedia.Profiles.CurrentId, chosen)) return chosen;
        return own;
    }

    // Прежний выбор на устройстве один раз переносится основному профилю. Остальные начинают со своего набора.
    public static void AdoptDeviceChoice(string deviceValue)
    {
        if (!HomeMedia.Profiles.Enabled || HomeMedia.Profiles.CurrentId != HomeMedia.Profiles.MainId) return;
        if (HomeDb.Get("yt_profile_bound") == "1") return;
        var cfg = (JObject)HomeMedia.Profiles.Get(HomeMedia.Profiles.CurrentId)["settings"];
        if (cfg["youtube_profile"] == null && CanAssign(HomeMedia.Profiles.CurrentId, deviceValue)) cfg["youtube_profile"] = deviceValue;
        HomeDb.With(c => HomeMedia.Profiles.Exec(c, "UPDATE user_profiles SET settings=@j WHERE id=@p", ("@j", cfg.ToString(Newtonsoft.Json.Formatting.None)), ("@p", HomeMedia.Profiles.CurrentId)));
        HomeDb.Set("yt_profile_bound", "1");
    }

    public static void AddProfile(string name)
    {
        EnsureSubs();
        if(HomeMedia.Profiles.Enabled&&!name.StartsWith("usr_",StringComparison.Ordinal)&&!name.StartsWith("pack:",StringComparison.Ordinal))name=CleanPackName(name);
        if(string.IsNullOrEmpty(name))return;
        HomeDb.With(c => HomeDb.Exec(c, "INSERT OR IGNORE INTO yt_profiles(name, at) VALUES(@n, @a)", ("@n", name), ("@a", HomeDb.Now() / 1000)));
    }

    public static JArray Profiles()
    {
        EnsureSubs();
        if(HomeMedia.Profiles.Enabled&&HomeMedia.Profiles.Current!=null){
            string own=UserProfile(),prefix="pack:"+HomeMedia.Profiles.CurrentId+":";
            var arr=new JArray();
            var seen=new HashSet<string>();
            void Put(string name,string label,bool personal,long subs){ if(!seen.Add(name))return; arr.Add(new JObject{["name"]=name,["label"]=label,["subs"]=subs,["personal"]=personal}); }
            Put(own,own.StartsWith("usr_",StringComparison.Ordinal)?(string)HomeMedia.Profiles.Get(HomeMedia.Profiles.CurrentId)["name"]+" · свой":own=="дети"?"Дети":own,true,Subs(own).Count);
            foreach(var r in HomeDb.With(c=>HomeDb.Query(c,"SELECT p.name,(SELECT COUNT(*) FROM yt_subs s WHERE s.profile=p.name) FROM yt_profiles p ORDER BY p.at"))){
                string name=(string)r[0];
                if(name.StartsWith("usr_",StringComparison.Ordinal))continue;
                if(name.StartsWith("pack:",StringComparison.Ordinal)){ if(name.StartsWith(prefix,StringComparison.Ordinal))Put(name,name.Substring(prefix.Length),false,Convert.ToInt64(r[1])); continue; }
                Put(name,name=="дети"?"Дети":name,false,Convert.ToInt64(r[1]));
            }
            return arr;
        }
        AddProfile("main");   // профиль по умолчанию, чтобы список не был пустым
        return HomeDb.With(c =>
        {
            var arr = new JArray();
            foreach (var r in HomeDb.Query(c, "SELECT p.name, (SELECT COUNT(*) FROM yt_subs s WHERE s.profile=p.name) FROM yt_profiles p ORDER BY p.at"))
                arr.Add(new JObject { ["name"] = (string)r[0], ["subs"] = Convert.ToInt64(r[1]) });
            return arr;
        });
    }

    // загрузка подписок из аккаунта: новые добавляются, названия обновляются, отписанные каналы убираются
    public static int ImportSubs(string profile, JArray chs)
    {
        EnsureSubs(); AddProfile(profile);
        var ids = new HashSet<string>();
        long now = HomeDb.Now() / 1000;
        HomeDb.With(c =>
        {
            foreach (var ch in chs.Children<JObject>())
            {
                string id = (string)ch["id"];
                if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[0-9A-Za-z_-]{10,40}$")) continue;
                ids.Add(id);
                HomeDb.Exec(c, @"INSERT INTO yt_subs(profile,ch,title,thumb,at) VALUES(@p,@c,@t,@h,@a)
                                 ON CONFLICT(profile,ch) DO UPDATE SET title=@t, thumb=@h",
                    ("@p", profile), ("@c", id), ("@t", (string)ch["title"] ?? ""), ("@h", (string)ch["thumb"] ?? ""), ("@a", now));
            }
            if (ids.Count > 0)
                foreach (var r in HomeDb.Query(c, "SELECT ch FROM yt_subs WHERE profile=@p", ("@p", profile)).ToList())
                    if (!ids.Contains((string)r[0])) HomeDb.Exec(c, "DELETE FROM yt_subs WHERE profile=@p AND ch=@c", ("@p", profile), ("@c", (string)r[0]));
        });
        packChannels.TryRemove(profile, out _);
        Feeds.TryRemove(profile, out _);
        return ids.Count;
    }

    public static JArray Subs(string profile)
    {
        EnsureSubs();
        return HomeDb.With(c =>
        {
            var arr = new JArray();
            foreach (var r in HomeDb.Query(c, "SELECT ch, title, thumb FROM yt_subs WHERE profile=@p ORDER BY title COLLATE NOCASE", ("@p", profile)))
                arr.Add(new JObject { ["id"] = (string)r[0], ["title"] = (string)r[1], ["thumb"] = (string)r[2] });
            return arr;
        });
    }

    // ссылка, @имя или id канала -> (id, название)
    public static async Task<(string id, string title)> ResolveChannel(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0 || input.Length > 200) return (null, null);
        var m = Regex.Match(input, @"(UC[0-9A-Za-z_-]{22})");
        string id = m.Success ? m.Groups[1].Value : null;
        string url = id != null ? $"https://www.youtube.com/channel/{id}/videos" : null;
        if (url == null)
        {
            string h = input;
            var hm = Regex.Match(input, @"youtube\.com/(@[^/?#\s]+|c/[^/?#\s]+|user/[^/?#\s]+)");
            if (hm.Success) h = hm.Groups[1].Value;
            if (h.StartsWith("@")) url = $"https://www.youtube.com/{h}/videos";
            else if (Regex.IsMatch(h, @"^(c|user)/[^/\s]+$")) url = $"https://www.youtube.com/{h}/videos";
            else if (Regex.IsMatch(h, @"^[\w.-]{3,40}$")) url = $"https://www.youtube.com/@{h}/videos";
            else return (null, null);
        }
        string key = "rc:" + url;
        if (Cached(key, 86400 * 30) is JObject c) return ((string)c["id"], (string)c["title"]);
        var o = await Run(40, "--flat-playlist", "-J", "--playlist-end", "1", url);
        if (o == null) return (id, null);
        var d = JObject.Parse(o);
        string rid = (string)d["channel_id"] ?? id;
        string title = (string)d["channel"] ?? (string)d["uploader"] ?? ((string)d["title"] ?? "").Replace(" - Видео", "").Replace(" - Videos", "");
        if (rid == null) return (null, null);
        Put(key, new JObject { ["id"] = rid, ["title"] = title });
        return (rid, title);
    }

    public static void AddSub(string profile, string id, string title, string thumb = "")
    {
        EnsureSubs(); AddProfile(profile);
        HomeDb.With(c => HomeDb.Exec(c, @"INSERT INTO yt_subs(profile,ch,title,thumb,at) VALUES(@p,@c,@t,@h,@a)
                         ON CONFLICT(profile,ch) DO UPDATE SET title=@t", ("@p", profile), ("@c", id), ("@t", title ?? ""), ("@h", thumb ?? ""), ("@a", HomeDb.Now() / 1000)));
        packChannels.TryRemove(profile, out _);
        Feeds.TryRemove(profile, out _);
    }

    public static void RemoveSub(string profile, string id)
    {
        EnsureSubs();
        HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM yt_subs WHERE profile=@p AND ch=@c", ("@p", profile), ("@c", id)));
        packChannels.TryRemove(profile, out _);
        Feeds.TryRemove(profile, out _);
    }

    public static void RemoveProfile(string profile)
    {
        EnsureSubs();
        HomeDb.With(c => { HomeDb.Exec(c, "DELETE FROM yt_subs WHERE profile=@p", ("@p", profile)); HomeDb.Exec(c, "DELETE FROM yt_profiles WHERE name=@p", ("@p", profile)); });
        packChannels.TryRemove(profile,out _);
        foreach(var key in Feeds.Keys.Where(k=>k==profile||k.StartsWith(profile+"|",StringComparison.Ordinal)))Feeds.TryRemove(key,out _);
    }

    static HttpClient rssClient;
    static string rssFor;

    // последние ролики канала из RSS YouTube (без ключей и входа), кэш 20 минут
    static async Task<JArray> ChannelRss(string ch)
    {
        string key = "rss:" + ch;
        if (Cached(key, 1200) is JArray c) return c;
        try
        {
            string p = Proxy;
            if (rssClient == null || rssFor != p)
            {
                rssClient = new HttpClient(new SocketsHttpHandler { Proxy = NetworkProxy.Web(p), UseProxy = !string.IsNullOrWhiteSpace(p), ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(15) };
                rssFor = p;
            }
            string xml = await rssClient.GetStringAsync($"https://www.youtube.com/feeds/videos.xml?channel_id={ch}");
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            System.Xml.Linq.XNamespace a = "http://www.w3.org/2005/Atom", yt = "http://www.youtube.com/xml/schemas/2015";
            var arr = new JArray();
            foreach (var e in doc.Root.Elements(a + "entry").Take(15))
            {
                string id = (string)e.Element(yt + "videoId");
                arr.Add(new JObject
                {
                    ["id"] = id, ["title"] = (string)e.Element(a + "title"),
                    ["channel"] = (string)e.Element(a + "author")?.Element(a + "name"), ["channel_id"] = ch,
                    ["published"] = (string)e.Element(a + "published"), ["duration"] = 0, ["thumb"] = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg"
                });
            }
            Put(key, arr);
            return arr;
        }
        catch { return Cached(key, 86400 * 7) as JArray ?? new JArray(); }
    }

    sealed class FeedBuf
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<JObject> Items = new();
        public readonly HashSet<string> Seen = new();
        public List<string> Channels;
        public int Next;
        public bool End;
        public DateTime Used = DateTime.UtcNow;
    }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FeedBuf> Feeds = new();

    // Finish the snapshot before publishing pages: inserting newly read channels
    // ahead of an already returned prefix caused duplicates and skipped videos.
    public static async Task<(JArray results, bool more)> FeedPage(string profile, int page, int size,string session=null)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 40);
        foreach (var stale in Feeds.Where(x => DateTime.UtcNow - x.Value.Used > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToList()) Feeds.TryRemove(stale, out _);
        string key=string.IsNullOrEmpty(session)?profile??"main":(profile??"main")+"|"+session;
        var buf = Feeds.GetOrAdd(key, _ => new FeedBuf());
        await buf.Gate.WaitAsync();
        buf.Used = DateTime.UtcNow;
        try
        {
            buf.Channels ??= Subs(profile).Children<JObject>().Select(x => (string)x["id"]).Where(x => !string.IsNullOrEmpty(x)).ToList();
            var deadline = DateTime.UtcNow.AddSeconds(2.5);
            while (!buf.End && DateTime.UtcNow < deadline)
            {
                var batch = buf.Channels.Skip(buf.Next).Take(8).ToList();
                if (batch.Count == 0) { buf.End = true; break; }
                buf.Next += batch.Count;
                var lists = await Task.WhenAll(batch.Select(ChannelRss));
                foreach (var arr in lists)
                    foreach (var v in arr.Children<JObject>())
                    {
                        string id = (string)v["id"];
                        if (!string.IsNullOrEmpty(id) && buf.Seen.Add(id)) buf.Items.Add(v);
                    }
                if (buf.Next >= buf.Channels.Count) buf.End = true;
            }
            if(!buf.End)return(new JArray(),true);
            buf.Items.Sort((a,b)=>{int n=string.CompareOrdinal((string)b["published"],(string)a["published"]);return n!=0?n:string.CompareOrdinal((string)a["id"],(string)b["id"]);});
            var slice = buf.Items.Skip((page - 1) * size).Take(size).ToList();
            bool more = buf.Items.Count > page * size || !buf.End;
            return (new JArray(slice), more);
        }
        finally { buf.Gate.Release(); }
    }

    // страница канала: описание, подписчики, аватар и последние видео
    public static async Task<JObject> ChannelPage(string id, int n, int page=1)
    {
        string key = "cp9:" + n + ":" + page + ":" + id;
        if (Cached(key, 1800) is JObject c) return (JObject)c.DeepClone();
        var o = await Run(60, "--flat-playlist", "-J", "--playlist-items",$"{(page-1)*n+1}-{page*n+1}", $"https://www.youtube.com/channel/{id}/videos");
        if (o == null) throw new HttpRequestException("youtube_channel_unavailable");
        var d = JObject.Parse(o);
        string avatar = null, banner = null;
        foreach (var t in ((JArray)d["thumbnails"] ?? new JArray()).Children<JObject>())
        {
            string tid = (string)t["id"] ?? "", url = (string)t["url"] ?? "";
            if (tid == "avatar_uncropped" && avatar == null) avatar = url;
            else if (tid == "banner_uncropped" && banner == null) banner = url;
        }
        if (avatar == null)
            foreach (var t in ((JArray)d["thumbnails"] ?? new JArray()).Children<JObject>())
                if ((int?)t["width"] > 0 && (int?)t["width"] == (int?)t["height"]) { avatar = (string)t["url"]; break; }
        string title = (string)d["channel"] ?? (string)d["uploader"] ?? ((string)d["title"] ?? "").Replace(" - Видео", "").Replace(" - Videos", "");
        var res = new JObject
        {
            ["info"] = new JObject
            {
                ["id"] = id, ["title"] = title, ["description"] = (string)d["description"] ?? "",
                ["followers"] = (long?)d["channel_follower_count"] ?? 0, ["avatar"] = avatar ?? "", ["banner"] = banner ?? ""
            },
            ["results"] = new JArray(((JArray)d["entries"] ?? new JArray()).Children<JObject>().Take(n).Select(Card)),
            ["page"]=page,["has_more"]=((JArray)d["entries"]??new JArray()).Count>n
        };
        // в списке канала нет ни даты, ни названия канала: даты берём из RSS (последние 15 видео), канал подставляем сами
        try
        {
            var dates = new Dictionary<string, string>();
            foreach (var r in (await ChannelRss(id)).Children<JObject>()) dates[(string)r["id"]] = (string)r["published"];
            foreach (var v in ((JArray)res["results"]).Children<JObject>())
            {
                v["channel"] = title; v["channel_id"] = id;
                if (dates.TryGetValue((string)v["id"], out var pd)) v["published"] = pd;
            }
            // у остальных видео точной даты нет — берём подпись со страницы канала («4 дн. назад»)
            var ago = ParseAgo(await PageHtml($"https://www.youtube.com/channel/{id}/videos"));
            foreach (var v in ((JArray)res["results"]).Children<JObject>())
                if (string.IsNullOrEmpty((string)v["published"]))
                    if (ago.TryGetValue((string)v["id"], out var t)) v["ago"] = t;
        }
        catch { }
        Put(key, res);
        if (!string.IsNullOrEmpty(avatar))
        {
            EnsureSubs();
            HomeDb.With(cn => HomeDb.Exec(cn, "UPDATE yt_subs SET thumb=@h WHERE ch=@c AND (thumb IS NULL OR thumb='')", ("@h", avatar), ("@c", id)));
        }
        return res;
    }

    // аватарки каналов подписок подгружаются в фоне (по два запроса одновременно)
    static DateTime fillAt = DateTime.MinValue;
    public static void FillAvatars(string profile)
    {
        if ((DateTime.UtcNow - fillAt).TotalMinutes < 10) return;
        var todo = Subs(profile).Children<JObject>().Where(x => string.IsNullOrEmpty((string)x["thumb"])).Select(x => (string)x["id"]).Take(40).ToList();
        if (todo.Count == 0) return;
        fillAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(2);
            await Task.WhenAll(todo.Select(async ch =>
            {
                await sem.WaitAsync();
                try { await ChannelPage(ch, 1); } catch { } finally { sem.Release(); }
            }));
        });
    }

    static readonly Regex idRx = new("^[0-9A-Za-z_-]{11}$");
    public static bool ValidId(string id) => !string.IsNullOrEmpty(id) && idRx.IsMatch(id);

    public static string Enc(string u) => Convert.ToBase64String(Encoding.UTF8.GetBytes(u)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Dec(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        while (s.Length % 4 != 0) s += "=";
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    // сведения о ролике: лучший поток в пределах maxh, звук (русский — если есть), сегменты рекламы
    // vp9=true: приставка умеет VP9 — для 1440p/4K, иначе потолок 1080p (H.264)
    static readonly YoutubeFlights<JObject> videoFlights = new();
    static string ProxyStamp => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Proxy ?? "")));
    public static async Task<JObject> VideoInfo(string id, bool force, CancellationToken caller)
    {
        string key = "v3:" + id, route = ProxyStamp;
        JObject cached = Cached(key, 4 * 3600) as JObject;
        if (!force && YoutubeLinks.Fresh(cached, route, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return cached;
        return await videoFlights.Get(id + ":" + route, async cancel => {
            if (!force && YoutubeLinks.Fresh(cached = Cached(key, 4 * 3600) as JObject, route, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return cached;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel); budget.CancelAfter(TimeSpan.FromSeconds(70));
            string failure = "no_stream";
            // Keep default extraction first. Alternate public clients are bounded fallbacks,
            // not a proxy change or a Google account login.
            foreach (string client in new[] { "", "web_embedded", "android_vr" }) {
                var args = new List<string> { "-J", "--no-playlist" };
                if (client.Length > 0) { args.Add("--extractor-args"); args.Add("youtube:player_client=" + client); }
                args.Add("https://www.youtube.com/watch?v=" + id);
                YoutubeProcess.Result result;
                try { result = await YoutubeProcess.Run(Bin, Proxy, client == "android_vr" ? 12 : 20, budget.Token, args.ToArray()); }
                catch (OperationCanceledException) { cancel.ThrowIfCancellationRequested(); throw new YoutubeFailure("extract_timeout"); }
                failure = result.Error ?? "no_stream";
                if (result.Output != null) {
                    try {
                        var d = JObject.Parse(result.Output);
                        var fs = ((JArray)d["formats"] ?? new JArray()).Children<JObject>()
                            .Where(f => (string)f["protocol"] == "https" && !string.IsNullOrEmpty((string)f["url"]))
                            .Select(f => new JObject {
                                ["id"] = f["format_id"], ["url"] = f["url"], ["h"] = f["height"], ["w"] = f["width"], ["fps"] = f["fps"],
                                ["v"] = (string)f["vcodec"] == "none" ? null : (string)f["vcodec"], ["a"] = (string)f["acodec"] == "none" ? null : (string)f["acodec"],
                                ["lang"] = f["language"], ["abr"] = f["abr"], ["br"] = f["tbr"], ["size"] = f["filesize"] ?? f["filesize_approx"]
                            });
                        var info = new JObject {
                            ["id"] = id, ["title"] = d["title"], ["channel"] = d["channel"], ["duration"] = d["duration"], ["live"] = d["is_live"],
                            ["video_id"] = id, ["channel_id"] = d["channel_id"], ["age_limit"] = d["age_limit"],
                            ["formats"] = new JArray(fs), ["proxy_stamp"] = route, ["extraction_client"] = client.Length == 0 ? "default" : client
                        };
                        if (((JArray)info["formats"]).Count > 0) { Put(key, info); DLogStore.Server("i", "yt_extract", id, "client=" + info["extraction_client"]); return info; }
                    } catch (Newtonsoft.Json.JsonException) { failure = "no_stream"; }
                }
                DLogStore.Server("w", "yt_extract", id, "client=" + (client.Length == 0 ? "default" : client) + " error=" + failure);
                if (failure is "unavailable" or "rate_limited" or "busy") break;
            }
            throw new YoutubeFailure(failure);
        }, caller);
    }

    public static async Task<JObject> Video(string id, int maxh, bool vp9, string host, bool force = false, CancellationToken caller = default)
    {
        JObject info = await VideoInfo(id, force, caller);
        if(!await ProfilePolicy.Allows(info))return null;
        var all = ((JArray)info["formats"]).Children<JObject>().ToList();
        var vids = all.Where(f => (string)f["v"] != null && (string)f["a"] == null && (int?)f["h"] > 0).ToList();
        var auds = all.Where(f => (string)f["a"] != null && (string)f["v"] == null).ToList();
        var prog = all.Where(f => (string)f["a"] != null && (string)f["v"] != null && (int?)f["h"] <= (maxh > 0 ? maxh : 2160)).OrderByDescending(f => (int?)f["h"] ?? 0).FirstOrDefault();

        int cap = maxh > 0 ? maxh : 2160;
        if (!vp9) cap = Math.Min(cap, 1080);
        string Codec(JObject f) => ((string)f["v"] ?? "").Split('.')[0];
        bool Ok(JObject f) => Codec(f) == "avc1" || (vp9 && (Codec(f) == "vp9" || Codec(f) == "vp09"));
        // лучшее разрешение в пределах профиля; при равенстве — H.264 (аппаратно везде), затем VP9
        var cand = vids.Where(f => Ok(f) && (int)f["h"] <= cap).OrderByDescending(f => (int)f["h"])
            .ThenBy(f => Codec(f) == "avc1" ? 0 : 1).ThenByDescending(f => (double?)f["br"] ?? 0).FirstOrDefault();
        // звук: русская дорожка, иначе оригинальная; AAC (140) надёжнее на ТВ, чем opus
        var aud = auds.Where(f => ((string)f["lang"] ?? "").StartsWith("ru")).OrderByDescending(f => (double?)f["abr"] ?? 0).FirstOrDefault()
               ?? auds.Where(f => ((string)f["a"] ?? "").StartsWith("mp4a")).OrderByDescending(f => (double?)f["abr"] ?? 0).FirstOrDefault()
               ?? auds.OrderByDescending(f => (double?)f["abr"] ?? 0).FirstOrDefault();

        var res = new JObject
        {
            ["id"] = id, ["title"] = info["title"], ["channel"] = info["channel"], ["duration"] = info["duration"], ["video_id"] = id, ["channel_id"] = info["channel_id"], ["age_limit"] = info["age_limit"],
            ["heights"] = new JArray(vids.Where(Ok).Select(f => (int)f["h"]).Distinct().OrderByDescending(x => x))
        };
        string P(JObject f) => $"{host}/yt/p?id={id}&format={Uri.EscapeDataString((string)f["id"])}&u={Enc((string)f["url"])}";
        if (cand != null && aud != null)
        {
            res["video"] = P(cand); res["audio"] = P(aud); res["height"] = cand["h"]; res["vcodec"] = Codec(cand);
        }
        else if (prog != null)
        {
            res["video"] = P(prog); res["height"] = prog["h"]; res["vcodec"] = "progressive";
        }
        else return null;
        // все доступные качества в пределах профиля: на каждую высоту лучший кодек (H.264, а VP9 — если H.264 такой высоты нет)
        if (cand != null && aud != null)
            res["quals"] = new JArray(vids.Where(f => Ok(f) && (int)f["h"] <= cap)
                .GroupBy(f => (int)f["h"]).OrderByDescending(g => g.Key)
                .Select(g => g.OrderBy(f => Codec(f) == "avc1" ? 0 : 1).ThenByDescending(f => (double?)f["br"] ?? 0).First())
                .Select(f => new JObject { ["h"] = f["h"], ["v"] = Codec(f), ["url"] = P(f) }));
        res["sponsor"] = await Sponsor(id);
        return res;
    }

    // сегменты, которые надо пропускать: спонсор, самореклама, «подпишитесь», заставка и концовка (SponsorBlock)
    public static async Task<JArray> Sponsor(string id)
    {
        string key = "sb:" + id;
        if (Cached(key, 86400) is JArray c) return c;
        try
        {
            using var h = new HttpClient(new SocketsHttpHandler { Proxy = NetworkProxy.Web(Proxy), UseProxy = !string.IsNullOrWhiteSpace(Proxy), ConnectTimeout = TimeSpan.FromSeconds(6) }) { Timeout = TimeSpan.FromSeconds(8) };
            string cats = Uri.EscapeDataString("[\"sponsor\",\"selfpromo\",\"interaction\",\"intro\",\"outro\"]");
            var r = await h.GetAsync($"https://sponsor.ajay.app/api/skipSegments?videoID={id}&categories={cats}&actionTypes=%5B%22skip%22%5D");
            var arr = new JArray();
            if (r.IsSuccessStatusCode)
                foreach (var s in JArray.Parse(await r.Content.ReadAsStringAsync()).Children<JObject>())
                    arr.Add(new JArray((double)s["segment"][0], (double)s["segment"][1]));
            else if ((int)r.StatusCode != 404) return new JArray();   // сбой сервиса — не кэшируем
            Put(key, arr);
            return arr;
        }
        catch { return new JArray(); }
    }

    // трейлер карточки: сначала TMDB (русские, потом любые), затем поиск по названию с «трейлер русский»
    public static async Task<JObject> Trailer(string media, long tmdb, string title, string year)
    {
        string key = $"t:{media}:{tmdb}";
        if (Cached(key, 86400 * 14) is JObject c) return c.Count == 0 ? null : c;
        JObject best = null;
        try
        {
            foreach (var lang in new[] { "ru", "en" })
            {
                var d = await KpMap.TmdbRaw($"{media}/{tmdb}/videos?language={lang}-{(lang == "ru" ? "RU" : "US")}");
                var vs = ((JArray)d?["results"] ?? new JArray()).Children<JObject>()
                    .Where(v => (string)v["site"] == "YouTube" && ValidId((string)v["key"]))
                    .OrderBy(v => (string)v["type"] == "Trailer" ? 0 : (string)v["type"] == "Teaser" ? 1 : 2)
                    .ThenByDescending(v => (bool?)v["official"] == true).ToList();
                if (vs.Count > 0 && ((string)vs[0]["type"] == "Trailer" || lang == "en")) { best = new JObject { ["id"] = vs[0]["key"], ["title"] = vs[0]["name"], ["lang"] = lang }; break; }
            }
        }
        catch { }
        // русская озвучка важнее: ищем дубляж, если TMDB дал только английский
        if (best == null || (string)best["lang"] != "ru")
        {
            var found = await Search($"{title} {year} трейлер русский", 6);
            var hit = found.Children<JObject>().FirstOrDefault(v =>
                (double)v["duration"] >= 30 && (double)v["duration"] <= 420 &&
                Regex.IsMatch((string)v["title"] ?? "", "трейлер|trailer", RegexOptions.IgnoreCase) &&
                Regex.IsMatch((string)v["title"] ?? "", "русск|дубляж|rus|ru", RegexOptions.IgnoreCase));
            if (hit != null) best = new JObject { ["id"] = hit["id"], ["title"] = hit["title"], ["lang"] = "ru" };
        }
        Put(key, best ?? new JObject());
        return best;
    }
}

[ProfileResult]
public class YoutubeController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    [HttpGet, AllowAnonymous]
    [Route("youtube.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "youtube.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/search")]
    public async Task<ActionResult> Search(string q = null, int n = 20, int page=1)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 120) return Json(new { error = "bad_q" }, 400);
        if(page<1||page>500)return Json(new{error="bad_page"},400);
        try{return Json(await YtStore.SearchPage(q.Trim(),Math.Clamp(n,1,40),page));}catch{return Json(new{error="youtube_search_unavailable"},502);}
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/channels")]
    public async Task<ActionResult> Channels(string q = null, int n = 12)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 120) return Json(new { error = "bad_q" }, 400);
        return Json(new { results = await YtStore.SearchChannels(q.Trim(), Math.Clamp(n, 1, 30)) });
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/channel")]
    public async Task<ActionResult> Channel(string id = null, int n = 20, int page=1)
    {
        if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[0-9A-Za-z_-]{10,40}$")) return Json(new { error = "bad_id" }, 400);
        if(page<1||page>500)return Json(new{error="bad_page"},400);
        try{return Json(await YtStore.ChannelPage(id,Math.Clamp(n,1,40),page));}catch{return Json(new{error="youtube_channel_unavailable"},502);}
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/trailer")]
    public async Task<ActionResult> Trailer(string media = "movie", long tmdb = 0, string title = null, string year = null)
    {
        if (tmdb <= 0 || (media != "movie" && media != "tv")) return Json(new { error = "bad_args" }, 400);
        var t = await YtStore.Trailer(media, tmdb, title ?? "", year ?? "");
        return Json(t == null ? new { none = true } : (object)t);
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/video")]
    public async Task<ActionResult> Video(string id = null, int maxh = 0, bool vp9 = false, bool force = false)
    {
        if (!YtStore.ValidId(id)) return Json(new { error = "bad_id" }, 400);
        try {
            var v = await YtStore.Video(id, Math.Clamp(maxh, 0, 4320), vp9, host, force, HttpContext.RequestAborted);
            return v == null ? Json(new { error = "no_stream", message = "У видео нет доступного потока.", retryable = false }, 502) : Json(v);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (YoutubeFailure e) { if(e.Retryable || e.Code == "rate_limited")Response.Headers["Retry-After"]="15";return Json(new { error = e.Code, message = e.UserMessage, retryable = e.Retryable }, e.Status); }
    }

    static readonly object clientGate = new();
    static HttpClient proxied;
    static string proxiedFor;
    static HttpClient Client()
    {
        lock (clientGate) {
            string p = YtStore.Proxy;
            if (proxied == null || proxiedFor != p) {
                // Existing requests retain their client until complete; new connections use the new route.
                proxied = new HttpClient(new SocketsHttpHandler { Proxy = NetworkProxy.Web(p), UseProxy = !string.IsNullOrWhiteSpace(p), ConnectTimeout = TimeSpan.FromSeconds(12), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
                proxiedFor = p;
            }
            return proxied;
        }
    }

    static async Task<HttpResponseMessage> Fetch(Uri uri, long from, long to, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        return await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    static bool MediaUri(string url, out Uri uri)
        => Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
            (uri.Host.EndsWith(".googlevideo.com") || uri.Host.EndsWith(".youtube.com") || uri.Host.EndsWith(".ytimg.com"));
    static Uri RouteUri(Uri uri) => uri.Host.EndsWith(".googlevideo.com") && uri.AbsolutePath == "/videoplayback" ? new UriBuilder(uri) { Host = "redirector.googlevideo.com" }.Uri : uri;

    [HttpGet, AllowAnonymous]
    [Route("yt/p")]
    public async Task<ActionResult> Proxy(string u = null, string id = null, string format = null)
    {
        string url;
        try { url = YtStore.Dec(u ?? ""); } catch { return Json(new { error = "bad_u" }, 400); }
        if (!MediaUri(url, out var uri)) return Json(new { error = "bad_host" }, 400);
        if (!uri.Host.EndsWith(".googlevideo.com") || uri.AbsolutePath != "/videoplayback") {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                Response.StatusCode = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) {
                    Response.ContentType = response.Content.Headers.ContentType?.ToString();Response.ContentLength=response.Content.Headers.ContentLength;
                    await response.Content.CopyToAsync(Response.Body, timeout.Token);
                }
            } catch { if(Response.HasStarted)HttpContext.Abort();else Response.StatusCode=502; }
            return new EmptyResult();
        }
        async Task<Uri> Renew(CancellationToken ct) {
            var info = await YtStore.VideoInfo(id, true, ct);
            var f = ((JArray)info["formats"]).Children<JObject>().FirstOrDefault(f => (string)f["id"] == format);
            if (!MediaUri((string)f?["url"], out var fresh)) throw new YoutubeFailure("no_stream");
            return RouteUri(fresh);
        }
        bool canRenew = YtStore.ValidId(id) && Regex.IsMatch(format ?? "", @"^[0-9A-Za-z_.-]{1,80}$");
        await new YoutubeRelay(Fetch, canRenew ? Renew : null).Send(HttpContext, RouteUri(uri));
        return new EmptyResult();
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/profiles")]
    public ActionResult Profiles(string add = null)
    {
        if (!string.IsNullOrEmpty(add))
        {
            if (HomeMedia.Profiles.Enabled)
            {
                if (HomeMedia.Profiles.Current == null) return Json(new { error = "session_required" }, 401);
                if (HomeMedia.Profiles.Current.Child) return Json(new { error = "adult_required" }, 400);
                string key = YtStore.PackKey(add);
                // Имя без привязки к одному зрителю: его можно назначить любому профилю Lampa.
                if (key != null) key = key.Substring(key.LastIndexOf(':') + 1);
                if (string.IsNullOrEmpty(key)) return Json(new { error = "bad_name" }, 400);
                YtStore.AddProfile(key);
            }
            else
            {
                if (!YtStore.profRx.IsMatch(add)) return Json(new { error = "bad_name" }, 400);
                YtStore.AddProfile(add);
            }
        }
        return Json(new { profiles = YtStore.Profiles() });
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/subs")]
    public ActionResult Subs(string profile = "main")
    {
        if(HomeMedia.Profiles.Enabled)profile=YtStore.ResolvePack(profile);
        else if (!YtStore.profRx.IsMatch(profile ?? "")) return Json(new { error = "bad_profile" }, 400);
        YtStore.FillAvatars(profile);
        return Json(new { channels = YtStore.Subs(profile) });
    }

    // подписка по ссылке, @имени или id канала
    [HttpGet, AllowAnonymous]
    [Route("yt/subs/add")]
    public async Task<ActionResult> Add(string profile = "main", string channel = null)
    {
        if(HomeMedia.Profiles.Enabled)profile=YtStore.ResolvePack(profile);
        else if (!YtStore.profRx.IsMatch(profile ?? "")) return Json(new { error = "bad_profile" }, 400);
        var (id, title) = await YtStore.ResolveChannel(channel);
        if (id == null) return Json(new { error = "not_found" }, 404);
        YtStore.AddSub(profile, id, title ?? id);
        return Json(new { ok = true, id, title });
    }

    // несколько ссылок за раз (по одной в строке или через запятую)
    [HttpPost, AllowAnonymous]
    [Route("yt/subs/addmany")]
    public async Task<ActionResult> AddMany(string profile = "main")
    {
        if(HomeMedia.Profiles.Enabled)profile=YtStore.ResolvePack(profile);
        else if (!YtStore.profRx.IsMatch(profile ?? "")) return Json(new { error = "bad_profile" }, 400);
        string body;
        using (var rd = new StreamReader(Request.Body, Encoding.UTF8)) body = await rd.ReadToEndAsync();
        var added = new JArray(); var failed = new JArray();
        foreach (var line in Regex.Split(body ?? "", @"[\r\n,;]+").Select(x => x.Trim()).Where(x => x.Length > 0).Take(100))
        {
            var (id, title) = await YtStore.ResolveChannel(line);
            if (id == null) { failed.Add(line); continue; }
            YtStore.AddSub(profile, id, title ?? id);
            added.Add(new JObject { ["id"] = id, ["title"] = title });
        }
        return Json(new { ok = true, added, failed });
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/subs/remove")]
    public ActionResult Remove(string profile = "main", string ch = null)
    {
        if(HomeMedia.Profiles.Enabled)profile=YtStore.ResolvePack(profile);
        else if (!YtStore.profRx.IsMatch(profile ?? "") || string.IsNullOrEmpty(ch)) return Json(new { error = "bad_args" }, 400);
        if (string.IsNullOrEmpty(ch)) return Json(new { error = "bad_args" }, 400);
        YtStore.RemoveSub(profile, ch);
        return Json(new { ok = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/profiles/remove")]
    public ActionResult RemoveProfile(string name = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (HomeMedia.Profiles.Enabled)
        {
            if (HomeMedia.Profiles.Current == null) return Json(new { error = "session_required" }, 401);
            if (HomeMedia.Profiles.Current.Child) return Json(new { error = "adult_required" }, 400);
            string own = YtStore.UserProfile(), prefix = "pack:" + HomeMedia.Profiles.CurrentId + ":";
            bool global = name != null && name != "main" && name != own && !name.StartsWith("usr_", StringComparison.Ordinal) && !name.StartsWith("pack:", StringComparison.Ordinal);
            if (!(name != null && name.StartsWith(prefix, StringComparison.Ordinal) || global)) return Json(new { error = "bad_name" }, 400);
        }
        else if (!YtStore.profRx.IsMatch(name ?? "")) return Json(new { error = "bad_name" }, 400);
        YtStore.RemoveProfile(name);
        return Json(new { ok = true });
    }

    [HttpGet,AllowAnonymous,Route("yt/history")]
    public ActionResult History()
    {
        if(HomeMedia.Profiles.FromRequest(HttpContext)==null)return Json(new{error="session_required"},401);
        var rows=HomeDb.With(c=>HomeDb.Query(c,"SELECT id,t,d,p,u FROM timeline WHERE profile_id=@profile AND id LIKE 'yt-%' AND t>1 ORDER BY u DESC LIMIT 100"));
        var cards=new JArray();foreach(var row in rows){string id=((string)row[0]).Substring(3);var meta=YtStore.Cached("v3:"+id,30*86400) as JObject;if(meta==null)continue;
            cards.Add(new JObject{["id"]=id,["video_id"]=id,["title"]=meta["title"],["channel"]=meta["channel"],["channel_id"]=meta["channel_id"],["age_limit"]=meta["age_limit"],["duration"]=meta["duration"],["thumb"]="https://i.ytimg.com/vi/"+id+"/hqdefault.jpg",["position"]=Convert.ToInt64(row[1])});}
        return Json(new{results=cards});
    }
    [HttpGet, AllowAnonymous]
    [Route("yt/feed")]
    public async Task<ActionResult> Feed(string profile = "main", int n = 24, int page = 1,string session=null)
    {
        if(HomeMedia.Profiles.Enabled)profile=YtStore.ResolvePack(profile);
        else if (!YtStore.profRx.IsMatch(profile ?? "")) return Json(new { error = "bad_profile" }, 400);
        if(session?.Length>80)return Json(new{error="bad_session"},400);
        var (results, more) = await YtStore.FeedPage(profile, page < 1 ? 1 : page, Math.Clamp(n, 1, 40),session);
        return Json(new { results, page = page < 1 ? 1 : page, has_more = more, retry_page=more&&results.Count==0 });
    }

    [HttpPost, AllowAnonymous]
    [Route("yt/subs/import")]
    public async Task<ActionResult> Import(string profile = "main")
    {
        if(HomeMedia.Profiles.Enabled)profile=YtStore.ResolvePack(profile);
        else if (!YtStore.profRx.IsMatch(profile ?? "")) return Json(new { error = "bad_profile" }, 400);
        try
        {
            string body;
            using (var rd = new StreamReader(Request.Body, Encoding.UTF8)) body = await rd.ReadToEndAsync();
            var chs = JObject.Parse(body)["channels"] as JArray;
            if (chs == null || chs.Count == 0 || chs.Count > 5000) return Json(new { error = "bad_body" }, 400);
            return Json(new { ok = true, count = YtStore.ImportSubs(profile, chs) });
        }
        catch { return Json(new { error = "bad_body" }, 400); }
    }

    // заглушка аватара канала
    [HttpGet, AllowAnonymous]
    [Route("yt/avatar.svg")]
    public ActionResult Avatar()
        => ContentTo("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect width='100' height='100' fill='#2b2b2b'/><circle cx='50' cy='38' r='18' fill='#777'/><path d='M14 92c4-24 20-34 36-34s32 10 36 34z' fill='#777'/></svg>", "image/svg+xml");

    [HttpGet, AllowAnonymous]
    [Route("yt/dbg")]
    public async Task<ActionResult> Dbg(string url = null)
    {
        var h = await YtStore.PageHtml(url ?? "");
        var ago = YtStore.ParseAgo(h);
        return Json(new { len = h?.Length ?? -1, lockups = h == null ? 0 : Regex.Matches(h, "LOCKUP_CONTENT_TYPE_VIDEO").Count, naz = h == null ? 0 : Regex.Matches(h, "назад").Count, ago = ago.Count, ids = ago.Keys.Take(40), head = h == null ? "" : h.Substring(0, Math.Min(200, h.Length)) });
    }

    [HttpGet, AllowAnonymous]
    [Route("yt/status")]
    public async Task<ActionResult> Status()
    {
        string ver = (await YtStore.Run(15, "--version"))?.Trim();
        return Json(new { ytdlp = ver, proxy = NetworkProxy.Display(YtStore.Proxy) });
    }
}
