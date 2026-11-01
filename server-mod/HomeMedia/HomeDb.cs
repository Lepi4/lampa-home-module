using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HomeMedia;

// Единая база Home: просмотры и тайминги, закладки, «Мои фильмы/сериалы», скрытое, выбранные дорожки, настройки.
// Один файл database/home.db (SQLite, WAL), одно соединение под замком. Старые JSON-файлы при первом запуске переносятся и
// переименовываются в *.migrated. Устройства только получают и отправляют изменения — клиентских копий истины нет.
public static class HomeDb
{
    static readonly string dir = Path.Combine(Directory.GetCurrentDirectory(), "database");
    static readonly string path = Path.Combine(dir, "home.db");
    static readonly object wl = new();
    static SqliteConnection conn;

    // ---------- песочница ----------
    // Копия рабочей базы во временном файле. Запрос, который явно вошёл в песочницу (HomeDb.Enter(id)), читает и пишет только её: рабочая база, мост Кинопаба,
    // журнал и устройства её не видят. Контекст — AsyncLocal: действует на весь асинхронный путь этого запроса и больше ни на что.
    public sealed class SbxCtx { public string Id; public SqliteConnection Conn; public readonly object Lk = new(); public DateTime At = DateTime.UtcNow; }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SbxCtx> sbxReg = new();
    static readonly System.Threading.AsyncLocal<SbxCtx> sbx = new();
    static readonly string sbxDir = Path.Combine(dir, "sandbox");

    public static string SandboxId => sbx.Value?.Id;

    sealed class Leave : IDisposable { readonly Action a; public Leave(Action a) { this.a = a; } public void Dispose() => a(); }

    // Вход в песочницу на время запроса. Пустой id — рабочая база (null). Неизвестный id — ошибка, а не тихий возврат к рабочей базе.
    public static IDisposable Enter(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (!sbxReg.TryGetValue(id, out var c)) throw new InvalidOperationException("sandbox not found");
        var prev = sbx.Value;
        sbx.Value = c;
        return new Leave(() => sbx.Value = prev);
    }

    public static string SandboxCreate()
    {
        foreach (var k in sbxReg.Where(x => DateTime.UtcNow - x.Value.At > TimeSpan.FromHours(3)).Select(x => x.Key).ToList()) SandboxDrop(k);
        if (sbx.Value != null) throw new InvalidOperationException("already in sandbox");
        Directory.CreateDirectory(sbxDir);
        string id = Guid.NewGuid().ToString("N").Substring(0, 8), p = Path.Combine(sbxDir, id + ".db");
        With(c => Exec(c, "VACUUM INTO '" + p.Replace("'", "''") + "'"));
        var c2 = new SqliteConnection("Data Source=" + p);
        c2.Open();
        TlV2.EnsureSchema(c2);
        sbxReg[id] = new SbxCtx { Id = id, Conn = c2 };
        return id;
    }

    public static bool SandboxDrop(string id)
    {
        if (!sbxReg.TryRemove(id, out var c)) return false;
        try { c.Conn.Close(); c.Conn.Dispose(); } catch { }
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path.Combine(sbxDir, id + ".db"), Path.Combine(sbxDir, id + ".db-wal"), Path.Combine(sbxDir, id + ".db-shm") }) try { File.Delete(f); } catch { }
        return true;
    }

    public static IEnumerable<object> SandboxList() => sbxReg.Values.Select(c => new { id = c.Id, age_min = (int)(DateTime.UtcNow - c.At).TotalMinutes });

    public static T With<T>(Func<SqliteConnection, T> f)
    {
        var sb = sbx.Value;
        if (sb != null) { lock (sb.Lk) return f(sb.Conn); }
        lock (wl)
        {
            Ensure();
            return f(conn);
        }
    }

    public static void With(Action<SqliteConnection> f) => With<int>(c => { f(c); return 0; });

    public static int Exec(SqliteConnection c, string sql, params (string, object)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        if (sql.Contains("@profile") && !p.Any(x => x.Item1 == "@profile")) cmd.Parameters.AddWithValue("@profile", Profiles.CurrentId);
        return cmd.ExecuteNonQuery();
    }

    public static List<object[]> Query(SqliteConnection c, string sql, params (string, object)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        if (sql.Contains("@profile") && !p.Any(x => x.Item1 == "@profile")) cmd.Parameters.AddWithValue("@profile", Profiles.CurrentId);
        using var r = cmd.ExecuteReader();
        var rows = new List<object[]>();
        while (r.Read())
        {
            var o = new object[r.FieldCount];
            r.GetValues(o);
            rows.Add(o);
        }
        return rows;
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static void Ensure()
    {
        if (conn != null) return;
        Directory.CreateDirectory(dir);
        var c = new SqliteConnection("Data Source=" + path);
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL");
        Exec(c, "PRAGMA synchronous=NORMAL");
        Exec(c, @"CREATE TABLE IF NOT EXISTS timeline (h TEXT PRIMARY KEY, c TEXT, t INTEGER, d INTEGER, p REAL, u INTEGER, s INTEGER);
                  CREATE INDEX IF NOT EXISTS ix_timeline_s ON timeline(s);
                  CREATE TABLE IF NOT EXISTS audio (k TEXT PRIMARY KEY, label TEXT, lang TEXT, at INTEGER);
                  CREATE TABLE IF NOT EXISTS watchlist (media TEXT, tmdb INTEGER, at INTEGER, PRIMARY KEY(media, tmdb));
                  CREATE TABLE IF NOT EXISTS hidden (k TEXT PRIMARY KEY, at INTEGER);
                  CREATE TABLE IF NOT EXISTS bookmarks (cat TEXT, k TEXT, card TEXT, u INTEGER, s INTEGER, deleted INTEGER DEFAULT 0, PRIMARY KEY(cat, k));
                  CREATE INDEX IF NOT EXISTS ix_bm_s ON bookmarks(s);
                  CREATE TABLE IF NOT EXISTS anime_map (k TEXT PRIMARY KEY, j TEXT, at INTEGER);
                  CREATE TABLE IF NOT EXISTS anime_last (card TEXT PRIMARY KEY, source TEXT, voice TEXT, season INTEGER, ep INTEGER, at INTEGER);
                  CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, name TEXT, seen INTEGER, cfg TEXT);
                  CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT)");
        Profiles.EnsureSchema(c);
        conn = c;
        try {
            try { Migrate(c); } catch { }
            using (var membershipImport = c.BeginTransaction()) { ProfileLibrary.ImportLegacy(c); membershipImport.Commit(); }
            Profiles.LoadEnabled(c);
        }catch{conn=null;c.Dispose();throw;}
        try { TlV2.EnsureSchema(c); } catch { }                // необязательные колонки id/event/session/op (добавление безопасно для прежних читателей)
    }

    static string Json(string name) => Path.Combine(dir, name);

    static void Done(string file)
    {
        try { File.Move(Json(file), Json(file) + ".migrated", true); } catch { }
    }

    static void Migrate(SqliteConnection c)
    {
        // общая таблица просмотров (прежний home_timeline.json)
        if (File.Exists(Json("home_timeline.json")))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(Json("home_timeline.json")));
                using var tx = c.BeginTransaction();
                foreach (var p in o.Properties())
                    Exec(c, "INSERT OR IGNORE INTO timeline(profile_id,h,c,t,d,p,u,s) VALUES(@profile,@h,'',@t,@d,@p,@u,@s)",
                        ("@h", p.Name), ("@t", (long?)p.Value["t"] ?? 0), ("@d", (long?)p.Value["d"] ?? 0), ("@p", (double?)p.Value["p"] ?? 0),
                        ("@u", (long?)p.Value["u"] ?? 0), ("@s", (long?)p.Value["s"] ?? (long?)p.Value["u"] ?? 0));
                tx.Commit();
                Done("home_timeline.json");
            }
            catch { }
        }
        if (File.Exists(Json("home_audio.json")))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(Json("home_audio.json")));
                using var tx = c.BeginTransaction();
                foreach (var p in o.Properties())
                    Exec(c, "INSERT OR IGNORE INTO audio(profile_id,k,label,lang,at) VALUES(@profile,@k,@l,@g,@a)",
                        ("@k", p.Name), ("@l", (string)p.Value["label"] ?? ""), ("@g", (string)p.Value["lang"] ?? ""), ("@a", (long?)p.Value["at"] ?? 0));
                tx.Commit();
                Done("home_audio.json");
            }
            catch { }
        }
        if (File.Exists(Json("watchlist.json")))
        {
            try
            {
                var a = JArray.Parse(File.ReadAllText(Json("watchlist.json")));
                using var tx = c.BeginTransaction();
                foreach (var x in a.Children<JObject>())
                    Exec(c, "INSERT OR IGNORE INTO watchlist(profile_id,media,tmdb,at) VALUES(@profile,@m,@t,@a)",
                        ("@m", (string)x["media"]), ("@t", (long)x["tmdb"]), ("@a", (long?)x["at"] ?? 0));
                tx.Commit();
                Done("watchlist.json");
            }
            catch { }
        }
        if (File.Exists(Json("continue_hidden.json")))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(Json("continue_hidden.json")));
                using var tx = c.BeginTransaction();
                foreach (var p in o.Properties())
                    Exec(c, "INSERT OR IGNORE INTO hidden(profile_id,k,at) VALUES(@profile,@k,@a)", ("@k", p.Name), ("@a", (long?)p.Value ?? 0));
                tx.Commit();
                Done("continue_hidden.json");
            }
            catch { }
        }
    }

    // ---- настройки (ключ-значение) ----
    public static string Get(string k)
        => With(c => Query(c, "SELECT v FROM kv WHERE k=@k", ("@k", k)).FirstOrDefault()?[0] as string);

    public static void Set(string k, string v)
        => With(c => Exec(c, "INSERT INTO kv(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v", ("@k", k), ("@v", v ?? "")));

    public static Dictionary<string, string> All()
        => With(c => Query(c, "SELECT k,v FROM kv").ToDictionary(r => (string)r[0], r => (string)r[1]));

    public static (long timeline, long bookmarks, long watchlist, long hidden, long audio) Counts()
        => With(c => (
            (long)Query(c, "SELECT COUNT(*) FROM timeline WHERE profile_id=@profile")[0][0],
            (long)Query(c, "SELECT COUNT(*) FROM bookmarks WHERE profile_id=@profile AND deleted=0")[0][0],
            (long)Query(c, "SELECT COUNT(*) FROM watchlist WHERE profile_id=@profile")[0][0],
            (long)Query(c, "SELECT COUNT(*) FROM hidden WHERE profile_id=@profile")[0][0],
            (long)Query(c, "SELECT COUNT(*) FROM audio WHERE profile_id=@profile")[0][0]));
}

// Общая таблица просмотренного: ключ — хэш файла Lampa, побеждает более свежая отметка (u — время изменения на устройстве, мс),
// s — когда запись поступила на сервер (курсор для устройств), c — карточка тайтла («tmdb_tv» / «tmdb_movie»), если известна.
public static class TlStore
{
    public class Row { public long T, D, U, S; public double P; public string C = "", Op; }

    static Row Read(object[] r) => new Row
    {
        T = Convert.ToInt64(r[1]), D = Convert.ToInt64(r[2]), P = Convert.ToDouble(r[3]), U = Convert.ToInt64(r[4]), S = Convert.ToInt64(r[5]),
        C = r[6] as string ?? "", Op = r.Length > 7 ? r[7] as string : null
    };

    const string Cols = "h,t,d,p,u,s,c,op";

    public static Row Get(string h)
        => HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile AND h=@h", ("@h", h));
            return q.Count == 0 ? null : Read(q[0]);
        });

    public static List<object> Since(long since)
        => HomeDb.With(c => TlV2.Since(c, since).Select(o => (object)o).ToList());

    public static string OpOf(string h) => HomeDb.With(c => TlV2.Get(c, h)?.Op);

    // Все записи идут через TlV2 (порядок по паре (u,event), ответ по каждому событию); вызов возвращает число принятых
    public static int Merge(JArray arr) => TlV2.Submit(arr).results.Count(r => r.Status == "accepted");

    static int MergeOld(JArray arr)
    {
        int n = 0;
        HomeDb.With(c =>
        {
            long maxS = Convert.ToInt64(HomeDb.Query(c, "SELECT COALESCE(MAX(s),0) FROM timeline WHERE profile_id=@profile")[0][0]);
            using var tx = c.BeginTransaction();
            foreach (var x in arr.Children<JObject>().Take(5000))
            {
                string h = (string)x["h"];
                if (string.IsNullOrEmpty(h) || !Regex.IsMatch(h, "^-?[0-9]{1,15}$") || h == "0") continue;
                long u = (long?)x["u"] ?? 0;
                if (u <= 0) continue;
                string card = (string)x["c"] ?? "";
                if (card != "" && !Regex.IsMatch(card, "^[0-9]{1,12}_(tv|movie)$")) card = "";
                var cur = HomeDb.Query(c, "SELECT u,c FROM timeline WHERE profile_id=@profile AND h=@h", ("@h", h));
                if (cur.Count > 0)
                {
                    long cu = Convert.ToInt64(cur[0][0]);
                    string cc = cur[0][1] as string ?? "";
                    if (cu >= u)
                    {
                        // отметка не новее, но карточку, если её не было, запоминаем
                        if (cc == "" && card != "") { maxS = Math.Max(HomeDb.Now(), maxS + 1); HomeDb.Exec(c, "UPDATE timeline SET c=@c, s=@s WHERE profile_id=@profile AND h=@h", ("@c", card), ("@s", maxS), ("@h", h)); }
                        continue;
                    }
                    if (card == "") card = cc;
                }
                maxS = Math.Max(HomeDb.Now(), maxS + 1);
                HomeDb.Exec(c, @"INSERT INTO timeline(profile_id,h,c,t,d,p,u,s) VALUES(@profile,@h,@c,@t,@d,@p,@u,@s)
                                 ON CONFLICT(profile_id,h) DO UPDATE SET c=@c,t=@t,d=@d,p=@p,u=@u,s=@s",
                    ("@h", h), ("@c", card), ("@t", Math.Max(0, (long?)x["t"] ?? 0)), ("@d", Math.Max(0, (long?)x["d"] ?? 0)),
                    ("@p", Math.Clamp((double?)x["p"] ?? 0, 0, 100)), ("@u", u), ("@s", maxS));
                n++;
            }
            tx.Commit();
        });
        return n;
    }

    // Накладывает отметки этой таблицы на дамп штатной базы таймингов Lampac и добавляет строки по тайтлам, которых там нет:
    // «Продолжить просмотр», «Я смотрю» и обмен с kino.pub читают дамп через это. Штатная синхронизация Lampac для kino.pub и Plex молчит.
    public static JObject Overlay(JObject dump, bool persist = true)
    {
        try
        {
            var all = HomeDb.With(c => HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile"))
                .ToDictionary(r => (string)r[0], r => Read(r));
            var rows = (JArray)dump["rows"];
            var seen = new HashSet<string>();
            foreach (var r in rows.Children<JObject>())
            {
                string h = (string)r["hash"];
                if (string.IsNullOrEmpty(h)) continue;
                seen.Add((string)r["card"] + "|" + h);
                // карточка есть в штатной базе, а у нас её нет — запоминаем (однократное дополнение прежних записей)
                if (all.TryGetValue(h, out var t0) && t0.C == "" && (string)r["card"] is string cd && Regex.IsMatch(cd, "^[0-9]{1,12}_(tv|movie)$"))
                {
                    t0.C = cd;
                    if (persist) HomeDb.With(c => HomeDb.Exec(c, "UPDATE timeline SET c=@c, s=@s WHERE profile_id=@profile AND h=@h AND (c IS NULL OR c='')", ("@c", cd), ("@s", HomeDb.Now()), ("@h", h)));
                }
                if (!all.TryGetValue(h, out var t) || t.U <= ((long?)r["watched_at"] ?? (long?)r["updated_at"] ?? 0)) continue;     // время действия против времени действия (updated_at — версия доставки, не время просмотра)
                r["position"] = t.T; r["duration"] = t.D; r["percent"] = t.P; r["watched_at"] = t.U; r["updated_at"] = t.U; r["op"] = t.Op;
            }
            foreach (var kv in all)
            {
                var t = kv.Value;
                if (t.C == "" || seen.Contains(t.C + "|" + kv.Key)) continue;
                rows.Add(new JObject { ["id"] = "home-" + kv.Key, ["hash"] = kv.Key, ["card"] = t.C, ["position"] = t.T, ["duration"] = t.D, ["percent"] = t.P,
                                       ["profile"] = 0, ["deleted"] = false, ["watched_at"] = t.U, ["updated_at"] = t.U });
            }
        }
        catch { }
        return dump;
    }
}

// Выбранная звуковая дорожка по тайтлу (ключ — «tmdb_tv/movie»): общая для всех устройств.
public static class AudioStore
{
    public static JObject Get(string key)
        => HomeDb.With(c =>
        {
            var q = HomeDb.Query(c, "SELECT label,lang FROM audio WHERE profile_id=@profile AND k=@k", ("@k", key));
            return q.Count == 0 ? null : new JObject { ["label"] = q[0][0] as string ?? "", ["lang"] = q[0][1] as string ?? "" };
        });

    public static void Set(string key, string label, string lang)
        => HomeDb.With(c => HomeDb.Exec(c, @"INSERT INTO audio(profile_id,k,label,lang,at) VALUES(@profile,@k,@l,@g,@a)
                                             ON CONFLICT(profile_id,k) DO UPDATE SET label=@l,lang=@g,at=@a",
            ("@k", key), ("@l", label ?? ""), ("@g", lang ?? ""), ("@a", HomeDb.Now())));
}

// Закладки (Избранное, Позже, Смотрю…): общие для всех устройств и kino.pub. Ключ — категория + «tv:tmdb»/«movie:tmdb»,
// побеждает более свежая запись, удаление — «надгробие» (deleted=1), чтобы оно дошло до остальных устройств.
public static class BmStore
{
    static readonly Regex catRx = new("^[a-z_]{2,20}$");
    static readonly Regex keyRx = new("^(tv|movie):[0-9]{1,12}$");

    public static string KeyOf(JObject c)
    {
        long id = (long?)c?["id"] ?? 0;
        if (id <= 0) return null;
        bool tv = (string)c["media_type"] == "tv" || c["first_air_date"] != null || c["number_of_seasons"] != null;
        return (tv ? "tv" : "movie") + ":" + id;
    }

    public static int Merge(JArray arr)
    {
        int n = 0;
        HomeDb.With(c =>
        {
            long maxS = Convert.ToInt64(HomeDb.Query(c, "SELECT COALESCE(MAX(s),0) FROM bookmarks WHERE profile_id=@profile")[0][0]);
            using var tx = c.BeginTransaction();
            foreach (var x in arr.Children<JObject>().Take(2000))
            {
                string cat = (string)x["cat"], k = (string)x["k"];
                if (cat == null || k == null || !catRx.IsMatch(cat) || !keyRx.IsMatch(k)) continue;
                long u = (long?)x["u"] ?? 0;
                if (u <= 0) continue;
                bool del = (bool?)x["d"] ?? false;
                var cur = HomeDb.Query(c, "SELECT u,card FROM bookmarks WHERE profile_id=@profile AND cat=@c AND k=@k", ("@c", cat), ("@k", k));
                string cardJson = x["card"] is JObject co ? co.ToString(Newtonsoft.Json.Formatting.None) : null;
                if (cur.Count > 0)
                {
                    if (Convert.ToInt64(cur[0][0]) >= u) continue;
                    if (cardJson == null) cardJson = cur[0][1] as string;
                }
                if (!del && cardJson == null) continue;
                maxS = Math.Max(HomeDb.Now(), maxS + 1);
                HomeDb.Exec(c, @"INSERT INTO bookmarks(profile_id,cat,k,card,u,s,deleted) VALUES(@profile,@c,@k,@j,@u,@s,@d)
                                 ON CONFLICT(profile_id,cat,k) DO UPDATE SET card=@j,u=@u,s=@s,deleted=@d",
                    ("@c", cat), ("@k", k), ("@j", cardJson ?? "{}"), ("@u", u), ("@s", maxS), ("@d", del ? 1 : 0));
                if(Profiles.Enabled&&(cat=="book"||cat=="wath")){var idParts=k.Split(':');ProfileLibrary.SetInTransaction(c,Profiles.CurrentId,idParts[0],long.Parse(idParts[1]),!del,cat=="book"?"favorite":"later");}
                n++;
            }
            tx.Commit();
        });

        return n;
    }

    public static List<object> Since(long since)
        => HomeDb.With(c => HomeDb.Query(c, "SELECT cat,k,card,u,s,deleted FROM bookmarks WHERE profile_id=@profile AND s>@s ORDER BY s", ("@s", since))
            .Select(r => (object)new { cat = (string)r[0], k = (string)r[1], card = JObject.Parse((string)r[2] ?? "{}"), u = Convert.ToInt64(r[3]), s = Convert.ToInt64(r[4]), d = Convert.ToInt64(r[5]) == 1 }).ToList());

    // В виде дампа Lampac ({rows:[{card, categories:{book:true}}]}): с ним работает обмен с kino.pub
    public static JObject Dump()
    {
        var byKey = new Dictionary<string, JObject>();
        foreach (var r in HomeDb.With(c => HomeDb.Query(c, "SELECT cat,k,card FROM bookmarks WHERE profile_id=@profile AND deleted=0")))
        {
            string k = (string)r[1];
            if (!byKey.TryGetValue(k, out var row))
                byKey[k] = row = new JObject { ["card"] = JObject.Parse((string)r[2] ?? "{}"), ["categories"] = new JObject() };
            ((JObject)row["categories"])[(string)r[0]] = true;
        }
        return new JObject { ["rows"] = new JArray(byKey.Values) };
    }

    public static void Add(string cat, JObject card)
    {
        string k = KeyOf(card);
        if (k == null) return;
        Merge(new JArray(new JObject { ["cat"] = cat, ["k"] = k, ["card"] = card, ["u"] = HomeDb.Now(), ["d"] = false }));
    }

    public static void Remove(string cat, string media, long id)
        => Merge(new JArray(new JObject { ["cat"] = cat, ["k"] = media + ":" + id, ["u"] = HomeDb.Now(), ["d"] = true }));
}


// Отложенная и атомарная запись кэш-файлов: изменения накапливаются в памяти и сбрасываются на диск не чаще раза в несколько секунд
// (прежде при каждом изменении файл в сотни КБ переписывался целиком, а запись шла прямо в рабочий файл — при сбое он мог остаться обрезанным).
public static class Persist
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<string>> pending = new();
    static System.Threading.Timer timer;
    static readonly object tl = new();
    static Persist() { AppDomain.CurrentDomain.ProcessExit += (_, __) => Flush(); }

    static DateTime firstPending = DateTime.MinValue;

    public static void Later(string path, Func<string> produce, int delayMs = 4000)
    {
        pending[path] = produce;
        lock (tl)
        {
            timer ??= new System.Threading.Timer(_ => Flush(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            if (firstPending == DateTime.MinValue) firstPending = DateTime.UtcNow;
            // частые изменения не должны откладывать запись бесконечно: не позднее 30 секунд от первого
            int left = (int)Math.Max(0, 30000 - (DateTime.UtcNow - firstPending).TotalMilliseconds);
            timer.Change(Math.Min(delayMs, left), System.Threading.Timeout.Infinite);
        }
    }

    public static void Flush()
    {
        lock (tl) firstPending = DateTime.MinValue;
        foreach (var path in pending.Keys.ToList())
        {
            if (!pending.TryRemove(path, out var produce)) continue;
            try
            {
                string text = produce();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, text);
                System.IO.File.Move(tmp, path, true);
            }
            catch { pending.TryAdd(path, produce); Later(path, produce, 5000); }      // ошибка записи: повторить, а не потерять изменения
        }
    }
}
