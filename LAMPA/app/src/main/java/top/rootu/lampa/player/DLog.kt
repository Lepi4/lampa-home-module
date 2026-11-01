package top.rootu.lampa.player

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Диагностический журнал клиента: ограниченный по сроку и по объёму, не поток строк.
 *
 * — Пишутся только переходы состояния и ошибки. Одинаковые события (код + ключ) схлопываются в одну запись со счётчиком, временем первого и последнего и свежим примером.
 * — В буфере не больше MAX_ENTRIES записей и MAX_KEYS разных ключей на код; лишние ключи считаются в общую запись «*». Буфер хранится в файле и переживает перезапуск.
 * — Отправка пачкой раз в несколько минут (и при вызове flush) на /home/dlog/put; ответ сервера — единственное подтверждение, при сбое записи возвращаются в буфер.
 * — Срок сбора задаёт сервер (until, мс): после него клиент ничего не пишет и не шлёт, буфер очищается. Без связи действует последнее известное значение; без значения буферизуем.
 * — Адреса (http://…) в журнал не попадают, длины ограничены. Любая ошибка журнала глотается: он не должен влиять на работу приложения.
 */
object DLog {
    private const val MAX_ENTRIES = 200
    private const val MAX_KEYS = 25
    private const val SEND_EVERY_MS = 5 * 60_000L
    private const val CFG_STALE_MS = 60 * 60_000L

    private class E(val lvl: String, val code: String, val key: String, var n: Long, val t0: Long, var t1: Long, var x: String)

    private val lock = Any()
    private val buf = LinkedHashMap<String, E>()
    private var dir: File? = null
    private var devId = ""
    @Volatile var ver = ""
    @Volatile private var base = ""
    @Volatile private var until = 0L              // 0 — срок ещё не известен
    @Volatile private var onFlag = true
    private var cfgAt = 0L
    private var dirty = false
    private var started = false
    private val exec = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "dlog").apply { isDaemon = true } }
    private val urlRx = Regex("https?://\\S+")

    fun init(ctx: Context) {
        synchronized(lock) {
            if (dir != null) return
            try {
                dir = ctx.applicationContext.filesDir
                val idf = File(dir, "dlog.id")
                devId = try { if (idf.exists()) idf.readText().trim() else "" } catch (_: Exception) { "" }
                if (devId.isEmpty()) { devId = "d" + UUID.randomUUID().toString().replace("-", "").substring(0, 10); try { idf.writeText(devId) } catch (_: Exception) {} }
                try { File(dir, "dlog.cfg").takeIf { it.exists() }?.let { val o = JSONObject(it.readText()); until = o.optLong("until"); onFlag = o.optBoolean("on", true); cfgAt = o.optLong("at") } } catch (_: Exception) {}
                try { File(dir, "dlog.json").takeIf { it.exists() }?.let { val a = JSONArray(it.readText()); for (i in 0 until a.length()) { val o = a.getJSONObject(i); put(E(o.optString("l"), o.optString("c"), o.optString("k"), o.optLong("n", 1), o.optLong("t0"), o.optLong("t1"), o.optString("x"))) } } } catch (_: Exception) {}
            } catch (_: Exception) {}
        }
        if (!started) {
            started = true
            try { exec.scheduleWithFixedDelay({ tick() }, 60_000L, SEND_EVERY_MS, TimeUnit.MILLISECONDS) } catch (_: Exception) {}
        }
    }

    /** Адрес сервера Lampac, куда отправлять журнал (последний известный). */
    fun setBase(b: String) { if (b.isNotBlank()) base = b.trimEnd('/') }

    private fun enabled(): Boolean {
        val u = until
        if (!onFlag) return false
        return u == 0L || System.currentTimeMillis() < u        // срок мерили по часам устройства; сервер при отправке всё равно закроет приём
    }

    private fun clean(s: String, max: Int): String {
        val t = urlRx.replace(s, "<url>").replace('\n', ' ').replace('\r', ' ')
        return if (t.length > max) t.substring(0, max) else t
    }

    // под lock
    private fun put(e: E) {
        val k = e.code + "|" + e.key
        val old = buf[k]
        if (old != null) { old.n += e.n; old.t1 = maxOf(old.t1, e.t1); if (e.x.isNotEmpty()) old.x = e.x; return }
        if (buf.values.count { it.code == e.code } >= MAX_KEYS) {                  // лишние ключи одного кода считаются вместе
            val sk = e.code + "|*"
            val star = buf[sk]
            if (star != null) { star.n += e.n; star.t1 = maxOf(star.t1, e.t1) } else buf[sk] = E(e.lvl, e.code, "*", e.n, e.t0, e.t1, "")
            return
        }
        if (buf.size >= MAX_ENTRIES) {
            val victim = buf.entries.firstOrNull { it.value.lvl == "i" }?.key ?: buf.entries.first().key       // вытесняем самое старое информационное
            buf.remove(victim)
        }
        buf[k] = e
    }

    /** lvl: "i" информация, "w" предупреждение, "e" ошибка. code — латиница/цифры/_, key — подкатегория (статус, причина), x — короткий пример. */
    fun log(lvl: String, code: String, key: String = "", x: String = "") {
        try {
            if (dir == null || !enabled()) return
            val now = System.currentTimeMillis()
            synchronized(lock) { put(E(lvl, code, clean(key, 60), 1, now, now, clean(x, 160))); dirty = true }
            if (lvl == "e") try { exec.execute { persist() } } catch (_: Exception) {}
        } catch (_: Exception) {}
    }

    private fun persist() {
        try {
            val d = dir ?: return
            val arr = JSONArray()
            synchronized(lock) {
                if (!dirty) return
                dirty = false
                for (e in buf.values) arr.put(JSONObject().put("l", e.lvl).put("c", e.code).put("k", e.key).put("n", e.n).put("t0", e.t0).put("t1", e.t1).put("x", e.x))
            }
            val f = File(d, "dlog.json"); val tmp = File(d, "dlog.json.tmp")
            tmp.writeText(arr.toString())
            if (!tmp.renameTo(f)) { f.delete(); tmp.renameTo(f) }
        } catch (_: Exception) {}
    }

    private fun saveCfg() { try { File(dir ?: return, "dlog.cfg").writeText(JSONObject().put("until", until).put("on", onFlag).put("at", cfgAt).toString()) } catch (_: Exception) {} }

    private fun tick() { try { flushNow() } catch (_: Exception) {} }

    /** Отправить накопленное сейчас (фоновый поток). */
    fun flush() { try { exec.execute { try { flushNow() } catch (_: Exception) {} } } catch (_: Exception) {} }

    private fun http(url: String, body: String?): Pair<Int, String> {
        val c = URL(url).openConnection() as HttpURLConnection
        try {
            c.connectTimeout = 5000; c.readTimeout = 10000
            if (body != null) { c.requestMethod = "POST"; c.doOutput = true; c.setRequestProperty("Content-Type", "application/json"); c.outputStream.use { it.write(body.toByteArray()) } }
            val code = c.responseCode
            val txt = try { (if (code in 200..299) c.inputStream else c.errorStream)?.bufferedReader()?.use { it.readText() } ?: "" } catch (_: Exception) { "" }
            return code to txt
        } finally { try { c.disconnect() } catch (_: Exception) {} }
    }

    private fun flushNow() {
        val b = base; if (b.isEmpty() || dir == null) return
        val now = System.currentTimeMillis()
        // конфигурация сбора: срок задаёт сервер; спрашиваем при отправке и не реже раза в час
        val hasRows = synchronized(lock) { buf.isNotEmpty() }
        if (hasRows || now - cfgAt > CFG_STALE_MS) {
            try {
                val (code, txt) = http("$b/home/dlog/cfg", null)
                if (code == 200) {
                    val o = JSONObject(txt)
                    until = o.optLong("until"); onFlag = o.optBoolean("on", false); cfgAt = now
                    // часы устройства и сервера могут расходиться: срок сравниваем по серверному времени, пересчитанному на часы устройства
                    val sn = o.optLong("now", 0L); if (sn > 0 && until > 0) until = until - sn + now
                    saveCfg()
                }
            } catch (_: Exception) { return }
        }
        if (!enabled()) { synchronized(lock) { buf.clear(); dirty = true }; persist(); return }
        val snap: List<E> = synchronized(lock) { val l = buf.values.map { E(it.lvl, it.code, it.key, it.n, it.t0, it.t1, it.x) }; l }
        if (snap.isEmpty()) return
        val rows = JSONArray()
        for (e in snap) rows.put(JSONObject().put("l", e.lvl).put("c", e.code).put("k", e.key).put("n", e.n).put("t0", e.t0).put("t1", e.t1).put("x", e.x))
        val model = try { android.os.Build.MODEL ?: "" } catch (_: Throwable) { "" }
        val body = JSONObject().put("dev", devId).put("ver", (ver + (if (model.isNotEmpty()) "|$model" else ""))).put("ct", System.currentTimeMillis()).put("rows", rows).toString()
        try {
            val (code, txt) = http("$b/home/dlog/put", body)
            val ok = code == 200 && (try { JSONObject(txt).optBoolean("ok") } catch (_: Exception) { false })
            val closed = try { JSONObject(txt).optBoolean("closed") } catch (_: Exception) { false }
            if (closed) { onFlag = false; until = 1L; saveCfg(); synchronized(lock) { buf.clear(); dirty = true }; persist(); return }
            if (!ok) return
            // отправленное вычитается из буфера (то, что накопилось за время отправки, остаётся)
            synchronized(lock) {
                for (s in snap) {
                    val k = s.code + "|" + s.key
                    val cur = buf[k] ?: continue
                    if (cur.n <= s.n) buf.remove(k) else { cur.n -= s.n }
                }
                dirty = true
            }
            persist()
        } catch (_: Exception) {}
    }
}
