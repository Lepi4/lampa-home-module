using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HomeMedia;

// Расширенная таблица просмотров (этап C плана слияния): timeline получает необязательные id / event / session / op.
// Ключ строки прежний — h (хеш файла Lampa); старый доступ и старый клиент работают как раньше.
//   u  — время действия (мс, исходное: при повторе и переносе не меняется);  s — серверная ревизия (только для выдачи изменений).
//   event — стабильный идентификатор действия «сеанс:номер»;  op — progress | completed | reset (отдельно от event).
// Порядок конфликтов — пара (u, event): большая принимается; та же пара с тем же содержимым — повтор (duplicate); меньшая — устарело (stale);
// та же пара с другим содержимым — ошибка контракта (invalid). Сравнение «event != последний» не используется: A→B→повтор A откатывало бы позицию.
// Этот класс работает с переданным соединением — его проверяют на копиях и в памяти, не трогая рабочую базу.
public static class TlV2
{
    public const int SchemaVersion = 2;
    static readonly System.Net.Http.HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly string journalFile = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "database", "tl_journal.jsonl");
    static readonly object jl = new();

    // Режим: «legacy» — читатели берут штатный дамп TimeCode с наложением timeline; «unified» — единственный источник timeline (после переноса истории).
    public static bool Unified => HomeDb.Get("tl_mode") == "unified";
    // Поколение синхронизации: клиент с другим поколением получает полный снимок (курсоры s прежних последовательностей не сравниваются).
    public static string Epoch => (HomeDb.Get("tl_epoch") ?? "1") + ":order2" + (Profiles.Enabled ? ":" + Profiles.CurrentId : "");
    static readonly Regex hashRx = new("^-?[0-9]{1,15}$");
    // Отметка «досмотрено» иногда приходит только с identity (tv-252105-s1e1) и пустой карточкой. Без карточки дамп её выбрасывал, и сериал оставался в «Моих».
    public static string CardFromIdentity(string id)
    {
        var tv = Regex.Match(id ?? "", @"^tv-(\d+)-s\d+e\d+$");
        if (tv.Success) return tv.Groups[1].Value + "_tv";
        var movie = Regex.Match(id ?? "", @"^movie-(\d+)$");
        if (movie.Success) return movie.Groups[1].Value + "_movie";
        var vk = Regex.Match(id ?? "", @"^vk--?[0-9]{1,15}_[0-9]{1,15}$");
        if (vk.Success) return "vk_" + id.Substring(3);
        return "";
    }
    static readonly Regex cardRx = new("^([0-9]{1,12}_(tv|movie)|yt_[0-9A-Za-z_-]{11}|wr_[0-9A-Za-z_-]{1,80}|vk_-?[0-9]{1,15}_[0-9]{1,15})$");
    static readonly Regex idRx = new("^(movie-[0-9]{1,12}|tv-[0-9]{1,12}-s[0-9]{1,3}e[0-9]{1,4}|yt-[0-9A-Za-z_-]{11}|tl-[0-9A-Za-z_-]{1,20}|vk--?[0-9]{1,15}_[0-9]{1,15})$");
    static readonly Regex evRx = new("^[0-9A-Za-z_.:-]{1,60}$");
    static readonly HashSet<string> ops = new() { "progress", "completed", "reset" };

    public class Row
    {
        public string H, C = "", Id, Event, Session, Op;
        public long T, D, U, S;
        public double P;
        public bool ResumeIgnored;  // resume прислан, но не принят (слишком большой или неверной структуры): прогресс принят, клиент узнаёт об этом из ответа
        public JObject Resume;      // сведения для «Продолжить» (источник, серия, озвучка…): записываются в lastplay в той же транзакции; старые клиенты их не присылают
    }

    public const long ClockSkewMs = 120_000;

    public static readonly string[] NewColumns = { "id", "event", "session", "op" };

    // ---------- схема ----------
    // Добавляет недостающие необязательные колонки; повторный вызов ничего не меняет. Возвращает список реально добавленного.
    public static List<string> EnsureSchema(SqliteConnection c)
    {
        var added = new List<string>();
        var have = new HashSet<string>(HomeDb.Query(c, "PRAGMA table_info(timeline)").Select(r => (string)r[1]), StringComparer.OrdinalIgnoreCase);
        foreach (var col in NewColumns)
            if (!have.Contains(col)) { HomeDb.Exec(c, "ALTER TABLE timeline ADD COLUMN " + col + " TEXT"); added.Add(col); }
        HomeDb.Exec(c, "CREATE INDEX IF NOT EXISTS ix_timeline_id ON timeline(id)");
        HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT)");
        // журнал подтверждённых действий (окно отката): пишется В ТОЙ ЖЕ транзакции, что и просмотр; в файл выгружается повторяемым обработчиком
        HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS tl_journal (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER, h TEXT, j TEXT, exported INTEGER DEFAULT 0)");
        HomeDb.Exec(c, "CREATE INDEX IF NOT EXISTS ix_tl_journal_exp ON tl_journal(exported, id)");          // выгрузка читает только невыгруженное, без просмотра всей истории
        HomeDb.Exec(c, "INSERT INTO kv(k,v) VALUES('tl_schema',@v) ON CONFLICT(k) DO UPDATE SET v=@v", ("@v", SchemaVersion.ToString()));
        return added;
    }

    const string Cols = "h,c,t,d,p,u,s,id,event,session,op";

    static Row Read(object[] r) => new Row
    {
        H = (string)r[0], C = r[1] as string ?? "", T = Convert.ToInt64(r[2] is DBNull ? 0L : r[2]), D = Convert.ToInt64(r[3] is DBNull ? 0L : r[3]),
        P = Convert.ToDouble(r[4] is DBNull ? 0.0 : r[4]), U = Convert.ToInt64(r[5] is DBNull ? 0L : r[5]), S = Convert.ToInt64(r[6] is DBNull ? 0L : r[6]),
        Id = r[7] as string, Event = r[8] as string, Session = r[9] as string, Op = r[10] as string
    };

    public static Row Get(SqliteConnection c, string h)
    {
        var q = HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile AND h=@h", ("@h", h));
        if (q.Count == 0 && Profiles.Enabled)
            q = HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile AND id IN (SELECT id FROM progress_aliases WHERE profile_id=@profile AND hash=@h) LIMIT 1", ("@h", h));
        return q.Count == 0 ? null : Read(q[0]);
    }

    public static Row Resolve(SqliteConnection c, string id, string hash, string card)
    {
        var q = HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile AND id=@id LIMIT 1", ("@id", id));
        var r = q.Count == 0 ? Get(c, hash) : Read(q[0]);
        if (r == null || (r.Id != null && r.Id != id)) return null;
        return r.C == card || (r.C == "" && CardFromIdentity(r.Id) == card) ? r : null;
    }

    public static List<Row> All(SqliteConnection c) => HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile").Select(Read).ToList();

    public static long MaxS(SqliteConnection c) => Convert.ToInt64(HomeDb.Query(c, "SELECT COALESCE(MAX(s),0) FROM timeline WHERE profile_id=@profile")[0][0]);

    // ---------- порядок событий ----------
    // event = «сеанс:номер»: сеанс — время старта плеера (мс), номер растёт внутри сеанса. Пустой event (старые клиенты) — наименьший.
    // Единый строгий порядок: сначала пустой, затем «сеанс:номер» (числа), затем все прочие строки по ordinal. Смешанные пары не образуют цикла.
    static int Rank(string e, out long s, out long q)
    {
        s = q = 0;
        if (e == "") return 0;
        return TryParts(e, out s, out q) ? 1 : 2;
    }

    public static int CmpEvent(string a, string b)
    {
        a ??= ""; b ??= "";
        if (a == b) return 0;
        int ra = Rank(a, out long sa, out long qa), rb = Rank(b, out long sb, out long qb);
        if (ra != rb) return ra.CompareTo(rb);
        if (ra == 1)
        {
            int c = sa != sb ? sa.CompareTo(sb) : qa.CompareTo(qb);
            return c != 0 ? c : string.CompareOrdinal(a, b);          // «2:1» и «02:01» — равные числа, но разные события: порядок задаёт ordinal
        }
        return string.CompareOrdinal(a, b);
    }

    static bool TryParts(string e, out long s, out long q)
    {
        s = q = 0;
        if (string.IsNullOrEmpty(e)) return false;
        int i = e.LastIndexOf(':');
        return i > 0 && long.TryParse(e.Substring(0, i), out s) && long.TryParse(e.Substring(i + 1), out q);
    }

    public static int CmpPair(long u1, string e1, long u2, string e2)
    {
        // One transitive order for all actors and all delivery permutations.
        return u1 != u2 ? u1.CompareTo(u2) : CmpEvent(e1, e2);
    }

    // ---------- проверка и применение события ----------
    public class Result
    {
        public string H, Event, Status, Reason;     // accepted | duplicate | stale | invalid | retryable
        public long S;
        public bool Enriched, ResumeIgnored;
        public JObject ToJson() => new JObject { ["h"] = H, ["event"] = Event, ["status"] = Status, ["reason"] = Reason, ["s"] = S, ["enriched"] = Enriched ? true : (bool?)null, ["resume_ignored"] = ResumeIgnored ? true : (bool?)null };
    }

    static bool SamePayload(Row cur, Row n)
        => cur.T == n.T && cur.D == n.D && Math.Abs(cur.P - n.P) < 0.05 && (cur.Op ?? "") == (n.Op ?? "");

    // Разбор входящего события из любого из форматов: свой ({h,c,t,d,p,u,event,session,op,id}) и штатный TimeCode ({hash,card,position,duration,percent,watched_at,...}).
    // Значение события приводится к числу только после проверки типа: «u:"abc"», объект вместо t, переполнение — это invalid именно этого события (конкретная причина),
    // а не исключение, которое откатило бы всю пачку. Ошибки базы данных сюда не попадают: они остаются отказом транзакции.
    static bool Num(JToken t, out double v)
    {
        v = 0;
        if (t == null || t.Type == JTokenType.Null) return true;
        if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) { try { v = (double)t; } catch { return false; } return !double.IsNaN(v) && !double.IsInfinity(v); }
        if (t.Type == JTokenType.String) return double.TryParse((string)t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        return false;
    }

    static string Str(JToken t, out bool ok)
    {
        ok = true;
        if (t == null || t.Type == JTokenType.Null) return null;
        if (t.Type == JTokenType.String || t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (string)t;
        ok = false; return null;
    }

    public static string SafeStr(JObject x, string k) { try { var t = x[k]; return t != null && t.Type == JTokenType.String ? (string)t : t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? t.ToString() : null; } catch { return null; } }

    public static Row Parse(JObject x, out string error)
    {
        try { return ParseCore(x, out error); }
        catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException || ex is ArgumentException) { error = "bad_structure"; return null; }
    }

    static Row ParseCore(JObject x, out string error)
    {
        error = null;
        bool ok;
        string h = Str(x["h"], out ok); if (!ok) { error = "bad_hash"; return null; }
        h ??= Str(x["hash"], out ok); if (!ok) { error = "bad_hash"; return null; }
        if (string.IsNullOrEmpty(h) || !hashRx.IsMatch(h) || h == "0") { error = "bad_hash"; return null; }
        var ut = x["u"] ?? x["watched_at"];
        if (!Num(ut, out double ud)) { error = "bad_time_type"; return null; }
        if (ud <= 0) { error = "no_time"; return null; }
        if (ud > 4102444800000d) { error = "bad_time_range"; return null; }
        // время «из будущего» (сбой часов устройства) выиграло бы у всех нормальных действий с других устройств: пакет не принимаем и не выбрасываем — он ждёт (retryable) и принимается, когда время наступит
        if (ud > HomeDb.Now() + ClockSkewMs) { error = "clock_ahead"; return null; }
        long u = (long)ud;
        string card = Str(x["c"], out ok) ?? Str(x["card"], out ok) ?? ""; if (!ok) { error = "bad_card"; return null; }
        if (card != "" && !cardRx.IsMatch(card)) card = "";
        string id = Str(x["id"], out ok); if (!ok) { error = "bad_id"; return null; }
        if (id != null && !idRx.IsMatch(id)) id = null;
        if (card == "") card = CardFromIdentity(id);
        string ev = Str(x["event"], out ok); if (!ok) { error = "bad_event"; return null; }
        if (ev != null && !evRx.IsMatch(ev)) { error = "bad_event"; return null; }
        // «сеанс:номер» пишется только канонически (без ведущих нулей): «2:1» и «02:01» не должны быть двумя разными событиями с равным порядком
        if (ev != null && TryParts(ev, out long es, out long eq) && ev != es + ":" + eq) { error = "bad_event_format"; return null; }
        string op = Str(x["op"], out ok); if (!ok) { error = "bad_op"; return null; }
        if (op != null && !ops.Contains(op)) { error = "bad_op"; return null; }
        string session = Str(x["session"], out ok); if (!ok) { error = "bad_session"; return null; }
        if (session != null && !evRx.IsMatch(session)) session = null;
        if (!Num(x["d"] ?? x["duration"], out double dur)) { error = "bad_duration_type"; return null; }
        if (!Num(x["t"] ?? x["position"], out double pos)) { error = "bad_position_type"; return null; }
        if (!Num(x["p"] ?? x["percent"], out double pct)) { error = "bad_percent_type"; return null; }
        if (dur < 0 || dur > 1e9 || pos < 0 || pos > 1e9) { error = "bad_position_range"; return null; }
        var r = new Row
        {
            H = h, C = card, Id = id, Event = ev, Session = session, Op = op, U = u,
            T = Math.Max(0, (long)Math.Round(pos)), D = Math.Max(0, (long)Math.Round(dur)), P = Math.Clamp(pct, 0, 100)
        };
        if (op == "reset") { r.T = 0; r.P = 0; }
        else if (op == "completed") r.P = 100;
        else if (dur <= 0 && r.P > 0) r.P = 0;   // без длительности percent не хранится: duration=0 + percent=100 не должно считаться досмотром
        else if (r.D <= 0) r.P = 0;
        // resume — необязательное поле: лишнее/слишком большое не делает событие недействительным, просто не используется
        if (x["resume"] != null && x["resume"].Type != JTokenType.Null && op != "reset")
        {
            if (x["resume"] is JObject rs && card != "" && ResumeOk(rs)) r.Resume = rs; else r.ResumeIgnored = true;
        }
        return r;
    }

    public const int ResumeMaxChars = 32_000;
    public const int ResumeUrlMaxChars = 2048;

    // Структура resume: источник — строка, список серий (pl) ограничен по числу и длине полей, индекс попадает в список, общий размер в пределе
    static bool ResumeOk(JObject rs)
    {
        if (rs["src"]?.Type != JTokenType.String) return false;
        if (rs.ToString(Newtonsoft.Json.Formatting.None).Length > ResumeMaxChars) return false;
        if (rs["pl"] != null && rs["pl"].Type != JTokenType.Null)
        {
            if (rs["pl"] is not JArray pl || pl.Count > 80) return false;
            foreach (var e in pl)
            {
                if (e is not JObject o) return false;
                if (((string)o["u"] ?? "").Length > ResumeUrlMaxChars || ((string)o["t"] ?? "").Length > 100 || ((string)o["h"] ?? "").Length > 60) return false;
            }
            var ix = rs["idx"];
            if (ix != null && ix.Type != JTokenType.Null && (ix.Type != JTokenType.Integer || (long)ix < 0 || (long)ix >= Math.Max(1, pl.Count))) return false;
        }
        return true;
    }

    // Действия при принятом событии ВНУТРИ транзакции просмотра: явный сброс снимает «Продолжить» (в том числе недосмотренную, если она не новее сброса),
    // в режиме unified событие попадает в таблицу журнала. Ошибка любого действия откатывает всю пачку: подтверждение (accepted) означает, что всё выполнено.
    public static void InTx(SqliteConnection c, Row n)
    {
        if (n.Op == "reset") LastPlayStore.ResetAccepted(n);
        // реальный просмотр сериала сам создаёт постоянное отслеживание (раньше оно появлялось только при построении списка «Мои сериалы»);
        // ручное скрытие учитывается при показе: оно сравнивается с датой последнего просмотра
        if (n.Resume != null && n.Op != "reset") LastPlayStore.FromResume(n);
        HistoryStore.Record(c,n);
        if (n.Op != "reset" && (n.T > 0 || n.P > 0) && n.C != null && n.C.EndsWith("_tv") && !(n.Event ?? "").StartsWith("9:") && !(n.Event ?? "").StartsWith("manual:") && long.TryParse(n.C.Substring(0, n.C.Length - 3), out long tm) && tm > 0)
        {
            HomeDb.Exec(c, "CREATE TABLE IF NOT EXISTS tracked (tmdb INTEGER PRIMARY KEY, at INTEGER)");
            HomeDb.Exec(c, "INSERT INTO tracked(profile_id,tmdb,at) VALUES(@profile,@t,@a) ON CONFLICT(profile_id,tmdb) DO UPDATE SET at=MAX(at,@a)", ("@t", tm), ("@a", n.U));
        }
        if (Unified)
            HomeDb.Exec(c, "INSERT INTO tl_journal(profile_id,at,h,j) VALUES(@profile,@a,@h,@j)", ("@a", HomeDb.Now()), ("@h", n.H), ("@j", ToWire(n).ToString(Newtonsoft.Json.Formatting.None)));
    }

    // Выгрузка журнала из БД в файл (для чтения человеком и обратного адаптера). Повторяема: выгруженное помечается только после успешной записи файла.
    public static int FlushJournal()
    {
        if (HomeDb.SandboxId != null) return 0;            // журнал рабочей системы: из песочницы в файл ничего не пишется
        try
        {
            // замок базы держим только на чтение и на пометку; файловая запись идёт вне его (не тормозит сохранение прогресса)
            var rows = HomeDb.With(c => HomeDb.Query(c, "SELECT id,j FROM tl_journal WHERE profile_id=@profile AND exported=0 ORDER BY id LIMIT 2000"));
            if (rows.Count == 0) return 0;
            lock (jl)
            {
                var fi = new System.IO.FileInfo(journalFile);
                if (fi.Exists && fi.Length > 16L * 1024 * 1024) System.IO.File.Move(journalFile, journalFile + "." + HomeDb.Now(), true);
                System.IO.File.AppendAllText(journalFile, string.Concat(rows.Select(r => {var entry=JObject.Parse((string)r[1]);entry["user_profile_id"]=Profiles.CurrentId;return entry.ToString(Newtonsoft.Json.Formatting.None)+"\n";})));
            }
            long mx = Convert.ToInt64(rows[rows.Count - 1][0]);
            HomeDb.With(c => HomeDb.Exec(c, "UPDATE tl_journal SET exported=1 WHERE profile_id=@profile AND id<=@m AND exported=0", ("@m", mx)));
            return rows.Count;
        }
        catch { try { HomeDb.Set("tl_journal_err", HomeDb.Now().ToString()); } catch { } return -1; }
    }

    // Ограниченное хранение: выгруженные записи старше окна отката (по умолчанию 90 суток) удаляются; невыгруженные не удаляются никогда.
    public static int PurgeJournal(int days = 90)
    {
        if (HomeDb.SandboxId != null) return 0;
        // Журнал отката глобальный: чистим выгруженные строки всех профилей, а не только текущего.
        // Фоновый из Ops.Tick вызов идёт вне request-scope, где @profile = u_default, и строки
        // не-основных профилей иначе никогда не удалялись.
        try { return HomeDb.With(c => HomeDb.Exec(c, "DELETE FROM tl_journal WHERE exported=1 AND at<@t", ("@t", HomeDb.Now() - days * 86400000L))); } catch { return -1; }
    }

    // Приём тела запроса в любом из форматов: массив строк, {rows:[...]} или одна строка.
    public static (List<Result> results, long rev) Submit(JToken body)
    {
        JArray arr = body is JArray a ? a : (body is JObject o ? (o["rows"] as JArray ?? new JArray(o)) : new JArray());
        var res = HomeDb.With(c => ApplyBatch(c, arr, InTx));
        FlushJournal();
        return res;
    }

    // Дамп в формате штатного TimeCode для читателей (мост, «Продолжить», «Мои сериалы»): режим legacy — штатный дамп + наложение timeline, unified — только timeline.
    public static async System.Threading.Tasks.Task<JObject> DumpAsync()
    {
        if (!Profiles.Enabled && !Unified) return TlStore.Overlay(JObject.Parse(await http.GetStringAsync("http://127.0.0.1:9118/timecode/dump")));
        return HomeDb.With(c => DumpFrom(c));
    }

    // Те же строки, но из любого соединения (проверки на копиях)
    public static JObject DumpFrom(SqliteConnection c)
    {
        var rows = new JArray();
        foreach (var r in All(c))
        {
            string card = string.IsNullOrEmpty(r.C) ? CardFromIdentity(r.Id) : r.C;
            if (string.IsNullOrEmpty(card)) continue;
            rows.Add(new JObject { ["id"] = r.Id ?? "home-" + r.H, ["hash"] = r.H, ["card"] = card, ["position"] = r.T, ["duration"] = r.D, ["percent"] = r.P,
                                   ["profile"] = 0, ["deleted"] = r.Op == "reset" && r.T == 0 && r.P == 0, ["watched_at"] = r.U, ["updated_at"] = r.U, ["s"] = r.S, ["op"] = r.Op, ["event"] = r.Event });
        }
        return new JObject { ["rows"] = rows };
    }

    // Применяет одно событие в рамках уже открытой транзакции.
    public static Result Apply(SqliteConnection c, Row n, ref long maxS, Action<Row> onAccepted = null)
    {
        var res = new Result { H = n.H, Event = n.Event, ResumeIgnored = n.ResumeIgnored };
        var cur = Get(c, n.H);
        if (Profiles.Enabled && n.Id != null)
        {
            var same = HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile AND id=@id LIMIT 1", ("@id", n.Id));
            if (same.Count > 0) cur = Read(same[0]);
            else if (cur?.Id != null && cur.Id != n.Id)
            { res.Status = "invalid"; res.Reason = "identity_conflict"; return res; }
        }
        if (Profiles.Enabled && cur != null && cur.H != n.H)
        {
            HomeDb.Exec(c, "INSERT INTO progress_aliases(profile_id,hash,card,id) VALUES(@profile,@h,@c,@id) ON CONFLICT(profile_id,hash,card) DO UPDATE SET id=@id", ("@h", n.H), ("@c", n.C ?? cur.C ?? ""), ("@id", cur.Id));
            n.H = cur.H; // ACK retains the incoming hash in res.H; only storage uses the canonical row.
        }
        if (cur != null)
        {
            int cmp = CmpPair(n.U, n.Event, cur.U, cur.Event);
            if (cmp < 0 || (cmp == 0 && SamePayload(cur, n)))
            {
                res.Status = cmp < 0 ? "stale" : "duplicate";
                // старое событие не меняет прогресс, но не должно потерять сведения об эпизоде: карточку и identity дополняем, это новая ревизия для клиентов
                bool needC = cur.C == "" && n.C != "", needId = cur.Id == null && n.Id != null;
                if (needC || needId)
                {
                    maxS = Math.Max(HomeDb.Now(), maxS + 1);
                    HomeDb.Exec(c, "UPDATE timeline SET c=CASE WHEN c IS NULL OR c='' THEN @c ELSE c END, id=COALESCE(id,@id), s=@s WHERE profile_id=@profile AND h=@h",
                        ("@c", n.C), ("@id", n.Id), ("@s", maxS), ("@h", n.H));
                    res.Enriched = true; res.S = maxS;
                }
                else res.S = cur.S;
                return res;
            }
            if (cmp == 0) { res.Status = "invalid"; res.Reason = "same_pair_different_payload"; res.S = cur.S; return res; }
            if (n.C == "") n.C = cur.C;
            n.Id ??= cur.Id;
        }
        maxS = Math.Max(HomeDb.Now(), maxS + 1);
        n.S = maxS;
        HomeDb.Exec(c, @"INSERT INTO timeline(profile_id,h,c,t,d,p,u,s,id,event,session,op) VALUES(@profile,@h,@c,@t,@d,@p,@u,@s,@id,@e,@se,@op)
                         ON CONFLICT(profile_id,h) DO UPDATE SET c=@c,t=@t,d=@d,p=@p,u=@u,s=@s,id=@id,event=@e,session=@se,op=@op",
            ("@h", n.H), ("@c", n.C ?? ""), ("@t", n.T), ("@d", n.D), ("@p", n.P), ("@u", n.U), ("@s", maxS), ("@id", n.Id), ("@e", n.Event), ("@se", n.Session), ("@op", n.Op));
        res.Status = "accepted"; res.S = maxS; res.ResumeIgnored = n.ResumeIgnored;
        onAccepted?.Invoke(n);
        return res;
    }

    // Пачка: каждое событие получает собственный результат; недопустимое не мешает остальным. Одна транзакция на пачку.
    public static (List<Result> results, long rev) ApplyBatch(SqliteConnection c, JArray arr, Action<SqliteConnection, Row> inTx = null)
    {
        var list = new List<Result>();
        long maxS = MaxS(c);
        using var tx = c.BeginTransaction();
        try
        {
        foreach (var x in arr.Children<JObject>().Take(5000))
        {
            var n = Parse(x, out string err);
            if (n == null) { list.Add(new Result { H = SafeStr(x, "h") ?? SafeStr(x, "hash"), Event = SafeStr(x, "event"), Status = err == "clock_ahead" ? "retryable" : "invalid", Reason = err }); continue; }
            var res = Apply(c, n, ref maxS, null);
            list.Add(res);
            if (inTx != null && res.Status == "accepted") inTx(c, n);
            // повтор уже принятого сброса: действие идемпотентно (условие по времени), поэтому «Продолжить» снимается и при duplicate
            else if (inTx != null && res.Status == "duplicate" && n.Op == "reset") LastPlayStore.ResetAccepted(n);
            else if (inTx != null && res.Status == "duplicate" && n.Resume != null) {
                // Same event/position may carry repaired compact resume metadata.
                n.S = res.S;
                LastPlayStore.FromResume(n);
            }
        }
        tx.Commit();
        }
        catch { tx.Rollback(); throw; }
        return (list, maxS);
    }

    public static JObject ToWire(Row r)
    {
        var o = new JObject { ["h"] = r.H, ["t"] = r.T, ["d"] = r.D, ["p"] = r.P, ["u"] = r.U, ["s"] = r.S, ["c"] = r.C ?? "" };
        if (r.Id != null) o["id"] = r.Id;
        else o["legacy_identity"]="legacy:"+r.H;
        if (r.Event != null) o["event"] = r.Event;
        if (r.Session != null) o["session"] = r.Session;
        if (r.Op != null) o["op"] = r.Op;
        return o;
    }

    public static List<JObject> Since(SqliteConnection c, long since)
        // строки проверок (event «9:…») устройствам не выдаются: иначе они осели бы в file_view и вернулись бы на сервер после очистки
    {
        var rows = HomeDb.Query(c, "SELECT " + Cols + " FROM timeline WHERE profile_id=@profile AND s>@s AND (event IS NULL OR event NOT LIKE '9:%') ORDER BY s", ("@s", since)).Select(r => ToWire(Read(r))).ToList();
        if (!Profiles.Enabled) return rows;
        var aliases = HomeDb.Query(c, "SELECT hash,id FROM progress_aliases WHERE profile_id=@profile");
        var expanded = new List<JObject>(rows);
        foreach (var row in rows)
            foreach (var a in aliases.Where(a => (string)a[1] == (string)row["id"] && (string)a[0] != (string)row["h"]))
            { var copy = (JObject)row.DeepClone(); copy["h"] = (string)a[0]; expanded.Add(copy); }
        return expanded;
    }
}
