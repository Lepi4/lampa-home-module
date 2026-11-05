using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Единые правила «есть ли русская аудиодорожка» и «какое качество не пускать в ленты».
// Используется Availability (ленты), KpSearch (поиск) и KpUnified (quality bump).
public static class ReleasePolicy
{
    static readonly Regex reSubs = new(@"\bСТ\b|\[СТ\]|\bSub\b|\bSUB\b|субтитр", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex reRu = new(@"Дубл|дубл|\bMVO\b|\bDVO\b|\bAVO\b|\bПМ\b|\bЛД\b|\bЛМ\b|\bАП\b|озвуч|Озвуч|Многоголос|Закадров|\bDUB\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public const int RuClassifierVersion = 2;
    static readonly string[] TypeRank = { "TS", "DVD", "HDRIP", "WEBRIP", "WEBDL", "BDRIP" };
    static readonly HashSet<string> Cam = new(StringComparer.OrdinalIgnoreCase)
        { "TS", "CAMRIP", "CAM", "HDCAM", "TC", "TELESYNC", "TELECINE", "SCREENER", "DVDSCR", "TSRIP" };

    public static JObject SettingsFor(string profileId) =>
        (JObject)(Profiles.Get(profileId)?["settings"] ?? new JObject());

    public static JObject Settings => SettingsFor(Profiles.CurrentId);

    public static bool AllowSubtitlesOnlyFor(string profileId) =>
        (bool?)SettingsFor(profileId)["allow_subtitles_only"] == true;

    public static bool AllowSubtitlesOnly => AllowSubtitlesOnlyFor(Profiles.CurrentId);

    // KinoPub — русскоязычный источник: его файлы считаются русской озвучкой,
    // даже если торрент-индекс для этой серии молчит или видит только СТ.
    public static bool TrustKinopubAudioFor(string profileId) =>
        (bool?)SettingsFor(profileId)["kinopub_ru_audio"] != false;

    public static bool TrustKinopubAudio => TrustKinopubAudioFor(Profiles.CurrentId);

    public static string NormType(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "";
        t = t.Trim().ToUpperInvariant();
        return Cam.Contains(t) ? "TS" : t;
    }

    public static bool AllowedType(string t) => Array.IndexOf(TypeRank, NormType(t)) >= 0;

    public static HashSet<string> BlockedFor(string profileId) =>
        new(((SettingsFor(profileId)["feed_block_quality"] as JArray) ?? new JArray())
            .Select(x => NormType((string)x))
            .Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> Blocked => BlockedFor(Profiles.CurrentId);

    static int Rank(string type)
    {
        type = NormType(type);
        for (int i = 0; i < TypeRank.Length; i++) if (TypeRank[i] == type) return i;
        return -1;
    }

    public static int TypeRankScore(string type) => Rank(NormType(type));

    public static bool BetterRelease(int typeRank, int tier, int seeders, int curTypeRank, int curTier, int curSeeders)
        => typeRank > curTypeRank || (typeRank == curTypeRank && (tier > curTier || (tier == curTier && seeders > curSeeders)));


    public static bool IsSubtitlesOnly(string title) => reSubs.IsMatch(title ?? "") && !reRu.IsMatch(title ?? "");

    public static bool HasRuAudio(JObject r)
    {
        string t = (string)r["Title"] ?? "";
        var voices = r["info"]?["voices"] as JArray;
        var langs = r["languages"] as JArray;
        bool rus = langs != null && langs.Any(x => (string)x == "rus");
        if (IsSubtitlesOnly(t)) return (voices != null && voices.Count > 0);
        return (voices != null && voices.Count > 0) || reRu.IsMatch(t) || rus;
    }

    // true => раздачу нужно скрыть, если профиль не разрешает релизы только с субтитрами
    public static bool Hide(JObject r, string profileId) =>
        IsSubtitlesOnly((string)r["Title"] ?? "") && !HasRuAudio(r) && !AllowSubtitlesOnlyFor(profileId);

    public static bool BlockedQuality(JObject r, string profileId)
    {
        string type = NormType(CardQuality.TypeLabel((string)r["Title"] ?? ""));
        return type.Length > 0 && BlockedFor(profileId).Contains(type);
    }

    public static void Track(string media, long tmdb, string title, string profileId)
    {
        if (tmdb <= 0) return;
        string type = NormType(CardQuality.TypeLabel(title));
        if (type.Length == 0) return;
        int tier = CardQuality.Tier(title);
        int rank = Rank(type);
        HomeDb.With(c =>
        {
            var row = HomeDb.Query(c,
                "SELECT type,tier FROM release_events WHERE profile_id=@p AND media=@m AND tmdb=@t",
                ("@p", profileId), ("@m", media), ("@t", tmdb)).FirstOrDefault();
            int oldTier = row?[1] is long l ? (int)l : -1;
            int oldRank = row?[0] is string s ? Rank(s) : -1;
            // Тип релиза старше разрешения: WEB-DL 720 не уступает TS 2160.
            if (row != null && !BetterRelease(rank, tier, 0, oldRank, oldTier, 0)) return;
            HomeDb.Exec(c, @"INSERT INTO release_events(profile_id,media,tmdb,type,tier,event_at)
                VALUES(@p,@m,@t,@ty,@ti,@at)
                ON CONFLICT(profile_id,media,tmdb) DO UPDATE SET type=@ty,tier=@ti,event_at=@at",
                ("@p", profileId), ("@m", media), ("@t", tmdb),
                ("@ty", type), ("@ti", tier), ("@at", HomeDb.Now()));
        });
    }

    public static long? BumpAt(string media, long tmdb)
    {
        var row = HomeDb.With(c => HomeDb.Query(c,
            "SELECT event_at FROM release_events WHERE profile_id=@p AND media=@m AND tmdb=@t",
            ("@p", Profiles.CurrentId), ("@m", media), ("@t", tmdb))).FirstOrDefault();
        return row?[0] is long l ? l : null;
    }

    static readonly HttpClient episodeHttp = new() { Timeout = TimeSpan.FromSeconds(7) };
    static readonly Regex reSE = new(@"(?:S(?<s>\d{1,2})\s*\.?\s*E(?<e>\d{1,3}))|(?:(?<s2>\d{1,2})\s*x\s*(?<e2>\d{1,3}))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (int s, int e) ParseEpisode(string title)
    {
        var m = reSE.Match(title ?? "");
        if (!m.Success) return (0, 0);
        int s = int.TryParse(m.Groups["s"].Value, out var a) ? a : int.TryParse(m.Groups["s2"].Value, out a) ? a : 0;
        int e = int.TryParse(m.Groups["e"].Value, out var b) ? b : int.TryParse(m.Groups["e2"].Value, out b) ? b : 0;
        return s > 0 && e > 0 ? (s, e) : (0, 0);
    }

    // null — источник не ответил / эпизод неизвестен (не скрываем); false — есть только СТ/Sub без русской озвучки.
    public static async Task<bool?> EpisodeHasRuAsync(string media, long tmdb, string title, int season, int episode)
    {
        if (tmdb <= 0 || season <= 0 || episode <= 0 || string.IsNullOrWhiteSpace(title)) return null;
        string profileId = Profiles.CurrentId;
        var cached = HomeDb.With(c => HomeDb.Query(c,
            "SELECT has_ru,checked_at FROM release_episodes WHERE profile_id=@p AND media=@m AND tmdb=@t AND season=@s AND episode=@e",
            ("@p", profileId), ("@m", media), ("@t", tmdb), ("@s", season), ("@e", episode))).FirstOrDefault();
        if (cached != null)
        {
            bool ru = Convert.ToInt64(cached[0]) != 0;
            long at = Convert.ToInt64(cached[1]);
            if (HomeDb.Now() - at < (ru ? 30L * 86400_000 : 6L * 3600_000)) return ru;
        }

        bool? result = null;
        foreach (var q in new[] { $"{title} S{season:D2}E{episode:D2}", $"{title} {season}x{episode:D2}" })
        {
            try
            {
                var arr = await JacredSearch.Aggregate("Query=" + Uri.EscapeDataString(q), applyHideFilter: true, profileId: profileId);
                result = false;
                foreach (var r in arr.Children<JObject>())
                {
                    if (!int.TryParse((string)r["Seeders"] ?? "0", out int sd) || sd <= 0) continue;
                    if (ParseEpisode((string)r["Title"] ?? "") != (season, episode)) continue;
                    if (Hide(r, profileId)) continue;
                    if (HasRuAudio(r)) { result = true; break; }
                }
                if (result == true) break;
            }
            catch { }
        }
        if (result == null) return null;

        HomeDb.With(c => HomeDb.Exec(c, @"INSERT INTO release_episodes(profile_id,media,tmdb,season,episode,has_ru,checked_at)
            VALUES(@p,@m,@t,@s,@e,@ru,@at)
            ON CONFLICT(profile_id,media,tmdb,season,episode) DO UPDATE SET has_ru=@ru,checked_at=@at",
            ("@p", profileId), ("@m", media), ("@t", tmdb), ("@s", season), ("@e", episode),
            ("@ru", result == true ? 1 : 0), ("@at", HomeDb.Now())));
        return result;
    }

    // S01E07, S01E07-E08, 01x07, 01x01-07, «1 сезон: 1-7 серии».
    public static List<(int s, int e)> ParseEpisodes(string title)
    {
        var list = new List<(int s, int e)>();
        string t = title ?? "";
        foreach (Match m in Regex.Matches(t, @"S(?<s>\d{1,2})\s*\.?\s*E(?<e>\d{1,3})(?:\s*[-–]\s*E?(?<e2>\d{1,3}))?", RegexOptions.IgnoreCase))
        {
            int s = int.Parse(m.Groups["s"].Value), a = int.Parse(m.Groups["e"].Value);
            int b = m.Groups["e2"].Success ? int.Parse(m.Groups["e2"].Value) : a;
            for (int e = a; e <= b; e++) list.Add((s, e));
        }
        foreach (Match m in Regex.Matches(t, @"(?<!\d)(?<s>\d{1,2})\s*x\s*(?<e>\d{1,3})(?:\s*[-–]\s*(?<e2>\d{1,3}))?", RegexOptions.IgnoreCase))
        {
            int s = int.Parse(m.Groups["s"].Value), a = int.Parse(m.Groups["e"].Value);
            int b = m.Groups["e2"].Success ? int.Parse(m.Groups["e2"].Value) : a;
            for (int e = a; e <= b; e++) list.Add((s, e));
        }
        var range = Regex.Match(t, @"(?<s>\d{1,2})\s*сезон[^0-9]*(?<a>\d{1,3})\s*[-–]\s*(?<b>\d{1,3})\s*сери", RegexOptions.IgnoreCase);
        if (range.Success)
        {
            int s = int.Parse(range.Groups["s"].Value), a = int.Parse(range.Groups["a"].Value), b = int.Parse(range.Groups["b"].Value);
            for (int e = a; e <= b; e++) list.Add((s, e));
        }
        return list.Distinct().ToList();
    }

    // Горячий путь: только чтение индекса. null — ещё не индексировано (fail-open).
    public static bool? EpisodeRu(string media, long tmdb, int season, int episode)
    {
        var row = HomeDb.With(c => HomeDb.Query(c,
            "SELECT has_ru,checked_at FROM release_episodes WHERE profile_id=@p AND media=@m AND tmdb=@t AND season=@s AND episode=@e",
            ("@p", Profiles.CurrentId), ("@m", media), ("@t", tmdb), ("@s", season), ("@e", episode))).FirstOrDefault();
        // checked_at=0 — классификатор ещё не проверял серию; это unknown, а не «нет русской озвучки».
        if (row == null || Convert.ToInt64(row[1]) <= 0) return null;
        return Convert.ToInt64(row[0]) != 0;
    }
}

// Сеть только в фоне. Горячий путь (ProfileAvailability/LastPlay) читает release_episodes.
public static class ReleaseIndexer
{
    static readonly SemaphoreSlim gate = new(2, 2);
    static readonly ConcurrentDictionary<string, Task> pending = new();

    static void EnsureClassifierVersion()
    {
        int v = int.TryParse(HomeDb.Get("release_classifier_ver"), out var x) ? x : 0;
        if (v >= ReleasePolicy.RuClassifierVersion) return;
        // checked_at=0 снимает кэш EpisodeHasRu (ложный has_ru=0 ещё 6 часов).
        HomeDb.With(c => HomeDb.Exec(c, "UPDATE release_episodes SET has_ru=0, checked_at=0"));
        HomeDb.Set("release_classifier_ver", ReleasePolicy.RuClassifierVersion.ToString());
    }

    public static Task Ensure(string media, long tmdb, string title)
    {
        if (tmdb <= 0 || string.IsNullOrWhiteSpace(title)) return Task.CompletedTask;
        string profileId = Profiles.CurrentId;
        string key = profileId + "|" + media + "|" + tmdb;
        return pending.GetOrAdd(key, k => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try { await Refresh(media, tmdb, title, profileId); }
            catch { }
            finally { gate.Release(); pending.TryRemove(key, out _); }
        }));
    }

    static async Task Refresh(string media, long tmdb, string title, string profileId)
    {
        EnsureClassifierVersion();
        // has_ru считается по аудио, поэтому субтитровые раздачи остаются в выборке.
        var results = await JacredSearch.Aggregate("Query=" + Uri.EscapeDataString(title), applyHideFilter: false, profileId: profileId);
        if (results.Count == 0) return;

        var episodes = new Dictionary<(int s, int e), (bool ru, int tier, string type, int sd, int typeRank)>();
        foreach (var r in results.Children<JObject>())
        {
            if (!int.TryParse((string)r["Seeders"] ?? "0", out int sd) || sd <= 0) continue;
            string t = (string)r["Title"] ?? "";
            bool ru = ReleasePolicy.HasRuAudio(r);
            int tier = CardQuality.Tier(t);
            string type = ReleasePolicy.NormType(CardQuality.TypeLabel(t));
            int typeRank = ReleasePolicy.TypeRankScore(type);
            foreach (var (s, e) in ReleasePolicy.ParseEpisodes(t))
            {
                var key = (s, e);
                if (!episodes.TryGetValue(key, out var cur))
                    episodes[key] = (ru, tier, type, sd, typeRank);
                else
                {
                    bool ru2 = cur.ru || ru;
                    if (ReleasePolicy.BetterRelease(typeRank, tier, sd, cur.typeRank, cur.tier, cur.sd))
                        episodes[key] = (ru2, tier, type, sd, typeRank);
                    else
                        episodes[key] = (ru2, cur.tier, cur.type, cur.sd, cur.typeRank);
                }
            }
        }
        foreach (var kv in episodes)
        {
            var (s, e) = kv.Key;
            var v = kv.Value;
            int tier = v.tier, sd = v.sd, typeRank = v.typeRank;
            string type = v.type;
            HomeDb.With(c =>
            {
                var row = HomeDb.Query(c,
                    "SELECT best_tier,best_type,seeders FROM release_episodes WHERE profile_id=@p AND media=@m AND tmdb=@t AND season=@s AND episode=@e",
                    ("@p", profileId), ("@m", media), ("@t", tmdb), ("@s", s), ("@e", e)).FirstOrDefault();
                if (row != null)
                {
                    int oldTier = Convert.ToInt32(row[0]);
                    string oldType = (string)row[1] ?? "";
                    int oldSd = Convert.ToInt32(row[2]);
                    int oldRank = ReleasePolicy.TypeRankScore(oldType);
                    if (!ReleasePolicy.BetterRelease(typeRank, tier, sd, oldRank, oldTier, oldSd))
                    {
                        tier = oldTier;
                        type = oldType;
                    }
                    if (oldSd > sd) sd = oldSd;
                }
                HomeDb.Exec(c, @"
                    INSERT INTO release_episodes(profile_id,media,tmdb,season,episode,has_ru,best_tier,best_type,seeders,checked_at)
                    VALUES(@p,@m,@t,@s,@e,@ru,@tier,@type,@sd,@at)
                    ON CONFLICT(profile_id,media,tmdb,season,episode) DO UPDATE SET
                      has_ru=MAX(release_episodes.has_ru,@ru),
                      best_tier=@tier,
                      best_type=@type,
                      seeders=@sd,
                      checked_at=@at",
                    ("@p", profileId), ("@m", media), ("@t", tmdb),
                    ("@s", s), ("@e", e), ("@ru", v.ru ? 1 : 0), ("@tier", tier),
                    ("@type", type), ("@sd", sd), ("@at", HomeDb.Now()));
            });
        }
    }
}
