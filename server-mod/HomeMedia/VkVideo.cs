using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Публичный VK Видео: поиск, один MP4 не выше потолка, подписки на плейлисты. Эфиры и клипы не отдаём.
// Токен остаётся на сервере. Подписанный адрес файла в ленту и в журнал не пишется.
public static class VkStore
{
    const string Ua = "VKAndroidApp/1.166-53253 (Android 13; SDK 33; arm64-v8a)";
    const string Referer = "https://vkvideo.ru/";
    const string ClientId = "51452549";
    const string ApiVersion = "5.290";
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    static readonly SemaphoreSlim TokenGate = new(1, 1);
    static readonly SemaphoreSlim FeedGate = new(1, 1);
    static readonly Regex PairRx = new(@"\A(-?[1-9][0-9]*)_([1-9][0-9]*)\z", RegexOptions.Compiled);
    static readonly Regex ScriptRx = new(@"(/dist/[^""']+core_spa[^""']+\.js)", RegexOptions.Compiled);
    static readonly Regex BundleRx = new(@"let\{clientSecret:\w+,clientId:\w+,\w+:\w+\}=\(\w+=(\d+),\w+=""([^""]+)"",\w+=""(5\.\d+)""", RegexOptions.Compiled);
    static string token, clientId, apiVersion;
    static long refreshAt;
    static int feedRev;
    static JArray feedCache;
    static long feedAt;
    static string feedProfile;

    public static string Profile()
    {
        if (!Profiles.Enabled) return "main";
        return string.IsNullOrEmpty(Profiles.CurrentId) ? "main" : Profiles.CurrentId;
    }

    public static string ForUser(string id) => "vku_" + Profiles.Hash(id).Substring(0, 24);

    public static string Pack()
    {
        if (!Profiles.Enabled || Profiles.Current == null) return "main";
        string id = Profiles.CurrentId;
        string chosen = (string)Profiles.Get(id)?["settings"]?["vk_profile"];
        if (CanAssign(id, chosen)) return chosen;
        string own = ForUser(id);
        Ensure();
        if (HomeDb.Get("vk_pack_" + id) != "1")
        {
            HomeDb.With(c => HomeDb.Exec(c, "INSERT OR IGNORE INTO vk_subs(profile,album,title,at) SELECT @own,album,title,at FROM vk_subs WHERE profile=@id", ("@own", own), ("@id", id)));
            HomeDb.Set("vk_pack_" + id, "1");
            feedRev++;
        }
        AddPack(own);
        return own;
    }

    public static bool CanAssign(string lampaId, string name)
    {
        if (string.IsNullOrEmpty(lampaId) || string.IsNullOrEmpty(name)) return false;
        Ensure();
        if (HomeDb.With(c => HomeDb.Query(c, "SELECT 1 FROM vk_profiles WHERE name=@n", ("@n", name)).Count == 0)) return false;
        if (name == ForUser(lampaId)) return true;
        return name.StartsWith("vkp:" + lampaId + ":", StringComparison.Ordinal);
    }

    public static void AddPack(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        Ensure();
        HomeDb.With(c => HomeDb.Exec(c, "INSERT OR IGNORE INTO vk_profiles(name, at) VALUES(@n,@a)", ("@n", name), ("@a", HomeDb.Now())));
    }

    public static string CreatePack(string raw)
    {
        if (Profiles.Current == null) return null;
        string label = Regex.Replace((raw ?? "").Trim(), @"\s+", " ");
        if (label.Length < 1 || label.Length > 32 || label.IndexOfAny(new[] { '/', '?', '&', '#', '<', '>', '"', '\'', '\\', ':' }) >= 0) return null;
        string name = "vkp:" + Profiles.CurrentId + ":" + label;
        AddPack(name);
        return name;
    }

    public static JArray Packs()
    {
        if (!Profiles.Enabled || Profiles.Current == null) return new JArray { new JObject { ["name"] = "main", ["label"] = "свой", ["subs"] = Subs("main").Count, ["personal"] = true } };
        string personal = ForUser(Profiles.CurrentId);
        AddPack(personal);
        string current = Pack();
        string prefix = "vkp:" + Profiles.CurrentId + ":";
        var arr = new JArray();
        var seen = new HashSet<string>();
        void Put(string name, string label, bool mine)
        {
            if (!seen.Add(name)) return;
            arr.Add(new JObject { ["name"] = name, ["label"] = label, ["subs"] = Subs(name).Count, ["personal"] = mine });
        }
        Put(personal, "свой", true);
        if (current != personal && current.StartsWith(prefix, StringComparison.Ordinal)) Put(current, current.Substring(prefix.Length), false);
        foreach (var r in HomeDb.With(c => HomeDb.Query(c, "SELECT name FROM vk_profiles WHERE name LIKE @p ORDER BY at", ("@p", prefix + "%"))))
            Put((string)r[0], ((string)r[0]).Substring(prefix.Length), false);
        return arr;
    }

    static void Ensure()
    {
        HomeDb.With(c => {
            HomeDb.Exec(c, @"CREATE TABLE IF NOT EXISTS vk_subs (
            profile TEXT, album TEXT, title TEXT, at INTEGER, PRIMARY KEY(profile, album))");
            HomeDb.Exec(c, @"CREATE TABLE IF NOT EXISTS vk_seen (
            profile TEXT, video TEXT, at INTEGER, PRIMARY KEY(profile, video))");
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS vk_profiles (name TEXT PRIMARY KEY, at INTEGER)");
        });
    }

    public static bool TryPair(string raw, out long owner, out long id)
    {
        owner = 0; id = 0;
        var m = PairRx.Match((raw ?? "").Trim());
        if (!m.Success) return false;
        return long.TryParse(m.Groups[1].Value, out owner) && owner != 0
            && long.TryParse(m.Groups[2].Value, out id) && id > 0;
    }

    public static bool TryAlbumLink(string raw, out string album, out string title)
    {
        album = null; title = null;
        string s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Length > 300) return false;
        if (TryPair(s, out long owner, out long id)) { album = owner + "_" + id; return true; }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        if (uri.Host != "vkvideo.ru" && uri.Host != "vk.com" && uri.Host != "m.vk.com") return false;
        if (uri.AbsolutePath.IndexOf("playlist", StringComparison.OrdinalIgnoreCase) < 0) return false;
        var m = Regex.Match(uri.AbsolutePath, @"(-?[1-9][0-9]*)_([1-9][0-9]*)");
        if (!m.Success || !TryPair(m.Value, out owner, out id)) return false;
        album = owner + "_" + id;
        return true;
    }

    public static JArray Subs(string profile)
    {
        Ensure();
        var a = new JArray();
        foreach (var r in HomeDb.With(c => HomeDb.Query(c, "SELECT album, title, at FROM vk_subs WHERE profile=@p ORDER BY title COLLATE NOCASE", ("@p", profile))))
            a.Add(new JObject { ["id"] = r[0] as string, ["title"] = r[1] as string, ["at"] = Convert.ToInt64(r[2]) });
        return a;
    }

    static readonly Dictionary<string, (string thumb, long at)> Covers = new();

    public static async Task<JArray> Albums(string profile)
    {
        var list = Subs(profile);
        long now = HomeDb.Now();
        foreach (var album in list.Children<JObject>())
        {
            string id = (string)album["id"];
            if (string.IsNullOrEmpty(id)) continue;
            lock (Covers)
            {
                if (Covers.TryGetValue(id, out var hit) && now - hit.at < 15 * 60_000) { album["thumb"] = hit.thumb; continue; }
            }
            if (!TryPair(id, out long owner, out long aid)) continue;
            try
            {
                var cover = await Api("video.getAlbumById", new JObject { ["owner_id"] = owner, ["album_id"] = aid });
                string thumb = Thumb(cover);
                album["thumb"] = thumb;
                lock (Covers)
                {
                    if (Covers.Count > 200) Covers.Clear();
                    Covers[id] = (thumb, now);
                }
            }
            catch { album["thumb"] = ""; }
        }
        return list;
    }

    public static async Task<JObject> Add(string profile, string raw, string givenTitle)
    {
        if (!TryAlbumLink(raw, out string album, out _)) return null;
        if (!TryPair(album, out long owner, out long id)) return null;
        await Api("video.getFromAlbum", new JObject { ["owner_id"] = owner, ["album_id"] = id, ["count"] = 1, ["offset"] = 0 });
        string title = (givenTitle ?? "").Trim();
        if (title.Length == 0 || title.Length > 120) title = "Плейлист " + album;
        Ensure();
        HomeDb.With(c => HomeDb.Exec(c, @"INSERT INTO vk_subs(profile,album,title,at) VALUES(@p,@a,@t,@at)
            ON CONFLICT(profile,album) DO UPDATE SET title=@t",
            ("@p", profile), ("@a", album), ("@t", title), ("@at", HomeDb.Now())));
        feedRev++;
        return new JObject { ["id"] = album, ["title"] = title };
    }

    public static HashSet<string> Seen(string profile)
    {
        Ensure();
        var ids = new HashSet<string>();
        foreach (var r in HomeDb.With(c => HomeDb.Query(c, "SELECT video FROM vk_seen WHERE profile=@p", ("@p", profile))))
            if (r[0] is string id && id.Length > 0) ids.Add(id);
        return ids;
    }

    public static HashSet<string> Watched(string profile)
    {
        var ids = Seen(profile);
        foreach (var r in HomeDb.With(c => HomeDb.Query(c, "SELECT id,p,op FROM timeline WHERE profile_id=@profile AND id LIKE 'vk-%'")))
        {
            string id = r[0] as string ?? "";
            if (!id.StartsWith("vk-", StringComparison.Ordinal)) continue;
            string video = id.Substring(3);
            if (!TryPair(video, out _, out _)) continue;
            double pct = r[1] == null || r[1] is DBNull ? 0 : Convert.ToDouble(r[1]);
            string op = r[2] as string ?? "";
            if (op == "completed" || pct >= 90) ids.Add(video);
        }
        return ids;
    }

    public static void SetSeen(string profile, string video, bool on)
    {
        if (!TryPair(video, out _, out _)) return;
        Ensure();
        if (on) HomeDb.With(c => HomeDb.Exec(c, "INSERT INTO vk_seen(profile,video,at) VALUES(@p,@v,@at) ON CONFLICT(profile,video) DO UPDATE SET at=@at", ("@p", profile), ("@v", video), ("@at", HomeDb.Now())));
        else HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM vk_seen WHERE profile=@p AND video=@v", ("@p", profile), ("@v", video)));
    }

    public static void Remove(string profile, string album)
    {
        if (!TryPair(album, out _, out _)) return;
        Ensure();
        HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM vk_subs WHERE profile=@p AND album=@a", ("@p", profile), ("@a", album)));
        feedRev++;
    }

    public static string Target(string requested)
    {
        if (string.IsNullOrEmpty(requested)) return Pack();
        if (!Profiles.Enabled || Profiles.Current == null) return null;
        return CanAssign(Profiles.CurrentId, requested) ? requested : null;
    }

    public static bool RemovePack(string name)
    {
        if (Profiles.Current == null || Profiles.Current.Child) return false;
        if (name == ForUser(Profiles.CurrentId) || !CanAssign(Profiles.CurrentId, name)) return false;
        var cfg = Profiles.Get(Profiles.CurrentId)?["settings"] as JObject;
        bool clear = (string)cfg?["vk_profile"] == name;
        if (clear) cfg.Remove("vk_profile");
        Ensure();
        HomeDb.With(c =>
        {
            HomeDb.Exec(c, "DELETE FROM vk_subs WHERE profile=@p", ("@p", name));
            HomeDb.Exec(c, "DELETE FROM vk_profiles WHERE name=@p", ("@p", name));
            if (clear) Profiles.Exec(c, "UPDATE user_profiles SET settings=@j WHERE id=@id", ("@j", cfg.ToString(Formatting.None)), ("@id", Profiles.CurrentId));
        });
        feedRev++;
        return true;
    }

    public static async Task<JObject> Search(string q)
    {
        var response = await Api("catalog.getVideoSearch", new JObject { ["q"] = q });
        if (response == null) return new JObject { ["videos"] = new JArray(), ["albums"] = new JArray() };
        var seen = new HashSet<string>();
        var videos = new JArray();
        foreach (var item in VideoItems(response))
        {
            var card = VideoCard(item);
            if (card == null) continue;
            string id = (string)card["id"];
            if (!seen.Add(id)) continue;
            videos.Add(card);
            if (videos.Count >= 30) break;
        }
        var albums = new JArray();
        foreach (var a in (response["albums"] as JArray ?? new JArray()).Children<JObject>())
        {
            long owner = (long?)a["owner_id"] ?? 0, id = (long?)a["id"] ?? 0;
            if (owner == 0 || id <= 0) continue;
            albums.Add(new JObject { ["id"] = owner + "_" + id, ["title"] = (string)a["title"] ?? ("Плейлист " + owner + "_" + id), ["count"] = (int?)a["count"] ?? 0, ["thumb"] = Thumb(a) });
            if (albums.Count >= 12) break;
        }
        return new JObject { ["videos"] = videos, ["albums"] = albums };
    }

    public static async Task<JObject> Feed(string profile)
    {
        var rows = await RawFeed(profile);
        var seen = Watched(Profile());
        var fresh = new JArray(rows.Where(x => !seen.Contains((string)x["id"])).Take(40));
        return new JObject { ["results"] = fresh };
    }

    static async Task<List<JObject>> RawFeed(string profile)
    {
        int rev = feedRev;
        if (feedCache != null && feedProfile == profile && feedAt > 0 && rev == feedRev && HomeDb.Now() - feedAt < 15 * 60_000)
            return feedCache.Children<JObject>().ToList();
        await FeedGate.WaitAsync();
        try
        {
            if (feedCache != null && feedProfile == profile && HomeDb.Now() - feedAt < 15 * 60_000 && rev == feedRev)
                return feedCache.Children<JObject>().ToList();
            var rows = new List<JObject>();
            foreach (var sub in Subs(profile).Children<JObject>())
            {
                if (!TryPair((string)sub["id"], out long owner, out long album)) continue;
                string title = (string)sub["title"];
                try { rows.AddRange(await ReadAlbum(owner, album, (string)sub["id"], title, 3)); }
                catch { }
            }
            var fresh = new JArray(rows.GroupBy(x => (string)x["id"]).Select(g => g.First()).OrderByDescending(x => (long?)x["date"] ?? 0).Take(80));
            if (rev == feedRev) { feedCache = fresh; feedAt = HomeDb.Now(); feedProfile = profile; }
            return fresh.Children<JObject>().ToList();
        }
        finally { FeedGate.Release(); }
    }

    public static async Task<JObject> Album(string albumId)
    {
        if (!TryPair(albumId, out long owner, out long album)) return null;
        var rows = await ReadAlbum(owner, album, albumId, null, 4);
        var seen = Watched(Profile());
        foreach (var card in rows) card["seen"] = seen.Contains((string)card["id"]);
        return new JObject { ["id"] = owner + "_" + album, ["results"] = new JArray(rows) };
    }

    static async Task<List<JObject>> ReadAlbum(long owner, long album, string albumId, string title, int pages)
    {
        var rows = new List<JObject>();
        int offset = 0;
        for (int page = 0; page < pages; page++)
        {
            var response = await Api("video.getFromAlbum", new JObject { ["owner_id"] = owner, ["album_id"] = album, ["count"] = 50, ["offset"] = offset });
            var items = response?["items"] as JArray ?? new JArray();
            int count = (int?)response?["count"] ?? items.Count;
            foreach (var item in items.Children<JObject>())
            {
                var card = VideoCard(item);
                if (card == null) continue;
                card["album"] = albumId;
                if (!string.IsNullOrEmpty(title)) card["album_title"] = title;
                rows.Add(card);
            }
            offset += items.Count;
            if (items.Count == 0 || items.Count < 50 || offset >= count) break;
        }
        return rows;
    }

    public static async Task<JObject> Play(string id, int maxh)
    {
        if (!TryPair(id, out long owner, out long video)) return null;
        int cap = maxh <= 0 ? 1080 : maxh;
        var response = await Api("video.getByIds", new JObject { ["videos"] = owner + "_" + video, ["video_fields"] = "files,subtitles" });
        var item = (response?["items"] as JArray)?.Children<JObject>().Select(Clip).FirstOrDefault();
        if (item == null) return new JObject { ["error"] = "not_found" };
        if (Skip(item)) return new JObject { ["error"] = "not_supported" };
        var files = item["files"] as JObject;
        if (files == null) return new JObject { ["error"] = "no_supported_mp4" };
        int best = 0; string url = null;
        foreach (var p in files.Properties())
        {
            var m = Regex.Match(p.Name, @"\Amp4_(\d+)\z");
            if (!m.Success || p.Value.Type != JTokenType.String) continue;
            int h = int.Parse(m.Groups[1].Value);
            if (h <= cap && h > best) { best = h; url = (string)p.Value; }
        }
        if (url == null) return new JObject { ["error"] = "no_supported_mp4" };
        return new JObject
        {
            ["source"] = "vk",
            ["id"] = owner + "_" + video,
            ["title"] = (string)item["title"] ?? "VK Видео",
            ["video"] = url,
            ["height"] = best,
            ["duration"] = (int?)item["duration"] ?? 0,
            ["headers"] = new JObject { ["User-Agent"] = Ua, ["Referer"] = Referer }
        };
    }

    static string Thumb(JObject item)
    {
        if (item?["image"] is not JArray images) return "";
        string best = "", fallback = "";
        int bestScore = int.MaxValue, fallbackArea = -1;
        foreach (var image in images.Children<JObject>())
        {
            string url = (string)image["url"];
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.Ordinal)) continue;
            int w = (int?)image["width"] ?? 0, h = (int?)image["height"] ?? 0;
            int area = w > 0 && h > 0 ? w * h : 1;
            if (area > fallbackArea) { fallbackArea = area; fallback = url; }
            int score = w >= 640 && w <= 1280 ? w : 10000 + Math.Abs((w == 0 ? 4000 : w) - 960);
            if (score < bestScore) { bestScore = score; best = url; }
        }
        return string.IsNullOrEmpty(best) ? fallback : best;
    }

    public static async Task<(byte[] bytes, string type)> Poster(string encoded)
    {
        string raw;
        try
        {
            string s = (encoded ?? "").Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch { throw new InvalidOperationException("vk_http"); }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidOperationException("vk_http");
        string host = uri.IdnHost;
        if (host != "userapi.com" && !host.EndsWith(".userapi.com") && host != "vkuser.net" && !host.EndsWith(".vkuser.net") && host != "vkuserphoto.ru" && !host.EndsWith(".vkuserphoto.ru"))
            throw new InvalidOperationException("vk_http");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        Stamp(req);
        using var res = await Http.SendAsync(req);
        if ((int)res.StatusCode != 200) throw new InvalidOperationException("vk_http");
        string type = res.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.StartsWith("image/", StringComparison.Ordinal)) throw new InvalidOperationException("vk_http");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0 || bytes.Length > 4000000) throw new InvalidOperationException("vk_http");
        return (bytes, type);
    }

    static JObject Clip(JObject item)
    {
        if (item?["video"] is JObject inner && (inner["owner_id"] != null || inner["id"] != null)) return inner;
        return item;
    }

    static IEnumerable<JObject> VideoItems(JObject response)
    {
        foreach (var v in (response["videos"] as JArray ?? new JArray()).Children<JObject>()) yield return Clip(v);
        foreach (var wrap in (response["catalog_videos"] as JArray ?? new JArray()).Children<JObject>()) yield return Clip(wrap);
    }

    static bool Skip(JObject item)
    {
        string type = (string)item["type"] ?? "video";
        if (type == "live" || type == "short_video") return true;
        if (item["restriction"] != null && item["restriction"].Type != JTokenType.Null) return true;
        if ((bool?)item["live"] == true || (int?)item["live"] == 1) return true;
        if (item["player"] != null && (string)item["player"] == "external") return true;
        return false;
    }

    static JObject VideoCard(JObject item)
    {
        item = Clip(item);
        if (item == null || Skip(item)) return null;
        long owner = (long?)item["owner_id"] ?? 0, id = (long?)item["id"] ?? 0;
        if (owner == 0 || id <= 0) return null;
        string thumb = Thumb(item);
        return new JObject
        {
            ["source"] = "vk",
            ["provider"] = "vk",
            ["id"] = owner + "_" + id,
            ["title"] = (string)item["title"] ?? "VK Видео",
            ["duration"] = (int?)item["duration"] ?? 0,
            ["date"] = (long?)item["date"] ?? 0,
            ["thumb"] = thumb
        };
    }

    static async Task<JObject> Api(string method, JObject fields)
    {
        var tok = await Token();
        var form = new Dictionary<string, string> { ["access_token"] = tok.Token, ["device_id"] = DeviceId(), ["client_id"] = tok.Client };
        foreach (var p in fields.Properties()) form[p.Name] = p.Value.Type == JTokenType.Integer ? p.Value.ToString() : (string)p.Value ?? "";
        var deadline = DateTime.UtcNow.AddSeconds(8);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("rate_limited");
            var uri = new Uri("https://api.vkvideo.ru/method/" + method + "?v=" + Uri.EscapeDataString(tok.Version) + "&client_id=" + Uri.EscapeDataString(tok.Client));
            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
            Stamp(req);
            using var res = await Http.SendAsync(req);
            int code = (int)res.StatusCode;
            if (code == 429 || code == 503)
            {
                if (attempt == 2 || DateTime.UtcNow.AddSeconds(2) >= deadline) throw new InvalidOperationException("rate_limited");
                await Task.Delay(1000);
                continue;
            }
            if (code != 200) throw new InvalidOperationException("vk_http");
            string body = await Read(res, 1024 * 1024);
            var json = JObject.Parse(body);
            if (json["error"] is JObject err)
            {
                int c = (int?)err["error_code"] ?? 0;
                if (c == 5) { token = null; throw new InvalidOperationException("vk_auth"); }
                if (c == 6 || c == 9 || c == 29) throw new InvalidOperationException("rate_limited");
                throw new InvalidOperationException("vk_api");
            }
            return json["response"] as JObject;
        }
        throw new InvalidOperationException("rate_limited");
    }

    sealed record Tok(string Token, string Client, string Version);

    static string DeviceId()
    {
        string id = HomeDb.Get("vk_device_id");
        if (!string.IsNullOrEmpty(id) && id.Length >= 16 && id.Length <= 64 && id.All(c => char.IsLetterOrDigit(c))) return id;
        id = Guid.NewGuid().ToString("N");
        HomeDb.Set("vk_device_id", id);
        return id;
    }

    static async Task<Tok> Token()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(token) && now < refreshAt) return new Tok(token, ClientId, ApiVersion);
        await TokenGate.WaitAsync();
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!string.IsNullOrEmpty(token) && now < refreshAt) return new Tok(token, ClientId, ApiVersion);
            var form = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["api_id"] = ClientId,
                ["device_id"] = DeviceId(),
                ["v"] = ApiVersion,
                ["https"] = "1",
                ["lang"] = "ru"
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.vkvideo.ru/method/auth.getAnonymToken") { Content = new FormUrlEncodedContent(form) };
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            using var res = await Http.SendAsync(req);
            if ((int)res.StatusCode != 200) throw new InvalidOperationException("vk_auth");
            var data = JObject.Parse(await Read(res, 64 * 1024));
            if (data["error"] != null) throw new InvalidOperationException("vk_auth");
            var box = data["response"] as JObject;
            string access = (string)box?["token"];
            long exp = (long?)box?["expired_at"] ?? 0;
            if (string.IsNullOrEmpty(access) || exp <= now + 60) throw new InvalidOperationException("vk_auth");
            token = access;
            clientId = ClientId;
            apiVersion = ApiVersion;
            refreshAt = exp - 60;
            return new Tok(token, clientId, apiVersion);
        }
        finally { TokenGate.Release(); }
    }

    static void Stamp(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Headers.TryAddWithoutValidation("Referer", Referer);
        req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
    }

    static async Task<string> GetText(string url, int max)
    {
        for (int hop = 0; hop < 4; hop++)
        {
            var here = new Uri(url);
            if (here.Scheme != "https" || (here.Host != "vkvideo.ru" && here.Host != "vk.com" && here.Host != "login.vk.com" && here.Host != "api.vkvideo.ru"))
                throw new InvalidOperationException("vk_http");
            using var req = new HttpRequestMessage(HttpMethod.Get, here);
            Stamp(req);
            using var res = await Http.SendAsync(req);
            int code = (int)res.StatusCode;
            if (code >= 300 && code < 400)
            {
                var loc = res.Headers.Location;
                if (loc == null) throw new InvalidOperationException("vk_http");
                var next = loc.IsAbsoluteUri ? loc : new Uri(here, loc);
                if (next.Host != here.Host) throw new InvalidOperationException("vk_http");
                url = next.ToString();
                continue;
            }
            if (code != 200) throw new InvalidOperationException("vk_http");
            return await Read(res, max);
        }
        throw new InvalidOperationException("vk_http");
    }

    static async Task<string> Read(HttpResponseMessage res, int max)
    {
        var buf = new byte[max];
        using var stream = await res.Content.ReadAsStreamAsync();
        int n = 0, read;
        while (n < buf.Length && (read = await stream.ReadAsync(buf, n, buf.Length - n)) > 0) n += read;
        if (n == buf.Length) throw new InvalidOperationException("too_large");
        return Encoding.UTF8.GetString(buf, 0, n);
    }
}

[ProfileResult]
public class VkController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    static ActionResult Fail(Exception ex)
    {
        string name = ex.Message;
        if (name == "rate_limited") return Json(new { error = "rate_limited" }, 429);
        if (name == "vk_auth") return Json(new { error = "vk_auth" }, 502);
        return Json(new { error = "vk_unavailable" }, 502);
    }

    [HttpGet, AllowAnonymous]
    [Route("vk.js")]
    public ActionResult Plugin()
    {
        string dir = string.IsNullOrEmpty(ModInit.modpath) ? "/opt/lampac/mods/HomeMedia" : ModInit.modpath;
        string js = System.IO.File.ReadAllText(Path.Combine(dir, "vk.js")).Replace("{localhost}", host);
        return ContentTo(js, "application/javascript; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/search")]
    public async Task<ActionResult> Search(string q = null, string kind = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!ProfilePolicy.SourceEnabled("vk")) return Json(new { error = "source_disabled", source = "vk" }, 403);
        q = (q ?? "").Trim();
        if (q.Length < 2 || q.Length > 120) return Json(new { error = "bad_q" }, 400);
        try
        {
            var found = await VkStore.Search(q);
            if (kind == "video") return Json(new { results = found["videos"] });
            if (kind == "album") return Json(new { results = found["albums"] });
            return Json(found);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/p")]
    public async Task<ActionResult> Poster(string u = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        try
        {
            var (bytes, type) = await VkStore.Poster(u);
            return File(bytes, type);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/video")]
    public async Task<ActionResult> Video(string id = null, int maxh = 0)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!ProfilePolicy.SourceEnabled("vk")) return Json(new { error = "source_disabled", source = "vk" }, 403);
        try
        {
            var v = await VkStore.Play(id, Math.Clamp(maxh, 0, 4320));
            if (v == null) return Json(new { error = "bad_id" }, 400);
            if (v["error"] != null) return Json(v, 404);
            return Json(v);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/album")]
    public async Task<ActionResult> Album(string id = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!ProfilePolicy.SourceEnabled("vk")) return Json(new { error = "source_disabled", source = "vk" }, 403);
        try
        {
            var page = await VkStore.Album(id);
            return page == null ? Json(new { error = "bad_album" }, 400) : Json(page);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/feed")]
    public async Task<ActionResult> Feed()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!ProfilePolicy.SourceEnabled("vk")) return Json(new { error = "source_disabled", source = "vk" }, 403);
        try { return Json(await VkStore.Feed(VkStore.Pack())); }
        catch (Exception ex) { return Fail(ex); }
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/progress")]
    public ActionResult Progress(string id = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!ProfilePolicy.SourceEnabled("vk")) return Json(new { error = "source_disabled", source = "vk" }, 403);
        if (!VkStore.TryPair(id, out _, out _)) return Json(new { error = "bad_id" }, 400);
        var row = HomeDb.With(c => HomeDb.Query(c, "SELECT t,d,p,op FROM timeline WHERE profile_id=@profile AND id=@id", ("@id", "vk-" + id))).FirstOrDefault();
        if (row == null) return Json(new { position = 0 });
        bool done = (row[3] as string) == "completed";
        return Json(new { position = done ? 0 : Convert.ToInt64(row[0]), duration = Convert.ToInt64(row[1]), percent = Convert.ToDouble(row[2]) });
    }

    [HttpPost, AllowAnonymous]
    [Route("vk/seen")]
    public ActionResult Seen(string id = null, int on = 1)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!ProfilePolicy.SourceEnabled("vk")) return Json(new { error = "source_disabled", source = "vk" }, 403);
        if (!VkStore.TryPair(id, out _, out _)) return Json(new { error = "bad_id" }, 400);
        VkStore.SetSeen(VkStore.Profile(), id, on != 0);
        return Json(new { ok = true, id, seen = on != 0 });
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/profiles")]
    public ActionResult Profiles(string add = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (!string.IsNullOrEmpty(add))
        {
            if (HomeMedia.Profiles.Current == null) return Json(new { error = "session_required" }, 401);
            if (HomeMedia.Profiles.Current.Child) return Json(new { error = "adult_required" }, 403);
            string created = VkStore.CreatePack(add);
            if (created == null) return Json(new { error = "bad_name" }, 400);
        }
        return Json(new { profiles = VkStore.Packs(), current = VkStore.Pack() });
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/profiles/remove")]
    public ActionResult RemoveProfile(string name = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (HomeMedia.Profiles.Current == null) return Json(new { error = "session_required" }, 401);
        return VkStore.RemovePack(name) ? Json(new { ok = true }) : Json(new { error = "bad_name" }, 400);
    }

    [HttpGet, AllowAnonymous]
    [Route("vk/subs")]
    public async Task<ActionResult> List(string profile = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        string pack = VkStore.Target(profile);
        if (pack == null) return Json(new { error = "bad_vk_profile" }, 400);
        try { return Json(new { albums = await VkStore.Albums(pack) }); }
        catch (Exception ex) { return Fail(ex); }
    }

    [HttpPost, AllowAnonymous]
    [Route("vk/subs/add")]
    public async Task<ActionResult> Add(string profile = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        string pack = VkStore.Target(profile);
        if (pack == null) return Json(new { error = "bad_vk_profile" }, 400);
        string body;
        using (var rd = new StreamReader(Request.Body, Encoding.UTF8)) body = await rd.ReadToEndAsync();
        JObject o;
        try { o = JObject.Parse(body); } catch { return Json(new { error = "bad_body" }, 400); }
        string raw = (string)o["url"] ?? (string)o["id"];
        try
        {
            var saved = await VkStore.Add(pack, raw, (string)o["title"]);
            return saved == null ? Json(new { error = "bad_album" }, 400) : Json(saved);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [HttpPost, AllowAnonymous]
    [Route("vk/subs/remove")]
    public ActionResult Remove(string id = null, string profile = null)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        string pack = VkStore.Target(profile);
        if (pack == null) return Json(new { error = "bad_vk_profile" }, 400);
        VkStore.Remove(pack, id);
        return Json(new { ok = true });
    }
}
