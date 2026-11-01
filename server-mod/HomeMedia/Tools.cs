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
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HomeMedia;

// Служебные операции над историей просмотров. Все изменяющие режимы (apply=1) выполняют только то, что перед этим показал apply=0.
public class ToolsController : BaseController
{
    static ActionResult J(object o, int code = 200) => new ContentResult { Content = o is string s ? s : JsonConvert.SerializeObject(o), ContentType = "application/json; charset=utf-8", StatusCode = code };

    // ---------- песочница для проверок: копия рабочей базы, живёт до 3 часов ----------
    [HttpGet, AllowAnonymous]
    [Route("home/ops/sandbox")]
    public ActionResult Sandbox(string op = "status", string id = null)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        try
        {
            if (op == "create") return J(new { id = HomeDb.SandboxCreate() });
            if (op == "drop") return J(new { dropped = HomeDb.SandboxDrop(id ?? "") });
            return J(new { sandboxes = HomeDb.SandboxList() });
        }
        catch (Exception ex) { return J(new { error = ex.GetType().Name, message = ex.Message }, 500); }
    }

    // ---------- идентификаторы серий для строк без id ----------
    // Для строки с карточкой «N_tv» перебираем серии сериала по TMDB, считаем хеш каждой и ищем совпадение: однозначное — записываем id, иначе строка остаётся как есть.
    [HttpGet, AllowAnonymous]
    [Route("home/ops/idfill")]
    public async Task<ActionResult> IdFill(int apply = 0)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        try
        {
            var rows = HomeDb.With(c => TlV2.All(c)).Where(r => r.Id == null && !string.IsNullOrEmpty(r.C)).ToList();
            var found = new List<object>(); var missed = new List<object>(); var writes = new List<(string h, string id)>();
            foreach (var grp in rows.GroupBy(r => r.C))
            {
                var m = Regex.Match(grp.Key, @"^(\d+)_(tv|movie)$");
                if (!m.Success) { foreach (var r in grp) missed.Add(new { r.H, card = r.C, why = "card format" }); continue; }
                long tmdb = long.Parse(m.Groups[1].Value);
                if (m.Groups[2].Value == "movie")
                {
                    var info = await KpMap.TmdbRaw($"movie/{tmdb}");
                    var names = new[] { (string)info?["original_title"], (string)info?["title"] }.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                    foreach (var r in grp)
                    {
                        if (names.Any(n => KpBridge.LampaHash(n) == r.H)) { found.Add(new { r.H, card = r.C, id = $"movie-{tmdb}" }); writes.Add((r.H, $"movie-{tmdb}")); }
                        else missed.Add(new { r.H, card = r.C, why = "hash does not match the movie title" });
                    }
                    continue;
                }
                var tv = await KpMap.TmdbRaw($"tv/{tmdb}");
                var origs = new[] { (string)tv?["original_name"], (string)tv?["name"] }.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                var map = new Dictionary<string, List<(int s, int e)>>();
                foreach (var se in ((JArray)(tv?["seasons"] ?? new JArray())).Children<JObject>())
                {
                    int sn = (int?)se["season_number"] ?? 0, cnt = (int?)se["episode_count"] ?? 0;
                    if (sn <= 0) continue;
                    for (int e = 1; e <= Math.Max(cnt, 1) + 5; e++)
                        foreach (var o in origs)
                        {
                            string h = KpBridge.EpisodeHash(sn, e, o);
                            if (!map.TryGetValue(h, out var l)) map[h] = l = new List<(int, int)>();
                            if (!l.Contains((sn, e))) l.Add((sn, e));
                        }
                }
                foreach (var r in grp)
                {
                    if (map.TryGetValue(r.H, out var l) && l.Count == 1) { var id = $"tv-{tmdb}-s{l[0].s}e{l[0].e}"; found.Add(new { r.H, card = r.C, id }); writes.Add((r.H, id)); }
                    else missed.Add(new { r.H, card = r.C, why = l == null ? "no episode with this hash" : "ambiguous" });
                }
            }
            int written = 0;
            if (apply == 1)
                foreach (var (h, id) in writes)
                    written += HomeDb.With(c => HomeDb.Exec(c, "UPDATE timeline SET id=@id, s=@s WHERE profile_id=@profile AND h=@h AND id IS NULL", ("@id", id), ("@s", Math.Max(HomeDb.Now(), TlV2.MaxS(c) + 1)), ("@h", h)));
            return J(new { candidates = rows.Count, found = found.Count, missed = missed.Count, written, found_list = found, missed_list = missed });
        }
        catch (Exception ex) { return J(new { error = ex.GetType().Name, message = ex.Message }, 500); }
    }

    // ---------- отслеживание старых сериалов ----------
    // Кандидаты: сериал с карточкой в истории, у которого есть начатые серии и вышедшие, но непросмотренные; ещё не отслеживается и не скрыт вручную.
    [HttpGet, AllowAnonymous]
    [Route("home/ops/trackfill")]
    public async Task<ActionResult> TrackFill(int apply = 0)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        try
        {
            // постоянное право на отслеживание не зависит от текущего долга: берём каждый сериал, который зритель когда-либо реально смотрел (в том числе досмотренный целиком)
            var rows = HomeDb.With(c => TlV2.All(c)).Where(r => (r.C ?? "").EndsWith("_tv") && (r.T > 0 || r.P > 0) && r.Op != "reset" && !((r.Event ?? "").StartsWith("9:"))).ToList();
            var tracked = KpUnified.TrackList().Select(t => t.tmdb).ToHashSet();
            var wsets = await KpUnified.WatchedSets();
            var starts = await KpUnified.StartedFirst();
            var cand = new List<JObject>();
            foreach (var grp in rows.GroupBy(r => r.C))
            {
                if (!long.TryParse(grp.Key.Split('_')[0], out long tmdb) || tracked.Contains(tmdb)) continue;
                if (Hidden(tmdb)) continue;
                wsets.TryGetValue(tmdb, out var seen); seen ??= new HashSet<(int, int)>();
                var un = KpUnified.HasUnseenAired(await KpUnified.AiredCounts(tmdb), seen, starts.TryGetValue(tmdb, out int fs) ? fs : (int?)null);
                var info = await KpMap.TmdbRaw($"tv/{tmdb}?language=ru");
                long last = grp.Max(r => r.U);
                cand.Add(new JObject { ["tmdb"] = tmdb, ["name"] = (string)info?["name"], ["unseen"] = un, ["watched_episodes"] = seen.Count, ["last_watched"] = DateTimeOffset.FromUnixTimeMilliseconds(last).ToString("yyyy-MM-dd"), ["last_u"] = last });
            }
            int added = 0;
            if (apply == 1) foreach (var c in cand) { KpUnified.TrackAdd((long)c["tmdb"], (long)c["last_u"]); added++; }
            return J(new { candidates = cand.Count, added, list = cand.OrderByDescending(c => (long)c["last_u"]).Select(c => new { tmdb = (long)c["tmdb"], name = (string)c["name"], unseen = (bool?)c["unseen"], watched = (int)c["watched_episodes"], last = (string)c["last_watched"] }) });
        }
        catch (Exception ex) { return J(new { error = ex.GetType().Name, message = ex.Message }, 500); }
    }

    static bool Hidden(long tmdb)
    {
        try { return KpUnified.Hidden()["w_" + tmdb + "_tv"] != null && !KpUnified.TrackedVisible(tmdb); } catch { return false; }
    }

    // ---------- испытание ненулевого переноса и отката на копии предмиграционной базы ----------
    [HttpGet, AllowAnonymous]
    [Route("home/ops/tlpremig")]
    public ActionResult PreMigration(string name = null)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "database", "backup", "home");
        if (string.IsNullOrEmpty(name) || name.Contains("/") || name.Contains("..") || !name.EndsWith(".db")) return J(new { error = "name" }, 400);
        string src = Path.Combine(dir, name);
        if (!System.IO.File.Exists(src)) return J(new { error = "not found" }, 404);
        string tmp = Path.Combine(Path.GetTempPath(), "tlpre-" + Guid.NewGuid().ToString("N") + ".db");
        var checks = new JArray();
        void Chk(string n, bool ok, string info = "") => checks.Add(new JObject { ["name"] = n, ["ok"] = ok, ["info"] = info });
        try
        {
            System.IO.File.Copy(src, tmp, true);
            using var c = new SqliteConnection("Data Source=" + tmp); c.Open();
            TlV2.EnsureSchema(c);
            string snap0 = TlMigrate.Snapshot(c);
            var tc = TlMigrate.ReadTc();
            var decs = TlMigrate.Plan(c, tc);
            var ledger = TlMigrate.Apply(c, decs, tc.Count == 0 ? 0 : tc.Max(t => t.Updated));
            Chk("non-zero migration applied on the pre-migration copy", ledger.Count > 0, "entries=" + ledger.Count);
            Chk("quick_check ok", (string)HomeDb.Query(c, "PRAGMA quick_check")[0][0] == "ok");
            Chk("second run changes nothing", TlMigrate.Plan(c, tc).All(d => !d.Change));
            // новый просмотр после переноса одной из строк: откат его не затирает
            var touched = ledger.Children<JObject>().Select(e => (string)e["h"]).First();
            long now = HomeDb.Now();
            HomeDb.Exec(c, "UPDATE timeline SET t=777, p=33, u=@u, event='99:1', s=@s WHERE profile_id=@profile AND h=@h", ("@u", now), ("@s", now), ("@h", touched));
            var (restored, kept) = TlMigrate.Rollback(c, ledger);
            Chk("rollback keeps the newer view and restores the rest", kept == 1 && restored == ledger.Count - 1 && TlV2.Get(c, touched).T == 777, $"restored={restored} kept={kept}");
            // откат без нового просмотра возвращает исходный образ в точности
            using var c2 = new SqliteConnection("Data Source=" + tmp + ".2"); c2.Open();
            System.IO.File.Copy(src, tmp + ".2", true);
            TlV2.EnsureSchema(c2);
            var led2 = TlMigrate.Apply(c2, TlMigrate.Plan(c2, tc), tc.Count == 0 ? 0 : tc.Max(t => t.Updated));
            TlMigrate.Rollback(c2, led2);
            Chk("rollback without new views restores the original image exactly", SameData(c2, src));
            return J(new { checks, passed = checks.Count(x => (bool)x["ok"]), total = checks.Count });
        }
        catch (Exception ex) { return J(new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace }, 500); }
        finally { foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm", tmp + ".2", tmp + ".2-wal", tmp + ".2-shm" }) try { System.IO.File.Delete(f); } catch { } }
    }

    // все значимые колонки, включая служебные: повреждение id/event/session/op/s не должно проходить как «точное восстановление»
    static string Rows(SqliteConnection c) => TlMigrate.Sha(JsonConvert.SerializeObject(HomeDb.Query(c, "SELECT h,c,t,d,p,u,s,id,event,session,op FROM timeline WHERE profile_id=@profile ORDER BY h")));

    // сравнение значений (без служебных колонок) с исходной копией
    static bool SameData(SqliteConnection c, string srcPath)
    {
        string t2 = Path.Combine(Path.GetTempPath(), "tlpre-cmp-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            System.IO.File.Copy(srcPath, t2, true);
            using var o = new SqliteConnection("Data Source=" + t2); o.Open();
            return Rows(o) == Rows(c);
        }
        finally { foreach (var f in new[] { t2, t2 + "-wal", t2 + "-shm" }) try { System.IO.File.Delete(f); } catch { } }
    }

    // ---------- обратный адаптер: состояние после перехода -> формат штатного TimeCode (для возврата прежнего APK) ----------
    // Источник полноты — ТЕКУЩАЯ таблица timeline (последнее действие по каждому хешу), а не журнал: журнал нужен только для сверки.
    // Каждая строка получает судьбу: replay (нужно записать), same (в TimeCode уже то же), excluded (синтетика / нет карточки — с объяснением).
    // apply=1 разрешён только на рабочей системе: в песочнице получатель — рабочий TimeCode, поэтому отправка отклоняется.
    // Результат apply — requested / attempted / confirmed: подтверждением считается только сверка записанных строк обратным чтением TimeCode.
    [HttpGet, AllowAnonymous]
    [Route("home/ops/tlreplay")]
    public async Task<ActionResult> Replay(long since = 0, int apply = 0, string sbx = null, string cardPrefix = null)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return J(new { error = "no sandbox" }, 404); }
        using var _sb = sb;
        if (apply == 1 && HomeDb.SandboxId != null) return J(new { error = "refused", message = "apply is not allowed inside a sandbox: the receiver is the production TimeCode" }, 409);
        try
        {
            if (since <= 0)
            {
                try { since = (long)JObject.Parse(HomeDb.Get("tl_migration") ?? "{}")["at"]; } catch { since = 0; }      // момент первоначального переноса
            }
            long sinceRev = since;                                                                                     // ревизии s — миллисекунды: сравнимы с моментом переноса
            var rowsNow = HomeDb.With(c => TlV2.All(c)).Where(r => r.S >= sinceRev).ToList();
            var tc = TlMigrate.ReadTc().Where(t => !string.IsNullOrEmpty(t.Item)).GroupBy(t => t.Item).ToDictionary(g => g.Key, g => g.First());
            var fates = new List<JObject>(); var rows = new JArray();
            foreach (var r in rowsNow.OrderBy(x => x.S))
            {
                void Fate(string f, string why) => fates.Add(new JObject { ["h"] = r.H, ["id"] = r.Id, ["card"] = r.C, ["fate"] = f, ["reason"] = why, ["u"] = r.U });
                if ((r.Event ?? "").StartsWith("9:")) { Fate("excluded", "synthetic test row"); continue; }
                if (string.IsNullOrEmpty(r.C)) { Fate("excluded", "no card: TimeCode cannot store a row without card (kept by hash in timeline)"); continue; }
                if (!string.IsNullOrEmpty(cardPrefix) && !r.C.StartsWith(cardPrefix)) { Fate("excluded", "outside cardPrefix filter"); continue; }
                bool reset = r.Op == "reset" && r.T == 0 && r.P == 0;
                if (tc.TryGetValue(r.H, out var t) && t.Watched == r.U && t.Deleted == reset &&
                    (reset || (KpUnified.IsDone(t.Pos, t.Dur, t.Pct) && KpUnified.IsDone(r.T, r.D, r.P)) || (Math.Abs(t.Pos - r.T) <= 1 && Math.Abs(t.Pct - r.P) < 0.1)))
                { Fate("same", "TimeCode already holds this state (both completed, or equal position)"); continue; }
                var o = new JObject { ["hash"] = r.H, ["card"] = r.C, ["position"] = reset ? 0 : r.T, ["duration"] = r.D, ["percent"] = reset ? 0 : r.P, ["watched_at"] = r.U };
                if (r.Id != null) o["id"] = r.Id;
                if (reset) o["deleted"] = true;
                rows.Add(o); Fate("replay", reset ? "explicit reset" : r.Op == "completed" ? "completed" : "progress");
            }
            // сверка с журналами: что из «replay» нигде не записано как событие (информативно)
            var jsql = HomeDb.With(c => HomeDb.Query(c, "SELECT h FROM tl_journal WHERE profile_id=@profile")).Select(r => (string)r[0]).ToHashSet();
            var jfile = new HashSet<string>();
            try
            {
                string jf = Path.Combine(Directory.GetCurrentDirectory(), "database", "tl_journal.jsonl");
                if (System.IO.File.Exists(jf)) foreach (var line in System.IO.File.ReadLines(jf)) { try { jfile.Add((string)JObject.Parse(line)["h"]); } catch { } }
            }
            catch { }
            int notInAnyJournal = rows.Children<JObject>().Count(o => !jsql.Contains((string)o["hash"]) && !jfile.Contains((string)o["hash"]));
            var summary = fates.GroupBy(f => (string)f["fate"]).ToDictionary(g => g.Key, g => g.Count());

            if (apply != 1)
                return J(new { since, current_rows_since = rowsNow.Count, summary, not_in_any_journal = notInAnyJournal, note = "lastplay, hidden and tracked live in the same home.db and are not reverted by a rollback: nothing to replay for them", fates, rows });

            // ---- рабочая запись и проверка результата ----
            int http = 0; bool success = false; int accepted = 0; string body = null;
            if (rows.Count > 0)
            {
                using var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                try
                {
                    var res = await hc.PostAsync("http://127.0.0.1:9118/timecode/set?connectionId=tl-replay", new System.Net.Http.StringContent(new JObject { ["rows"] = rows }.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json"));
                    http = (int)res.StatusCode; body = await res.Content.ReadAsStringAsync();
                    try { var jb = JObject.Parse(body); success = (bool?)jb["success"] == true; accepted = (int?)jb["accepted"] ?? 0; } catch { }
                }
                catch (Exception ex) { body = ex.GetType().Name; }
            }
            // подтверждение — только обратным чтением: каждая строка сверяется по hash/u/позиции/сбросу
            var back = TlMigrate.ReadTc().Where(t => !string.IsNullOrEmpty(t.Item)).GroupBy(t => t.Item).ToDictionary(g => g.Key, g => g.First());
            var confirmed = new List<string>(); var failed = new List<object>();
            foreach (var o in rows.Children<JObject>())
            {
                string h = (string)o["hash"]; bool del = (bool?)o["deleted"] == true;
                if (back.TryGetValue(h, out var t) && t.Watched == (long)o["watched_at"] && Math.Abs(t.Pos - (double)o["position"]) <= 1 && Math.Abs(t.Pct - (double)o["percent"]) < 0.1 && t.Deleted == del) confirmed.Add(h);
                else failed.Add(new { h, reason = http != 200 ? "http " + http : !success ? "receiver success=false" : "not stored with this state: the core TimeCode skips a write whose watched_at is older than the row it already holds" });
            }
            return J(new { requested = rows.Count, attempted = rows.Count > 0, http, success, accepted, confirmed = confirmed.Count, failed, all_confirmed = rows.Count == confirmed.Count, summary });
        }
        catch (Exception ex) { return J(new { error = ex.GetType().Name, message = ex.Message }, 500); }
    }

    // Удержание замка моста на заданное время (не более 40 с): нужно для проверки, что снимок без остановленного моста не публикуется.
    [HttpGet, AllowAnonymous]
    [Route("home/ops/bridgehold")]
    public ActionResult BridgeHold(int ms = 25000)
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        ms = Math.Clamp(ms, 1000, 40000);
        if (!KpBridge.PauseForSnapshot(2000)) return J(new { held = false, reason = "bridge is running" }, 409);
        _ = Task.Run(async () => { try { await Task.Delay(ms); } finally { KpBridge.ResumeAfterSnapshot(); } });
        return J(new { held = true, ms });
    }

    // ---------- проверка восстановления набора резервных копий ----------
    [HttpGet, AllowAnonymous]
    [Route("home/ops/restoretest")]
    public ActionResult RestoreTest(string set = "latest")
    {
        if (!Guard.Ok(Request)) return J(new { error = "forbidden" }, 403);
        return J(Ops.RestoreTest(set));
    }
}
