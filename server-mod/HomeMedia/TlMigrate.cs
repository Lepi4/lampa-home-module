using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HomeMedia;

// Перенос истории просмотров из штатной базы TimeCode (область home) в timeline HomeMedia.
// Режимы: inspect (только данные и группы id/hash), dry (решение по каждой исходной строке, ничего не пишет),
//         copy (применение на копии рабочей home.db + проверки + повторный запуск + откат), live (рабочая база; только по подтверждению и сверке отчёта).
// Правило выбора для сопоставленных строк сохраняет нынешний результат наложения Overlay: побеждает более позднее время действия (u против watched_at),
// при равенстве — запись TimeCode (как сейчас). Время переноса нигде не становится временем просмотра: u всегда исходное.
public static class TlMigrate
{
    public class Tc { public string Card, Item, Identity; public double Pos, Dur, Pct; public long Watched, Updated; public bool Deleted; }

    public class Dec
    {
        public string H, Fate, Rule, Note;
        public TlV2.Row Before, After;
        public Tc Source;
        public bool Change => After != null;
        public bool Semantic;                 // расхождение по смыслу (завершён/сброс/нет), а не просто по числам
        public JObject ToJson() => new JObject
        {
            ["h"] = H, ["fate"] = Fate, ["rule"] = Rule, ["note"] = Note, ["semantic"] = Semantic ? true : (bool?)null,
            ["timeline"] = Before == null ? null : Img(Before),
            ["timecode"] = Source == null ? null : new JObject { ["id"] = Source.Identity, ["card"] = Source.Card, ["t"] = Source.Pos, ["d"] = Source.Dur, ["p"] = Source.Pct, ["u"] = Source.Watched, ["deleted"] = Source.Deleted },
            ["result"] = After == null ? (Before == null ? null : Img(Before)) : Img(After)
        };
    }

    static JObject Img(TlV2.Row r) => new JObject { ["t"] = r.T, ["d"] = r.D, ["p"] = r.P, ["u"] = r.U, ["c"] = r.C, ["id"] = r.Id, ["op"] = r.Op, ["event"] = r.Event };

    static string TcPath => Path.Combine(Directory.GetCurrentDirectory(), "database", "TimeCode.sql");

    public static List<Tc> ReadTc(string area = "home")
    {
        var res = new List<Tc>();
        using var c = new SqliteConnection("Data Source=" + TcPath + ";Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT card,item,identity,position,duration,percent,watched_at,updated_at,deleted FROM timecodes WHERE user=@u";
        cmd.Parameters.AddWithValue("@u", area);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            res.Add(new Tc
            {
                Card = r.IsDBNull(0) ? "" : r.GetString(0), Item = r.IsDBNull(1) ? null : r.GetString(1), Identity = r.IsDBNull(2) ? null : r.GetString(2),
                Pos = r.GetDouble(3), Dur = r.GetDouble(4), Pct = r.GetDouble(5), Watched = r.GetInt64(6), Updated = r.GetInt64(7), Deleted = r.GetInt64(8) != 0
            });
        return res;
    }

    static bool DoneTl(TlV2.Row r) => KpUnified.IsDone(r.T, r.D, r.P);
    static bool DoneTc(Tc t) => !t.Deleted && KpUnified.IsDone(t.Pos, t.Dur, t.Pct);

    static TlV2.Row FromTc(Tc t, TlV2.Row baseRow)
    {
        long u = t.Watched > 0 ? t.Watched : t.Updated;
        var r = new TlV2.Row
        {
            H = t.Item, C = t.Card ?? "", Id = t.Identity ?? baseRow?.Id, U = u, S = baseRow?.S ?? 0,
            T = t.Deleted ? 0 : (long)Math.Round(t.Pos), D = (long)Math.Round(t.Dur), P = t.Deleted ? 0 : Math.Clamp(t.Pct, 0, 100),
            Event = "0:0", Session = null, Op = t.Deleted ? "reset" : null          // событие переноса: наименьшее из возможных, на равном времени любое настоящее его перебивает
        };
        return r;
    }

    // Решение по каждой строке обоих источников. Ничего не пишет.
    public static List<Dec> Plan(SqliteConnection home, List<Tc> tcAll)
    {
        var decs = new List<Dec>();
        var tl = TlV2.All(home).ToDictionary(r => r.H);
        var tcByHash = new Dictionary<string, Tc>();
        foreach (var t in tcAll)
        {
            if (string.IsNullOrEmpty(t.Item)) { decs.Add(new Dec { H = null, Fate = "unresolved_no_hash", Rule = "TimeCode-строка без хеша: в timeline её ключ (h) получить негде", Source = t }); continue; }
            if (tcByHash.ContainsKey(t.Item)) { decs.Add(new Dec { H = t.Item, Fate = "unresolved_duplicate_hash", Rule = "два разных эпизода TimeCode с одним хешем", Source = t }); continue; }
            tcByHash[t.Item] = t;
        }
        foreach (var kv in tcByHash)
        {
            var t = kv.Value; string h = kv.Key;
            if (!tl.TryGetValue(h, out var l))
            {
                decs.Add(new Dec { H = h, Fate = "inserted_from_timecode", Rule = "есть только в TimeCode: добавляется с исходным временем и identity", Source = t, After = FromTc(t, null) });
                continue;
            }
            long tu = t.Watched;                                                  // как в Overlay: watched_at
            bool cardMismatch = l.C != "" && !string.IsNullOrEmpty(t.Card) && l.C != t.Card;
            bool sem = DoneTl(l) != DoneTc(t) || (t.Deleted && (l.T > 0 || l.P > 0));
            if (cardMismatch)
            {
                decs.Add(new Dec { H = h, Fate = "unresolved_card_mismatch", Rule = "карточки различаются — строка не меняется, нужен разбор", Before = l, Source = t, Semantic = sem, Note = $"timeline {l.C} против TimeCode {t.Card}" });
                continue;
            }
            if (l.U > tu)
            {
                // побеждает timeline; недостающие карточку и identity берём из TimeCode (новая ревизия — клиенты их получат)
                bool enrich = (l.Id == null && !string.IsNullOrEmpty(t.Identity)) || (l.C == "" && !string.IsNullOrEmpty(t.Card));
                TlV2.Row after = null;
                if (enrich) { after = Copy(l); after.Id ??= t.Identity; if (after.C == "") after.C = t.Card; }
                decs.Add(new Dec { H = h, Fate = enrich ? "timeline_wins_enriched" : "timeline_wins", Rule = "время действия timeline позже: состояние не меняется", Before = l, Source = t, After = after, Semantic = sem });
            }
            else
            {
                var after = FromTc(t, l);
                if (l.C != "" ) after.C = l.C;
                bool same = l.T == after.T && l.D == after.D && Math.Abs(l.P - after.P) < 0.05 && l.U == after.U && (l.Id ?? "") == (after.Id ?? "") && l.C == after.C;
                decs.Add(new Dec { H = h, Fate = same ? "equal" : "timecode_wins", Rule = "время действия TimeCode не раньше (при равенстве — TimeCode, как в Overlay)", Before = l, Source = t, After = same ? null : after, Semantic = sem });
            }
        }
        foreach (var l in tl.Values)
            if (!tcByHash.ContainsKey(l.H))
                decs.Add(new Dec { H = l.H, Fate = l.C == "" ? "timeline_only_no_card" : "timeline_only", Rule = "есть только в timeline: остаётся как есть (id пустой, доступ по хешу сохранён)", Before = l });
        return decs;
    }

    static TlV2.Row Copy(TlV2.Row r) => new TlV2.Row { H = r.H, C = r.C, Id = r.Id, Event = r.Event, Session = r.Session, Op = r.Op, T = r.T, D = r.D, U = r.U, S = r.S, P = r.P };

    // Применение решений в одной транзакции; возвращает журнал «до» для отката.
    public static JArray Apply(SqliteConnection c, List<Dec> decs, long minS, Action<JArray> beforeCommit = null)
    {
        var ledger = new JArray();
        long maxS = Math.Max(TlV2.MaxS(c), minS);
        using var tx = c.BeginTransaction();
        foreach (var d in decs.Where(x => x.Change))
        {
            ledger.Add(new JObject { ["h"] = d.H, ["before"] = d.Before == null ? null : FullImg(d.Before), ["after"] = new JObject { ["u"] = d.After.U, ["event"] = d.After.Event, ["t"] = d.After.T, ["d"] = d.After.D, ["p"] = d.After.P } });
            maxS = Math.Max(HomeDb.Now(), maxS + 1);
            var a = d.After;
            HomeDb.Exec(c, @"INSERT INTO timeline(profile_id,h,c,t,d,p,u,s,id,event,session,op) VALUES(@profile,@h,@c,@t,@d,@p,@u,@s,@id,@e,@se,@op)
                             ON CONFLICT(profile_id,h) DO UPDATE SET c=@c,t=@t,d=@d,p=@p,u=@u,s=@s,id=@id,event=@e,session=@se,op=@op",
                ("@h", d.H), ("@c", a.C ?? ""), ("@t", a.T), ("@d", a.D), ("@p", a.P), ("@u", a.U), ("@s", maxS), ("@id", a.Id), ("@e", a.Event), ("@se", a.Session), ("@op", a.Op));
        }
        beforeCommit?.Invoke(ledger);              // журнал сохраняется ДО фиксации: сбой записи журнала отменяет перенос, а не оставляет его без отката
        tx.Commit();
        return ledger;
    }

    static JObject FullImg(TlV2.Row r) => new JObject { ["c"] = r.C, ["t"] = r.T, ["d"] = r.D, ["p"] = r.P, ["u"] = r.U, ["s"] = r.S, ["id"] = r.Id, ["event"] = r.Event, ["session"] = r.Session, ["op"] = r.Op };

    // Откат только того, что перенос записал и что с тех пор не менялось: строка, изменённая новым просмотром, остаётся как есть. Возвращает (восстановлено, оставлено новее)
    public static (int restored, int keptNewer) Rollback(SqliteConnection c, JArray ledger)
    {
        int restored = 0, kept = 0;
        using var tx = c.BeginTransaction();
        foreach (var e in ledger.Children<JObject>().Reverse())
        {
            string h = (string)e["h"];
            var cur = TlV2.Get(c, h);
            if (e["after"] is JObject af && cur != null && (cur.U != (long)af["u"] || (cur.Event ?? "") != ((string)af["event"] ?? ""))) { kept++; continue; }
            restored++;
            if (e["before"] is not JObject b || b.Type == JTokenType.Null) { HomeDb.Exec(c, "DELETE FROM timeline WHERE profile_id=@profile AND h=@h", ("@h", h)); continue; }
            HomeDb.Exec(c, "UPDATE timeline SET c=@c,t=@t,d=@d,p=@p,u=@u,s=@s,id=@id,event=@e,session=@se,op=@op WHERE profile_id=@profile AND h=@h",
                ("@h", h), ("@c", (string)b["c"] ?? ""), ("@t", (long)b["t"]), ("@d", (long)b["d"]), ("@p", (double)b["p"]), ("@u", (long)b["u"]), ("@s", (long)b["s"]),
                ("@id", (string)b["id"]), ("@e", (string)b["event"]), ("@se", (string)b["session"]), ("@op", (string)b["op"]));
        }
        tx.Commit();
        return (restored, kept);
    }

    public static string Snapshot(SqliteConnection c)
        => Sha(JsonConvert.SerializeObject(HomeDb.Query(c, "SELECT h,c,t,d,p,u,s,id,event,session,op FROM timeline WHERE profile_id=@profile ORDER BY h")));

    public static string Sha(string s) => BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();

    public static string ReportId(List<Dec> decs)
        => Sha(JsonConvert.SerializeObject(decs.Select(d => new { d.H, d.Fate, a = d.After == null ? null : Img(d.After).ToString(Formatting.None) }))).Substring(0, 12);

    // Откуда сейчас берут данные читатели в режиме legacy: штатный дамп TimeCode + наложение timeline
    static JObject LegacyDump(List<Tc> tc)
    {
        var rows = new JArray();
        foreach (var t in tc)
            rows.Add(new JObject { ["id"] = t.Identity, ["hash"] = t.Item, ["card"] = t.Card, ["position"] = t.Pos, ["duration"] = t.Dur, ["percent"] = t.Pct, ["profile"] = 0, ["deleted"] = t.Deleted, ["watched_at"] = t.Watched, ["updated_at"] = t.Updated });
        return TlStore.Overlay(new JObject { ["rows"] = rows }, false);
    }

    // Что изменится в выдаче (читатели по card|hash): было (legacy) → будет (unified)
    static JArray DiffViews(JObject legacy, JObject unified)
    {
        JObject Key(JObject d) { var o = new JObject(); foreach (var r in ((JArray)d["rows"]).Children<JObject>()) { if ((bool?)r["deleted"] == true) continue; string k = (string)r["card"] + "|" + (string)r["hash"]; if (!string.IsNullOrEmpty((string)r["card"])) o[k] = r; } return o; }
        var a = Key(legacy); var b = Key(unified);
        var diff = new JArray();
        foreach (var k in a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name)))
        {
            var x = a[k] as JObject; var y = b[k] as JObject;
            if (x == null) { diff.Add(new JObject { ["key"] = k, ["change"] = "appears", ["to"] = Brief(y) }); continue; }
            if (y == null) { diff.Add(new JObject { ["key"] = k, ["change"] = "disappears", ["from"] = Brief(x) }); continue; }
            double xp = (double)x["position"], yp = (double)y["position"], xd = (double)x["duration"], yd = (double)y["duration"], xc = (double)x["percent"], yc = (double)y["percent"];
            bool dx = KpUnified.IsDone(xp, xd, xc), dy = KpUnified.IsDone(yp, yd, yc);
            if (dx != dy || Math.Abs(xp - yp) > 1 || (long)x["watched_at"] != (long)y["watched_at"])
                diff.Add(new JObject { ["key"] = k, ["change"] = dx != dy ? "done_changes" : "values", ["from"] = Brief(x), ["to"] = Brief(y) });
        }
        return diff;
    }

    static JObject Brief(JObject r) => new JObject { ["t"] = r["position"], ["d"] = r["duration"], ["p"] = r["percent"], ["u"] = r["watched_at"], ["id"] = r["id"] };

    public static JObject Summary(List<Dec> decs)
    {
        var by = decs.GroupBy(d => d.Fate).ToDictionary(g => g.Key, g => g.Count());
        return new JObject
        {
            ["by_fate"] = JObject.FromObject(by), ["total"] = decs.Count, ["will_change"] = decs.Count(d => d.Change),
            ["semantic_conflicts"] = decs.Count(d => d.Semantic), ["unresolved"] = decs.Count(d => d.Fate.StartsWith("unresolved")), ["report_id"] = ReportId(decs)
        };
    }

    // Группы для проверки ключей: один id на несколько хешей и один хеш с разными карточками/id (на целевой схеме)
    public static JObject KeyGroups(SqliteConnection c, List<Dec> decs)
    {
        var final = new Dictionary<string, TlV2.Row>();
        foreach (var d in decs) if (d.H != null) { var r = d.After ?? d.Before; if (r != null) final[d.H] = r; }
        var idMany = final.Values.Where(r => r.Id != null).GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => new JObject { ["id"] = g.Key, ["hashes"] = new JArray(g.Select(x => x.H)) });
        return new JObject { ["id_with_many_hashes"] = new JArray(idMany), ["rows_without_id"] = final.Values.Count(r => r.Id == null), ["rows_without_card"] = final.Values.Count(r => string.IsNullOrEmpty(r.C)), ["rows_total"] = final.Count };
    }

    // Полная проверка на копии рабочей базы
    public static JObject RunOnCopy(bool full)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "tlmig-" + Guid.NewGuid().ToString("N") + ".db");
        var checks = new JArray();
        void Chk(string name, bool ok, string info = "") => checks.Add(new JObject { ["name"] = name, ["ok"] = ok, ["info"] = info });
        try
        {
            HomeDb.With(src => { using var dst = new SqliteConnection("Data Source=" + tmp); dst.Open(); src.BackupDatabase(dst); });
            using var c = new SqliteConnection("Data Source=" + tmp); c.Open();
            TlV2.EnsureSchema(c);
            var tc = ReadTc();
            string snap0 = Snapshot(c);
            var legacy = LegacyDump(tc);                                   // как читатели видят историю сейчас
            var decs = Plan(c, tc);
            var sum = Summary(decs);
            Chk("every source row has a fate (timeline + timecode)", decs.Count(d => d.Fate != "") == decs.Count && decs.Count >= tc.Count, $"decisions={decs.Count}, timecode rows={tc.Count}");
            Chk("no unresolved rows", (int)sum["unresolved"] == 0, JsonConvert.SerializeObject(decs.Where(d => d.Fate.StartsWith("unresolved")).Select(d => new { d.H, d.Fate, d.Note })));
            var ledger = Apply(c, decs, tc.Count == 0 ? 0 : tc.Max(t => t.Updated));
            Chk("quick_check ok after apply", (string)HomeDb.Query(c, "PRAGMA quick_check")[0][0] == "ok");
            // неизменность выбранных состояний: у побед timeline значения те же; у побед TimeCode — значения TimeCode
            bool vals = true; string badH = "";
            foreach (var d in decs.Where(x => x.H != null))
            {
                var now = TlV2.Get(c, d.H);
                if (now == null) { vals = false; badH = d.H; break; }
                var exp = d.After ?? d.Before;
                if (exp != null && (now.T != exp.T || now.D != exp.D || Math.Abs(now.P - exp.P) > 0.05 || now.U != exp.U)) { vals = false; badH = d.H; break; }
            }
            Chk("stored values equal decided values; original action time (u) kept", vals, badH);
            Chk("no row got migration time as action time", decs.Where(d => d.Change && d.After != null).All(d => d.After.U <= HomeDb.Now() - 1000 || d.After.U == d.Before?.U));
            // повторный запуск
            var decs2 = Plan(c, tc);
            Chk("second run changes nothing (idempotent)", decs2.All(d => !d.Change), $"would change {decs2.Count(d => d.Change)}");
            var uni = TlV2.DumpFrom(c);
            var diff = DiffViews(legacy, uni);
            // откат
            string snap1 = Snapshot(c);
            Rollback(c, ledger);
            Chk("rollback restores the original timeline exactly", Snapshot(c) == snap0 && snap1 != snap0 || ledger.Count == 0, $"ledger={ledger.Count}");
            // вторая применённая копия для сравнения с эталоном после отката
            return new JObject
            {
                ["mode"] = "copy", ["summary"] = sum, ["checks"] = checks, ["checks_passed"] = checks.Count(x => (bool)x["ok"]), ["checks_total"] = checks.Count,
                ["key_groups"] = KeyGroups(c, decs),
                ["view_changes"] = diff, ["semantic_conflicts"] = new JArray(decs.Where(d => d.Semantic).Select(d => d.ToJson())),
                ["decisions"] = full ? new JArray(decs.Select(d => d.ToJson())) : null
            };
        }
        finally { try { System.IO.File.Delete(tmp); System.IO.File.Delete(tmp + "-wal"); System.IO.File.Delete(tmp + "-shm"); } catch { } }
    }
}

public class TlMigrateController : BaseController
{
    static ActionResult J(object o, int code = 200) => new ContentResult { Content = o is string s ? s : JsonConvert.SerializeObject(o, Formatting.None), ContentType = "application/json; charset=utf-8", StatusCode = code };

    // GET /home/ops/tlmig?mode=inspect|dry|copy[&full=1]  — ничего не меняет;  mode=live&confirm=YES&report=<id> — рабочее применение
    [HttpGet, AllowAnonymous]
    [Route("home/ops/tlmig")]
    public ActionResult Run(string mode = "inspect", int full = 0, string confirm = null, string report = null)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        try
        {
            if (mode == "copy") return J(TlMigrate.RunOnCopy(full == 1).ToString(Formatting.None));
            var tc = TlMigrate.ReadTc();
            if (mode == "inspect" || mode == "dry")
            {
                return HomeDb.With(c =>
                {
                    var decs = TlMigrate.Plan(c, tc);
                    var o = new JObject { ["mode"] = mode, ["columns"] = new JArray(HomeDb.Query(c, "PRAGMA table_info(timeline)").Select(r => (string)r[1])), ["timecode_rows"] = tc.Count,
                                          ["timeline_rows"] = Convert.ToInt64(HomeDb.Query(c, "SELECT COUNT(*) FROM timeline WHERE profile_id=@profile")[0][0]), ["summary"] = TlMigrate.Summary(decs), ["key_groups"] = TlMigrate.KeyGroups(c, decs),
                                          ["mode_now"] = TlV2.Unified ? "unified" : "legacy", ["epoch"] = TlV2.Epoch };
                    if (mode == "dry") o["decisions"] = new JArray(decs.Select(d => d.ToJson()));
                    return J(o.ToString(Formatting.None));
                });
            }
            if (mode == "live")
            {
                if (confirm != "YES") return J(new { error = "confirm=YES required" }, 400);
                return HomeDb.With(c =>
                {
                    var decs = TlMigrate.Plan(c, tc);
                    string id = TlMigrate.ReportId(decs);
                    if (report != id) return J(new { error = "report mismatch: data changed since the dry run or wrong id", current_report_id = id }, 409);
                    if (decs.Any(d => d.Fate.StartsWith("unresolved"))) return J(new { error = "unresolved rows", report_id = id }, 409);
                    // свои «до»-копии: целостная копия базы и журнал отката рядом
                    string dir = Path.Combine(Directory.GetCurrentDirectory(), "database", "backup", "home");
                    Directory.CreateDirectory(dir);
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string bak = Path.Combine(dir, $"pre-migration-{stamp}.db");
                    HomeDb.Exec(c, "VACUUM INTO '" + bak.Replace("'", "''") + "'");
                    string pend = Path.Combine(dir, $"pre-migration-{stamp}.ledger.json.pending");
                    var ledger = TlMigrate.Apply(c, decs, tc.Count == 0 ? 0 : tc.Max(t => t.Updated), l => System.IO.File.WriteAllText(pend, l.ToString(Formatting.None)));
                    System.IO.File.Move(pend, Path.Combine(dir, $"pre-migration-{stamp}.ledger.json"), true);         // журнал получает окончательное имя только после успешной фиксации
                    HomeDb.Exec(c, "INSERT INTO kv(k,v) VALUES('tl_migration',@v) ON CONFLICT(k) DO UPDATE SET v=@v", ("@v", JsonConvert.SerializeObject(new { at = HomeDb.Now(), report = id, changed = ledger.Count, backup = Path.GetFileName(bak) })));
                    return J(new { ok = true, report_id = id, changed = ledger.Count, backup = bak, ledger = $"pre-migration-{stamp}.ledger.json" });
                });
            }
            return J(new { error = "mode" }, 400);
        }
        catch (Exception ex) { return J(new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace }, 500); }
    }

    // Восстановление послеобразов старого журнала: повторный расчёт решений по предмиграционной копии базы и неизменному TimeCode. Результат — новый файл *.v2.json; исходный не меняется.
    [HttpGet, AllowAnonymous]
    [Route("home/ops/tlledger")]
    public ActionResult UpgradeLedger(string name = null, int apply = 0)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        if (string.IsNullOrEmpty(name) || name.Contains("/") || name.Contains("..") || !name.EndsWith(".ledger.json")) return J(new { error = "name" }, 400);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "database", "backup", "home");
        string lp = Path.Combine(dir, name), dbp = Path.Combine(dir, name.Replace(".ledger.json", ".db"));
        if (!System.IO.File.Exists(lp) || !System.IO.File.Exists(dbp)) return J(new { error = "ledger or pre-migration db not found" }, 404);
        var old = JArray.Parse(System.IO.File.ReadAllText(lp));
        string tmp = Path.Combine(Path.GetTempPath(), "tlledger-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            System.IO.File.Copy(dbp, tmp, true);
            using var c = new SqliteConnection("Data Source=" + tmp); c.Open();
            TlV2.EnsureSchema(c);
            var decs = TlMigrate.Plan(c, TlMigrate.ReadTc()).Where(d => d.Change).ToList();
            var want = decs.Select(d => d.H).OrderBy(x => x).ToList();
            var have = old.Children<JObject>().Select(e => (string)e["h"]).OrderBy(x => x).ToList();
            bool same = want.SequenceEqual(have);
            // дополнительная проверка: у каждой строки «до» в старом журнале совпадает с «до» по пересчёту
            int beforeMismatch = 0;
            foreach (var e in old.Children<JObject>())
            {
                var d = decs.FirstOrDefault(x => x.H == (string)e["h"]); if (d == null) continue;
                var b = e["before"] as JObject;
                if ((d.Before == null) != (b == null || b.Type == JTokenType.Null)) { beforeMismatch++; continue; }
                if (d.Before != null && ((long)b["u"] != d.Before.U || (long)b["t"] != d.Before.T)) beforeMismatch++;
            }
            if (!same || beforeMismatch > 0) return J(new { ok = false, reason = "recomputed decisions do not match the ledger", ledger = have.Count, recomputed = want.Count, before_mismatch = beforeMismatch }, 409);
            var res = new JArray();
            foreach (var e in old.Children<JObject>())
            {
                var d = decs.First(x => x.H == (string)e["h"]);
                var o = (JObject)e.DeepClone();
                o["after"] = new JObject { ["u"] = d.After.U, ["event"] = d.After.Event, ["t"] = d.After.T, ["d"] = d.After.D, ["p"] = d.After.P };
                res.Add(o);
            }
            if (apply == 1) System.IO.File.WriteAllText(lp.Replace(".ledger.json", ".ledger.v2.json"), res.ToString(Formatting.None));
            return J(new { ok = true, entries = res.Count, written = apply == 1 ? Path.GetFileName(lp.Replace(".ledger.json", ".ledger.v2.json")) : null });
        }
        finally { try { System.IO.File.Delete(tmp); } catch { } }
    }

    // Переключение режима чтения и поколения синхронизации. unified — читатели берут только timeline; legacy — прежний режим (мгновенный возврат).
    [HttpGet, AllowAnonymous]
    [Route("home/ops/tlmode")]
    public ActionResult Mode(string set = null, string confirm = null, int bump = 0)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        if (set != null)
        {
            if (confirm != "YES") return J(new { error = "confirm=YES required" }, 400);
            if (set != "unified" && set != "legacy") return J(new { error = "set" }, 400);
            HomeDb.Set("tl_mode", set);
            if (bump == 1) HomeDb.Set("tl_epoch", (long.TryParse(TlV2.Epoch, out long e) ? e + 1 : 2).ToString());
        }
        return J(new { mode = TlV2.Unified ? "unified" : "legacy", epoch = TlV2.Epoch, migration = HomeDb.Get("tl_migration") });
    }

    // Откат применённого переноса по журналу (после отката режим возвращается в legacy)
    [HttpGet, AllowAnonymous]
    [Route("home/ops/tlrollback")]
    public ActionResult RollbackLive(string ledger = null, string confirm = null)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        if (confirm != "YES" || string.IsNullOrEmpty(ledger) || ledger.Contains("/") || ledger.Contains("..")) return J(new { error = "confirm=YES and ledger name required" }, 400);
        string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "backup", "home", ledger);
        if (!System.IO.File.Exists(path)) return J(new { error = "ledger not found" }, 404);
        var l = JArray.Parse(System.IO.File.ReadAllText(path));
        // журнал без послеобразов не позволяет отличить новый просмотр от результата переноса: откат запрещён (сначала /home/ops/tlledger)
        if (l.Children<JObject>().Any(e => e["after"] is not JObject)) return J(new { error = "ledger without after-images: upgrade it first (home/ops/tlledger)", entries = l.Count }, 409);
        var (restored, kept) = HomeDb.With(c => TlMigrate.Rollback(c, l));
        HomeDb.Set("tl_mode", "legacy");
        HomeDb.Set("tl_epoch", (long.TryParse(TlV2.Epoch, out long e0) ? e0 + 1 : 2).ToString());         // клиенты получают полный снимок после отката
        return J(new { ok = true, restored, kept_newer = kept, mode = "legacy", epoch = TlV2.Epoch });
    }
}
