using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Служебное: память процесса и её очистка при раздувании, ежедневная копия home.db (хранится 7), журнал запусков приложений,
// страница состояния сервера. Дежурная проверка «Lampac отвечает?» живёт на хосте Proxmox и пишет свой файл ops_host.json —
// панель его только показывает.
public static class Ops
{
    public static readonly DateTime Started = DateTime.UtcNow;
    static readonly string dbDir = Path.Combine(Directory.GetCurrentDirectory(), "database");
    static readonly string bakDir = Path.Combine(dbDir, "backup", "home");
    static readonly List<long[]> samples = new();           // [время мс, рабочая память МБ, куча МБ]
    static readonly object sl = new();
    static readonly object logLock = new();
    static Timer timer;

    public static long LastCleanAt;
    public static string LastClean = "ещё не требовалась";
    public static long LimitMb => long.TryParse(HomeDb.Get("ops_mem_mb"), out var v) && v >= 600 ? v : 1500;

    public static void Start() => timer ??= new Timer(_ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

    public static void Stop() { timer?.Dispose(); timer = null; }

    public static void Tick()
    {
        try
        {
            AppUpdates.Cleanup();
            using var p = Process.GetCurrentProcess();
            long ws = p.WorkingSet64 / 1048576, heap = GC.GetTotalMemory(false) / 1048576;
            lock (sl)
            {
                samples.Add(new[] { HomeDb.Now(), ws, heap });
                if (samples.Count > 288) samples.RemoveAt(0);
            }
            if (ws > LimitMb && HomeDb.Now() - LastCleanAt > 15 * 60_000) Clean(ws);
        }
        catch { }
        try { Backup(); } catch { }
    }

    // Очистка без перезапуска: полная сборка мусора с уплотнением кучи, журнал базы, кеши мода. Если память всё равно высокая, это видно в панели.
    public static string Clean(long before)
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            try { HomeDb.With(c => HomeDb.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)")); } catch { }
            using var p = Process.GetCurrentProcess();
            long after = p.WorkingSet64 / 1048576;
            LastCleanAt = HomeDb.Now();
            LastClean = $"{DateTime.Now:dd.MM HH:mm}: {before} → {after} МБ";
        }
        catch (Exception ex) { LastClean = "ошибка очистки: " + ex.Message; }
        return LastClean;
    }

    // Копии каждые 6 часов набором: home.db, база таймингов Lampac (TimeCode.sql), состояние моста и конфиги Кинопаба/локальных папок.
    // Набор собирается во временном каталоге, получает manifest (размеры, SHA-256, число строк) и признак complete и только после этого переименовывается;
    // недособранный набор не публикуется, а при следующем тике собирается заново. Хранятся 28 последних наборов (около недели).
    static void Backup()
    {
        Directory.CreateDirectory(bakDir);
        var now = DateTime.Now;
        string slot = $"{now:yyyyMMdd}-{now.Hour / 6 * 6:00}";
        string fin = Path.Combine(bakDir, $"set-{slot}"), tmp = fin + ".tmp";
        if (!Directory.Exists(fin))
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            Directory.CreateDirectory(tmp);
            // общий момент снимка: мост Кинопаба на паузе, отложенные записи сброшены, писатели HomeMedia ждут (замок базы) на время копирования всех частей
            bool paused = KpBridge.PauseForSnapshot(20000);
            if (!paused)
            {
                // согласованный набор невозможен без остановленного моста: финальный каталог не публикуется, причина сохраняется, повтор — на следующем тике
                try { Directory.Delete(tmp, true); } catch { }
                try { HomeDb.Set("backup_last_error", HomeDb.Now() + ": bridge pause timeout, set " + slot + " not published"); } catch { }
                return;
            }
            long maxS = 0; string mode = "legacy", epoch = "1";
            string home = Path.Combine(tmp, "home.db"), tcDst = Path.Combine(tmp, "timecode.db");
            bool haveTc = false;
            try
            {
                Persist.Flush();
                HomeDb.With(c =>
                {
                    HomeDb.Exec(c, "VACUUM INTO '" + home.Replace("'", "''") + "'");
                    foreach (var extra in new[] { "kp_sync.json", "kinopub.json", "localmedia.json" })
                    {
                        string src = Path.Combine(dbDir, extra);
                        if (File.Exists(src)) File.Copy(src, Path.Combine(tmp, extra), true);
                    }
                    maxS = TlV2.MaxS(c);
                    mode = TlV2.Unified ? "unified" : "legacy"; epoch = TlV2.Epoch;
                });
                string tcSrc = Path.Combine(dbDir, "TimeCode.sql");
                if (File.Exists(tcSrc))
                {
                    using (var c2 = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + tcSrc + ";Mode=ReadOnly"))
                    {
                        c2.Open();
                        using var cmd = c2.CreateCommand();
                        cmd.CommandText = "VACUUM INTO '" + tcDst.Replace("'", "''") + "'";
                        cmd.ExecuteNonQuery();
                    }
                    haveTc = true;
                }
            }
            finally { if (paused) KpBridge.ResumeAfterSnapshot(); }

            // метаданные манифеста считаются по СОХРАНЁННЫМ файлам, а не по продолжающей меняться рабочей базе
            var files = new JArray();
            var tables = new JObject(); JArray cols = new JArray();
            using (var cs = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + home + ";Mode=ReadOnly"))
            {
                cs.Open();
                foreach (var t in new[] { "timeline", "lastplay", "hidden", "tracked", "watchlist", "bookmarks" })
                {
                    try { using var q = cs.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM " + t; tables[t] = Convert.ToInt64(q.ExecuteScalar()); } catch { }
                }
                using var q2 = cs.CreateCommand(); q2.CommandText = "PRAGMA table_info(timeline)";
                using var rd = q2.ExecuteReader(); while (rd.Read()) cols.Add(rd.GetString(1));
            }
            files.Add(Entry(home, "home.db", (long)tables["timeline"]));
            if (haveTc)
            {
                long tcRows; using (var c3 = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + tcDst + ";Mode=ReadOnly")) { c3.Open(); using var cmd = c3.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM timecodes"; tcRows = Convert.ToInt64(cmd.ExecuteScalar()); }
                files.Add(Entry(tcDst, "timecode.db", tcRows));
            }
            foreach (var extra in new[] { "kp_sync.json", "kinopub.json", "localmedia.json" })
            {
                string dst = Path.Combine(tmp, extra);
                if (File.Exists(dst)) files.Add(Entry(dst, extra, -1));
            }
            var required = new JArray("home.db", "kp_sync.json", "kinopub.json"); if (haveTc) required.Add("timecode.db");
            File.WriteAllText(Path.Combine(tmp, "manifest.json"), new JObject
            {
                ["version"] = 2, ["slot"] = slot, ["at"] = HomeDb.Now(), ["complete"] = true, ["required"] = required, ["optional"] = new JArray("localmedia.json"),
                ["generation"] = new JObject { ["mode"] = mode, ["epoch"] = epoch, ["max_s"] = maxS, ["schema_columns"] = cols },
                ["tables"] = tables, ["files"] = files
            }.ToString(Formatting.Indented));
            Directory.Move(tmp, fin);          // набор появляется целиком или не появляется
        }
        foreach (var old in new DirectoryInfo(bakDir).GetDirectories("set-*").Where(d => !d.Name.EndsWith(".tmp")).OrderByDescending(x => x.Name).Skip(28))
            try { old.Delete(true); } catch { }
        // прежние одиночные файлы (до перехода на наборы) чистятся по прежнему правилу
        foreach (var pat in new[] { "home-*.db", "timecode-*.db", "kp_sync-*.json", "kinopub-*.json", "localmedia-*.json" })
            foreach (var old in new DirectoryInfo(bakDir).GetFiles(pat).OrderByDescending(x => x.Name).Skip(28))
                try { old.Delete(); } catch { }
        TlV2.FlushJournal();       // повторная выгрузка журнала, если прошлая попытка не удалась
        TlV2.PurgeJournal();       // ограниченное окно хранения выгруженного журнала
    }

    static JObject Entry(string path, string name, long rows)
    {
        using var fs = File.OpenRead(path);
        string sha = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(fs)).Replace("-", "").ToLowerInvariant();
        return new JObject { ["name"] = name, ["bytes"] = new FileInfo(path).Length, ["sha256"] = sha, ["rows"] = rows };
    }

    // Проверка восстановления: набор копируется в отдельный каталог; проверяются обязательный состав, контрольные суммы, quick_check, схема timeline,
    // число строк таблиц и поколение. Манифест без обязательных частей или с изменённой схемой даёт отрицательный результат.
    public static JObject RestoreTest(string set)
    {
        var checks = new JArray();
        void Chk(string n, bool ok, string info = "") => checks.Add(new JObject { ["name"] = n, ["ok"] = ok, ["info"] = info });
        string work = Path.Combine(Path.GetTempPath(), "restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = set == "latest"
                ? new DirectoryInfo(bakDir).GetDirectories("set-*").Where(d => !d.Name.EndsWith(".tmp") && File.Exists(Path.Combine(d.FullName, "manifest.json"))).OrderByDescending(d => d.Name).FirstOrDefault()
                : new DirectoryInfo(Path.Combine(bakDir, "set-" + set));
            if (dir == null || !dir.Exists) return new JObject { ["error"] = "set not found" };
            var man = JObject.Parse(File.ReadAllText(Path.Combine(dir.FullName, "manifest.json")));
            Chk("manifest complete", (bool?)man["complete"] == true, dir.Name);
            int ver = (int?)man["version"] ?? 1;
            var listed = ((JArray)man["files"]).Children<JObject>().Select(f => (string)f["name"]).ToHashSet();
            // обязательный состав задан форматом, а не самим манифестом
            var must = new List<string> { "home.db", "kp_sync.json", "kinopub.json" };
            if (File.Exists(Path.Combine(dbDir, "TimeCode.sql"))) must.Add("timecode.db");
            foreach (var m in must) Chk("required part " + m + " listed in manifest and present", listed.Contains(m) && File.Exists(Path.Combine(dir.FullName, m)));
            Directory.CreateDirectory(work);
            foreach (var f in ((JArray)man["files"]).Children<JObject>())
            {
                string name = (string)f["name"], src = Path.Combine(dir.FullName, name), dst = Path.Combine(work, name);
                if (!File.Exists(src)) { Chk(name + " present", false); continue; }
                File.Copy(src, dst, true);
                var e = Entry(dst, name, -1);
                Chk(name + " checksum equals manifest", (string)e["sha256"] == (string)f["sha256"] && (long)e["bytes"] == (long)f["bytes"]);
                if (name.EndsWith(".db"))
                {
                    using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dst + ";Mode=ReadOnly"); c.Open();
                    using var q = c.CreateCommand(); q.CommandText = "PRAGMA quick_check";
                    Chk(name + " quick_check", (string)q.ExecuteScalar() == "ok");
                    if (name == "home.db")
                    {
                        var have = new List<string>(); using (var q3 = c.CreateCommand()) { q3.CommandText = "PRAGMA table_info(timeline)"; using var rd = q3.ExecuteReader(); while (rd.Read()) have.Add(rd.GetString(1)); }
                        foreach (var col in new[] { "h", "c", "t", "d", "p", "u", "s", "id", "event", "session", "op" }) if (!have.Contains(col)) { Chk("timeline column " + col, false); }
                        if (have.Count > 0 && new[] { "h", "c", "t", "d", "p", "u", "s", "id", "event", "session", "op" }.All(have.Contains)) Chk("timeline schema complete", true);
                        if (ver >= 2 && man["tables"] is JObject tb)
                            foreach (var p in tb.Properties())
                            {
                                using var q4 = c.CreateCommand(); q4.CommandText = "SELECT COUNT(*) FROM " + p.Name;
                                Chk("table " + p.Name + " row count equals manifest", Convert.ToInt64(q4.ExecuteScalar()) == (long)p.Value, p.Name);
                            }
                        if (ver >= 2 && man["generation"] is JObject g)
                        {
                            using var q5 = c.CreateCommand(); q5.CommandText = "SELECT v FROM kv WHERE k='tl_epoch'";
                            string ep = q5.ExecuteScalar() as string ?? "1";
                            Chk("generation (epoch) equals manifest", ep == (string)g["epoch"], ep);
                        }
                    }
                    else if ((long)f["rows"] >= 0)
                    {
                        using var q2 = c.CreateCommand(); q2.CommandText = "SELECT COUNT(*) FROM timecodes";
                        Chk(name + " row count equals manifest", Convert.ToInt64(q2.ExecuteScalar()) == (long)f["rows"]);
                    }
                }
                else { try { JToken.Parse(File.ReadAllText(dst)); Chk(name + " parses as JSON", true); } catch { Chk(name + " parses as JSON", false); } }
            }
        }
        catch (Exception ex) { Chk("exception", false, ex.Message); }
        finally { try { Directory.Delete(work, true); } catch { } }
        return new JObject { ["checks"] = checks, ["passed"] = checks.Count(x => (bool)x["ok"]), ["total"] = checks.Count, ["ok"] = checks.All(x => (bool)x["ok"]) };
    }

    public static JArray Backups()
    {
        var arr = new JArray();
        try
        {
            if (Directory.Exists(bakDir))
            {
                foreach (var d in new DirectoryInfo(bakDir).GetDirectories("set-*").Where(x => !x.Name.EndsWith(".tmp")).OrderByDescending(x => x.Name))
                    arr.Add(new JObject { ["name"] = d.Name, ["bytes"] = d.GetFiles().Sum(x => x.Length), ["at"] = d.LastWriteTimeUtc });
                foreach (var f in new DirectoryInfo(bakDir).GetFiles("home-*.db").OrderByDescending(x => x.Name))
                    arr.Add(new JObject { ["name"] = f.Name, ["bytes"] = f.Length, ["at"] = f.LastWriteTimeUtc });
            }
        }
        catch { }
        return arr;
    }

    public static JArray Samples()
    {
        lock (sl) return new JArray(samples.Select(s => new JArray(s[0], s[1], s[2])));
    }

    // ---- журнал запусков приложений ----
    public static void AddAppLog(string dev, JArray items)
    {
        lock (logLock)
        {
            JArray all;
            try { all = JArray.Parse(HomeDb.Get("applog") ?? "[]"); } catch { all = new JArray(); }
            foreach (var it in items.Children<JObject>())
            {
                long t = (long?)it["t"] ?? HomeDb.Now();
                string m = (string)it["m"] ?? "";
                if (m.Length > 160) m = m.Substring(0, 160);
                all.Add(new JObject { ["dev"] = dev, ["t"] = t, ["m"] = m, ["net"] = (bool?)it["net"] ?? true });
            }
            var ordered = new JArray(all.Children<JObject>().OrderBy(x => (long)x["t"]).Skip(Math.Max(0, all.Count - 200)));
            HomeDb.Set("applog", ordered.ToString(Formatting.None));
        }
    }

    public static JArray AppLog(int n)
    {
        try { return new JArray(JArray.Parse(HomeDb.Get("applog") ?? "[]").Children<JObject>().OrderByDescending(x => (long)x["t"]).Take(n)); }
        catch { return new JArray(); }
    }

    static long cacheBytes, cacheAt;
    public static long CacheBytes()
    {
        if (HomeDb.Now() - cacheAt < 10 * 60_000) return cacheBytes;
        try { cacheBytes = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "cache")).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
        cacheAt = HomeDb.Now();
        return cacheBytes;
    }
}

public class OpsController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    [HttpPost, AllowAnonymous]
    [Route("home/applog")]
    public async Task<ActionResult> AppLogPost(string dev = null)
    {
        if (!DeviceStore.ValidId((dev ?? "").Replace(" ", "_").Replace(".", "_"))) dev = "unknown";
        string body;
        using (var sr = new StreamReader(Request.Body)) body = await sr.ReadToEndAsync();
        if (body.Length > 20000) return Json(new { error = "too_big" }, 413);
        try { Ops.AddAppLog(dev, JArray.Parse(body)); } catch { return Json(new { error = "bad_json" }, 400); }
        return Json(new { ok = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("home/ops/status")]
    public async Task<ActionResult> Status()
    {
        using var p = Process.GetCurrentProcess();
        string version = "";
        try { version = System.IO.File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "version.txt")).Trim(); } catch { }

        // Channel Manager: отвечает ли и за сколько
        object cm;
        string cmUrl = (string)TvStore.Config()["url"] ?? "";
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var sw = Stopwatch.StartNew();
            var r = await h.GetAsync(cmUrl);
            cm = new { url = cmUrl, ok = (int)r.StatusCode < 500, code = (int)r.StatusCode, ms = sw.ElapsedMilliseconds };
        }
        catch (Exception ex) { cm = new { url = cmUrl, ok = false, code = 0, ms = 0, error = ex.GetType().Name }; }

        JObject host = null;
        try { host = JObject.Parse(System.IO.File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "database", "ops_host.json"))); } catch { }

        long dbSize = 0;
        try { dbSize = new FileInfo(Path.Combine(Directory.GetCurrentDirectory(), "database", "home.db")).Length; } catch { }

        return Json(new
        {
            version,
            started = Ops.Started,
            uptime_s = (long)(DateTime.UtcNow - Ops.Started).TotalSeconds,
            mem_mb = p.WorkingSet64 / 1048576,
            heap_mb = GC.GetTotalMemory(false) / 1048576,
            limit_mb = Ops.LimitMb,
            last_clean = Ops.LastClean,
            samples = Ops.Samples(),
            db_bytes = dbSize,
            cache_bytes = Ops.CacheBytes(),
            backups = Ops.Backups(),
            channel_manager = cm,
            bridge_last = KpBridge.LastRun == default ? (DateTime?)null : KpBridge.LastRun,
            bridge_result = KpBridge.LastResult,
            import_result = KpImport.Last,
            sources = AnimeCatalog.Status(),
            applog = Ops.AppLog(40),
            host
        });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/ops/clean")]
    public ActionResult Clean()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        using var p = Process.GetCurrentProcess();
        return Json(new { ok = true, result = Ops.Clean(p.WorkingSet64 / 1048576) });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/ops/limit")]
    public ActionResult Limit(long mb = 0)
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        if (mb < 600 || mb > 3500) return Json(new { error = "bad_limit" }, 400);
        HomeDb.Set("ops_mem_mb", mb.ToString());
        return Json(new { ok = true, limit_mb = mb });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/ops/backup")]
    public ActionResult BackupNow()
    {
        if (!Guard.Ok(Request)) return Json(new { error = "forbidden" }, 403);
        // внеочередная копия: удаляем сегодняшнюю и создаём заново
        try
        {
            string f = Path.Combine(dbDir(), $"home-{DateTime.Now:yyyyMMdd}.db");
            if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
        }
        catch { }
        try { Ops.Tick(); } catch { }
        return Json(new { ok = true, backups = Ops.Backups() });
    }

    static string dbDir() => Path.Combine(Directory.GetCurrentDirectory(), "database", "backup", "home");
}

// Закладки Кинопаба → «Мои фильмы». Папки, отмеченные в панели, читаются раз в цикл моста; фильмы, которых ещё не было, один раз
// добавляются в локальный список (если убрать фильм из «Моих фильмов», он не вернётся).
public static class KpImport
{
    public static string Last = "папки не выбраны";

    static HashSet<string> Load(string key)
    {
        try { return JArray.Parse(HomeDb.Get(key) ?? "[]").Select(x => (string)x).ToHashSet(); } catch { return new HashSet<string>(); }
    }

    static void Save(string key, HashSet<string> set) => HomeDb.Set(key, new JArray(set).ToString(Formatting.None));

    public static HashSet<string> Selected() => Load("kp_import_folders");

    public static void SetSelected(IEnumerable<string> ids) => Save("kp_import_folders", ids.Where(x => x.All(char.IsDigit) && x.Length > 0).ToHashSet());

    public static async Task<JArray> Folders(string prof)
    {
        var res = new JArray();
        string tok = await KpStore.Token(prof);
        if (tok == null) return res;
        var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/bookmarks", tok);
        if (code != 200) return res;
        var sel = Selected();
        foreach (var f in ((JArray)(JObject.Parse(body)["items"] ?? new JArray())).Children<JObject>())
            res.Add(new JObject { ["id"] = (string)f["id"], ["title"] = (string)f["title"], ["count"] = (int?)f["count"] ?? 0, ["selected"] = sel.Contains((string)f["id"]) });
        return res;
    }

    // Правила обмена «Мои фильмы» <-> папка Кинопаба:
    //  - фильм появился в папке -> добавляется в «Мои фильмы» (один раз); появился в «Моих фильмах» -> уходит в папку;
    //  - фильм ушёл из «Моих фильмов» вручную -> убирается из папки; ушёл, потому что его досмотрели -> убирается из папки;
    //  - фильм убрали из папки в Кинопабе -> убирается из «Моих фильмов».
    // Пары «kp id -> tmdb», о которых оба списка уже знают, хранятся в kv kp_pairs.
    // Правила обмена «Мои фильмы» <-> папка Кинопаба:
    //  - фильм появился в папке -> добавляется в «Мои фильмы» (один раз); появился в «Моих фильмах» -> уходит в папку;
    //  - фильм ушёл из «Моих фильмов» вручную -> убирается из папки; ушёл, потому что его досмотрели -> убирается из папки;
    //  - ушёл, потому что его начали смотреть (ещё не досмотрен) -> в папке остаётся до конца просмотра;
    //  - фильм убрали из папки в Кинопабе -> убирается из «Моих фильмов».
    // Пары «kp id -> tmdb», о которых оба списка уже знают, хранятся в kv kp_pairs.
    public static async Task<int> Run(string prof)
    {
        var sel = Selected();
        if (sel.Count == 0) { Last = "папки не выбраны"; return 0; }
        string tok = await KpStore.Token(prof);
        if (tok == null) { Last = "нет привязки Кинопаба"; return 0; }
        string target = sel.OrderBy(x => x).First();

        var seen = Load("kp_imported");
        JObject pairs;
        try { pairs = JObject.Parse(HomeDb.Get("kp_pairs") ?? "{}"); } catch { pairs = new JObject(); }

        // 1. что сейчас в выбранных папках (только фильмы): kp id -> tmdb (0, если соответствия пока нет)
        var inFolder = new Dictionary<string, long>();
        var folderOf = new Dictionary<string, string>();
        bool complete = true;
        foreach (var folder in sel)
        {
            for (int page = 1; page <= 10; page++)
            {
                var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/bookmarks/{folder}?perpage=100&page={page}", tok);
                if (code != 200) { complete = false; break; }
                var d = JObject.Parse(body);
                var items = ((JArray)(d["items"] ?? new JArray())).Children<JObject>().ToList();
                foreach (var it in items)
                {
                    string type = (string)it["type"] ?? "";
                    if (type == "serial" || type == "docuserial" || type == "tvshow") continue;   // сериалы живут в «Я смотрю»
                    string kpId = (string)it["id"];
                    long tmdb = pairs[kpId] != null ? (long)pairs[kpId] : 0;
                    if (tmdb == 0)
                    {
                        var card = await KpMap.ToTmdbCard(it, tok);
                        tmdb = card != null ? ((long?)card["id"] ?? 0) : 0;
                    }
                    inFolder[kpId] = tmdb;
                    folderOf[kpId] = folder;
                }
                int cur = (int?)d["pagination"]?["current"] ?? page, tot = (int?)d["pagination"]?["total"] ?? 1;
                if (cur >= tot || items.Count == 0) break;
            }
        }
        if (!complete) { Last = $"{DateTime.Now:HH:mm}: папка Кинопаба не прочиталась, обмен пропущен"; return 0; }

        var local = LocalWatchlist.Load().Children<JObject>().Where(x => (string)x["media"] == "movie").Select(x => (long)x["tmdb"]).ToHashSet();
        var progress = await KpUnified.MovieProgress();
        var watched = await KpUnified.MovieWatched();
        int added = 0, pushed = 0, removedLocal = 0, removedFolder = 0;

        // 2. изменения по уже известным парам
        foreach (var pr in pairs.Properties().ToList())
        {
            string kpId = pr.Name; long tmdb = (long)pr.Value;
            bool inF = inFolder.ContainsKey(kpId), inL = local.Contains(tmdb);
            if (inF && inL) continue;
            if (inF && !inL)
            {
                if (watched.Contains(tmdb) || !progress.Contains(tmdb))
                {
                    await KpStore.PostJson($"{KpStore.Api}/v1/bookmarks/remove-item", new { item = long.Parse(kpId), folder = long.Parse(folderOf[kpId]) }, tok);
                    inFolder.Remove(kpId); pairs.Remove(kpId); removedFolder++;
                }
                // иначе: фильм начали смотреть — он остаётся в папке до конца просмотра
            }
            else if (!inF && inL)
            {
                LocalWatchlist.Set("movie", tmdb, false); pairs.Remove(kpId); local.Remove(tmdb); removedLocal++;
            }
            else pairs.Remove(kpId);
        }

        // 3. новое из папки -> «Мои фильмы»
        foreach (var kv in inFolder.ToList())
        {
            if (kv.Value <= 0 || pairs[kv.Key] != null) continue;
            if (seen.Contains(kv.Key)) { if (local.Contains(kv.Value)) pairs[kv.Key] = kv.Value; continue; }
            if (!local.Contains(kv.Value)) { LocalWatchlist.Set("movie", kv.Value, true); KpUnified.Unhide("w_" + kv.Value + "_movie"); local.Add(kv.Value); added++; }
            pairs[kv.Key] = kv.Value; seen.Add(kv.Key);
        }

        // 4. новое из «Моих фильмов» -> папка
        int lookups = 0;
        var known = pairs.Properties().Select(x => (long)x.Value).ToHashSet();
        foreach (long tmdb in local.ToList())
        {
            if (known.Contains(tmdb)) continue;
            long kp = KpMap.CachedKp("movie", tmdb);
            if (kp == 0 && !KpMap.IsKnown("movie", tmdb) && lookups < 10) { lookups++; kp = await KpMap.LookupKp("movie", tmdb, prof); }
            if (kp <= 0 || inFolder.ContainsKey(kp.ToString())) continue;
            await KpStore.PostForm($"{KpStore.Api}/v1/bookmarks/add", new() { ["item"] = kp.ToString(), ["folder"] = target }, tok);
            pairs[kp.ToString()] = tmdb; seen.Add(kp.ToString()); pushed++;
        }

        Save("kp_imported", seen);
        HomeDb.Set("kp_pairs", pairs.ToString(Formatting.None));
        Last = $"{DateTime.Now:HH:mm}: в папках {inFolder.Count} фильмов · из Кинопаба +{added} · в папку +{pushed} · убрано из списка {removedLocal} · убрано из папки {removedFolder}";
        return added;
    }
}

public class KpImportController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = JsonConvert.SerializeObject(o),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };
    static bool AdminWrite(Microsoft.AspNetCore.Http.HttpRequest r, Microsoft.AspNetCore.Http.HttpContext ctx)
        => Guard.Ok(r) && (!Profiles.Enabled || Profiles.FromRequest(ctx)?.Admin == true);

    [HttpGet, AllowAnonymous]
    [Route("home/kp/folders")]
    public async Task<ActionResult> Folders()
    {
        if (!AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        return Json(new { folders = await KpImport.Folders(KpStore.DefaultProfile), last = KpImport.Last });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("home/kp/folders/set")]
    public async Task<ActionResult> SetFolders(string ids = "")
    {
        if (!AdminWrite(Request, HttpContext)) return Json(new { error = "admin_required" }, 403);
        KpImport.SetSelected((ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        int n = await KpImport.Run(KpStore.DefaultProfile);
        return Json(new { ok = true, added = n, last = KpImport.Last });
    }
}
