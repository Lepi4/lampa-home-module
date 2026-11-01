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
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Мост «единого просмотренного»: позиции Lampa (TimeCode: торренты, онлайн-источники, Wrestling)
// <-> просмотр kino.pub. По каждой серии побеждает запись с самым свежим временем.
// Запомненное состояние (kp_sync.json) исключает пинг-понг: действуем только если с прошлого раза
// изменилась именно та сторона, которую копируем.
public static class KpBridge
{
    static readonly string statePath = Path.Combine(Directory.GetCurrentDirectory(), "database", "kp_sync.json");
    static readonly object fileLock = new();
    static JObject state;
    static readonly SemaphoreSlim runLock = new(1, 1);
    static readonly HttpClient Loop = new() { Timeout = TimeSpan.FromSeconds(40) };
    static Timer timer;

    public static string LastResult = "ещё не запускался";
    public static DateTime LastRun;

    static Timer warmTimer, janitorTimer;

    // Уборка раз в 6 часов: чтобы ничего не раздувалось. Старые сопоставления аниме (старше 14 дней), журнал базы (WAL), копии init.conf,
    // которые Lampac делает при каждом изменении (оставляем 40 последних), и общий размер каталога cache (больше 2 ГБ — удаляем старейшие
    // файлы в hls/img/ffmpeg).
    static void Janitor()
    {
        try
        {
            HomeDb.With(c =>
            {
                HomeDb.Exec(c, "DELETE FROM anime_map WHERE at < @t", ("@t", HomeDb.Now() - 14L * 24 * 3600 * 1000));
                HomeDb.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");
            });
        }
        catch { }
        try
        {
            var bak = Path.Combine(Directory.GetCurrentDirectory(), "database", "backup", "init");
            if (Directory.Exists(bak))
                foreach (var f in new DirectoryInfo(bak).GetFiles().OrderByDescending(x => x.LastWriteTimeUtc).Skip(40))
                    try { f.Delete(); } catch { }
        }
        catch { }
        try
        {
            var cache = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "cache"));
            if (cache.Exists)
            {
                long total = cache.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                if (total > 2L * 1024 * 1024 * 1024)
                {
                    foreach (var sub in new[] { "hls", "img", "ffmpeg", "transcoding" })
                    {
                        var d = new DirectoryInfo(Path.Combine(cache.FullName, sub));
                        if (!d.Exists) continue;
                        foreach (var f in d.EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(x => x.LastWriteTimeUtc))
                        {
                            try { total -= f.Length; f.Delete(); } catch { }
                            if (total < 1536L * 1024 * 1024) break;
                        }
                        if (total < 1536L * 1024 * 1024) break;
                    }
                }
            }
        }
        catch { }
    }

    public static void Start()
    {
        timer ??= new Timer(_ => { _ = RunSafe(false); }, null, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(4));
        // прогрев строк главной: кеш списков (30 с) держится горячим, и первый запрос устройства получает готовый ответ
        warmTimer ??= new Timer(_ => { _ = WarmRows(); }, null, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(25));
        janitorTimer ??= new Timer(_ => Janitor(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));
    }

    static int warming;

    static async Task WarmRows()
    {
        if (Profiles.Enabled) return;
        if (System.Threading.Interlocked.Exchange(ref warming, 1) == 1) return;       // долгий прогон ещё идёт — не накладываем второй
        try
        {
            foreach (var mode in new[] { "watching", "new_movie", "new_serial", "top_new", "anime", "ru_serial", "ru_movie", "my_movies" })
            {
                try { await Loop.GetStringAsync($"http://127.0.0.1:9118/kp/cards?mode={mode}&page=1"); } catch { }
            }
        }
        finally { System.Threading.Interlocked.Exchange(ref warming, 0); }
    }

    public static void Stop()
    {
        timer?.Dispose();
        timer = null;
        warmTimer?.Dispose();
        warmTimer = null;
        janitorTimer?.Dispose();
        janitorTimer = null;
    }

    // Ключ позиции в Lampa: 32-битный хеш строки (Lampa.Utils.hash), как есть, без знака.
    public static string LampaHash(string s)
    {
        int h = 0;
        foreach (char c in s ?? "")
            h = unchecked((h << 5) - h + c);
        long abs = h == int.MinValue ? 2147483648L : Math.Abs((long)h);
        return abs.ToString();
    }

    public static string EpisodeHash(int season, int episode, string original)
        => LampaHash($"{season}{(season > 10 ? ":" : "")}{episode}{original}");

    static JObject State()
    {
        lock (fileLock)
        {
            if (state != null) return state;
            try { if (File.Exists(statePath)) state = JObject.Parse(File.ReadAllText(statePath)); } catch { }
            return state ??= new JObject();
        }
    }

    static void SaveState()
    {
        lock (fileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                File.WriteAllText(statePath, State().ToString(Formatting.None));
            }
            catch { }
        }
    }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Пауза моста на время общего снимка резервной копии: пока набор копируется, мост не подтверждает и не записывает действий
    public static bool PauseForSnapshot(int ms) { try { return runLock.Wait(ms); } catch { return false; } }
    public static void ResumeAfterSnapshot() { try { runLock.Release(); } catch { } }

    public static async Task<string> RunSafe(bool forceDry)
    {
        if (Profiles.IsTest) return "test instance: cloud writes disabled";
        if (!await runLock.WaitAsync(0)) return "уже выполняется";
        try
        {
            bool dry = forceDry || ((bool?)KpStore.Load()["bridge_dry_run"] ?? true);
            LastResult = Profiles.Enabled ? await ProfileMirror.Run(forceDry) : await Run(dry);
            // свои сериалы, которые появились в kino.pub, переводим в его «Я смотрю»
            if (!forceDry && !Profiles.Enabled)
            {
                try { int moved = await WatchListController.PromoteLocal(KpStore.DefaultProfile); if (moved > 0) LastResult += $" · перенесено в «Я смотрю» kino.pub: {moved}"; }
                catch { }
                try { int imp = await KpImport.Run(KpStore.DefaultProfile); if (imp > 0) LastResult += $" · из закладок kino.pub в «Мои фильмы»: {imp}"; }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LastResult = "ошибка: " + ex.GetType().Name + " " + ex.Message;
        }
        finally
        {
            LastRun = DateTime.UtcNow;
            runLock.Release();
        }
        return LastResult;
    }

    sealed class Plan
    {
        public List<string> Lines = new();
        public List<JObject> ToLampa = new();
        // exp — действие в timeline, на основании которого построена команда: перед отправкой и после ответа проверяем, что оно всё ещё последнее
        public List<(Func<Task<bool>> run, Action ok, (string h, long u, string ev)? exp)> ToKino = new();
        // что запомнить как «синхронизировано», когда запись в Lampa подтверждена именно по этому событию (h + event)
        public List<(string h, string ev, Action act)> AfterLampa = new();
    }

    // Действие, на котором построена команда, всё ещё последнее в timeline. Проверка только в едином режиме; без ожидания (нет строки) команда не блокируется.
    static bool StillCurrent((string h, long u, string ev)? exp)
    {
        if (exp == null || !TlV2.Unified) return true;
        try
        {
            var cur = HomeDb.With(c => TlV2.Get(c, exp.Value.h));
            if (cur == null || cur.U != exp.Value.u) return false;
            return exp.Value.ev == null || (cur.Event ?? "") == exp.Value.ev;
        }
        catch { return false; }
    }

    static (string h, long u, string ev)? Expect(JObject row, string hash) => row == null ? null : ((string, long, string)?)(hash, (long?)row["watched_at"] ?? 0, (string)row["event"]);

    static async Task<JObject> KpGet(string tok, string path)
    {
        var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/{path}", tok);
        return code == 200 ? JObject.Parse(body) : null;
    }

    static async Task<string> Run(bool dry)
    {
        string prof = KpStore.DefaultProfile;
        string tok = await KpStore.Token(prof);
        if (tok == null) return "нет токена kino.pub";

        // позиции Lampa пользователя home
        var dump = await TlV2.DumpAsync();
        var rows = new Dictionary<string, JObject>();
        var cardsRecent = new Dictionary<string, long>();
        long recentFrom = NowMs() - 14L * 24 * 3600 * 1000;

        foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
        {
            if ((bool?)r["deleted"] == true && (string)r["op"] != "reset") continue;         // явный сброс нужен ветке ниже, остальные удалённые пропускаем
            if (((string)r["event"] ?? "").StartsWith("9:")) continue;                          // синтетические строки проверок в Кинопаб не передаются
            string card = (string)r["card"], hash = (string)r["hash"];
            if (card == null || hash == null) continue;
            rows[$"{card}|{hash}"] = r;
            long wa = (long?)r["watched_at"] ?? 0;
            if (wa >= recentFrom && (!cardsRecent.TryGetValue(card, out long cur) || wa > cur)) cardsRecent[card] = wa;
        }

        // что синхронизируем: «Я смотрю» kino.pub + недавно смотренное в Lampa, найденное в Кинопаб
        var ids = new Dictionary<long, string>();
        var sl = await KpGet(tok, "watching/serials?subscribed=1");
        foreach (var i in ((JArray)(sl?["items"] ?? new JArray())).Children<JObject>()) ids[(long)i["id"]] = "serial";
        var ml = await KpGet(tok, "watching/movies");
        foreach (var i in ((JArray)(ml?["items"] ?? new JArray())).Children<JObject>()) ids[(long)i["id"]] = "movie";

        int lookups = 0;
        foreach (var card in cardsRecent.OrderByDescending(k => k.Value).Select(k => k.Key).Take(40))
        {
            var p = card.Split('_');
            if (p.Length != 2 || (p[1] != "movie" && p[1] != "tv") || !long.TryParse(p[0], out long tmdb)) continue;
            long kp = KpMap.CachedKp(p[1], tmdb);
            if (kp == 0 && !KpMap.IsKnown(p[1], tmdb) && lookups < 6)
            {
                lookups++;
                kp = await KpMap.LookupKp(p[1], tmdb, prof);
            }
            if (kp > 0 && !ids.ContainsKey(kp)) ids[kp] = p[1] == "tv" ? "serial" : "movie";
        }

        var plan = new Plan();
        // пробный прогон работает с копией состояния и ничего не запоминает
        var st = dry ? (JObject)State().DeepClone() : State();

        var gate = new SemaphoreSlim(4, 4);
        var tasks = ids.Select(async kv =>
        {
            await gate.WaitAsync();
            try { await ProcessItem(tok, kv.Key, kv.Value, rows, st, plan); }
            catch (Exception ex) { lock (plan) plan.Lines.Add($"kp {kv.Key}: ошибка {ex.GetType().Name}"); }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);

        try { await BookmarkMirror(tok, dry, plan, st); }
        catch (Exception ex) { plan.Lines.Add("закладки: ошибка " + ex.GetType().Name); }

        string head = $"тайтлов {ids.Count}, в Lampa {plan.ToLampa.Count}, в kino.pub {plan.ToKino.Count}" + (dry ? " [пробный прогон, ничего не записано]" : "");

        if (!dry)
        {
            if (plan.ToLampa.Count > 0 && TlV2.Unified)
            {
                // единый режим: одна запись в общую таблицу с исходным временем действия Кинопаба; результат — по каждому событию
                var wr = new JArray();
                foreach (var w in plan.ToLampa) { var x = (JObject)w.DeepClone(); x["event"] = "0:" + (long)x["watched_at"]; wr.Add(x); }
                var (results, _) = TlV2.Submit(wr);
                // подтверждение по каждому событию: accepted/duplicate — значение перенесено; stale/invalid/нет ответа — база не меняется, следующий прогон сверит заново
                int unconfirmed = 0;
                foreach (var (h, ev, act) in plan.AfterLampa)
                {
                    var ack = results.FirstOrDefault(r => r.H == h && r.Event == ev);
                    if (ack != null && (ack.Status == "accepted" || ack.Status == "duplicate")) { try { act(); } catch { } }
                    else unconfirmed++;
                }
                if (unconfirmed > 0) head += $" (в единую таблицу не подтверждено событий: {unconfirmed}; " + string.Join(",", results.Where(r => r.Status != "accepted" && r.Status != "duplicate").Select(r => r.Status + ":" + r.Reason).Distinct().Take(5)) + ")";
            }
            else if (plan.ToLampa.Count > 0)
            {
                // единая таблица устройств: то, что досмотрели или начали на kino.pub, должны получить и все устройства
                try
                {
                    var shared = new JArray();
                    long nowMs = NowMs();
                    foreach (var w in plan.ToLampa)
                        shared.Add(new JObject { ["h"] = (string)w["hash"], ["t"] = (long)((double?)w["position"] ?? 0), ["d"] = (long)((double?)w["duration"] ?? 0), ["p"] = (double?)w["percent"] ?? 0, ["u"] = nowMs, ["c"] = (string)w["card"] });
                    TlStore.Merge(shared);
                }
                catch { }
                var content = new StringContent(JsonConvert.SerializeObject(plan.ToLampa), Encoding.UTF8, "application/json");
                var res = await Loop.PostAsync("http://127.0.0.1:9118/timecode/set?connectionId=kp-bridge", content);
                bool accepted = false;
                if (res.IsSuccessStatusCode)
                {
                    try { accepted = (bool?)JObject.Parse(await res.Content.ReadAsStringAsync())["success"] == true; } catch { }
                }
                if (accepted) { foreach (var (_, _, act) in plan.AfterLampa) { try { act(); } catch { } } }
                else head += " (запись в Lampa не подтверждена: HTTP " + (int)res.StatusCode + ")";
            }
            // базовое состояние запоминаем только после подтверждённой записи: неудача оставляет разницу, и следующий прогон повторит перенос
            foreach (var (run, ok, exp) in plan.ToKino)
            {
                try
                {
                    if (!StillCurrent(exp)) continue;                       // действие в Lampa изменилось, пока строился план: старую команду не отправляем
                    if (await run() && StillCurrent(exp)) ok();             // изменилось во время запроса: базу не обновляем, актуальное состояние сверит следующий прогон
                }
                catch { }
            }
            SaveState();
        }

        return head + (plan.Lines.Count > 0 ? "\n" + string.Join("\n", plan.Lines.Take(80)) : "");
    }


    // ---------- закладки: «Избранное»/«Позже» Lampa <-> папки kino.pub ----------
    static async Task BookmarkMirror(string tok, bool dry, Plan plan, JObject st)
    {
        var cfg = KpStore.Load();
        var pairs = (cfg["bookmark_mirror"] as JArray) ?? new JArray(
            new JObject { ["lampa"] = "book", ["folder"] = "Избранное (Lampa)" },
            new JObject { ["lampa"] = "wath", ["folder"] = "Позже (Lampa)" });

        // одноразовый перенос закладок из штатной таблицы Lampac в единую базу
        if (HomeDb.Get("bm_migrated") != "1")
        {
            try
            {
                var old = JObject.Parse(await Loop.GetStringAsync("http://127.0.0.1:9118/bookmark/dump"));
                var imp = new JArray();
                foreach (var row in ((JArray)old["rows"]).Children<JObject>())
                {
                    var card = row["card"] as JObject;
                    string k = BmStore.KeyOf(card);
                    if (k == null || row["categories"] is not JObject cts) continue;
                    foreach (var p in cts.Properties())
                    {
                        if (p.Value.Type == JTokenType.Null || (p.Value.Type == JTokenType.Boolean && !(bool)p.Value)) continue;
                        imp.Add(new JObject { ["cat"] = p.Name, ["k"] = k, ["card"] = card, ["u"] = HomeDb.Now(), ["d"] = false });
                    }
                }
                BmStore.Merge(imp);
                HomeDb.Set("bm_migrated", "1");
            }
            catch { }
        }
        var bm = BmStore.Dump();
        var rowsB = ((JArray)bm["rows"]).Children<JObject>().ToList();
        var folders = ((JArray)((await KpGet(tok, "bookmarks"))?["items"] ?? new JArray())).Children<JObject>().ToList();
        var stBm = (JObject)(st["bm"] ??= new JObject());
        int lookups = 0;

        static string MediaOfCard(JObject c)
            => (c["name"] != null || c["original_name"] != null || c["first_air_date"] != null) ? "tv" : "movie";

        foreach (var pair in pairs.Children<JObject>())
        {
            string cat = (string)pair["lampa"], title = (string)pair["folder"];
            if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(title)) continue;

            var fobj = folders.FirstOrDefault(f => (string)f["title"] == title);
            long folderId = fobj != null ? (long)fobj["id"] : 0;

            // Lampa: ключ "media:tmdb" -> карточка
            var L = new Dictionary<string, JObject>();
            foreach (var row in rowsB)
            {
                var cats = row["categories"] as JObject;
                var flag = cats?[cat];
                if (flag == null || flag.Type == JTokenType.Null || (flag.Type == JTokenType.Boolean && !(bool)flag)) continue;
                var card = row["card"] as JObject;
                long? id = (long?)card?["id"];
                if (card == null || id == null || id <= 0) continue;
                L[$"{MediaOfCard(card)}:{id}"] = card;
            }

            // kino.pub: ключ "media:tmdb" -> (kp id, карточка TMDB)
            var K = new Dictionary<string, (long kp, JObject card)>();
            if (folderId > 0)
            {
                for (int page = 1; page <= 10; page++)
                {
                    var d = await KpGet(tok, $"bookmarks/{folderId}?perpage=100&page={page}");
                    var items = ((JArray)(d?["items"] ?? new JArray())).Children<JObject>().ToList();
                    foreach (var it in items)
                    {
                        var c = await KpMap.ToTmdbCard(it, tok);
                        if (c == null) continue;
                        K[$"{(string)c["media_type"]}:{(long)c["id"]}"] = ((long)it["id"], c);
                    }
                    int cur = (int?)d?["pagination"]?["current"] ?? page, total = (int?)d?["pagination"]?["total"] ?? 1;
                    if (cur >= total || items.Count == 0) break;
                }
            }

            // какие из карточек Lampa есть в Кинопаб
            var Lkp = new Dictionary<string, long>();
            foreach (var key in L.Keys)
            {
                var pp = key.Split(':');
                long tmdb = long.Parse(pp[1]);
                long kp = KpMap.CachedKp(pp[0], tmdb);
                if (kp == 0 && !KpMap.IsKnown(pp[0], tmdb) && lookups < 12) { lookups++; kp = await KpMap.LookupKp(pp[0], tmdb, KpStore.DefaultProfile); }
                if (kp > 0) Lkp[key] = kp;
            }

            var S = ((st["bm"]?[cat] as JArray)?.Select(x => (string)x).ToHashSet()) ?? new HashSet<string>();

            var toKinoAdd = Lkp.Keys.Where(k => !K.ContainsKey(k) && !S.Contains(k)).ToList();
            var toKinoRemove = S.Where(k => !L.ContainsKey(k) && K.ContainsKey(k)).ToList();
            var toLampaAdd = K.Keys.Where(k => !L.ContainsKey(k) && !S.Contains(k)).ToList();
            var toLampaRemove = S.Where(k => !K.ContainsKey(k) && L.ContainsKey(k)).ToList();

            foreach (var k in toKinoAdd) plan.Lines.Add($"закладки Lampa[{cat}] → kino.pub «{title}»: + {(string)L[k]["title"] ?? (string)L[k]["name"]}");
            foreach (var k in toKinoRemove) plan.Lines.Add($"закладки Lampa[{cat}] → kino.pub «{title}»: − {k}");
            foreach (var k in toLampaAdd) plan.Lines.Add($"закладки kino.pub «{title}» → Lampa[{cat}]: + {(string)K[k].card["title"] ?? (string)K[k].card["name"]}");
            foreach (var k in toLampaRemove) plan.Lines.Add($"закладки kino.pub «{title}» → Lampa[{cat}]: − {k}");

            if (!dry)
            {
                if (toKinoAdd.Count > 0 && folderId == 0)
                {
                    await KpStore.PostForm($"{KpStore.Api}/v1/bookmarks/create", new() { ["title"] = title }, tok);
                    var again = ((JArray)((await KpGet(tok, "bookmarks"))?["items"] ?? new JArray())).Children<JObject>()
                        .FirstOrDefault(f => (string)f["title"] == title);
                    folderId = again != null ? (long)again["id"] : 0;
                }

                if (folderId > 0)
                {
                    foreach (var k in toKinoAdd)
                        await KpStore.PostForm($"{KpStore.Api}/v1/bookmarks/add", new() { ["item"] = Lkp[k].ToString(), ["folder"] = folderId.ToString() }, tok);
                    foreach (var k in toKinoRemove)
                        await KpStore.PostJson($"{KpStore.Api}/v1/bookmarks/remove-item", new { item = K[k].kp, folder = folderId }, tok);
                }

                foreach (var k in toLampaAdd) BmStore.Add(cat, K[k].card);
                foreach (var k in toLampaRemove)
                {
                    var pp = k.Split(':');
                    BmStore.Remove(cat, pp[0], long.Parse(pp[1]));
                }
            }

            // общий набор после применения изменений
            var Lnew = new HashSet<string>(L.Keys.Except(toLampaRemove).Union(toLampaAdd));
            var Knew = new HashSet<string>(K.Keys.Except(toKinoRemove).Union(toKinoAdd));
            Lnew.IntersectWith(Knew);
            stBm[cat] = new JArray(Lnew);
        }
    }

    // «+N новых серий» в «Я смотрю» считает kino.pub, а серию могли уже посмотреть у нас (торрент, Plex, другое устройство) раньше,
    // чем её опубликовал kino.pub. Сколько таких серий у каждого тайтла, считает сам обмен (раз в 4 минуты, он и так читает все серии)
    // и кладёт сюда; список «Я смотрю» только вычитает готовое число — без единого запроса при показе строки.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> LocalSeen = new();

    public static Task AdjustNew(string tok, JArray results)
    {
        foreach (var card in results.Children<JObject>())
        {
            int raw = (int?)card["kp_new"] ?? 0;
            long kpId = (long?)card["kp_id"] ?? 0;
            if (raw <= 0 || kpId <= 0 || !LocalSeen.TryGetValue(kpId, out int seen) || seen <= 0) continue;
            int left = Math.Max(0, raw - seen);
            if (left != raw) { card["kp_new_raw"] = raw; card["kp_new"] = left; }
        }
        return Task.CompletedTask;
    }

    static async Task ProcessItem(string tok, long kpId, string kpType, Dictionary<string, JObject> rows, JObject st, Plan plan)
    {
        var cardObj = KpMap.CardForKp(kpId)
            ?? await KpMap.ToTmdbCard(new JObject { ["id"] = kpId, ["type"] = kpType }, tok);
        if (cardObj == null) return;

        long tmdb = (long)cardObj["id"];
        string media = (string)cardObj["media_type"] ?? KpMap.MediaOf(kpType);
        string orig = media == "tv"
            ? ((string)cardObj["original_name"] ?? (string)cardObj["name"])
            : ((string)cardObj["original_title"] ?? (string)cardObj["title"]);
        if (string.IsNullOrEmpty(orig)) return;

        var w = (await KpGet(tok, $"watching?id={kpId}"))?["item"] as JObject;
        if (w == null) return;

        string cardKey = $"{tmdb}_{media}";

        // записи просмотра: (сезон, серия, номер видео в kino.pub, длительность, позиция, статус, время изменения, ключ identity)
        var entries = new List<(int s, int e, double dur, double time, int status, long upd)>();
        if (media == "tv")
        {
            foreach (var se in ((JArray)(w["seasons"] ?? new JArray())).Children<JObject>())
                foreach (var ep in ((JArray)(se["episodes"] ?? new JArray())).Children<JObject>())
                    entries.Add(((int)se["number"], (int)ep["number"], (double?)ep["duration"] ?? 0, (double?)ep["time"] ?? 0, (int?)ep["status"] ?? -1, (long?)ep["updated"] ?? 0));
        }
        else
        {
            var v = ((JArray)(w["videos"] ?? new JArray())).Children<JObject>().FirstOrDefault();
            if (v != null)
                entries.Add((0, (int)v["number"], (double?)v["duration"] ?? 0, (double?)v["time"] ?? 0, (int?)v["status"] ?? -1, (long?)v["updated"] ?? 0));
        }

        int seenUnmarked = 0;
        foreach (var en in entries)
        {
            string hash = media == "tv" ? EpisodeHash(en.s, en.e, orig) : LampaHash(orig);
            string identity = media == "tv" ? $"tv-{tmdb}-s{en.s}e{en.e}" : $"movie-{tmdb}";
            rows.TryGetValue($"{cardKey}|{hash}", out var row);
            if (row == null)
            {
                // в штатной базе строки нет (kino.pub/Plex её не создают) — берём отметку из общей таблицы устройств
                var tl = TlStore.Get(hash);
                if (tl != null)
                    row = new JObject { ["position"] = tl.T, ["duration"] = tl.D, ["percent"] = tl.P, ["watched_at"] = tl.U, ["updated_at"] = tl.U, ["op"] = TlStore.OpOf(hash) };
            }

            double kinoVal = en.status == 1 ? en.dur : en.time;
            bool kinoHas = en.status == 1 || en.time > 0;
            long kinoTs = en.upd * 1000;

            double lampaPos = (double?)row?["position"] ?? 0;
            double lampaPct = (double?)row?["percent"] ?? 0;
            double lampaDur = (double?)row?["duration"] ?? 0;
            long lampaTs = (long?)row?["watched_at"] ?? 0;
            bool lampaHas = row != null && (lampaPos > 0 || lampaPct > 0);

            string sk = $"{kpId}:{en.s}:{en.e}";
            var prev = st[sk] as JObject;
            double pk = (double?)prev?["kt"] ?? -1, pl = (double?)prev?["lt"] ?? -1;

            bool kinoChanged = kinoHas && Math.Abs(pk - kinoVal) > 1;
            bool lampaChanged = lampaHas && Math.Abs(pl - lampaPos) > 1;

            // досмотрено у нас, а kino.pub статус «просмотрено» не знает (позиции совпали, но серия не отмечена) — отметим
            bool needMark = lampaHas && en.status != 1 && KpUnified.IsDone(lampaPos, lampaDur > 0 ? lampaDur : en.dur, lampaPct);
            if (needMark) seenUnmarked++;

            // Сброс просмотра в Lampa (строка есть, позиция и процент обнулены) новее отметки Кинопаба, а раньше у нас позиция была: снимаем отметку и в Кинопабе.
            // Только свежие сбросы (за двое суток) — старые «нули» в базе не должны менять аккаунт задним числом.
            // Явный сброс (op=reset) действует независимо от прежней позиции и возраста доставки; старые «нули» без op — только свежие (за двое суток).
            bool explicitReset = row != null && (string)row["op"] == "reset";
            if (row != null && !lampaHas && kinoHas && lampaTs > kinoTs && (explicitReset || (pl > 1 && lampaTs > NowMs() - 2L * 24 * 3600 * 1000)))
            {
                string sq = media == "tv" ? $"&season={en.s}" : "";
                string rpath = en.status == 1 ? $"watching/toggle?id={kpId}&video={en.e}&status=0{sq}" : $"watching/marktime?id={kpId}&video={en.e}&time=0{sq}";
                string rlabel = media == "tv" ? $"{orig} S{en.s}E{en.e}" : orig;
                string rsk = sk;
                lock (plan)
                {
                    plan.ToKino.Add((async () => await KpGet(tok, rpath) != null, () => { st[rsk] = new JObject { ["kt"] = 0.0, ["lt"] = 0.0 }; KpUnified.KinoSeenForget(kpId); }, Expect(row, hash)));
                    plan.Lines.Add($"Lampa → kino.pub: {rlabel} сброс просмотра");
                }
                continue;
            }

            // Сброс в Кинопабе (раньше отметка была, теперь нет), а в Lampa позиция с тех пор не менялась: сбрасываем и у нас.
            // Тоже только свежие (по времени изменения серии в Кинопабе), чтобы не менять историю задним числом.
            if (!kinoHas && lampaHas && !lampaChanged && pk > 1 && en.upd > 0 && kinoTs > NowMs() - 2L * 24 * 3600 * 1000)
            {
                double rdur = lampaDur > 0 ? lampaDur : en.dur;
                var rwire = new JObject
                {
                    ["id"] = identity, ["hash"] = hash, ["card"] = cardKey,
                    ["position"] = 0, ["duration"] = rdur, ["percent"] = 0,
                    ["watched_at"] = (kinoTs > 0 ? kinoTs : NowMs()), ["profile"] = 0, ["op"] = "reset"
                };
                string rsk2 = sk; string rlabel2 = media == "tv" ? $"{orig} S{en.s}E{en.e}" : orig;
                lock (plan)
                {
                    plan.ToLampa.Add(rwire);
                    plan.AfterLampa.Add((hash, "0:" + (long)rwire["watched_at"], () => st[rsk2] = new JObject { ["kt"] = 0.0, ["lt"] = 0.0 }));
                    plan.Lines.Add($"kino.pub → Lampa: {rlabel2} сброс просмотра");
                }
                continue;
            }

            // уже одинаково — только запоминаем
            if (kinoHas && lampaHas && Math.Abs(kinoVal - lampaPos) <= 5 && !needMark)
            {
                st[sk] = new JObject { ["kt"] = kinoVal, ["lt"] = lampaPos };
                continue;
            }

            string label = media == "tv" ? $"{orig} S{en.s}E{en.e}" : orig;

            if (kinoChanged && (!lampaChanged || kinoTs >= lampaTs))
            {
                double dur = en.dur > 0 ? en.dur : lampaDur;
                double pct = en.status == 1 ? 100 : (dur > 0 ? Math.Round(en.time / dur * 100, 1) : 0);
                var wire = new JObject
                {
                    ["id"] = identity,
                    ["hash"] = hash,
                    ["card"] = cardKey,
                    ["position"] = en.status == 1 ? dur : en.time,
                    ["duration"] = dur,
                    ["percent"] = pct,
                    ["watched_at"] = kinoTs > 0 ? kinoTs : NowMs(),
                    ["profile"] = 0
                };
                if (en.status == 1) wire["op"] = "completed";
                lock (plan)
                {
                    plan.ToLampa.Add(wire);
                    plan.Lines.Add($"kino.pub → Lampa: {label} {(en.status == 1 ? "просмотрено" : (int)en.time + " с")}");
                }
                double wpos = (double)wire["position"], kv0 = kinoVal; string sk0 = sk;
                lock (plan) plan.AfterLampa.Add((hash, "0:" + (long)wire["watched_at"], () => st[sk0] = new JObject { ["kt"] = kv0, ["lt"] = wpos }));
            }
            else if ((lampaChanged || needMark) && (!kinoChanged || lampaTs > kinoTs))
            {
                bool done = KpUnified.IsDone(lampaPos, lampaDur > 0 ? lampaDur : en.dur, lampaPct);
                if (done && en.status == 1) { st[sk] = new JObject { ["kt"] = kinoVal, ["lt"] = lampaPos }; continue; }
                if (!done && lampaPos < 60) continue;

                string seasonQ = media == "tv" ? $"&season={en.s}" : "";
                string path = done
                    ? $"watching/toggle?id={kpId}&video={en.e}&status=1{seasonQ}"
                    : $"watching/marktime?id={kpId}&video={en.e}&time={(int)lampaPos}{seasonQ}";
                bool wasNeedMark = needMark; double kt0 = done ? en.dur : lampaPos, lt0 = lampaPos; string sk1 = sk;
                lock (plan)
                {
                    plan.ToKino.Add((async () => await KpGet(tok, path) != null, () =>
                    {
                        st[sk1] = new JObject { ["kt"] = kt0, ["lt"] = lt0 };
                        // серия отмечена в Кинопабе — его счётчик уже учитывает её, локальную поправку убираем (иначе одна серия вычиталась бы дважды)
                        if (wasNeedMark) LocalSeen.AddOrUpdate(kpId, 0, (_, v) => Math.Max(0, v - 1));
                    }, Expect(row, hash)));
                    plan.Lines.Add($"Lampa → kino.pub: {label} {(done ? "просмотрено" : (int)lampaPos + " с")}");
                }
            }
        }
        LocalSeen[kpId] = seenUnmarked;
    }

}

public class KinoPubSyncController : BaseController
{
    // Запуск моста по требованию (после просмотра на любом клиенте). ?dry=1 — пробный прогон.
    [HttpGet, AllowAnonymous]
    [Route("kp/sync/run")]
    public async Task<ActionResult> Run(int dry = 0)
    {
        string res = await KpBridge.RunSafe(dry == 1);
        return Content(res, "text/plain; charset=utf-8");
    }

    // Сверка серверной формулы ключа с Lampa.Utils.hash
    [HttpGet, AllowAnonymous]
    [Route("kp/sync/hash")]
    public ActionResult Hash(string s)
        => Content(KpBridge.LampaHash(s), "text/plain; charset=utf-8");

    [HttpGet, AllowAnonymous]
    [Route("kp/sync/state")]
    public ActionResult StateInfo()
        => Content($"последний запуск: {KpBridge.LastRun:u}\n{KpBridge.LastResult}", "text/plain; charset=utf-8");
}
