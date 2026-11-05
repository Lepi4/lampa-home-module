using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Единые списки «Продолжить просмотр» и «История»: строятся по общей базе позиций (TimeCode, uid home),
// куда пишут и Lampa (торренты, онлайн-источники), и мост kino.pub.
public static class KpUnified
{
    public const int NoveltyYears = 3;
    public const int BestNoveltyMonths = 12;
    static string NoveltyFrom => DateTime.UtcNow.Date.AddYears(-NoveltyYears).ToString("yyyy-MM-dd");
    static readonly HttpClient Loop = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly SemaphoreSlim Gate = new(8, 8);

    sealed class Entry
    {
        public long Tmdb;
        public string Media;
        public long LastAt;
        public double LastPct;
        public double LastPos;
        public bool LastDone;
        public string LastEv;
    }

    // Единый предикат «досмотрено» для строки дампа: явные completed/reset главнее, иначе общее правило IsDone с длительностью
    public static bool RowDone(JObject r)
    {
        string op = (string)r["op"];
        if (op == "reset") return false;
        if (op == "completed") return true;
        return IsDone((double?)r["position"] ?? 0, (double?)r["duration"] ?? 0, (double?)r["percent"] ?? 0);
    }

    // Недосмотренное по карточке: последняя по времени запись, если она не завершена (< 90%).
    // Единое правило: просмотрено — до конца осталось не больше 10 минут (и пройдена половина) или просмотр дошёл до конца
    public static bool IsDone(double pos, double dur, double pct)
        => dur > 0 ? ((dur - pos <= Math.Min(600, dur * 0.12) && pos >= dur * 0.5) || pct >= 90 || pos / dur >= 0.90) : pct >= 90;

    public static async Task<JObject> Progress(string media, long tmdb)
    {
        var dump = await TlV2.DumpAsync();
        string card = $"{tmdb}_{media}";
        JObject last = null;
        long lastAt = -1;

        foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
        {
            if ((bool?)r["deleted"] == true || (string)r["card"] != card) continue;
            long at = (long?)r["watched_at"] ?? 0;
            if (at >= lastAt) { lastAt = at; last = r; }
        }

        if (last == null) return null;
        double pct = (double?)last["percent"] ?? 0, pos = (double?)last["position"] ?? 0, dur = (double?)last["duration"] ?? 0;
        if (dur <= 0 || pos <= 1 || IsDone(pos, dur, pct)) return null;

        int season = 0, episode = 0;
        var m = System.Text.RegularExpressions.Regex.Match((string)last["id"] ?? "", @"^tv-\d+-s(\d+)e(\d+)$");
        if (m.Success) { season = int.Parse(m.Groups[1].Value); episode = int.Parse(m.Groups[2].Value); }

        return new JObject
        {
            ["season"] = season,
            ["episode"] = episode,
            ["position"] = pos,
            ["duration"] = dur,
            ["percent"] = pct,
            ["remaining"] = Math.Round(dur - pos),
            ["watched_at"] = lastAt
        };
    }

    // Вышедшие серии по TMDB: сезон → число вышедших серий (в последнем сезоне — до последней вышедшей; будущие сезоны не считаются)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime at, Dictionary<int, int> aired)> airedCache = new();

    public static Dictionary<int,int> AiredFromCard(JObject card)
    {
        var last=card?["last_episode_to_air"];int sn=(int?)last?["season_number"]??0,ep=(int?)last?["episode_number"]??0;
        if(sn<=0||ep<=0)return null;var counts=new Dictionary<int,int>();
        foreach(var season in (card["seasons"] as JArray??new JArray()).Children<JObject>()){int n=(int?)season["season_number"]??0,c=(int?)season["episode_count"]??0;if(n>0&&n<=sn)counts[n]=n==sn?Math.Min(ep,c>0?c:ep):c;}
        counts[sn]=ep;return counts;
    }
    // Личные сериалы: сначала тайтл, у которого последняя вышедшая серия новее. Дата премьеры сюда не подходит —
    // иначе старый сериал с новой серией оказывается в конце. Фильм без серий сортируется по дате выхода.
    public static JArray Chronological(JArray cards)
    {
        DateTime Date(JObject c)
        {
            string value = (string)c["last_episode_to_air"]?["air_date"] ?? (string)c["last_air_date"] ?? (string)c["release_date"];
            return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) && d.Date <= DateTime.UtcNow.Date ? d.Date : DateTime.MinValue;
        }
        return new JArray(cards.Children<JObject>().OrderByDescending(Date).ThenByDescending(c => (int?)c["last_episode_to_air"]?["season_number"] ?? 0).ThenByDescending(c => (int?)c["last_episode_to_air"]?["episode_number"] ?? 0).ThenBy(c => (string)c["name"] ?? (string)c["title"], StringComparer.Ordinal));
    }
    public static async Task<Dictionary<int, int>> AiredCounts(long tmdb)
    {
        if (airedCache.TryGetValue(tmdb, out var h) && DateTime.UtcNow - h.at < TimeSpan.FromHours(3)) return h.aired;
        try
        {
            var d = await KpMap.TmdbRaw($"tv/{tmdb}?language=ru");
            var l = d?["last_episode_to_air"];
            if (l == null || l.Type != JTokenType.Object) return null;
            int ls = (int?)l["season_number"] ?? 0, le = (int?)l["episode_number"] ?? 0;
            if (ls <= 0 || le <= 0) return null;
            var res = new Dictionary<int, int>();
            foreach (var se in ((JArray)(d["seasons"] ?? new JArray())).Children<JObject>())
            {
                int sn = (int?)se["season_number"] ?? 0, cnt = (int?)se["episode_count"] ?? 0;
                if (sn <= 0 || sn > ls) continue;                         // спецвыпуски (сезон 0) и невышедшие сезоны не считаем
                res[sn] = sn == ls ? Math.Min(le, cnt > 0 ? cnt : le) : cnt;
            }
            if (!res.ContainsKey(ls)) res[ls] = le;
            airedCache[tmdb] = (DateTime.UtcNow, res);
            return res;
        }
        catch { return null; }
    }

    // Просмотренные серии по данным Кинопаба (там отмечено то, что смотрели вне Lampa); кэш 10 минут
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, HashSet<(int, int)> seen)> kinoSeenCache = new();

    public static void KinoSeenForget(long kpId)
    {
        string pre = kpId + ":";
        foreach (var k in kinoSeenCache.Keys.Where(k => k.StartsWith(pre)).ToList()) kinoSeenCache.TryRemove(k, out _);
    }

    // Явные сбросы серий, которые остаются последним действием: вычитаются из «просмотрено», пока мост не снял отметку в Кинопабе
    public static async Task<Dictionary<long, HashSet<(int, int)>>> RecentResets()
    {
        var res = new Dictionary<long, HashSet<(int, int)>>();
        try
        {
            var dump = await TlV2.DumpAsync();
            foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
            {
                // действует, пока строка остаётся последним действием (новый просмотр заменит op); возраст доставки значения не имеет
                if ((string)r["op"] != "reset") continue;
                var m = System.Text.RegularExpressions.Regex.Match((string)r["id"] ?? "", @"^tv-(\d+)-s(\d+)e(\d+)$");
                if (!m.Success) continue;
                long id = long.Parse(m.Groups[1].Value);
                if (!res.TryGetValue(id, out var set)) res[id] = set = new HashSet<(int, int)>();
                set.Add((int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
            }
        }
        catch { }
        return res;
    }

    // Первый сезон, который зритель начал (досмотренный или с прогрессом): граница отслеживания, чтобы начатый, но не законченный сезон оставался долгом
    public static async Task<Dictionary<long, int>> StartedFirst()
    {
        var res = new Dictionary<long, int>();
        try
        {
            var dump = await TlV2.DumpAsync();
            foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
            {
                // сброшенная серия — тоже начатая: зритель к сезону приступал, значит сезон входит в диапазон отслеживания
                double pos = (double?)r["position"] ?? 0, pct = (double?)r["percent"] ?? 0;
                string op = (string)r["op"];
                if (op != "reset" && op != "completed" && pos <= 0 && pct <= 0) continue;
                var m = System.Text.RegularExpressions.Regex.Match((string)r["id"] ?? "", @"^tv-(\d+)-s(\d+)e(\d+)$");
                if (!m.Success) continue;
                long id = long.Parse(m.Groups[1].Value); int s = int.Parse(m.Groups[2].Value);
                if (!res.TryGetValue(id, out int cur) || s < cur) res[id] = s;
            }
        }
        catch { }
        return res;
    }

    public static HashSet<(int, int)> CachedKinoWatchedSet(long kpId, string tok)
    {
        if (Profiles.Enabled) return new HashSet<(int, int)>();
        if (kpId <= 0 || tok == null) return null;
        string ck = kpId + ":" + tok.GetHashCode();
        return kinoSeenCache.TryGetValue(ck, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromMinutes(10) ? hit.seen : null;
    }

    public static async Task<HashSet<(int, int)>> KinoWatchedSet(long kpId, string tok)
    {
        if (Profiles.Enabled) return new HashSet<(int, int)>();
        if (kpId <= 0 || tok == null) return null;
        string ck = kpId + ":" + tok.GetHashCode();
        if (kinoSeenCache.TryGetValue(ck, out var h) && DateTime.UtcNow - h.at < TimeSpan.FromMinutes(10)) return h.seen;
        try
        {
            var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/watching?id={kpId}", tok);
            if (code != 200) return null;
            var set = new HashSet<(int, int)>();
            foreach (var se in ((JArray)(JObject.Parse(body)["item"]?["seasons"] ?? new JArray())).Children<JObject>())
                foreach (var ep in ((JArray)(se["episodes"] ?? new JArray())).Children<JObject>())
                {
                    int st = (int?)ep["status"] ?? -1; double t = (double?)ep["time"] ?? 0, du = (double?)ep["duration"] ?? 0;
                    if (st == 1 || (du > 0 && IsDone(t, du, t / du * 100))) set.Add(((int?)se["number"] ?? 0, (int?)ep["number"] ?? 0));
                }
            kinoSeenCache[ck] = (DateTime.UtcNow, set);
            return set;
        }
        catch { return null; }
    }

    // Просмотренные серии по общей базе позиций: tmdb → множество (сезон, серия)
    public static async Task<Dictionary<long, HashSet<(int, int)>>> WatchedSets()
    {
        var res = new Dictionary<long, HashSet<(int, int)>>();
        try
        {
            var dump = Profiles.Enabled ? HomeDb.With(c=>TlV2.DumpFrom(c)) : await TlV2.DumpAsync();
            foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
            {
                if ((bool?)r["deleted"] == true) continue;
                var m = System.Text.RegularExpressions.Regex.Match((string)r["id"] ?? "", @"^tv-(\d+)-s(\d+)e(\d+)$");
                if (!m.Success) continue;
                if (!RowDone(r)) continue;
                long id = long.Parse(m.Groups[1].Value);
                if (!res.TryGetValue(id, out var set)) res[id] = set = new HashSet<(int, int)>();
                set.Add((int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
            }
        }
        catch { }
        return res;
    }

    // Есть ли у сериала вышедшие, но не просмотренные серии. Считаем от первого сезона, который зритель вообще начал (раньше не смотрел — не долг).
    // null — определить не удалось (тогда сериал не скрываем).
    public static bool? HasUnseenAired(Dictionary<int, int> aired, HashSet<(int, int)> seen, int? startedFirst = null, HashSet<(int, int)> resets = null)
    {
        // явно сброшенная вышедшая серия — непросмотренная независимо от положительного набора и границы сезонов
        if (aired != null && resets != null && resets.Any(x => aired.TryGetValue(x.Item1, out int n) && x.Item2 >= 1 && x.Item2 <= n)) return true;
        if (aired == null || aired.Count == 0 || seen == null || seen.Count == 0) return null;
        int first = seen.Min(x => x.Item1);
        if (startedFirst.HasValue && startedFirst.Value > 0 && startedFirst.Value < first) first = startedFirst.Value;
        foreach (var kv in aired)
        {
            if (kv.Key < first) continue;
            for (int e = 1; e <= kv.Value; e++) if (!seen.Contains((kv.Key, e))) return true;
        }
        return false;
    }

    // ---------- постоянное отслеживание сериала (не зависит от давности последнего просмотра) ----------
    static bool trackReady;
    static void TrackEnsure()
    {
        if (trackReady) return;
        HomeDb.With(c => HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS tracked (tmdb INTEGER PRIMARY KEY, at INTEGER)"));
        trackReady = true;
    }
    public static void TrackAdd(long tmdb, long at)
    {
        TrackEnsure();
        HomeDb.With(c => HomeDb.Exec(c, "INSERT INTO tracked(profile_id,tmdb,at) VALUES(@profile,@t,@a) ON CONFLICT(profile_id,tmdb) DO UPDATE SET at=MAX(at,@a)", ("@t", tmdb), ("@a", at)));
    }
    // сериал сейчас показывается в «Мои сериалы» благодаря отслеживанию (не убран вручную позже последнего просмотра)
    public static bool TrackedVisible(long tmdb)
    {
        var t = TrackList().Where(x => x.tmdb == tmdb).Select(x => (long?)x.at).FirstOrDefault();
        if (t == null) return false;
        var h = Hidden()["w_" + tmdb + "_tv"];
        return h == null || t.Value > (long)h;
    }

    public static List<(long tmdb, long at)> TrackList()
    {
        TrackEnsure();
        return HomeDb.With(c => HomeDb.Query(c, "SELECT tmdb, at FROM tracked WHERE profile_id=@profile ORDER BY at DESC").Select(r => (Convert.ToInt64(r[0]), Convert.ToInt64(r[1]))).ToList());
    }

    static readonly HashSet<string> BadLang = new() { "hi", "ta", "te", "ml", "kn", "bn", "pa", "tr", "mr", "ur", "gu" };

    // ключ сортировки новинок: позднейшая из дат — выхода или добавления на kino.pub
    static string SortKey(JObject c)
    {
        string a = DateOf(c), b = (string)c["kp_added"] ?? "";
        string key = string.CompareOrdinal(a, b) >= 0 ? a : b;
        string media = (string)c["media_type"] ?? ((c["name"] != null || c["original_name"] != null) ? "tv" : "movie");
        long? bump = ReleasePolicy.BumpAt(media, (long?)c["id"] ?? 0);
        if (bump != null)
        {
            string d = DateTimeOffset.FromUnixTimeMilliseconds(bump.Value).UtcDateTime.ToString("yyyy-MM-dd");
            if (string.CompareOrdinal(d, key) > 0) key = d;
        }
        return key;
    }

    static string DateOf(JObject c) => (string)c["release_date"] ?? (string)c["first_air_date"] ?? "";

    // Общие новинки: новые релизы TMDB (то, что показывает сама Lampa) + свежее kino.pub; совпавшие склеиваются.
    // Индийское и турецкое по языку оригинала не показываем.
    // Аниме: мультипликация (жанр 16) японского производства. anime: null — всё, "no" — без аниме
    public static bool IsAnime(JObject c) => CardKind.IsAnime(c);

    public static async Task<(JArray results, int total)> NewReleases(string media, int page, string tok, string anime = null, bool requireSource = false)
    {
        page = Math.Max(1, page);
        var today = DateTime.UtcNow.Date;
        string to = today.ToString("yyyy-MM-dd");
        // The same three-year window for film/series novelties in every profile.
        string from = NoveltyFrom;
        string tmdbPath = media == "tv"
            ? $"discover/tv?language=ru&sort_by=first_air_date.desc&first_air_date.gte={from}&first_air_date.lte={to}&vote_count.gte=5{(anime=="no"?"&without_genres=16":"")}&page={page}"
            : $"discover/movie?language=ru&sort_by=primary_release_date.desc&primary_release_date.gte={from}&primary_release_date.lte={to}&vote_count.gte=10&page={page}";

        var tmdbTask = Task.Run(async () =>
        {
            try { return await KpMap.TmdbRaw(tmdbPath); } catch { return null; }
        });

        var kinoTask = Task.Run(async () =>
        {
            var list = new List<JObject>(); int total = 1;
            if (tok == null) return (list, total);
            try
            {
                var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/fresh?type={(media == "tv" ? "serial" : "movie")}&perpage=20&page={page}", tok);
                if (code == 200)
                {
                    var d = JObject.Parse(body);
                    total = (int?)d["pagination"]?["total"] ?? 1;
                    var src = ((JArray)(d["items"] ?? new JArray())).Children<JObject>().ToList();
                    var cards = await KpMap.ToTmdbCards(src, tok);
                    for (int i = 0; i < cards.Length; i++)
                    {
                        var c = cards[i];
                        if (c == null)
                        {
                            // соответствия в TMDB нет: показываем карточку из данных самого Кинопаба (название, постер, рейтинг)
                            var it = src[i];
                            long kid = (long?)it["id"] ?? 0;
                            if (kid <= 0) continue;
                            string kt0 = (string)it["title"] ?? "";
                            var parts = kt0.Split(new[] { " / " }, StringSplitOptions.None);
                            var po = it["posters"] as JObject;
                            string tp = (string)it["type"];
                            bool tvk = tp == "serial" || tp == "docuserial" || tp == "tvshow";
                            c = new JObject
                            {
                                ["kp_legacy"] = true,
                                ["id"] = -kid,
                                ["kp_id"] = kid,
                                ["kp_type"] = tp,
                                ["media_type"] = tvk ? "tv" : "movie",
                                [tvk ? "name" : "title"] = parts[0].Trim(),
                                [tvk ? "original_name" : "original_title"] = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim(),
                                ["img"] = (string)po?["medium"] ?? (string)po?["big"] ?? (string)po?["small"] ?? "",
                                ["vote_average"] = (double?)it["kinopoisk_rating"] ?? (double?)it["imdb_rating"] ?? 0,
                                ["overview"] = (string)it["plot"] ?? "",
                                ["release_date"] = ((int?)it["year"] ?? 0) > 0 ? ((int)it["year"]).ToString() + "-01-01" : null,
                                ["genres_ru"] = new JArray(((it["genres"] as JArray) ?? new JArray()).Select(g => (string)g["title"]).Where(x => !string.IsNullOrEmpty(x))),
                                ["countries_ru"] = new JArray(((it["countries"] as JArray) ?? new JArray()).Select(g => (string)g["title"]).Where(x => !string.IsNullOrEmpty(x))),
                                ["source"] = "kinopub",
                                ["kp"] = it
                            };
                            long ca0 = (long?)it["created_at"] ?? 0;
                            if (ca0 > 0) c["kp_added"] = DateTimeOffset.FromUnixTimeSeconds(ca0).ToString("yyyy-MM-dd");
                            list.Add(c);
                            continue;
                        }
                        // дата добавления на kino.pub: старый фильм, который только что появился в каталоге, — тоже новинка
                        long ca = (long?)src[i]["created_at"] ?? 0;
                        if (ca > 0) c["kp_added"] = DateTimeOffset.FromUnixTimeSeconds(ca).ToString("yyyy-MM-dd");
                        // у свежих тайтлов в TMDB часто ещё нет русского названия — берём русское название из kino.pub («Русское / Original»)
                        string kt = (string)src[i]["title"] ?? "";
                        string ru = kt.Split(new[] { " / " }, StringSplitOptions.None)[0].Trim();
                        string key = c["title"] != null ? "title" : "name";
                        if (ru.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(ru, "[А-Яа-яЁё]") && !System.Text.RegularExpressions.Regex.IsMatch((string)c[key] ?? "", "[А-Яа-яЁё]"))
                            c[key] = ru;
                        // описание: у свежих тайтлов в TMDB оно пустое или английское — берём русское из kino.pub
                        string plot = (string)src[i]["plot"] ?? "";
                        if (plot.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch((string)c["overview"] ?? "", "[А-Яа-яЁё]"))
                            c["overview"] = plot;
                        list.Add(c);
                    }
                }
            }
            catch { }
            return (list, total);
        });

        var tmdb = await tmdbTask;
        var (kino, kinoTotal) = await kinoTask;
        // A failed source must not become a successful empty page and permanently exhaust the shared pool.
        if(requireSource && tmdb?["results"] is not JArray)
            throw new HttpRequestException("catalog_source_unavailable");

        var merged = new Dictionary<long, JObject>();
        foreach (var c in kino) merged[(long)c["id"]] = c;

        if (tmdb?["results"] is JArray arr)
        {
            foreach (var r in arr.Children<JObject>())
            {
                long id = (long)r["id"];
                if (merged.ContainsKey(id)) continue;
                if ((bool?)r["adult"] == true || BadLang.Contains((string)r["original_language"] ?? "")) continue;
                r["media_type"] = media;
                long kp = KpMap.CachedKp(media, id);
                if (kp > 0) r["kp_id"] = kp;
                merged[id] = r;
            }
        }

        // и среди «свежего» kino.pub тоже убираем индийское/турецкое
        var items = merged.Values.Where(c => !BadLang.Contains((string)c["original_language"] ?? "")&&string.CompareOrdinal(SortKey(c),from)>=0).ToList();
        if (anime == "no") items = items.Where(c => CardKind.Kind(c)=="series"&&
            !(c["genres_ru"] as JArray??new JArray()).Any(g=>System.Text.RegularExpressions.Regex.IsMatch((string)g??"","мульт|анимац|anime|animation",System.Text.RegularExpressions.RegexOptions.IgnoreCase))).ToList();
        items = items.OrderByDescending(SortKey, StringComparer.Ordinal).ToList();

        int tmdbPages = (int?)tmdb?["total_pages"] ?? 1;
        int total = Math.Max(1, Math.Min(500, Math.Max(kinoTotal, tmdbPages)));
        return (new JArray(items), total);
    }

    // Короткая страница «Лучших новинок» не прокручивается, и следующая так и не запрашивается.
    // Здесь копим уже отфильтрованные карточки и отдаём полные двадцатки; если в заданном окне их мало — окно расширяется, без повторов.
    sealed class TopPool
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<JObject> Items = new();
        public readonly HashSet<string> Seen = new();
        public int Next = 1, SourceTotal = 1, Span, Widened;
        public bool End;
        public DateTime Used = DateTime.UtcNow;
    }
    static readonly ConcurrentDictionary<string, TopPool> TopPools = new();

    public static async Task<(JArray results, int total)> TopNew(int page, int months, double minRate, string tok, bool sourcePage=false)
    {
        months=BestNoveltyMonths;
        page = Math.Max(1, page);
        // Public virtual pagination already packs and deduplicates the raw batches.
        // A second partial-page pager here skipped items and stalled at its 40-page cap.
        if(sourcePage){var raw=await TopSlice(page,months,minRate,tok);return(new JArray(raw.items),Math.Min(500,raw.totalPages));}
        foreach (var stale in TopPools.Where(x => DateTime.UtcNow - x.Value.Used > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToList()) TopPools.TryRemove(stale, out _);
        string key = Profiles.CurrentId + "|" + months + "|" + minRate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var pool = TopPools.GetOrAdd(key, _ => new TopPool { Span = months });
        await pool.Gate.WaitAsync();
        pool.Used = DateTime.UtcNow;
        try
        {
            // Одна порция источников на запрос. Следующую страницу «Смотреть все» догружает само, без ожидания полной двадцатки.
            if (pool.Items.Count < page * 20 && !pool.End && pool.Next <= 40)
            {
                var (batch, tp) = await TopSlice(pool.Next, pool.Span, minRate, tok);
                pool.SourceTotal = Math.Max(pool.Next, tp);
                foreach (var c in batch)
                {
                    string id = ((string)c["media_type"] ?? "movie") + ":" + c["id"];
                    if (pool.Seen.Add(id)) pool.Items.Add(c);
                }
                pool.Next++;
                pool.End = pool.Next > pool.SourceTotal;
            }
            var slice = pool.Items.Skip((page - 1) * 20).Take(20).ToList();
            bool more = pool.Items.Count > page * 20 || !pool.End;
            return (new JArray(slice), more ? page + 1 : Math.Max(1, page));
        }
        finally { pool.Gate.Release(); }
    }

    // Одна страница источников: TMDB (общий рейтинг и отдельно российское производство) и свежее kino.pub.
    static async Task<(List<JObject> items, int totalPages)> TopSlice(int page, int months, double minRate, string tok)
    {
        page = Math.Max(1, page);
        var today = DateTime.UtcNow.Date;
        string to = today.ToString("yyyy-MM-dd");
        string gte(string f) => months > 0 ? $"&{f}.gte={today.AddMonths(-months):yyyy-MM-dd}" : "";
        string rate = minRate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        string rateRu = Math.Max(6.5, minRate - 0.9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var queries = new List<(string media, string path)>
        {
            ("movie", $"discover/movie?language=ru&sort_by=primary_release_date.desc&vote_average.gte={rate}&vote_count.gte=100&primary_release_date.lte={to}{gte("primary_release_date")}&page={page}"),
            ("tv",    $"discover/tv?language=ru&sort_by=first_air_date.desc&vote_average.gte={rate}&vote_count.gte=100&first_air_date.lte={to}{gte("first_air_date")}&page={page}"),
            ("movie", $"discover/movie?language=ru&sort_by=primary_release_date.desc&with_original_language=ru&vote_average.gte={rateRu}&vote_count.gte=25&primary_release_date.lte={to}{gte("primary_release_date")}&page={page}"),
            ("tv",    $"discover/tv?language=ru&sort_by=first_air_date.desc&with_original_language=ru&vote_average.gte={rateRu}&vote_count.gte=25&first_air_date.lte={to}{gte("first_air_date")}&page={page}"),
        };
        var tmdbTasks = queries.Select(q => Task.Run(async () => { try { return (q.media, data: await KpMap.TmdbRaw(q.path)); } catch { return (q.media, data: (JObject)null); } })).ToList();

        // свежее kino.pub (последние добавленные): берём то, у чего рейтинг достаточный
        var kinoTask = Task.Run(async () =>
        {
            var list = new List<JObject>();
            if (tok == null) return list;
            foreach (var kind in new[] { "movie", "serial" })
            {
                try
                {
                    var (code, body) = await KpStore.Send(HttpMethod.Get, $"{KpStore.Api}/v1/items/fresh?type={kind}&perpage=20&page={page}", tok);
                    if (code != 200) continue;
                    foreach (var it in ((JArray)(JObject.Parse(body)["items"] ?? new JArray())).Children<JObject>())
                    {
                        double r = Math.Max((double?)it["kinopoisk_rating"] ?? 0, (double?)it["imdb_rating"] ?? 0);
                        if (r >= minRate) list.Add(it);
                    }
                }
                catch { }
            }
            return list;
        });

        await Task.WhenAll(tmdbTasks);
        var kinoItems = await kinoTask;
        if(tmdbTasks.Any(t=>t.Result.data?["results"] is not JArray))throw new HttpRequestException("catalog_source_unavailable");

        var merged = new Dictionary<string, JObject>();
        int tp = 1;
        foreach (var t in tmdbTasks.Select(x => x.Result))
        {
            if (t.data?["results"] is not JArray arr) continue;
            tp = Math.Max(tp, (int?)t.data["total_pages"] ?? 1);
            foreach (var r in arr.Children<JObject>())
            {
                if ((bool?)r["adult"] == true || BadLang.Contains((string)r["original_language"] ?? "")) continue;
                if (string.IsNullOrEmpty((string)r["poster_path"])) continue;
                r["media_type"] = t.media;
                long kp = KpMap.CachedKp(t.media, (long)r["id"]);
                if (kp > 0) r["kp_id"] = kp;
                merged[t.media + ":" + (long)r["id"]] = r;
            }
        }
        var cards = await KpMap.ToTmdbCards(kinoItems, tok);
        for (int i = 0; i < kinoItems.Count; i++)
        {
            var c = cards[i];
            if (c == null) continue;                                    // без соответствия в TMDB карточка получилась бы пустой — в этой ленте её нет
            string media = c["name"] != null && c["title"] == null ? "tv" : (string)c["media_type"] ?? (kinoItems[i]["type"]?.ToString() == "movie" ? "movie" : "tv");
            c["media_type"] = media;
            // показываем тот рейтинг, по которому тайтл прошёл отбор (Кинопоиск/IMDb из kino.pub), а не случайную оценку TMDB
            c["vote_average"] = Math.Max((double?)kinoItems[i]["kinopoisk_rating"] ?? 0, (double?)kinoItems[i]["imdb_rating"] ?? 0);
            long ca = (long?)kinoItems[i]["created_at"] ?? 0;
            if (ca > 0) c["kp_added"] = DateTimeOffset.FromUnixTimeSeconds(ca).ToString("yyyy-MM-dd");
            merged.TryAdd(media + ":" + (long)c["id"], c);
        }

        // русское название и озвучка — как в остальных лентах (фильмы и сериалы проверяются вместе, не друг за другом)
        var movies = new JArray(merged.Where(kv => kv.Key.StartsWith("movie:")).Select(kv => kv.Value));
        var tvs = new JArray(merged.Where(kv => kv.Key.StartsWith("tv:")).Select(kv => kv.Value));
        await Task.WhenAll(
            Availability.Filter(movies, "movie", tok, KpStore.DefaultProfile, 400),
            Availability.Filter(tvs, "tv", tok, KpStore.DefaultProfile, 400));

        string from=today.AddMonths(-BestNoveltyMonths).ToString("yyyy-MM-dd");
        var items = movies.Children<JObject>().Concat(tvs.Children<JObject>()).Where(c=>string.CompareOrdinal(SortKey(c),from)>=0)
            .OrderByDescending(SortKey, StringComparer.Ordinal).ToList();
        return (items, Math.Max(1, tp));
    }

    // Одна страница TMDB на один шаг прокрутки. query — без параметра page.
    static async Task<(JArray results, int total)> DiscoverPaged(string query, string media, int page)
    {
        page = Math.Max(1, page);
        JObject data = null;
        string dateField=media=="tv"?"first_air_date":"primary_release_date";
        data = await KpMap.TmdbRaw(query + "&"+dateField+".gte="+NoveltyFrom+"&page=" + Math.Min(page, 500));
        if(data?["results"] is not JArray)throw new HttpRequestException("catalog_source_unavailable");
        var items = new List<JObject>();
        int tp = Math.Max(1, (int?)data?["total_pages"] ?? 1);
        if (data?["results"] is JArray arr)
            foreach (var x in arr.Children<JObject>())
            {
                if ((bool?)x["adult"] == true) continue;
                x["media_type"] = media;
                long kp = KpMap.CachedKp(media, (long)x["id"]);
                if (kp > 0) x["kp_id"] = kp;
                items.Add(x);
            }
        return (new JArray(items), Math.Max(1, Math.Min(500, tp)));
    }

    // «Аниме»: японские аниме-сериалы, новые (по дате выхода) впереди и дальше без конца
    public static Task<(JArray results, int total)> AnimeList(int page)
    {
        string to = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        return DiscoverPaged($"discover/tv?language=ru&sort_by=first_air_date.desc&with_genres=16&with_original_language=ja&first_air_date.lte={to}&vote_count.gte=2", "tv", page);
    }

    // «Мультфильмы»: все полнометражные мультфильмы (отечественные и зарубежные вместе), сначала новинки, дальше по убыванию даты выхода.
    // Как «Аниме» — не только свежее; японское аниме сюда не попадает (у него своя лента), мелочь без голосов отсекает порог vote_count.
    public static async Task<(JArray results, int total)> CartoonList(int page)
    {
        string to = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var r = await DiscoverPaged($"discover/movie?language=ru&sort_by=primary_release_date.desc&with_genres=16&primary_release_date.lte={to}&vote_count.gte=8", "movie", page);
        var items = r.results.Children<JObject>()
            .Where(c => !IsAnime(c) && (bool?)c["adult"] != true && !BadLang.Contains((string)c["original_language"] ?? "")).ToList();
        return (new JArray(items), r.total);
    }

    public static async Task<(JArray results,int total)> CartoonSeriesList(int page)
    {
        string to=DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var result=await DiscoverPaged($"discover/tv?language=ru&sort_by=first_air_date.desc&with_genres=16&first_air_date.lte={to}&vote_count.gte=3","tv",page);
        return (new JArray(result.results.Children<JObject>().Where(c=>CardKind.Kind(c)=="cartoon_series"&&(bool?)c["adult"]!=true)),result.total);
    }

    // «Русские сериалы» / «Русские фильмы»: российское производство, сначала новинки, дальше по убыванию даты выхода.
    // Из сериалов убираем ток-шоу, новости и реалити (жанры 10767, 10763, 10764).
    public static Task<(JArray results, int total)> RussianList(string media, int page)
    {
        string to = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        string q = media == "tv"
            ? $"discover/tv?language=ru&sort_by=first_air_date.desc&with_original_language=ru&first_air_date.lte={to}&vote_count.gte=1&without_genres=10767,10763,10764"
            : $"discover/movie?language=ru&sort_by=primary_release_date.desc&with_original_language=ru&primary_release_date.lte={to}&vote_count.gte=2";
        return DiscoverPaged(q, media, page);
    }

    // Фильмы (tmdb id), по которым в общей базе позиций уже есть просмотр: начат (больше минуты) или досмотрен
    // Досмотренные фильмы (отметка не ниже 90%)
    public static async Task<HashSet<long>> MovieWatched()
    {
        var set = new HashSet<long>();
        try
        {
            var dump = await TlV2.DumpAsync();
            foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
            {
                if ((bool?)r["deleted"] == true) continue;
                string card = (string)r["card"] ?? "";
                if (!card.EndsWith("_movie")) continue;
                if (RowDone(r) && long.TryParse(card.Substring(0, card.Length - 6), out long id)) set.Add(id);
            }
        }
        catch { }
        return set;
    }

    public static async Task<HashSet<long>> MovieProgress()
    {
        var set = new HashSet<long>();
        try
        {
            var dump = await TlV2.DumpAsync();
            foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
            {
                if ((bool?)r["deleted"] == true) continue;
                string card = (string)r["card"] ?? "";
                if (!card.EndsWith("_movie")) continue;
                double pos = (double?)r["position"] ?? 0, pct = (double?)r["percent"] ?? 0;
                if ((pos > 60 || pct >= 2) && long.TryParse(card.Substring(0, card.Length - 6), out long id)) set.Add(id);
            }
        }
        catch { }
        return set;
    }

    // Библиотека Plex как штатные карточки Lampa: новые сверху; sectionKey — один раздел или null для всех
    public static async Task<(JArray results, int total)> PlexList(string sectionKey, int page, int perPage = 20)
    {
        await PlexClient.EnsureIndex();
        var all = PlexClient.All().Where(i => sectionKey == null || i.sectionKey == sectionKey).OrderByDescending(i => i.added).ToList();
        int total = Math.Max(1, (int)Math.Ceiling(all.Count / (double)perPage));
        var slice = all.Skip((Math.Max(1, page) - 1) * perPage).Take(perPage).ToList();

        var cards = await Task.WhenAll(slice.Select(async i =>
        {
            await Gate.WaitAsync();
            try
            {
                var c = await KpMap.TmdbCard(i.kind, i.tmdb);
                if (c == null) return null;
                c["local"] = "plex";
                long kp = KpMap.CachedKp(i.kind, i.tmdb);
                if (kp > 0) c["kp_id"] = kp;
                return c;
            }
            finally { Gate.Release(); }
        }));

        return (new JArray(cards.Where(c => c != null)), total);
    }

    public static JObject Hidden()
        => HomeDb.With(c =>
        {
            var o = new JObject();
            foreach (var r in HomeDb.Query(c, "SELECT k,at FROM hidden WHERE profile_id=@profile")) o[(string)r[0]] = Convert.ToInt64(r[1]);
            return o;
        });

    public static void Hide(string card)
        => HomeDb.With(c => HomeDb.Exec(c, "INSERT INTO hidden(profile_id,k,at) VALUES(@profile,@k,@a) ON CONFLICT(profile_id,k) DO UPDATE SET at=@a",
            ("@k", card), ("@a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));

    // снять отметку «скрыто» (когда тайтл снова добавляют в «Я смотрю» вручную)
    public static void Unhide(string card)
        => HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM hidden WHERE profile_id=@profile AND k=@k", ("@k", card)));

    // mode: continue | history
    public static async Task<(JArray results, int total)> List(string mode, int page, int perPage = 20, string onlyMedia = null)
    {
        var dump = await TlV2.DumpAsync();
        var byCard = new Dictionary<string, Entry>();

        foreach (var r in ((JArray)dump["rows"]).Children<JObject>())
        {
            if ((bool?)r["deleted"] == true) continue;
            string card = (string)r["card"];
            if (card == null) continue;
            var p = card.Split('_');
            if (p.Length != 2 || (p[1] != "movie" && p[1] != "tv") || !long.TryParse(p[0], out long tmdb) || tmdb <= 0) continue;

            long at = (long?)r["watched_at"] ?? 0;
            double pct = (double?)r["percent"] ?? 0;
            double pos = (double?)r["position"] ?? 0;
            if (pct <= 0 && pos <= 1) continue;

            if (!byCard.TryGetValue(card, out var e))
                byCard[card] = e = new Entry { Tmdb = tmdb, Media = p[1] };
            string ev = (string)r["event"];
            // при равном времени порядок обхода строк не должен решать исход: сравниваем пару (u,event)
            if (at > e.LastAt || (at == e.LastAt && TlV2.CmpPair(at, ev, e.LastAt, e.LastEv) >= 0)) { e.LastAt = at; e.LastPct = pct; e.LastPos = pos; e.LastDone = RowDone(r); e.LastEv = ev; }
        }

        IEnumerable<Entry> all = byCard.Values.OrderByDescending(e => e.LastAt);
        if (onlyMedia != null) all = all.Where(e => e.Media == onlyMedia);          // тип отбираем до пагинации, иначе первая страница может оказаться пустой

        if (mode == "continue")
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 45L * 24 * 3600 * 1000;
            // фильм — пока не досмотрен; сериал — если недавно смотрели (следующая серия ждёт)
            var hid = Hidden();
            all = all.Where(e => e.LastAt >= cutoff && (e.Media == "tv" || ((e.LastPos >= 30 || e.LastPct >= 1) && !e.LastDone)))
                     .Where(e => { var k = e.Tmdb + "_" + e.Media; return hid[k] == null || e.LastAt > (long)hid[k]; });
        }

        var list = all.ToList();
        int total = Math.Max(1, (int)Math.Ceiling(list.Count / (double)perPage));
        var slice = list.Skip((Math.Max(1, page) - 1) * perPage).Take(perPage).ToList();

        var cards = await Task.WhenAll(slice.Select(async e =>
        {
            await Gate.WaitAsync();
            try
            {
                var c = await KpMap.TmdbCard(e.Media, e.Tmdb);
                if (c == null) return null;
                long kp = KpMap.CachedKp(e.Media, e.Tmdb);
                if (kp > 0) c["kp_id"] = kp;
                c["watch_pct"] = e.LastPct;
                c["watch_at"] = e.LastAt;
                return c;
            }
            finally { Gate.Release(); }
        }));

        return (new JArray(cards.Where(c => c != null)), total);
    }
}
