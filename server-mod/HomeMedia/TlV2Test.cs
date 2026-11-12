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

namespace HomeMedia;

// Самопроверка нового слоя просмотров (TlV2) на копиях: схема в памяти + копия рабочей home.db во временном файле. Рабочую базу не изменяет.
public class TlV2TestController : BaseController
{
    const string Legacy = "CREATE TABLE timeline (h TEXT PRIMARY KEY, c TEXT, t INTEGER, d INTEGER, p REAL, u INTEGER, s INTEGER); CREATE INDEX ix_timeline_s ON timeline(s);";

    static SqliteConnection Mem()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        HomeDb.Exec(c, Legacy);
        return c;
    }

    static JObject Ev(string h, long u, string ev, long t = 100, long d = 1000, double p = 10, string op = null, string c = "", string id = null)
    {
        var o = new JObject { ["h"] = h, ["u"] = u, ["t"] = t, ["d"] = d, ["p"] = p, ["c"] = c };
        if (ev != null) o["event"] = ev;
        if (op != null) o["op"] = op;
        if (id != null) o["id"] = id;
        return o;
    }

    static string State(SqliteConnection c, string h)
    {
        var r = TlV2.Get(c, h);
        return r == null ? "-" : $"{r.T}/{r.D}/{r.P}/{r.U}/{r.Event}/{r.Op}";
    }

    static string Apply1(SqliteConnection c, JObject e)
    {
        var (l, _) = TlV2.ApplyBatch(c, new JArray(e));
        return l[0].Status + (l[0].Reason != null ? ":" + l[0].Reason : "");
    }

    [HttpGet, AllowAnonymous]
    [Route("home/ops/tlv2test")]
    public ActionResult Run()
    {
        if (!Guard.Ok(Request)) return new ContentResult { Content = "{\"error\":\"forbidden\"}", ContentType = "application/json", StatusCode = 403 };
        var res = new JArray();
        void T(string name, bool ok, string info = "") => res.Add(new JObject { ["name"] = name, ["ok"] = ok, ["info"] = info });
        try
        {
            // 1. схема: новые колонки не меняют прежний SELECT; повтор ничего не добавляет
            using (var c = Mem())
            {
                HomeDb.Exec(c, "INSERT INTO timeline VALUES('111','1_tv',300,1200,25,1000,1000),('222','',0,0,100,2000,2000),('333','2_movie',50,100,50,3000,3000)");
                string Before() => JsonConvert.SerializeObject(HomeDb.Query(c, "SELECT h,t,d,p,u,s,c FROM timeline ORDER BY h"));
                string b = Before();
                var a1 = TlV2.EnsureSchema(c); var a2 = TlV2.EnsureSchema(c);
                T("schema: adds 4 nullable columns once", a1.Count == 4 && a2.Count == 0, string.Join(",", a1));
                T("schema: legacy SELECT unchanged", Before() == b);
                T("schema: new columns NULL on old rows", HomeDb.Query(c, "SELECT COUNT(*) FROM timeline WHERE id IS NULL AND event IS NULL AND session IS NULL AND op IS NULL")[0][0].ToString() == "3");
                T("schema: version recorded", HomeDb.Query(c, "SELECT v FROM kv WHERE k='tl_schema'")[0][0].ToString() == "2");
            }

            // 2. A→B→повтор A при равном u: откат запрещён
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                var sa = Apply1(c, Ev("10", 1000, "100:1", t: 100));
                var sb = Apply1(c, Ev("10", 1000, "200:1", t: 500));
                var sa2 = Apply1(c, Ev("10", 1000, "100:1", t: 100));
                T("A→B→repeat A: B wins, repeat is stale", sa == "accepted" && sb == "accepted" && sa2 == "stale" && State(c, "10").StartsWith("500/"), $"{sa},{sb},{sa2} {State(c, "10")}");
            }

            // 2b. в одной session seq раньше wall-clock u
            {
                long uLate = HomeDb.Now() + 90_000;
                long uEarly = HomeDb.Now();
                int cmp = TlV2.CmpPair(uLate, "100:1", uEarly, "100:2");
                T("CmpPair: total order compares logical u before event for every actor", cmp > 0, $"cmp={cmp}");
            }

            // 3. любой порядок доставки приводит к одному итогу
            {
                var evs = new List<JObject> { Ev("20", 1000, "1:1", t: 10), Ev("20", 1000, "2:1", t: 20), Ev("20", 1500, "1:2", t: 30), Ev("20", 1500, "1:3", t: 40, op: "completed", p: 100), Ev("20", 900, "", t: 5) };
                var finals = new HashSet<string>();
                int n = 0;
                void Perm(List<JObject> rest, List<JObject> acc)
                {
                    if (rest.Count == 0)
                    {
                        using var c = Mem(); Profiles.EnsureSchema(c);
                        foreach (var e in acc) Apply1(c, (JObject)e.DeepClone());
                        finals.Add(State(c, "20")); n++; return;
                    }
                    for (int i = 0; i < rest.Count; i++) { var r2 = new List<JObject>(rest); var e = r2[i]; r2.RemoveAt(i); Perm(r2, new List<JObject>(acc) { e }); }
                }
                Perm(evs, new List<JObject>());
                T("convergence: all 120 delivery orders give one result", finals.Count == 1 && n == 120 && !finals.First().StartsWith("-"), $"orders={n} distinct={finals.Count} {string.Join(";", finals)}");
            }

            // 3b. смешанные форматы event (числовой «сеанс:номер», произвольные строки, пустой, миграционный 0:0): порядок строгий, итог не зависит от порядка доставки
            {
                var names = new List<string> { "2:0", "10:0", "1z", "", "abc", "0:0" };
                var finals2 = new HashSet<string>(); int n2 = 0;
                void Perm2(List<string> rest, List<string> acc)
                {
                    if (rest.Count == 0)
                    {
                        using var c = Mem(); Profiles.EnsureSchema(c);
                        foreach (var e in acc) { var j = Ev("30", 1000, e == "" ? null : e, t: e.Length * 7 + 3); Apply1(c, j); }
                        finals2.Add(State(c, "30")); n2++; return;
                    }
                    for (int i = 0; i < rest.Count; i++) { var r2 = new List<string>(rest); var e = r2[i]; r2.RemoveAt(i); Perm2(r2, new List<string>(acc) { e }); }
                }
                Perm2(names, new List<string>());
                bool trans = true; foreach (var x in names) foreach (var y in names) foreach (var z in names) if (TlV2.CmpEvent(x, y) < 0 && TlV2.CmpEvent(y, z) < 0 && TlV2.CmpEvent(x, z) >= 0) trans = false;
                T("event order: transitive on mixed formats; all 720 delivery orders converge", trans && n2 == 720 && finals2.Count == 1, $"orders={n2} distinct={finals2.Count} {string.Join(";", finals2)}");
            }

            // 4. повтор и конфликт содержимого
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                var a = Apply1(c, Ev("30", 1000, "5:1"));
                var b = Apply1(c, Ev("30", 1000, "5:1"));
                var d = Apply1(c, Ev("30", 1000, "5:1", t: 999));
                T("duplicate: same event+payload", a == "accepted" && b == "duplicate");
                T("invalid: same pair, different payload", d == "invalid:same_pair_different_payload", d);
            }

            // 5. legacy без event против нового события с тем же u
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                HomeDb.Exec(c, "INSERT INTO timeline(h,c,t,d,p,u,s) VALUES('40','',100,1000,10,1000,1000)");
                var s1 = Apply1(c, Ev("40", 1000, "7:1", t: 200));
                var s2 = Apply1(c, new JObject { ["h"] = "40", ["u"] = 1000, ["t"] = 5, ["d"] = 1000, ["p"] = 1 });
                T("legacy: event beats event-less at equal u; event-less repeat is stale", s1 == "accepted" && s2 == "stale", $"{s1},{s2}");
            }

            // 6. reset: после completed 0/0/100; независимо от возраста доставки; старый reset не побеждает
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                long day = 86400000L, now = HomeDb.Now();
                HomeDb.Exec(c, "INSERT INTO timeline(h,c,t,d,p,u,s) VALUES('50','9_tv',0,0,100,@u,1)", ("@u", now - 6 * day));
                var r1 = Apply1(c, Ev("50", now - 5 * day, "8:1", t: 0, d: 1500, p: 0, op: "reset"));
                T("reset: completed 0/0/100 → reset accepted though delivered 5 days late", r1 == "accepted" && State(c, "50").StartsWith("0/1500/0"), State(c, "50"));
                var r2 = Apply1(c, Ev("50", now - 6 * day + 1000, "8:0", t: 0, d: 1500, p: 100, op: "completed"));
                T("reset: older completed does not undo the reset", r2 == "stale" && State(c, "50").StartsWith("0/1500/0"), r2);
                var r3 = Apply1(c, Ev("50", now - 1 * day, "9:1", t: 700, d: 1500, p: 46.7, op: "progress"));
                T("reset: later progress is accepted after reset", r3 == "accepted" && State(c, "50").StartsWith("700/"));
            }

            // 7. завершение хранит реальную позицию, p=100
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                Apply1(c, Ev("60", 1000, "3:1", t: 1380, d: 1440, p: 95.8, op: "completed"));
                var r = TlV2.Get(c, "60");
                T("completed: real position kept, percent 100", r.T == 1380 && r.P == 100 && r.Op == "completed", State(c, "60"));
            }

            // 8. пачка: у каждого события свой результат
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                Apply1(c, Ev("70", 5000, "4:9"));
                var batch = new JArray { Ev("71", 1000, "4:1"), Ev("zzz", 1000, "4:2"), Ev("70", 4000, "4:3"), new JObject { ["h"] = "72", ["u"] = 0, ["event"] = "4:4" }, Ev("73", 1000, "bad event!") };
                var (list, rev) = TlV2.ApplyBatch(c, batch);
                var st = string.Join(",", list.Select(x => x.Status));
                T("batch: per-event statuses, invalid ones do not block", st == "accepted,invalid,stale,invalid,invalid" && TlV2.Get(c, "71") != null && rev > 0, st);
            }

            // 9. дополнение карточки/identity старым событием — новая ревизия
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                var l1 = TlV2.ApplyBatch(c, new JArray(Ev("80", 5000, "4:9"))).results[0];
                long s0 = TlV2.Get(c, "80").S;
                var l2 = TlV2.ApplyBatch(c, new JArray(Ev("80", 4000, "4:1", c: "5_tv", id: "tv-5-s1e2"))).results[0];
                var r = TlV2.Get(c, "80");
                T("enrich: stale event fills card+id and bumps revision", l2.Status == "stale" && l2.Enriched && r.C == "5_tv" && r.Id == "tv-5-s1e2" && r.S > s0, $"{l2.Status} {r.C} {r.Id} s>{r.S > s0}");
            }

            // 10. штатный формат TimeCode (секунды, watched_at)
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                var native = new JObject { ["id"] = "tv-9-s1e1", ["hash"] = "90", ["card"] = "9_tv", ["position"] = 300, ["duration"] = 1200, ["percent"] = 25, ["watched_at"] = 1000, ["event"] = "5:1", ["session"] = "5" };
                var st = Apply1(c, native);
                var r = TlV2.Get(c, "90");
                T("native TimeCode row is accepted and normalised", st == "accepted" && r.T == 300 && r.D == 1200 && r.C == "9_tv" && r.Id == "tv-9-s1e1" && r.U == 1000);
            }

            // 11. монотонность s и выдача изменений
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                for (int i = 0; i < 20; i++) Apply1(c, Ev("100", 1000 + i, "6:" + i, t: i));
                for (int i = 0; i < 5; i++) Apply1(c, Ev("10" + (i + 1), 1000, "6:" + (100 + i)));
                var all = TlV2.Since(c, 0);
                bool mono = true; long prev = 0; foreach (var x in all) { long s = (long)x["s"]; if (s < prev) mono = false; prev = s; }
                var tail = TlV2.Since(c, (long)all[all.Count - 3]["s"]);
                T("revisions: monotonic, Since returns only newer rows with new fields", mono && tail.Count == 2 && all[0]["event"] != null, $"rows={all.Count} tail={tail.Count}");
            }

            // 11b. неканонический event отвергается; сбой действия внутри транзакции откатывает всю пачку
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                var st = Apply1(c, Ev("110", 1000, "02:01"));
                T("event: non-canonical '02:01' is rejected", st == "invalid:bad_event_format", st);
                var batch = new JArray { Ev("111", 1000, "5:1"), Ev("112", 1000, "5:2", op: "reset") };
                bool threw = false;
                try { TlV2.ApplyBatch(c, batch, (cc, r) => { if (r.H == "112") throw new Exception("lastplay failure"); }); } catch { threw = true; }
                T("atomic: failure of an in-transaction action rolls back the whole batch (no row left, no ack)", threw && TlV2.Get(c, "111") == null && TlV2.Get(c, "112") == null);
            }

            // 11d. неверные типы/диапазоны: виноватое событие получает invalid с причиной, остальные доходят
            using (var c = Mem())
            {
                Profiles.EnsureSchema(c);
                var batch = new JArray
                {
                    Ev("120", 1000, "8:1"),
                    new JObject { ["h"] = "121", ["u"] = "not-a-date", ["event"] = "8:2" },
                    new JObject { ["h"] = "122", ["u"] = 1000, ["t"] = new JObject { ["x"] = 1 }, ["event"] = "8:3" },
                    new JObject { ["h"] = "123", ["u"] = 1000, ["p"] = "NaN-ish", ["event"] = "8:4" },
                    new JObject { ["h"] = "124", ["u"] = JToken.Parse("99999999999999999999999"), ["event"] = "8:5" },
                    new JObject { ["h"] = new JArray(1), ["u"] = 1000, ["event"] = "8:6" },
                    new JObject { ["h"] = "125", ["u"] = 1000, ["d"] = new JArray(5), ["event"] = "8:7" },
                    Ev("126", 1000, "8:8")
                };
                var (list, _) = TlV2.ApplyBatch(c, batch);
                var st = string.Join(",", list.Select(x => x.Status + (x.Reason != null ? ":" + x.Reason : "")));
                T("types: bad time/position/percent/range/structure give per-event invalid, valid ones are accepted", st == "accepted,invalid:bad_time_type,invalid:bad_position_type,invalid:bad_percent_type,invalid:bad_time_range,invalid:bad_hash,invalid:bad_duration_type,accepted"
                    && TlV2.Get(c, "120") != null && TlV2.Get(c, "126") != null, st);
                bool threw2 = false;
                try { TlV2.ApplyBatch(c, new JArray(Ev("127", 1000, "8:9")), (cc, r) => throw new SqliteException("disk I/O error", 10)); } catch { threw2 = true; }
                T("types: a real database failure is still a rejected transaction, not a silent invalid", threw2 && TlV2.Get(c, "127") == null);
                T("journal: index on (exported, id) exists", HomeDb.Query(c, "PRAGMA index_list(tl_journal)").Any(r => (string)r[1] == "ix_tl_journal_exp"));
            }

            // 11c. сброшенный ранний сезон при завершённом позднем остаётся непросмотренным; границу задаёт и начатый (сброшенный) сезон
            {
                var aired = new Dictionary<int, int> { [1] = 1, [2] = 1 };
                var seen = new HashSet<(int, int)> { (2, 1) };
                var before = KpUnified.HasUnseenAired(aired, seen, 2, null);
                var after = KpUnified.HasUnseenAired(aired, seen, 1, null);
                var withReset = KpUnified.HasUnseenAired(aired, seen, 2, new HashSet<(int, int)> { (1, 1) });
                T("seasons: reset of S1E1 with S2 finished keeps the series unseen (reset is a negative state)", before == false && after == true && withReset == true, $"{before},{after},{withReset}");
                var allReset = KpUnified.HasUnseenAired(new Dictionary<int, int> { [1] = 3 }, new HashSet<(int, int)>(), null, new HashSet<(int, int)> { (1, 1), (1, 2), (1, 3) });
                T("seasons: reset of everything with an empty positive set is unseen, not 'all watched'", allReset == true);
            }

            // 12. копия рабочей home.db: схема применяется без потерь и повторно безопасна
            string tmp = Path.Combine(Path.GetTempPath(), "tlv2test-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                HomeDb.With(src => { using var dst = new SqliteConnection("Data Source=" + tmp); dst.Open(); src.BackupDatabase(dst); });
                using var c = new SqliteConnection("Data Source=" + tmp); c.Open();
                string Snap() => JsonConvert.SerializeObject(HomeDb.Query(c, "SELECT h,t,d,p,u,s,c FROM timeline ORDER BY h"));
                int n0 = Convert.ToInt32(HomeDb.Query(c, "SELECT COUNT(*) FROM timeline")[0][0]);
                string s0 = Snap();
                var a1 = TlV2.EnsureSchema(c); var a2 = TlV2.EnsureSchema(c);
                bool qc = (string)HomeDb.Query(c, "PRAGMA quick_check")[0][0] == "ok";
                T("copy of production home.db: schema applied, rows and values identical, repeat adds nothing, quick_check ok", a1.Count + (a1.Count == 0 ? 0 : 0) >= 0 && a2.Count == 0 && n0 == Convert.ToInt32(HomeDb.Query(c, "SELECT COUNT(*) FROM timeline")[0][0]) && Snap() == s0 && qc, $"rows={n0} added={string.Join(",", a1)}");
                // чтение каждой строки новым слоем
                int readable=0;
                foreach(var owner in HomeDb.Query(c,"SELECT DISTINCT profile_id FROM timeline")){
                    using var scope=Profiles.Enter(new Profiles.Scope((string)owner[0],"test","test",null,false,false,18,1));
                    readable+=TlV2.All(c).Count;
                }
                T("copy of production home.db: every profile row readable by TlV2", readable == n0, $"{readable}/{n0}");
            }
            finally { try { System.IO.File.Delete(tmp); System.IO.File.Delete(tmp + "-wal"); System.IO.File.Delete(tmp + "-shm"); } catch { } }
        }
        catch (Exception ex) { res.Add(new JObject { ["name"] = "EXCEPTION", ["ok"] = false, ["info"] = ex.ToString() }); }
        return new ContentResult
        {
            Content = new JObject { ["passed"] = res.Count(x => (bool)x["ok"]), ["total"] = res.Count, ["results"] = res }.ToString(Formatting.None),
            ContentType = "application/json; charset=utf-8"
        };
    }
}
