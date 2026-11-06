using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HomeMedia;

// All personal stores use the immutable request/playback owner. Never a global "last chosen" user.
public static class Profiles
{
    public const string MainId = "u_default";
    public sealed record Scope(string Id, string Device, string Boot, string TokenHash, bool Admin, bool Child, int MaxAge, long PolicyRev);
    static readonly AsyncLocal<Scope> current = new();
    public static void Activate(){HomeDb.Set("user_profiles_enabled","1");Enabled=true;}
    public static Scope Current => current.Value;
    public static string CurrentId => current.Value?.Id ?? MainId;
    public static bool Enabled { get; private set; }
    public static bool IsTest => Environment.GetEnvironmentVariable("HOME_PROFILE_TEST") == "1";
    public static void SetScope(Scope scope) => current.Value = scope;
    sealed class Restore : IDisposable { readonly Scope old; public Restore(Scope value) { old = current.Value; current.Value = value; } public void Dispose() => current.Value = old; }
    public static IDisposable Enter(Scope scope) => new Restore(scope);

    internal static int Exec(SqliteConnection c, string sql, params (string,object)[] args)
    {
        using var q = c.CreateCommand(); q.CommandText = sql;
        foreach (var (key,value) in args) q.Parameters.AddWithValue(key,value ?? DBNull.Value);
        return q.ExecuteNonQuery();
    }
    internal static List<object[]> Rows(SqliteConnection c, string sql, params (string,object)[] args)
    {
        using var q = c.CreateCommand(); q.CommandText = sql;
        foreach (var (key,value) in args) q.Parameters.AddWithValue(key,value ?? DBNull.Value);
        using var r = q.ExecuteReader(); var result = new List<object[]>();
        while (r.Read()) { var row = new object[r.FieldCount]; r.GetValues(row); result.Add(row); }
        return result;
    }
    static readonly Dictionary<string,string> personal = new()
    {
        ["timeline"] = "CREATE TABLE IF NOT EXISTS timeline(h TEXT PRIMARY KEY,c TEXT,t INTEGER,d INTEGER,p REAL,u INTEGER,s INTEGER,id TEXT,event TEXT,session TEXT,op TEXT)",
        ["audio"] = "CREATE TABLE IF NOT EXISTS audio(k TEXT PRIMARY KEY,label TEXT,lang TEXT,at INTEGER)",
        ["watchlist"] = "CREATE TABLE IF NOT EXISTS watchlist(media TEXT,tmdb INTEGER,at INTEGER,PRIMARY KEY(media,tmdb))",
        ["hidden"] = "CREATE TABLE IF NOT EXISTS hidden(k TEXT PRIMARY KEY,at INTEGER)",
        ["bookmarks"] = "CREATE TABLE IF NOT EXISTS bookmarks(cat TEXT,k TEXT,card TEXT,u INTEGER,s INTEGER,deleted INTEGER DEFAULT 0,PRIMARY KEY(cat,k))",
        ["anime_last"] = "CREATE TABLE IF NOT EXISTS anime_last(card TEXT PRIMARY KEY,source TEXT,voice TEXT,season INTEGER,ep INTEGER,at INTEGER)",
        ["tracked"] = "CREATE TABLE IF NOT EXISTS tracked(tmdb INTEGER PRIMARY KEY,at INTEGER)",
        ["lastplay"] = "CREATE TABLE IF NOT EXISTS lastplay(card TEXT PRIMARY KEY,j TEXT,at INTEGER)",
        ["lastplay_gone"] = "CREATE TABLE IF NOT EXISTS lastplay_gone(card TEXT PRIMARY KEY,at INTEGER)"
    };

    // Run on a backed-up candidate database before enabling profiles. DDL + data movement are atomic.
    public static void EnsureSchema(SqliteConnection c)
    {
        TlV2.EnsureSchema(c);
        using var tx = c.BeginTransaction();
        foreach (var (table,ddl) in personal)
        {
            Exec(c,ddl);
            var info = Rows(c,"PRAGMA table_info("+table+")");
            if (info.Any(r => (string)r[1] == "profile_id")) continue;
            var cols = info.Select(r => (string)r[1]).ToArray();
            var keys = info.Where(r => Convert.ToInt32(r[5])>0).OrderBy(r => Convert.ToInt32(r[5])).Select(r => (string)r[1]).ToArray();
            if (keys.Length == 0) throw new InvalidOperationException("Missing personal key: "+table);
            string sql = (string)Rows(c,"SELECT sql FROM sqlite_master WHERE type='table' AND name=@n",("@n",table))[0][0];
            sql = Regex.Replace(sql,@"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?"+Regex.Escape(table)+@"\b","CREATE TABLE "+table+"_profiles_new",RegexOptions.IgnoreCase);
            sql = Regex.Replace(sql,@",?\s*PRIMARY\s+KEY\s*\([^)]*\)","",RegexOptions.IgnoreCase);
            sql = Regex.Replace(sql,@"\s+PRIMARY\s+KEY\b","",RegexOptions.IgnoreCase);
            int close = sql.LastIndexOf(')');
            sql = sql.Substring(0,close)+",profile_id TEXT NOT NULL DEFAULT '"+MainId+"',PRIMARY KEY(profile_id,"+string.Join(",",keys)+"))";
            Exec(c,sql);
            var names = string.Join(",",cols.Select(n => "\""+n+"\""));
            Exec(c,"INSERT INTO "+table+"_profiles_new("+names+",profile_id) SELECT "+names+",@p FROM "+table,("@p",MainId));
            long before = Convert.ToInt64(Rows(c,"SELECT COUNT(*) FROM "+table)[0][0]);
            long after = Convert.ToInt64(Rows(c,"SELECT COUNT(*) FROM "+table+"_profiles_new")[0][0]);
            if (before != after) throw new InvalidOperationException("Profile migration row loss: "+table);
            Exec(c,"DROP TABLE "+table); Exec(c,"ALTER TABLE "+table+"_profiles_new RENAME TO "+table);
        }
        Exec(c,"CREATE INDEX IF NOT EXISTS ix_timeline_profile_s ON timeline(profile_id,s)");
        Exec(c,"CREATE INDEX IF NOT EXISTS ix_bookmarks_profile_s ON bookmarks(profile_id,s)");
        if (!Rows(c,"PRAGMA table_info(tl_journal)").Any(r => (string)r[1]=="profile_id"))
            Exec(c,"ALTER TABLE tl_journal ADD COLUMN profile_id TEXT NOT NULL DEFAULT '"+MainId+"'");
        Exec(c,@"CREATE TABLE IF NOT EXISTS user_profiles(id TEXT PRIMARY KEY,name TEXT NOT NULL,is_child INTEGER NOT NULL DEFAULT 0,max_age INTEGER NOT NULL DEFAULT 18,pin TEXT,is_admin INTEGER NOT NULL DEFAULT 0,policy_rev INTEGER NOT NULL DEFAULT 1,settings TEXT NOT NULL DEFAULT '{}',archived INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS play_grants(token_hash TEXT PRIMARY KEY,profile_id TEXT,device TEXT,boot TEXT,identities TEXT,expires INTEGER);
CREATE TABLE IF NOT EXISTS user_sessions(token_hash TEXT PRIMARY KEY,profile_id TEXT NOT NULL,device TEXT NOT NULL,boot TEXT NOT NULL,expires INTEGER NOT NULL,revoked INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS ix_user_sessions_device ON user_sessions(device,boot);
CREATE TABLE IF NOT EXISTS profile_rows(profile_id TEXT NOT NULL,row_id TEXT NOT NULL,enabled INTEGER NOT NULL,sort_order INTEGER NOT NULL,PRIMARY KEY(profile_id,row_id));
CREATE TABLE IF NOT EXISTS view_preferences(profile_id TEXT NOT NULL,content_id TEXT NOT NULL,j TEXT NOT NULL,at INTEGER NOT NULL,PRIMARY KEY(profile_id,content_id));
CREATE TABLE IF NOT EXISTS content_items(id TEXT PRIMARY KEY,kind TEXT,override_kind TEXT,rating TEXT,min_age INTEGER,meta_rev INTEGER NOT NULL DEFAULT 1,j TEXT,at INTEGER);
CREATE TABLE IF NOT EXISTS content_refs(provider TEXT,ref_kind TEXT,scope TEXT NOT NULL DEFAULT '',external_id TEXT,content_id TEXT,mapping TEXT,PRIMARY KEY(provider,ref_kind,scope,external_id));
CREATE TABLE IF NOT EXISTS memberships(profile_id TEXT,content_id TEXT,collection TEXT,active INTEGER NOT NULL,origin TEXT,at INTEGER,event TEXT,revision INTEGER NOT NULL,PRIMARY KEY(profile_id,content_id,collection));
CREATE TABLE IF NOT EXISTS profile_rules(profile_id TEXT,kind TEXT,subject TEXT,decision TEXT,at INTEGER,PRIMARY KEY(profile_id,kind,subject));
CREATE TABLE IF NOT EXISTS progress_aliases(profile_id TEXT,hash TEXT,card TEXT,id TEXT,PRIMARY KEY(profile_id,hash,card));
CREATE TABLE IF NOT EXISTS kp_mirror_state(account TEXT,destination TEXT,content_id TEXT,remote_id TEXT,desired INTEGER,observed INTEGER,revision INTEGER NOT NULL DEFAULT 1,confirmed_revision INTEGER NOT NULL DEFAULT 0,retry_at INTEGER,attempts INTEGER NOT NULL DEFAULT 0,managed INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(account,destination,content_id));
CREATE TABLE IF NOT EXISTS release_events(profile_id TEXT NOT NULL,media TEXT NOT NULL,tmdb INTEGER NOT NULL,type TEXT NOT NULL,tier INTEGER NOT NULL,event_at INTEGER NOT NULL,PRIMARY KEY(profile_id,media,tmdb));
CREATE TABLE IF NOT EXISTS release_episodes(profile_id TEXT NOT NULL,media TEXT NOT NULL,tmdb INTEGER NOT NULL,season INTEGER NOT NULL,episode INTEGER NOT NULL,has_ru INTEGER NOT NULL DEFAULT 0,best_tier INTEGER NOT NULL DEFAULT -1,best_type TEXT NOT NULL DEFAULT '',seeders INTEGER NOT NULL DEFAULT 0,checked_at INTEGER NOT NULL,PRIMARY KEY(profile_id,media,tmdb,season,episode));");
        // Existing installations may already have the older release_episodes table.
        try{Exec(c,"ALTER TABLE release_episodes ADD COLUMN best_tier INTEGER NOT NULL DEFAULT -1");}catch{}
        try{Exec(c,"ALTER TABLE release_episodes ADD COLUMN best_type TEXT NOT NULL DEFAULT ''");}catch{}
        try{Exec(c,"ALTER TABLE release_episodes ADD COLUMN seeders INTEGER NOT NULL DEFAULT 0");}catch{}
        ProfilePolicy.EnsureAgeColumns(c);
        Exec(c,"INSERT OR IGNORE INTO user_profiles(id,name,is_admin) VALUES(@id,@name,1)",("@id",MainId),("@name","Основной"));
        Exec(c,"INSERT OR IGNORE INTO kv(k,v) VALUES('user_default_profile',@id)",("@id",MainId));
        Exec(c,"INSERT OR IGNORE INTO kv(k,v) VALUES('user_cloud_import_profile',@id)",("@id",MainId));
        Exec(c,"INSERT OR IGNORE INTO kv(k,v) VALUES('user_profiles_enabled','0')");
        Exec(c,"INSERT OR IGNORE INTO kv(k,v) VALUES('user_server_id',@id)",("@id",Guid.NewGuid().ToString("N")));
        Exec(c,"INSERT OR IGNORE INTO content_items(id,kind,override_kind) VALUES('tmdb:tv:235930','series','anime_series')");
        Exec(c,"CREATE UNIQUE INDEX IF NOT EXISTS ix_timeline_profile_identity ON timeline(profile_id,id) WHERE id IS NOT NULL AND id<>''");
        HistoryStore.Ensure(c);
        tx.Commit();

    }

    internal static void LoadEnabled(SqliteConnection c)
    {
        Enabled = Environment.GetEnvironmentVariable("HOME_PROFILES_ENABLED")=="1" || (Rows(c,"SELECT v FROM kv WHERE k='user_profiles_enabled'").FirstOrDefault()?[0] as string)=="1";
    }
    public static JObject Get(string id) => HomeDb.With(c =>
    {
        var r=Rows(c,"SELECT id,name,is_child,max_age,pin,is_admin,policy_rev,settings,archived FROM user_profiles WHERE id=@id",("@id",id)).FirstOrDefault();
        return r==null?null:new JObject { ["id"]=(string)r[0],["name"]=(string)r[1],["is_child"]=Convert.ToInt64(r[2])!=0,["max_age"]=Convert.ToInt32(r[3]),["pin_hash"]=r[4] as string,["is_admin"]=Convert.ToInt64(r[5])!=0,["policy_rev"]=Convert.ToInt64(r[6]),["settings"]=JObject.Parse((string)r[7]),["archived"]=Convert.ToInt64(r[8])!=0 };
    });
    public static JObject Public(JObject p) { if(p==null)return null;var o=(JObject)p.DeepClone();o["has_pin"]=!string.IsNullOrEmpty((string)o["pin_hash"]);o.Remove("pin_hash");
        var rows=new JObject();foreach(var r in HomeDb.With(c=>Rows(c,"SELECT row_id,enabled,sort_order FROM profile_rows WHERE profile_id=@p",("@p",(string)p["id"]))))rows[(string)r[0]]=new JObject{["enabled"]=Convert.ToInt64(r[1])!=0,["order"]=Convert.ToInt32(r[2])};o["rows"]=rows;return o; }
    public static JArray List() => new(HomeDb.With(c => Rows(c,"SELECT id FROM user_profiles WHERE archived=0 ORDER BY rowid")).Select(r => Public(Get((string)r[0]))));
    public static string DefaultId => HomeDb.Get("user_default_profile") ?? MainId;
    public static string ImportId => HomeDb.Get("user_cloud_import_profile") ?? MainId;
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    static readonly ConcurrentDictionary<string,(int n,long until)> failures = new();
    static readonly object[] pinLocks = System.Linq.Enumerable.Range(0,64).Select(_=>new object()).ToArray();
    public static bool CheckPin(JObject target,string pin,string device,string remoteIp=null)
    {
        // Ключ — серверно-наблюдаемый IP; без него — константа "unknown", но НЕ клиентский device
        // (иначе смена device в теле запроса снова обнуляет лимит).
        string key=(string.IsNullOrEmpty(remoteIp)?"unknown":remoteIp)+":"+(string)target["id"];long now=HomeDb.Now();
        bool grant;
        // Под замком только быстрый резерв попытки (check-then-act закрыт); PBKDF2 выполняется вне замка,
        // чтобы массовая ротация ключей не зажимала общие слоты дорогой проверкой.
        lock(pinLocks[(key.GetHashCode()&0x7fffffff)%pinLocks.Length]){
            if(failures.TryGetValue(key,out var x)){
                if(x.until>now&&x.n>=5){grant=false;}
                else{
                    if(x.until>0&&x.until<=now)failures.TryRemove(key,out _); // окно истекло — счёт заново
                    int n=(failures.TryGetValue(key,out var c)?c.n:0)+1;
                    failures[key]=(n,n>=5?now+300_000:0L);
                    grant=n<=5;
                }
            }else{failures[key]=(1,0L);grant=true;}
            // ограничение роста словаря: иначе ротация ключей копит записи без границы
            if(failures.Count>4096)foreach(var k in failures.Where(p=>p.Value.until<=now).Select(p=>p.Key).ToList())failures.TryRemove(k,out _);
        }
        if(!grant)return false;
        string stored=(string)target["pin_hash"];
        bool ok=string.IsNullOrEmpty(stored)?string.IsNullOrEmpty(pin):VerifyPin(stored,pin);
        if(ok)failures.TryRemove(key,out _);
        return ok;
    }
    public static string PinHash(string pin)
    {
        if(string.IsNullOrEmpty(pin))return null;
        if(!Regex.IsMatch(pin,@"^\d{4,8}$"))throw new ArgumentException("PIN must have 4-8 digits");
        var salt=RandomNumberGenerator.GetBytes(16);const int rounds=600000;
        var h=Rfc2898DeriveBytes.Pbkdf2(pin,salt,rounds,HashAlgorithmName.SHA256,32);
        return rounds+":"+Convert.ToBase64String(salt)+":"+Convert.ToBase64String(h);
    }
    static bool VerifyPin(string stored,string pin)
    {
        try { var a=stored.Split(':');var h=Rfc2898DeriveBytes.Pbkdf2(pin??"",Convert.FromBase64String(a[1]),int.Parse(a[0]),HashAlgorithmName.SHA256,32);return CryptographicOperations.FixedTimeEquals(h,Convert.FromBase64String(a[2])); } catch { return false; }
    }
    public static (string token,Scope scope) CreateSession(string profile,string device,string boot,string replaceHash=null)
    {
        var p=Get(profile);if(p==null||(bool)p["archived"])throw new ArgumentException("unknown_profile");
        string token=NewToken(),hash=Hash(token);
        HomeDb.With(c=> {
            using var tx=c.BeginTransaction();
            if(replaceHash!=null&&Exec(c,"UPDATE user_sessions SET revoked=1 WHERE token_hash=@h AND revoked=0",("@h",replaceHash))!=1)throw new InvalidOperationException("session_changed");
            Exec(c,"UPDATE user_sessions SET revoked=1 WHERE device=@d AND revoked=0",("@d",device));
            Exec(c,"INSERT INTO user_sessions(token_hash,profile_id,device,boot,expires) VALUES(@h,@p,@d,@b,@x)",("@h",hash),("@p",profile),("@d",device),("@b",boot),("@x",HomeDb.Now()+24L*3600_000));
            tx.Commit();
        });
        return(token,new Scope(profile,device,boot,hash,(bool)p["is_admin"],(bool)p["is_child"],(int)p["max_age"],(long)p["policy_rev"]));
    }
    public static Scope FromToken(string token)
    {
        if(string.IsNullOrWhiteSpace(token)||token.Length!=64)return null;
        string h=Hash(token);
        return HomeDb.With(c=> {
            var r=Rows(c,"SELECT s.profile_id,s.device,s.boot,p.is_admin,p.is_child,p.max_age,p.policy_rev FROM user_sessions s JOIN user_profiles p ON p.id=s.profile_id WHERE s.token_hash=@h AND s.revoked=0 AND s.expires>@n AND p.archived=0",("@h",h),("@n",HomeDb.Now())).FirstOrDefault();
            return r==null?null:new Scope((string)r[0],(string)r[1],(string)r[2],h,Convert.ToInt64(r[3])!=0,Convert.ToInt64(r[4])!=0,Convert.ToInt32(r[5]),Convert.ToInt64(r[6]));
        });
    }
    public static Scope Renew(string token, string boot)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64 || string.IsNullOrEmpty(boot)) return null;
        string hash = Hash(token);
        bool renewed = HomeDb.With(c => Exec(c,
            "UPDATE user_sessions SET expires=@expires WHERE token_hash=@hash AND boot=@boot AND revoked=0 AND expires>@grace AND profile_id IN(SELECT id FROM user_profiles WHERE archived=0)",
            ("@expires", HomeDb.Now()+24L*3600_000), ("@hash",hash), ("@boot",boot), ("@grace",HomeDb.Now()-7L*86400_000)) == 1);
        return renewed ? FromToken(token) : null;
    }
    public static string IssueProgressGrant(Scope owner,JArray identities,string card)
    {
        string token=NewToken();
        HomeDb.With(c=>{Exec(c,"DELETE FROM play_grants WHERE expires<@n",("@n",HomeDb.Now()));Exec(c,"INSERT INTO play_grants(token_hash,profile_id,device,boot,identities,expires) VALUES(@t,@p,@d,@b,@i,@x)",("@t",Hash(token)),("@p",owner.Id),("@d",owner.Device),("@b",owner.Boot),("@i",new JObject{["card"]=card,["ids"]=identities}.ToString(Newtonsoft.Json.Formatting.None)),("@x",HomeDb.Now()+8L*24*3600_000));});
        return token;
    }
    public static Scope ProgressOwner(HttpContext ctx)
    {
        string token=ctx.Request.Headers["X-Home-Progress"].FirstOrDefault();
        if(token?.Length!=64||ctx.Request.Path!="/home/tl/set")return null;
        var r=HomeDb.With(c=>Rows(c,"SELECT g.profile_id,g.device,g.boot,p.max_age,p.policy_rev FROM play_grants g JOIN user_profiles p ON p.id=g.profile_id WHERE g.token_hash=@t AND g.expires>@n AND p.archived=0",("@t",Hash(token)),("@n",HomeDb.Now()))).FirstOrDefault();
        return r==null?null:new Scope((string)r[0],(string)r[1],(string)r[2],"progress:"+Hash(token),false,false,Convert.ToInt32(r[3]),Convert.ToInt64(r[4]));
    }
    public static bool CheckProgressGrant(Scope owner,JToken body)
    {
        if(owner?.TokenHash?.StartsWith("progress:")!=true)return true;
        var raw=HomeDb.With(c=>Rows(c,"SELECT identities FROM play_grants WHERE token_hash=@t",("@t",owner.TokenHash.Substring(9)))).FirstOrDefault()?[0] as string;
        if(raw==null)return false;var info=JObject.Parse(raw);var ids=((JArray)info["ids"]).Select(x=>(string)x).ToHashSet();string card=(string)info["card"];
        var rows=body as JArray??body["rows"] as JArray;
        return rows!=null&&rows.Count<=50&&rows.Children<JObject>().Count()==rows.Count&&rows.Children<JObject>().All(r=>(string)r["c"]==card&&ids.Contains((string)r["id"])&&((string)r["op"]=="progress"||(string)r["op"]=="completed"));
    }
    public static Scope FromRequest(HttpContext ctx)
    {
        string token=ctx.Request.Headers["X-Home-Session"].FirstOrDefault();
        if(string.IsNullOrEmpty(token)&&ctx.Request.Headers["Authorization"].ToString().StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))token=ctx.Request.Headers["Authorization"].ToString().Substring(7);
        if(string.IsNullOrEmpty(token))ctx.Request.Cookies.TryGetValue("home_session",out token);
        return FromToken(token);
    }

    // Independent browsers must not share a session identity. The cookie is the
    // server-observed browser id; the client never supplies this value.
    public static bool GenericBrowserDevice(string device) =>
        device == "web_panel" || device == "web_profiles" || device == "web_admin";

    public static string BrowserDevice(HttpContext ctx)
    {
        string current = ctx.Request.Cookies["home_web_device"];
        if (!string.IsNullOrEmpty(current) && Regex.IsMatch(current, "^[A-Za-z0-9_-]{8,80}$")) return current;
        string device = "web_" + Guid.NewGuid().ToString("N");
        ctx.Response.Cookies.Append("home_web_device", device, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
            Secure = ctx.Request.IsHttps
        });
        return device;
    }

    public static void SetSessionCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append("home_session", token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromHours(24),
            Secure = ctx.Request.IsHttps
        });
}
