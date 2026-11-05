using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Фильтр «есть что смотреть по-русски» для списков новинок и аниме. Карточка остаётся, если
//   1) у названия есть кириллица (иероглифы и непереведённые названия убираем), и
//   2) тайтл есть в библиотеке Plex, либо в JacRed есть живая раздача с русской озвучкой, либо он есть на kino.pub (проверяется
//      последним и с лимитом)
//      (сиды > 0) с русской озвучкой. Если источник не ответил, это не считается отказом: карточку не прячем.
// Результат кэшируется (положительный дольше), проверка ограничена по времени: что не успели проверить, показываем
// как есть — при следующем открытии оно уже отфильтровано по кэшу.
public static class Availability
{
    static readonly string path = Path.Combine(Directory.GetCurrentDirectory(), "database", "availability_cache.json");
    static readonly ConcurrentDictionary<string, AEntry> cache = Load();
    static readonly SemaphoreSlim gate = new(6, 6);
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly object saveLock = new();

    class AEntry { public bool ok; public long at; }

    static readonly Regex reCyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);
    static readonly Regex reCjk = new("[぀-ヿ㐀-鿿가-힯]", RegexOptions.Compiled);
    static readonly Regex reVoice = new(@"Дубл|дубл|MVO|DVO|AVO|\bПМ\b|\bЛД\b|\bЛМ\b|\bАП\b|озвуч|Озвуч|Многоголос|Закадров|SHIZA|AniLibria|Anilibria|AniDub|\bDUB\b|\bRUS\b", RegexOptions.Compiled);

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static bool Fresh(AEntry e) => Now - e.at < (e.ok ? 12 * 3600 : 3 * 3600);

    static ConcurrentDictionary<string, AEntry> Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, AEntry>>(File.ReadAllText(path));
                if (d != null) return new ConcurrentDictionary<string, AEntry>(d);
            }
        }
        catch { }
        return new ConcurrentDictionary<string, AEntry>();
    }

    static void Save() => Persist.Later(path, () => { lock (saveLock) return JsonConvert.SerializeObject(cache); });

    // название на русском (кириллица) или оригинал на русском языке
    public static bool HasRu(JObject c)
    {
        if ((bool?)c["kp_legacy"] == true) return true;      // тайтл из самого Кинопаба: он по определению доступен
        string t = (string)c["title"] ?? (string)c["name"] ?? "";
        if ((string)c["original_language"] == "ru") return true;
        return reCyr.IsMatch(t);
    }

    // Проверка тайтла в kino.pub дорогая (до шести запросов), поэтому не больше 12 проверок в минуту на весь сервер.
    // Что не успели проверить, показываем как есть: оно докатится при следующих открытиях и запомнится в карте.
    static readonly object rl = new();
    static readonly Queue<long> lookups = new();
    static bool TakeLookup()
    {
        lock (rl)
        {
            long now = Now;
            while (lookups.Count > 0 && now - lookups.Peek() > 60) lookups.Dequeue();
            if (lookups.Count >= 12) return false;
            lookups.Enqueue(now);
            return true;
        }
    }

    static long probeAt;
    static bool probeOk;

    // JacRed считается живым, если на заведомо популярный запрос отвечает результатами. Иначе «раздач нет» ничего не значит:
    // при недоступном JacRed карточки не прячем и ничего не запоминаем.
    static async Task<bool> JacredAlive()
    {
        if (Now - probeAt < 300) return probeOk;
        try
        {
            var j = JObject.Parse(await http.GetStringAsync("http://127.0.0.1:9118/jr/api/v2.0/indexers/all/results?apikey=&Query=" + Uri.EscapeDataString("Матрица")));
            probeOk = j["Results"] is JArray a && a.Count > 0;
        }
        catch { probeOk = false; }
        probeAt = Now;
        return probeOk;
    }

    static bool TitleMatch(string query, string title)
    {
        var words = Regex.Split(query.ToLowerInvariant(), @"[^\p{L}\d]+").Where(w => w.Length >= 4).ToList();
        if (words.Count == 0) return true;
        string t = title.ToLowerInvariant();
        int hit = words.Count(w => t.Contains(w));
        return hit >= Math.Max(1, (words.Count + 1) / 2);
    }

    // живая раздача с русской озвучкой: сиды > 0 и (язык rus, озвучка в данных или в названии)
    static async Task<bool> TorrentRu(JObject c)
    {
        string ru = (string)c["title"] ?? (string)c["name"] ?? "";
        string orig = (string)c["original_title"] ?? (string)c["original_name"] ?? "";
        // оригинал на иероглифах в поиск не берём
        var queries = new[] { ru, orig }.Where(q => !string.IsNullOrWhiteSpace(q) && !reCjk.IsMatch(q)).Distinct().ToList();

        foreach (var q in queries)
        {
            string url = "http://127.0.0.1:9118/jr/api/v2.0/indexers/all/results?apikey=&Query=" + Uri.EscapeDataString(q);
            var j = JObject.Parse(await http.GetStringAsync(url));
            if (j["Results"] is not JArray results) continue;
            foreach (var r in results.Children<JObject>())
            {
                if (!int.TryParse((string)r["Seeders"] ?? "0", out int seeders) || seeders <= 0) continue;
                string title = (string)r["Title"] ?? "";
                if (!TitleMatch(q, title)) continue;
                if (ReleasePolicy.Hide(r)) continue;              // только субтитры, настройка выключена
                if (ReleasePolicy.BlockedQuality(r)) continue;    // тип качества в чёрном списке лент
                bool ruOrig = (string)c["original_language"] == "ru";   // российское: озвучка родная, достаточно живой раздачи
                bool rus = ruOrig || (r["languages"] is JArray l && l.Any(x => (string)x == "rus"));
                bool voices = r["info"]?["voices"] is JArray v && v.Count > 0;
                if (rus || voices || reVoice.IsMatch(title))
                {
                    string media = (string)c["media_type"] ?? ((c["name"] != null || c["original_name"] != null) ? "tv" : "movie");
                    ReleasePolicy.Track(media, (long?)c["id"] ?? 0, title);
                    return true;
                }
            }
        }
        return false;
    }

    static async Task<bool> Check(JObject c, string media, string tok, string prof)
    {
        long id = (long?)c["id"] ?? 0;
        string key = media + ":" + id;
        if (cache.TryGetValue(key, out var hit) && Fresh(hit)) return hit.ok;

        await gate.WaitAsync();
        try
        {
            bool ok = false, unknown = false;
            // есть в вашей библиотеке Plex — показываем
            try
            {
                if (PlexClient.Ready())
                {
                    await PlexClient.EnsureIndex();
                    if (PlexClient.All().Any(i => i.kind == media && i.tmdb == id)) { cache[key] = new AEntry { ok = true, at = Now }; return true; }
                }
            }
            catch { }

            // 1) уже сопоставлен с kino.pub — показываем сразу, без запросов
            long kp = (long?)c["kp_id"] ?? KpMap.CachedKp(media, id);
            if (kp > 0) ok = true;

            // 2) раздачи JacRed (все трекеры сразу: rutracker, rutor, kinozal, nnmclub, megapeer, bitru, lostfilm, baibako,
            //    torrentby, hdrezka, selezen, animelayer, anilibria, anifilm) — локальный запрос, дешёвый
            if (!ok)
            {
                if (!await JacredAlive()) unknown = true;
                else { try { ok = await TorrentRu(c); } catch { unknown = true; } }
            }

            // 3) нигде не нашли — спрашиваем kino.pub (дорого, поэтому в пределах лимита; результат запоминается в карте)
            if (!ok && !KpMap.IsKnown(media, id) && tok != null)
            {
                if (TakeLookup())
                {
                    try
                    {
                        kp = await KpMap.LookupKp(media, id, prof);
                        if (kp > 0) { ok = true; unknown = false; }
                        else if (!KpMap.IsKnown(media, id)) unknown = true;   // не удалось выяснить — не считаем «нет»
                    }
                    catch { unknown = true; }
                }
                else unknown = true;
            }
            if (!ok && unknown) return true;   // данных нет (источник не ответил / лимит) — не прячем и не запоминаем
            cache[key] = new AEntry { ok = ok, at = Now };
            return ok;
        }
        finally { gate.Release(); }
    }

    // Убирает из списка карточки без русского названия и без русской озвучки. budgetMs — сколько ждём проверку.
    public static async Task Filter(JArray cards, string media, string tok, string prof, int budgetMs = 2200)
    {
        try
        {
            foreach (var c in cards.Children<JObject>().Where(x => !HasRu(x)).ToList()) c.Remove();

            var list = cards.Children<JObject>().Where(x => (long?)x["id"] > 0).ToList();
            var resolved = new ConcurrentDictionary<long, bool>();
            var tasks = list.Select(async c =>
            {
                try { resolved[(long)c["id"]] = await Check(c, media, tok, prof); } catch { }
            }).ToList();

            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(budgetMs));
            foreach (var c in list)
                if (resolved.TryGetValue((long)c["id"], out bool ok) && !ok) c.Remove();

            _ = Task.WhenAll(tasks).ContinueWith(_ => Save());
        }
        catch { }
    }
}
