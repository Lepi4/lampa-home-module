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
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Единая карта тайтлов: карточка Lampa (TMDB id + тип) <-> элемент kino.pub.
// Кеш лежит в database/kp_map.json и общий для всех клиентов.
public static class KpMap
{
    static readonly string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "kp_map.json");
    static readonly object fileLock = new();
    static JObject db;
    static readonly HttpClient Loop = new() { Timeout = TimeSpan.FromSeconds(25) };
    static readonly SemaphoreSlim Gate = new(8, 8);

    const long NegativeTtl = 12 * 3600;

    static string TmdbBase => "http://127.0.0.1:9118/tmdb/api/3";
    static string TmdbKey => (string)KpStore.Load()["tmdb_key"] ?? "4ef0d7355d9ffb5151e987764708ce96";

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    static JObject Db()
    {
        lock (fileLock)
        {
            if (db != null) return db;
            try { if (File.Exists(path)) db = JObject.Parse(File.ReadAllText(path)); } catch { }
            db ??= new JObject();
            db["kp2t"] ??= new JObject();   // "<kp id>" -> { t, m, card, at }
            db["t2kp"] ??= new JObject();   // "movie:603" -> { kp, at }
            return db;
        }
    }

    static void Save() => Persist.Later(path, () => { lock (fileLock) return Db().ToString(Formatting.None); });

    public static string MediaOf(string kpType)
        => kpType == "serial" || kpType == "docuserial" || kpType == "tvshow" ? "tv" : "movie";

    static async Task<JObject> Tmdb(string pathAndQuery)
    {
        string sep = pathAndQuery.Contains('?') ? "&" : "?";
        string json = await Loop.GetStringAsync($"{TmdbBase}/{pathAndQuery}{sep}api_key={TmdbKey}");
        return JObject.Parse(json);
    }

    // Элемент kino.pub -> карточка TMDB (формат результата списков TMDB) + kp_* поля. null, если соответствия нет.
    public static async Task<JObject> ToTmdbCard(JObject kp, string tok = null)
    {
        string kpId = (string)kp["id"];
        string media = MediaOf((string)kp["type"]);
        JObject card = null;

        var hit = Db()["kp2t"][kpId] as JObject;
        if (hit != null)
        {
            if ((long?)hit["t"] > 0 && hit["card"] is JObject cached)
                card = (JObject)cached.DeepClone();
            else if ((long?)hit["t"] == 0 && Now() - (long)(hit["at"] ?? 0) < NegativeTtl)
                return null;
        }

        if (card == null)
        {
            long? imdb = (long?)kp["imdb"];

            // у сокращённых записей («Я смотрю», новинки) IMDb нет — дозапрашиваем полную запись
            if ((imdb == null || imdb == 0) && tok != null)
            {
                try
                {
                    var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/{kpId}", tok);
                    if (code == 200) imdb = (long?)JObject.Parse(body)["item"]?["imdb"];
                }
                catch { return null; }
            }

            if (imdb == null || imdb == 0) return Remember(kpId, null, media, null);

            try
            {
                var f = await Tmdb($"find/tt{imdb:D7}?external_source=imdb_id&language=ru");
                var arr = (JArray)f[media == "tv" ? "tv_results" : "movie_results"];
                var first = arr?.FirstOrDefault() as JObject;
                if (first == null) return Remember(kpId, null, media, null);
                first["media_type"] = media;
                Remember(kpId, (long)first["id"], media, first);

                // обратная связь тоже запоминаем
                var t2 = (JObject)Db()["t2kp"];
                t2[$"{media}:{(long)first["id"]}"] = new JObject { ["kp"] = long.Parse(kpId), ["at"] = Now() };
                Save();
                card = (JObject)first.DeepClone();
            }
            catch { return null; }
        }

        card["kp_id"] = long.Parse(kpId);
        card["kp_type"] = (string)kp["type"];
        if (kp["new"] != null) card["kp_new"] = kp["new"];
        if (kp["subscribed"] != null) card["kp_subscribed"] = kp["subscribed"];
        return card;
    }

    static JObject Remember(string kpId, long? tmdbId, string media, JObject card)
    {
        lock (fileLock)
        {
            Db()["kp2t"][kpId] = new JObject { ["t"] = tmdbId ?? 0, ["m"] = media, ["card"] = card, ["at"] = Now() };
        }
        Save();
        return null;
    }

    // Карточка TMDB (формат списков) по id, с кешем на 7 суток
    public static async Task<JObject> TmdbCard(string media, long tmdbId)
    {
        string key = $"{media}:{tmdbId}";
        JObject previous = null;
        bool fresh = false;
        lock (fileLock) {
            var cache = (JObject)(Db()["cards"] ??= new JObject());
            if (cache[key] is JObject hit && hit["card"] is JObject cc) {
                previous = (JObject)cc.DeepClone();
                fresh = Now() - (long)(hit["at"] ?? 0) < (media=="tv"?3600:7L*24*3600);
            }
        }
        if (fresh) return CardKind.Annotate(previous);
        var work = KinoListWork<JObject>.Start("tmdb-card|"+key, async () => {
            try {
                var d = await Tmdb($"{media}/{tmdbId}?language=ru");
                if ((long?)d["id"] != tmdbId) return null;
                d["media_type"] = media;
                if (d["genres"] is JArray gs) d["genre_ids"] = new JArray(gs.Select(g => g["id"]));
                lock (fileLock) ((JObject)(Db()["cards"] ??= new JObject()))[key] = new JObject { ["card"] = d, ["at"] = Now() };
                Save();
                return d;
            }
            catch { return null; }
        });
        // A known card is immediately useful; background refresh never contains
        // personal progress, and the response still passes the current policy.
        if (previous != null) {
            var stale = CardKind.Annotate(previous);
            stale["home_metadata_stale"] = true;
            return stale;
        }
        return CardKind.Annotate(await work);
    }

    public static Task<JObject> TmdbRaw(string pathAndQuery) => Tmdb(pathAndQuery);

    public static long CachedKp(string media, long tmdbId)
        => (long)((Db()["t2kp"][$"{media}:{tmdbId}"] as JObject)?["kp"] ?? 0);

    public static bool IsKnown(string media, long tmdbId)
    {
        var c = Db()["t2kp"][$"{media}:{tmdbId}"] as JObject;
        if (c == null) return false;
        return (long)(c["kp"] ?? 0) > 0 || Now() - (long)(c["at"] ?? 0) < NegativeTtl;
    }

    // Закешированная карточка TMDB для элемента kino.pub (без сетевых запросов)
    public static JObject CardForKp(long kpId)
    {
        var h = Db()["kp2t"][kpId.ToString()] as JObject;
        if (h != null && (long?)h["t"] > 0 && h["card"] is JObject c) return (JObject)c.DeepClone();
        return null;
    }

    // Карточка TMDB -> id в kino.pub. 0 — в Кинопаб нет (результат тоже кешируется).
    public static async Task<long> LookupKp(string media, long tmdbId, string profile)
    {
        string key = $"{media}:{tmdbId}";
        var cur = Db()["t2kp"][key] as JObject;
        if (cur != null)
        {
            long kp = (long)(cur["kp"] ?? 0);
            if (kp > 0) return kp;
            if (Now() - (long)(cur["at"] ?? 0) < NegativeTtl) return 0;
        }

        long found = 0;
        await Gate.WaitAsync();
        try
        {
            var ext = await Tmdb($"{media}/{tmdbId}/external_ids");
            string imdb = (string)ext["imdb_id"];
            if (!string.IsNullOrEmpty(imdb) && long.TryParse(imdb.Replace("tt", ""), out long num))
            {
                string tok = await KpStore.Token(profile);
                if (tok == null) return 0; // не авторизованы: не запоминаем отрицательный результат
                {
                    var en = await Tmdb($"{media}/{tmdbId}?language=en-US");
                    var ru = await Tmdb($"{media}/{tmdbId}?language=ru");
                    var queries = new[]
                    {
                        (string)en[media == "tv" ? "original_name" : "original_title"],
                        (string)en[media == "tv" ? "name" : "title"],
                        (string)ru[media == "tv" ? "name" : "title"],
                    }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

                    foreach (string q in queries)
                    {
                        var (code, body) = await KpStore.Send(HttpMethod.Get,
                            $"{KpStore.Api}/v1/items/search?q={Uri.EscapeDataString(q)}", tok);
                        if (code != 200) continue;
                        var items = (JArray)JObject.Parse(body)["items"];
                        var m = items?.FirstOrDefault(i => (long?)i["imdb"] == num && MediaOf((string)i["type"]) == media);
                        if (m != null) { found = (long)m["id"]; break; }
                    }
                }
            }
        }
        catch { return 0; }
        finally { Gate.Release(); }

        lock (fileLock)
            Db()["t2kp"][key] = new JObject { ["kp"] = found, ["at"] = Now() };
        Save();
        return found;
    }

    // Узнать карточку для списка kino.pub параллельно, не более 8 запросов к TMDB одновременно
    public static async Task<JObject[]> ToTmdbCards(IEnumerable<JObject> items, string tok = null)
    {
        var tasks = items.Select(async it =>
        {
            await Gate.WaitAsync();
            try { return await ToTmdbCard(it, tok); }
            finally { Gate.Release(); }
        }).ToList();
        return await Task.WhenAll(tasks);
    }
}

// Последние удачные ответы kino.pub для списков «Я смотрю»: пока сервис недоступен, показываем их (до 3 суток)
static class StaleCache
{
    static readonly string path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "database", "kp_stale.json");
    static readonly object fl = new();
    static JObject mem;

    static JObject Mem()
    {
        if (mem != null) return mem;
        try { if (System.IO.File.Exists(path)) mem = JObject.Parse(System.IO.File.ReadAllText(path)); } catch { }
        return mem ??= new JObject();
    }

    public static void Put(string key, string body)
    {
        lock (fl)
        {
            Mem()[key] = new JObject { ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["body"] = body };
        }
        Persist.Later(path, () => { lock (fl) return Mem().ToString(); }, 10000);
    }

    public static string Get(string key)
    {
        lock (fl)
        {
            if (Mem()[key] is not JObject e) return null;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)e["at"] < 3 * 24 * 3600 ? (string)e["body"] : null;
        }
    }
}

// BEGIN KINO_LIST_BUDGET
// Общая работа переживает таймаут отдельного экрана и наполняет существующие кэши.
// Пока она выполняется, другой экран не запускает тот же запрос повторно.
internal static class KinoListWork<T>
{
    static readonly object gate = new object();
    static readonly Dictionary<string, Lazy<Task<T>>> running = new Dictionary<string, Lazy<Task<T>>>();

    public static Task<T> Start(string key, Func<Task<T>> load)
    {
        Lazy<Task<T>> shared;
        bool created = false;
        lock (gate)
        {
            if (!running.TryGetValue(key, out shared))
            {
                shared = new Lazy<Task<T>>(async () => await load().ConfigureAwait(false));
                running[key] = shared;
                created = true;
            }
        }
        var task = shared.Value;
        if (created)
            task.ContinueWith(done =>
            {
                // Наблюдаем и ошибку фоновой работы, чей экран уже перестал ждать.
                var observed = done.Exception;
                lock (gate)
                {
                    Lazy<Task<T>> current;
                    if (running.TryGetValue(key, out current) && ReferenceEquals(current, shared)) running.Remove(key);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
}

internal sealed class KinoListBudget
{
    readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    readonly TimeSpan timeout;
    readonly CancellationToken aborted;
    public bool Unavailable { get; private set; }

    public KinoListBudget(TimeSpan timeout, CancellationToken aborted)
    {
        this.timeout = timeout;
        this.aborted = aborted;
    }

    public void MarkUnavailable() { Unavailable = true; }

    public async Task<T> Read<T>(string key, Func<Task<T>> load, Func<T> fallback)
    {
        aborted.ThrowIfCancellationRequested();
        var left = timeout - elapsed.Elapsed;
        if (left <= TimeSpan.Zero) { Unavailable = true; return fallback(); }
        try
        {
            var work = KinoListWork<T>.Start(key, load);
            using (var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(aborted))
            {
                var delay = Task.Delay(left, delayCancel.Token);
                var finished = await Task.WhenAny(work, delay).ConfigureAwait(false);
                aborted.ThrowIfCancellationRequested();
                if (finished != work) { Unavailable = true; return fallback(); }
                delayCancel.Cancel();
                return await work.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested) { throw; }
        catch (Exception) { Unavailable = true; return fallback(); }
    }
}
// END KINO_LIST_BUDGET

[ProfileResult]
public class KinoPubCardsController : BaseController
{
    static ActionResult Json(object o, int code = 200) => new ContentResult
    {
        Content = o is string s ? s : JsonConvert.SerializeObject(CardKind.Payload(o)),
        ContentType = "application/json; charset=utf-8",
        StatusCode = code
    };

    string Prof() => KpStore.ResolveProfile(Request.Headers["X-KP-Profile"].FirstOrDefault() ?? Request.Query["profile"].FirstOrDefault());

    async Task<(JArray items, int page, int total)> Fetch(string tok, string mode, int page, KinoListBudget budget = null)
    {
        // резервный ответ принадлежит профилю и поколению аккаунта; записи без принадлежности (прежний формат ключей) не используются
        string sk = Prof() + "|" + (KpStore.AcctGen(Prof()) ?? "-") + "|";
        async Task<JObject> Get(string p)
        {
            async Task<JObject> Load()
            {
                var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/{p}", tok);
                if (code != 200) throw new HttpRequestException("kino.pub HTTP " + code);
                var value = JObject.Parse(body);
                StaleCache.Put(sk + p, body);
                return value;
            }
            JObject Cached()
            {
                var stale = StaleCache.Get(sk + p);
                return stale != null ? JObject.Parse(stale) : null;
            }
            JObject value;
            if (budget != null) value = await budget.Read("api|" + sk + p, Load, Cached);
            else { try { value = await Load(); } catch { value = Cached(); } }
            // Результат общей работы не принадлежит одному экрану: subscribed и прочие поля меняем только в копии.
            return value == null ? null : (JObject)value.DeepClone();
        }

        if (mode == "watching")
        {
            var s = await Get("watching/serials?subscribed=1");
            var all = new JArray();
            foreach (var i in (JArray)(s?["items"] ?? new JArray())) { i["subscribed"] = true; all.Add(i); }
            return (all, 1, 1);
        }

        string path = mode switch
        {
            "fresh_movie" => $"items/fresh?type=movie&perpage=20&page={page}",
            "fresh_serial" => $"items/fresh?type=serial&perpage=20&page={page}",
            _ when mode.StartsWith("bookmark:") => $"bookmarks/{mode.Substring(9)}?perpage=20&page={page}",
            _ => null
        };
        if (path == null) return (new JArray(), 1, 1);

        var d = await Get(path);
        var pg = d?["pagination"];
        return ((JArray)(d?["items"] ?? new JArray()), (int)(pg?["current"] ?? page), (int)(pg?["total"] ?? 1));
    }

    // Список kino.pub в виде штатных карточек Lampa (TMDB). Что сопоставить не удалось — отдаём как kp_legacy.
    // Строки главной просят одни и те же списки несколько раз подряд (по несколько одновременных запросов на строку): отвечаем
    // одной общей работой и коротко держим готовый ответ. Строки, зависящие от просмотра, живут 3 секунды, остальные 30.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, Lazy<Task<(string body, int code)>> run)> cardsCache = new();
    static readonly object cardsLock = new();
    // Готовая лента главного экрана: несколько страниц уже склеены, чтобы ряд не обрывался на первой короткой порции.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, JArray cards, bool more)> stripCache = new();

    static JObject[] CachedCards(List<JObject> items)
        => items.Select(kp =>
        {
            long id = (long?)kp["id"] ?? 0;
            var card = KpMap.CardForKp(id);
            if (card == null) return null;
            card["kp_id"] = id;
            card["kp_type"] = kp["type"];
            if (kp["new"] != null) card["kp_new"] = kp["new"].DeepClone();
            if (kp["subscribed"] != null) card["kp_subscribed"] = kp["subscribed"].DeepClone();
            return card;
        }).ToArray();

    [HttpGet, AllowAnonymous]
    [Route("kp/cards")]
    public async Task<ActionResult> Cards(string mode, int page = 1, string sbx = null)
    {
        if (!string.IsNullOrEmpty(sbx) && !Guard.Ok(Request)) return new ContentResult { Content = "{\"error\":\"forbidden\"}", ContentType = "application/json", StatusCode = 403 };
        IDisposable sb;
        try { sb = HomeDb.Enter(sbx); } catch { return new ContentResult { Content = "{\"error\":\"no sandbox\"}", ContentType = "application/json", StatusCode = 404 }; }
        using var _sb = sb;
        // Hiding/clearing history must be visible on the very next request.
        if(mode=="history")return await CardsCore(mode,page);
        if (int.TryParse(Request.Query["strip"].FirstOrDefault(), out int stripN) && stripN > 0)
            return await HomeStrip(mode, Math.Clamp(stripN, 12, 30));
        string key = $"{Profiles.CurrentId}|{Profiles.Current?.PolicyRev}|{sbx}|{mode}|{page}|{Prof()}|{Request.Query["dev"].FirstOrDefault()}|{Request.Query["months"].FirstOrDefault()}";
        if(ProfileCatalogPages.Supports(mode))
        {
            if(page<1||page>500)return new ContentResult{Content="{\"error\":\"bad_page\"}",ContentType="application/json",StatusCode=400};
            string poolKey=$"{Profiles.CurrentId}|{Profiles.Current?.PolicyRev ?? 0}|{sbx}|{mode}|{Prof()}|{Request.Query["dev"]}|{Request.Query["months"]}|{Request.Query["catalog_session"]}";
            try
            {
                var filled=await ProfileCatalogPages.Get(poolKey,page,async sourcePage=>{
                    var raw=await CardsCore(mode,sourcePage,true) as ContentResult;
                    if(raw==null||(raw.StatusCode??200)!=200)throw new System.Net.Http.HttpRequestException("catalog_source_unavailable");
                    var data=JObject.Parse(raw.Content);
                    return(data["results"] as JArray??new JArray(),(int?)data["total_pages"]??1);
                },async candidates=>{
                    if(mode!="top_new")await Availability.Filter(candidates,(mode=="new_movie"||mode=="new_cartoon"||mode=="ru_movie")?"movie":"tv",null,Prof(),400);
                },HttpContext.RequestAborted);
                await CardQuality.Enrich(filled.results);
                ReleasePolicy.DropBlocked(filled.results);
                return Json(new{results=filled.results,page,total_pages=filled.pages,has_more=filled.more,retry_page=filled.more&&filled.results.Count==0});
            }catch{return new ContentResult{Content="{\"error\":\"catalog_unavailable\"}",ContentType="application/json",StatusCode=502};}
        }
        bool live = mode == "continue" || mode == "watching" || mode == "watching_anime" || mode == "my_movies" || mode == "my_cartoons" || mode == "watching_cartoons" || mode == "history";
        var ttl = TimeSpan.FromSeconds(live ? 3 : 30);
        bool fresh = Request.Query["fresh"].FirstOrDefault() == "1";
        Lazy<Task<(string body, int code)>> lazy;
        // проверка и создание общей работы — под одним замком: одновременные первые запросы не запускают одинаковую работу по нескольку раз
        lock (cardsLock)
        {
            if (!fresh && cardsCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < ttl) lazy = hit.run;
            else
            {
                lazy = new Lazy<Task<(string body, int code)>>(async () =>
                {
                    var res = await CardsCore(mode, page);
                    if (res is ContentResult cr) return (cr.Content, cr.StatusCode ?? 200);
                    return ("{\"error\":\"failed\"}", 500);
                });
                cardsCache[key] = (DateTime.UtcNow, lazy);
            }
        }
        var r = await lazy.Value;
        if (r.code != 200) cardsCache.TryRemove(key, out _);   // ошибки не запоминаем
        if (cardsCache.Count > 200) foreach (var k in cardsCache.Where(kv => DateTime.UtcNow - kv.Value.at > TimeSpan.FromMinutes(5)).Select(kv => kv.Key).ToList()) cardsCache.TryRemove(k, out _);
        return new ContentResult { Content = r.body, ContentType = "application/json; charset=utf-8", StatusCode = r.code };
    }

    async Task<(JArray results, bool more)> FetchPage(string mode, int page)
    {
        if (Profiles.Current?.Child == true && ProfileCatalogPages.Supports(mode))
        {
            string poolKey = $"{HomeDb.SandboxId}|{Profiles.CurrentId}|{Profiles.Current.PolicyRev}|{mode}|{Prof()}|{Request.Query["dev"]}|{Request.Query["months"]}";
            var filled = await ProfileCatalogPages.Get(poolKey, page, async sourcePage =>
            {
                var raw = await CardsCore(mode, sourcePage, true) as ContentResult;
                if (raw == null || (raw.StatusCode ?? 200) != 200) throw new System.Net.Http.HttpRequestException("catalog_source_unavailable");
                var data = JObject.Parse(raw.Content);
                return (data["results"] as JArray ?? new JArray(), (int?)data["total_pages"] ?? 1);
            }, async candidates =>
            {
                if (mode != "top_new") await Availability.Filter(candidates, (mode == "new_movie" || mode == "new_cartoon" || mode == "ru_movie") ? "movie" : "tv", null, Prof(), 400);
            }, fill: true);
            await CardQuality.Enrich(filled.results);
            ReleasePolicy.DropBlocked(filled.results);
            return (filled.results, filled.more);
        }
        var res = await CardsCore(mode, page);
        if (res is not ContentResult cr || (cr.StatusCode ?? 200) != 200) throw new System.Net.Http.HttpRequestException("catalog_page_unavailable");
        var body = JObject.Parse(cr.Content ?? "{}");
        var results = body["results"] as JArray ?? new JArray();
        int total = (int?)body["total_pages"] ?? 1;
        bool pageMore = (bool?)body["has_more"] == true || page < total;
        return (results, pageMore);
    }

    async Task<ActionResult> HomeStrip(string mode, int want)
    {
        bool personal = mode is "continue" or "watching" or "watching_anime" or "my_movies" or "my_cartoons" or "my_anime_movies" or "watching_cartoons";
        var ttl = personal ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(10);
        string key = $"strip|{HomeDb.SandboxId}|{Profiles.CurrentId}|{Profiles.Current?.PolicyRev}|{mode}|{Prof()}|{Request.Query["dev"].FirstOrDefault()}|{Request.Query["months"].FirstOrDefault()}|{want}";
        bool fresh = Request.Query["fresh"].FirstOrDefault() == "1";
        stripCache.TryGetValue(key, out var hit);
        if (!personal && !fresh && hit.cards != null && DateTime.UtcNow - hit.at < ttl)
            return Json(new { results = hit.cards, page = 1, total_pages = 1, has_more = hit.more });
        var acc = new List<JObject>();
        var seen = new HashSet<string>();
        bool sourceMore = false;
        bool stoppedEarly = false;
        bool childCatalog = Profiles.Current?.Child == true && ProfileCatalogPages.Supports(mode);
        var deadline = DateTime.UtcNow.AddSeconds(personal ? 4 : childCatalog ? 20 : 6);
        try
        {
            for (int p = 1; p <= 8 && acc.Count < want; p++)
            {
                if (DateTime.UtcNow >= deadline && acc.Count > 0) { stoppedEarly = sourceMore; break; }
                var (pageCards, pageMore) = await FetchPage(mode, p);
                int before = acc.Count;
                foreach (var c in pageCards.Children<JObject>())
                {
                    string id = ((string)c["media_type"] ?? (c["name"] != null ? "tv" : "movie")) + ":" + (c["id"] ?? c["kp_id"]);
                    if (seen.Add(id)) acc.Add((JObject)c.DeepClone());
                }
                sourceMore = pageMore;
                if (!pageMore) break;
                if (acc.Count == before) { stoppedEarly = true; break; }
            }
        }
        catch
        {
            if (!personal && !fresh && hit.cards != null) return Json(new { results = hit.cards, page = 1, total_pages = 1, has_more = hit.more });
            if (acc.Count == 0) return Json(new { error = "failed" }, 502);
        }
        bool more = acc.Count > want || sourceMore;
        var cards = new JArray(acc.Take(want));
        if (!personal && !stoppedEarly) stripCache[key] = (DateTime.UtcNow, cards, more);
        if (stripCache.Count > 80)
            foreach (var stale in stripCache.Where(kv => DateTime.UtcNow - kv.Value.at > TimeSpan.FromMinutes(30)).Select(kv => kv.Key).ToList())
                stripCache.TryRemove(stale, out _);
        return Json(new { results = cards, page = 1, total_pages = 1, has_more = more });
    }

    async Task<ActionResult> CardsCore(string mode, int page,bool catalogScan=false)
    {
        // «Мои аниме» — тот же список «Я смотрю», но только аниме; «Мои сериалы» — всё остальное
        bool cartoonRow=mode=="watching_cartoons";
        bool animeMovies=mode=="my_anime_movies",cartoonMovies=mode=="my_cartoons";
        if(animeMovies||cartoonMovies)mode="my_movies";
        bool animeRow = mode == "watching_anime";
        if(cartoonRow)mode="watching";
        if (animeRow) mode = "watching";
        // Общие новинки (TMDB + kino.pub) и библиотека Plex
        if (mode == "top_new" || mode == "new_movie" || mode == "new_cartoon" || mode == "new_cartoon_series" || mode == "new_serial" || mode == "anime" || mode == "ru_serial" || mode == "ru_movie" || mode == "my_movies" || (mode ?? "").StartsWith("plex"))
        {
            try
            {
                // Local catalogs must not depend on OAuth refresh of an optional provider.
                string tok0 = null;
                bool cloudUnavailable = false;
                if (mode is "top_new" or "new_movie" or "new_serial")
                {
                    var tokenBudget = new KinoListBudget(TimeSpan.FromSeconds(3), HttpContext.RequestAborted);
                    tok0 = await tokenBudget.Read("token|" + Prof() + "|" + KpStore.AcctGen(Prof()),
                        () => KpStore.Token(Prof()), () => (string)null);
                    cloudUnavailable = tok0 == null;
                }
                (JArray results, int totalPages) r;
                async Task<(JArray results,int pages)> LoadPublicSource()
                {
                    (JArray results,int totalPages) source;
                    if (mode == "top_new")
                    {
                        // порог рейтинга и окно (месяцев) — из настроек устройства; по умолчанию 7.9 и 3 месяца
                        int months = 3; double minRate = 7.9;
                        try
                        {
                            string dv = Request.Query["dev"].FirstOrDefault();
                            if (DeviceStore.ValidId(dv))
                            {
                                var dc = DeviceStore.Hello(dv, null, null) ?? new JObject();
                                if ((string)dc["mc_top"] == "0") return (new JArray(),1);   // лента выключена на этом устройстве
                                if (int.TryParse((string)dc["mc_topnew"], out int mo) && mo >= 0 && mo <= 120) months = mo;
                                if (double.TryParse((string)dc["mc_toprate"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mr) && mr >= 5 && mr <= 9.5) minRate = mr;
                            }
                            if (int.TryParse(Request.Query["months"].FirstOrDefault(), out int mq) && mq >= 0 && mq <= 120) months = mq;
                        }
                        catch { }
                        source = await KpUnified.TopNew(page, KpUnified.BestNoveltyMonths, minRate, tok0,catalogScan);     // фильтр доступности внутри
                    }
                    else if (mode == "new_movie") source = await KpUnified.NewReleases("movie", page, tok0, requireSource:catalogScan);
                    else if (mode == "new_cartoon") source = await KpUnified.CartoonList(page);
                    else if (mode == "new_cartoon_series") source = await KpUnified.CartoonSeriesList(page);
                    else if (mode == "new_serial") source = await KpUnified.NewReleases("tv", page, tok0, "no", requireSource:catalogScan);
                    else if (mode == "anime") source = await KpUnified.AnimeList(page);
                    else if (mode == "ru_serial") source = await KpUnified.RussianList("tv", page);
                    else if (mode == "ru_movie") source = await KpUnified.RussianList("movie", page);
                    else if (mode == "my_movies") source = (await WatchListController.MyMovies(), 1);
                    else source = await KpUnified.PlexList(mode.Contains(':') ? mode.Substring(mode.IndexOf(':') + 1) : null, page);
                    return (source.results,source.totalPages);
                }
                if (catalogScan && ProfileCatalogPages.Supports(mode))
                {
                    // No catalog_session/viewer in this raw key. Authorization,
                    // age filtering and personal progress are never cached here.
                    string sourceOptions = "";
                    if (mode == "top_new" && DeviceStore.ValidId(Request.Query["dev"].FirstOrDefault())) {
                        var dc = DeviceStore.Hello(Request.Query["dev"].FirstOrDefault(), null, null);
                        sourceOptions = (string)dc?["mc_top"]+":"+(string)dc?["mc_toprate"];
                    }
                    string sourceKey = string.Join("|", HomeDb.SandboxId, Prof(), KpStore.AcctGen(Prof()),
                        mode, page, sourceOptions, Request.Query["months"].ToString(),
                        tok0 == null ? "local" : "cloud", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                    r = await ProfileCatalogPages.Source(sourceKey, LoadPublicSource);
                }
                else r = await LoadPublicSource();
                if(Profiles.Current?.Child==true)
                {
                    var cards = r.results.Children<JObject>().Where(card => !catalogScan || Availability.HasRu(card)).ToList();
                    var kept = new List<JObject>();
                    var wait = new List<JObject>();
                    foreach (var card in cards)
                    {
                        var decision = ProfilePolicy.Classify(card, enqueue: !catalogScan);
                        if (decision == ProfilePolicy.AgeDecision.Allow) kept.Add(card);
                        else if (catalogScan && decision == ProfilePolicy.AgeDecision.Pending) wait.Add(card);
                    }
                    // Возраст ещё не известен: в этой же загрузке ленты его узнаём, а не оставляем одну уже проверенную карточку.
                    if (wait.Count > 0)
                    {
                        using var ageBudget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var work = wait.Select(async card =>
                        {
                            try
                            {
                                if (await ProfilePolicy.Allows(card).WaitAsync(ageBudget.Token))
                                    lock (kept) kept.Add(card);
                            }
                            catch { }
                        });
                        try { await Task.WhenAll(work); } catch { }
                    }
                    r.results = new JArray(kept);
                }
                // русское название и русская озвучка (kino.pub или живая раздача); библиотеку Plex не трогаем
                if(catalogScan)r.results=new JArray(r.results.Children<JObject>().Where(Availability.HasRu));
                if (!catalogScan && !(mode ?? "").StartsWith("plex") && mode != "my_movies" && mode != "top_new")
                    await Availability.Filter(r.results, (mode == "new_movie" || mode == "new_cartoon" || mode == "ru_movie") ? "movie" : "tv", tok0, Prof(), 400);
                if(mode=="my_movies")foreach(var card in r.results.Children<JObject>().ToList()){
                    string kind=CardKind.Kind(card);if(animeMovies?kind!="anime_movie":cartoonMovies?kind!="cartoon_movie":kind!="movie")card.Remove();
                }
                if(!catalogScan)await CardQuality.Enrich(r.results);
                if(!catalogScan && mode != "my_movies" && !(mode ?? "").StartsWith("plex"))
                    ReleasePolicy.DropBlocked(r.results);
                bool more=page<r.totalPages;
                return Json(new { results = r.results, page, total_pages = r.totalPages, has_more = more, kino_unavailable = cloudUnavailable });
            }
            catch (Exception ex) { return Json(new { error = "failed", message = ex.GetType().Name }, 502); }
        }

        // Единые списки по общей базе позиций (kino.pub + Lampa)
        if (mode == "continue" || mode == "history")
        {
            try
            {
                var (results, totalPages) = mode=="history" ? await HistoryStore.List(page,20) : await KpUnified.List(mode, page, 20, "movie");
                // «Продолжить просмотр» — только фильмы: начатые сериалы живут в «Я смотрю»
                if (mode == "continue")
                    foreach (var c in results.Children<JObject>().Where(c => c["name"] != null || c["original_name"] != null || c["number_of_seasons"] != null || c["first_air_date"] != null).ToList())
                        c.Remove();
                await CardQuality.Enrich(results);
                return Json(new { results, page, total_pages = totalPages });
            }
            catch (Exception ex) { return Json(new { error = "failed", message = ex.GetType().Name }, 502); }
        }

        string prof = Prof();
        string tok = null;
        var budget = mode == "watching" ? new KinoListBudget(TimeSpan.FromSeconds(3), HttpContext.RequestAborted) : null;
        string scope = Profiles.CurrentId+"|"+prof + "|" + (KpStore.AcctGen(prof) ?? "-");
        if (budget != null)
        {
            tok = await budget.Read("token|" + scope, () => KpStore.Token(prof), () => (string)null);
            if (tok == null) budget.MarkUnavailable();
        }
        else tok = await KpStore.Token(prof);
        // «Я смотрю»: нет токена или обновление не удалось — это «часть Кинопаба недоступна», а не отказ всего списка; локальные подписки, отслеживание и просмотры собираются дальше
        if (tok == null && mode != "watching") return Json(new { error = "not_authorized" }, 401);

        try
        {
            var (items, cur, total) = (tok == null || (Profiles.Enabled && mode == "watching")) ? (new JArray(), 1, 1) : await Fetch(tok, mode ?? "", page, budget);
            var objs = items.Children<JObject>().ToList();
            JObject[] cards;
            if (tok == null) cards = new JObject[0];
            else if (budget == null) cards = await KpMap.ToTmdbCards(objs, tok);
            else
            {
                string input = items.ToString(Formatting.None);
                string mapKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
                cards = await budget.Read("cards|" + scope + "|" + mapKey, () => KpMap.ToTmdbCards(objs, tok), () => CachedCards(objs));
                cards = cards.Select(card => card == null ? null : (JObject)card.DeepClone()).ToArray();
            }

            var results = new JArray();
            for (int i = 0; i < objs.Count; i++)
            {
                if (cards[i] != null) results.Add(cards[i]);
                else results.Add(new JObject { ["kp_legacy"] = true, ["kp"] = objs[i] });
            }

            // «Я смотрю»: к подпискам kino.pub добавляем тайтлы, отмеченные вручную (в том числе без Кинопаба)
            if (mode == "watching")
            {
                // убранные вручную («Убрать из Я смотрю») скрываем и среди карточек самого кино.пуб,
                // иначе фильм из его списка «смотрю» возвращался бы после каждого обновления
                var hid0 = KpUnified.Hidden();
                for (int i = results.Count - 1; i >= 0; i--)
                {
                    if (results[i] is not JObject r0 || r0["id"] == null) continue;
                    long tm0 = (long?)r0["id"] ?? 0;
                    if (tm0 <= 0) continue;
                    string md0 = (r0["name"] != null || r0["original_name"] != null || r0["number_of_seasons"] != null) ? "tv" : "movie";
                    if (hid0["w_" + tm0 + "_" + md0] != null) results.RemoveAt(i);
                }

                var have = new System.Collections.Generic.HashSet<long>(results.Children<JObject>().Where(r => r["id"] != null).Select(r => (long)r["id"]));
                foreach (var w in LocalWatchlist.Load().Children<JObject>().Where(x => (string)x["media"] == "tv"))
                {
                    string media = (string)w["media"]; long tmdb = (long)w["tmdb"];
                    if (have.Contains(tmdb)) continue;
                    var c = await KpMap.TmdbCard(media, tmdb);
                    if (c == null) continue;
                    long kpid = KpMap.CachedKp(media, tmdb);
                    if (kpid > 0) c["kp_id"] = kpid;
                    c["watch_manual"] = true;
                    results.Add(c);
                    have.Add(tmdb);
                }

                // и то, что начали смотреть в Lampa/Plex (по общим отметкам просмотра), если не убрано вручную
                var hid = KpUnified.Hidden();
                var (started, _) = await KpUnified.List("continue", 1, 300, "tv");
                foreach (var c in started.Children<JObject>())
                {
                    long tm = (long?)c["id"] ?? 0;
                    if (tm <= 0) continue;
                    // сериал, который смотрели в Lampa, остаётся «отслеживаемым» насовсем: новый сезон через несколько месяцев вернёт его в ленту
                    if ((long?)c["watch_at"] is long wa0 && wa0 > 0) KpUnified.TrackAdd(tm, wa0);
                    if (have.Contains(tm)) continue;
                    string md = (c["name"] != null || c["original_name"] != null) ? "tv" : "movie";
                    if (md != "tv") continue;   // «Мои сериалы» — только сериалы; начатые фильмы живут в «Продолжить просмотр»
                    long wat = (long?)c["watch_at"] ?? 0;
                    var hk = hid["w_" + tm + "_" + md];
                    if (hk != null && wat <= (long)hk) continue;
                    c["watch_auto"] = true;
                    results.Add(c);
                    have.Add(tm);
                }

                // отслеживаемые раньше, но давно не смотренные (вне 45-дневного окна «Продолжить»): показываем, если не убраны вручную; дальше их скроет/вернёт проверка серий
                foreach (var (ttm, tat) in KpUnified.TrackList())
                {
                    if (have.Contains(ttm)) continue;
                    var hk2 = hid["w_" + ttm + "_tv"];
                    if (hk2 != null && tat <= (long)hk2) continue;
                    var c = await KpMap.TmdbCard("tv", ttm);
                    if (c == null) continue;
                    long kpid = KpMap.CachedKp("tv", ttm);
                    if (kpid > 0) c["kp_id"] = kpid;
                    c["watch_auto"] = true;
                    results.Add(c);
                    have.Add(ttm);
                }
            }

            if (mode == "watching")
            {
                for (int i = results.Count - 1; i >= 0; i--)
                    if (results[i] is JObject rc && rc["kp_legacy"] == null){var kind=CardKind.Kind(rc);if(animeRow?!kind.StartsWith("anime_"):cartoonRow?kind!="cartoon_series":kind!="series")results.RemoveAt(i);}
                if(animeRow)foreach(var movie in (await WatchListController.MyMovies()).Children<JObject>().Where(c=>CardKind.Kind(c)=="anime_movie"))results.Add(movie);
                if (!Profiles.Enabled) await KpBridge.AdjustNew(tok, results);
                else
                {
                    string availabilityAccount = prof+"|"+KpStore.AcctGen(prof);
                    await ProfileAvailability.EnrichCached(results, availabilityAccount);
                    // Late provider work may finish after the screen's budget. Never
                    // let it mutate the array already used to build this response.
                    var copy = (JArray)results.DeepClone();
                    var enriched = await budget.Read("availability|"+scope+"|"+Guid.NewGuid().ToString("N"), async () => {
                        await ProfileAvailability.Enrich(copy,tok,availabilityAccount);
                        return copy;
                    }, () => (JArray)null);
                    if (enriched != null) results = enriched;
                }

                // Сериал, у которого всё вышедшее уже досмотрено, из «Мои сериалы/аниме» убираем — он вернётся сам, когда выйдет новая серия.
                // Смотрим по множеству серий (а не по максимальному номеру): пропущенная серия оставляет сериал в ленте.
                // Не трогаем то, что пользователь добавил вручную, и то, для чего не удалось узнать вышедшие серии.
                try
                {
                    var wsets = await KpUnified.WatchedSets();
                    var resets = await KpUnified.RecentResets();
                    var starts = await KpUnified.StartedFirst();
                    for (int i = results.Count - 1; i >= 0; i--)
                    {
                        if (results[i] is not JObject rc || rc["kp_legacy"] != null || (!Profiles.Enabled && (bool?)rc["watch_manual"] == true)) continue;
                        long tid = (long?)rc["id"] ?? 0;
                        if (tid <= 0) continue;
                        if (Profiles.Enabled && (int?)rc["kp_new"] > 0) continue;
                        bool fromKino = rc["kp_subscribed"] != null;
                        if (fromKino && (int?)rc["kp_new"] != 0) continue;                 // у подписки Кинопаба есть непросмотренные серии
                        var seen = new HashSet<(int, int)>();
                        if (wsets.TryGetValue(tid, out var ws)) seen.UnionWith(ws);
                        long kpId = KpMap.CachedKp("tv", tid);
                        HashSet<(int, int)> ks = null;
                        if (kpId > 0 && tok != null)
                        {
                            ks = await budget.Read("seen|" + scope + "|" + tok.GetHashCode() + "|" + kpId,
                                () => KpUnified.KinoWatchedSet(kpId, tok), () => KpUnified.CachedKinoWatchedSet(kpId, tok));
                            if (ks == null) budget.MarkUnavailable();
                        }
                        if (ks != null) seen.UnionWith(ks);
                        // явный сброс — отдельное отрицательное состояние серии; не выводится из пересечения с положительным набором
                        resets.TryGetValue(tid, out var rs);
                        bool hadReset = rs != null && rs.Count > 0;
                        if (hadReset) seen.ExceptWith(rs);                                                                   // сильнее положительной отметки Кинопаба, пока мост не снял её
                        if (seen.Count == 0) { if (fromKino && !hadReset && ks != null) results.RemoveAt(i); continue; }      // состояние Кинопаба не прочитано (ks==null) — это «неизвестно», а не основание скрыть карточку                    // после сброса пусто = «ничего не просмотрено», а не «всё досмотрено»
                        var un = KpUnified.HasUnseenAired(KpUnified.AiredFromCard(rc) ?? await KpUnified.AiredCounts(tid), seen, starts.TryGetValue(tid, out int fs) ? fs : (int?)null, rs);
                        if (un == false) results.RemoveAt(i);
                    }
                }
                catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
                catch { }
            }
            if(mode=="watching")results=KpUnified.Chronological(results);
            await CardQuality.Enrich(results);
            return budget?.Unavailable == true ? Json(new { results, page = cur, total_pages = total, kino_unavailable = true }) : Json(new { results, page = cur, total_pages = total });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Json(new { error = "failed", message = ex.GetType().Name }, 502);
        }
    }

    // Есть ли тайтлы в Кинопаб: keys=movie:603,tv:1396 -> { "movie:603": 113, "tv:1396": 0 }
    [HttpGet, AllowAnonymous]
    [Route("kp/has")]
    public async Task<ActionResult> Has(string keys)
    {
        string prof = Prof();
        var res = new JObject();
        var list = (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Take(60).ToList();

        var tasks = list.Select(async k =>
        {
            var p = k.Split(':');
            if (p.Length != 2 || (p[0] != "movie" && p[0] != "tv") || !long.TryParse(p[1], out long id)) return (k, 0L);
            return (k, await KpMap.LookupKp(p[0], id, prof));
        }).ToList();

        foreach (var (k, kp) in await Task.WhenAll(tasks)) res[k] = kp;
        return Json(res);
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/progress")]
    public async Task<ActionResult> ProgressInfo(string type, long tmdb)
    {
        if (type != "movie" && type != "tv") return Json(new { error = "bad_type" }, 400);
        var p = await KpUnified.Progress(type, tmdb);
        return Json(new { progress = p });
    }

    [HttpGet, AllowAnonymous]
    [Route("kp/lookup")]
    public async Task<ActionResult> Lookup(string type, long tmdb)
    {
        if (type != "movie" && type != "tv") return Json(new { error = "bad_type" }, 400);
        long kp = await KpMap.LookupKp(type, tmdb, Prof());
        return Json(new { kp = kp > 0 ? (long?)kp : null });
    }
}
