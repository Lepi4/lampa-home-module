package top.rootu.lampa.player

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Очередь отчётов о просмотре: переживает обрыв сети, закрытие плеера и пересоздание окна.
 *
 * — Хранится в файле; запись атомарная (временный файл + переименование) с резервной копией прежнего файла. Если записать на диск не удалось,
 *   очередь остаётся в памяти и enqueue сообщает false: вызывающий не считает действие сохранённым.
 * — Строка принадлежит конкретному серверу (_base), у новых событий он записывается явно. Строка прежней версии без адреса помечается origin_unknown и остаётся
 *   «на разборе»: ни первый flush, ни последующие enqueue не отправляют её на только что выбранный сервер (резерв исходной очереди сохраняется).
 * — Одна запись на серию (ключ: сервер + id). Побеждает событие с большей парой (u, event) — тот же порядок, что на сервере; более старое не заменяет ожидающее.
 * — Отправка идёт на /home/tl/set: сервер отвечает по каждому событию (accepted / duplicate / stale / invalid / retryable). Строка снимается только по ответу
 *   на СВОЁ событие (h + event): принято, повтор, устарело — доставлено; invalid — в архив с причиной; всё остальное (нет ответа, чужой ответ, success:false,
 *   сеть, 5xx) — временная ошибка: событие остаётся до срока хранения (7 суток от постановки; повтор срок не продлевает), потом в архив tl_outbox.expired.json.
 * — afterFlush выполняется, когда доставлены именно те события, что стояли в очереди на момент вызова; отклонённое или истёкшее событие отменяет ожидание.
 */
object ProgressOutbox {
    private const val TTL_MS = 7L * 24 * 3600 * 1000
    private const val MAX_REJECT = 10
    private val lock = Any()
    private var file: File? = null
    private val exec = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "tl-outbox").apply { isDaemon = true } }
    private var retryMs = 15_000L
    @Volatile private var retryScheduled = false
    private var memQueue: JSONObject? = null             // очередь, не попавшая на диск (под lock)

    private class Waiter(val events: MutableSet<String>, val cb: () -> Unit)
    private val waiters = ArrayList<Waiter>()            // под lock
    private val delivered = LinkedHashSet<String>()      // "ключ#event" подтверждённых событий (под lock, последние 2000)

    fun init(ctx: Context) {
        if (file == null) file = File(ctx.applicationContext.filesDir, "tl_outbox.json")
        DLog.init(ctx)
    }

    // ---------- порядок событий (как на сервере: пустой < «сеанс:номер» по числам < прочее по ordinal) ----------
    private fun parts(e: String): Pair<Long, Long>? {
        val i = e.lastIndexOf(':'); if (i <= 0) return null
        val s = e.substring(0, i).toLongOrNull() ?: return null
        val q = e.substring(i + 1).toLongOrNull() ?: return null
        return s to q
    }
    private fun rank(e: String): Int = if (e.isEmpty()) 0 else if (parts(e) != null) 1 else 2
    private fun cmpEvent(a: String, b: String): Int {
        if (a == b) return 0
        val ra = rank(a); val rb = rank(b)
        if (ra != rb) return ra.compareTo(rb)
        if (ra == 1) {
            val x = parts(a)!!; val y = parts(b)!!
            val c = if (x.first != y.first) x.first.compareTo(y.first) else x.second.compareTo(y.second)
            return if (c != 0) c else a.compareTo(b)
        }
        return a.compareTo(b)
    }
    private fun cmpPair(u1: Long, e1: String, u2: Long, e2: String) = if (u1 != u2) u1.compareTo(u2) else cmpEvent(e1, e2)
    private fun evOf(r: JSONObject) = r.optString("event")

    // ---------- файл ----------
    private fun metaFile() = file?.let { File(it.parentFile, "tl_outbox.meta.json") }

    private fun knownBases(): MutableSet<String> {
        val m = metaFile() ?: return mutableSetOf()
        return try {
            val a = JSONObject(m.readText()).optJSONArray("bases") ?: JSONArray()
            (0 until a.length()).map { a.getString(it) }.toMutableSet()
        } catch (_: Exception) { mutableSetOf() }
    }

    private fun noteBase(base: String) {
        if (base.isBlank()) return
        DLog.setBase(base)
        val b = knownBases()
        if (b.add(base)) try { metaFile()?.writeText(JSONObject().put("bases", JSONArray(b.toList())).toString()) } catch (_: Exception) {}
    }

    // Файл существует, но прочитать его не удалось (ошибка ввода-вывода, не повреждённый JSON): в памяти только новые события,
    // сам файл не перезаписывается, пока не прочитается снова и содержимое не будет слито с памятью (под lock)
    private var unreadable = false

    private fun mergeInto(base: JSONObject, extra: JSONObject) {
        val keys = ArrayList<String>(); for (k in extra.keys()) keys.add(k)
        for (k in keys) {
            val n = extra.optJSONObject(k) ?: continue
            val ex = base.optJSONObject(k)
            if (ex == null || cmpPair(n.optLong("u"), evOf(n), ex.optLong("u"), evOf(ex)) > 0) base.put(k, n)
        }
    }

    private fun load(): JSONObject {
        val f = file ?: return memQueue ?: JSONObject()
        if (unreadable) {
            // повторная попытка чтения: при успехе новые события из памяти добавляются к прочитанной очереди
            val mem = memQueue ?: JSONObject()
            if (!f.exists()) { unreadable = false; return mem }
            try {
                val disk = JSONObject(f.readText())
                mergeInto(disk, mem); unreadable = false; memQueue = disk
                return disk
            } catch (_: java.io.IOException) { return mem }
            catch (_: Exception) { unreadable = false }                   // файл читается, но повреждён — обычный разбор ниже
        }
        memQueue?.let { return it }
        if (!f.exists()) return JSONObject()
        return try { JSONObject(f.readText()) }
        catch (_: java.io.IOException) { unreadable = true; DLog.log("e", "outbox_unreadable", "", "queue file not readable"); memQueue = JSONObject(); memQueue!! }     // не прочитано ≠ пусто: файл остаётся нетронутым
        catch (_: Exception) {
            // повреждённый файл не стирает очередь молча: берём резервную копию, сам файл откладываем для разбора
            try { f.renameTo(File(f.parentFile, "tl_outbox.bad")) } catch (_: Exception) {}
            try { val b = File(f.parentFile, f.name + ".bak"); if (b.exists()) JSONObject(b.readText()) else JSONObject() } catch (_: Exception) { JSONObject() }
        }
    }

    /** true — очередь на диске; false — остаётся только в памяти (будет записана при следующей возможности). */
    private fun store(o: JSONObject): Boolean {
        val f = file ?: return false
        if (unreadable) { memQueue = o; return false }                      // не затираем файл, который не удалось прочитать
        val atomic = android.util.AtomicFile(f)
        var stream: java.io.FileOutputStream? = null
        return try {
            if (f.exists()) try { f.copyTo(File(f.parentFile, f.name + ".bak"), overwrite = true) } catch (_: Exception) {}
            stream = atomic.startWrite()
            stream.write(o.toString().toByteArray(Charsets.UTF_8))
            atomic.finishWrite(stream)
            memQueue = null
            true
        } catch (_: Exception) {
            stream?.let { try { atomic.failWrite(it) } catch (_: Exception) {} }
            memQueue = o; false
        }
    }

    private fun backupOnce() {
        val f = file ?: return
        try {
            val b = File(f.parentFile, "tl_outbox.pre-migration.json")
            if (!b.exists() && f.exists()) f.copyTo(b)
        } catch (_: Exception) {}
    }

    /** В архив (не более 200 последних): истёкшее или окончательно отклонённое событие нельзя выдавать за доставленное. */
    private fun archive(key: String, r: JSONObject, reason: String) {
        dropWaiters(key + "#" + evOf(r))
        DLog.log(if (reason == "ttl") "w" else "w", "outbox_archive", reason, "event=" + evOf(r))
        val f = file ?: return
        try {
            val af = File(f.parentFile, "tl_outbox.expired.json")
            val arr = try { JSONArray(if (af.exists()) af.readText() else "[]") } catch (_: Exception) { JSONArray() }
            arr.put(JSONObject(r.toString()).put("_reason", reason).put("_at", System.currentTimeMillis()))
            val out = JSONArray()
            for (i in maxOf(0, arr.length() - 200) until arr.length()) out.put(arr.get(i))
            val tmp = File(af.parentFile, af.name + ".tmp")
            tmp.writeText(out.toString())
            if (!tmp.renameTo(af)) { af.delete(); tmp.renameTo(af) }
        } catch (_: Exception) {}
    }

    // ---------- ожидание доставки ----------
    private fun dropWaiters(ev: String) { synchronized(lock) { waiters.removeAll { it.events.contains(ev) } } }          // отклонено/истекло — действие «после доставки» не выполняется
    private fun markDelivered(ev: String) { synchronized(lock) { delivered.add(ev); while (delivered.size > 2000) delivered.remove(delivered.first()) } }
    private fun supersede(key: String, oldEv: String, newEv: String) {
        synchronized(lock) { waiters.forEach { w -> if (w.events.remove("$key#$oldEv")) w.events.add("$key#$newEv") } }   // событие заменено более новым: ждём его
    }

    private fun checkWaiters() {
        val ready = ArrayList<Waiter>()
        synchronized(lock) {
            val it = waiters.iterator()
            while (it.hasNext()) { val w = it.next(); if (delivered.containsAll(w.events)) { ready.add(w); it.remove() } }
        }
        for (w in ready) Thread { try { w.cb() } catch (_: Exception) {} }.start()
    }

    /**
     * Приводит очередь прежних версий к текущему формату. Идемпотентно.
     * Строка без адреса получает его, только если известен ровно один сервер; иначе остаётся без адреса и не отправляется.
     * Отсутствие метки времени — «неизвестно», а не 1970 год: срок отсчитывается с момента переноса.
     */
    private fun migrate(q: JSONObject): Boolean {
        var changed = false
        val keys = ArrayList<String>(); for (k in q.keys()) keys.add(k)
        if (keys.any { k -> val r = q.optJSONObject(k); r == null || !k.contains("|") || !r.has("_base") || !r.has("_t") }) backupOnce()
        for (k in keys) {
            val r = q.optJSONObject(k) ?: run { q.remove(k); changed = true; null } ?: continue
            // происхождение строки без адреса неизвестно: текущий выбранный сервер его не доказывает (иначе после смены сервера старая история уйдёт на новый адрес)
            if (r.optString("_base").isEmpty() && !r.optBoolean("origin_unknown")) { r.put("origin_unknown", true); changed = true }
            if (!r.has("_t")) { r.put("_t", System.currentTimeMillis()); changed = true }
            if (!r.has("_n")) { r.put("_n", 0); changed = true }
            // строки формата штатного TimeCode (0065 и выпуск этапа A: hash/position/duration/percent/watched_at) -> формат /home/tl (h/t/d/p/u); идемпотентно
            if (!r.has("h") && r.has("hash")) {
                r.put("h", r.optString("hash")); r.remove("hash")
                if (r.has("card")) { r.put("c", r.optString("card")); r.remove("card") }
                if (r.has("position")) { r.put("t", r.optDouble("position", 0.0)); r.remove("position") }
                if (r.has("duration")) { r.put("d", r.optDouble("duration", 0.0)); r.remove("duration") }
                if (r.has("percent")) { r.put("p", r.optDouble("percent", 0.0)); r.remove("percent") }
                if (r.has("watched_at")) { r.put("u", r.optLong("watched_at")); r.remove("watched_at") }
                if (r.has("session")) r.put("session", r.optString("session"))
                if (r.optBoolean("ended") && !r.has("op")) r.put("op", "completed")
                changed = true
            }
            val b = r.optString("_base")
            if ((!k.contains("|") || !r.has("_profile")) && b.isNotEmpty()) {
                r.put("_profile", "u_default")
                val id = r.optString("id").ifEmpty { k }
                val nk = "$b|u_default|$id"
                q.remove(k)
                // столкновение со строкой нового формата: остаётся событие с большей парой (u, event), проигравшее уходит в архив, а не пропадает
                val ex = q.optJSONObject(nk)
                if (ex == null) q.put(nk, r)
                else if (cmpPair(r.optLong("u"), evOf(r), ex.optLong("u"), evOf(ex)) > 0) { archive(nk, ex, "superseded"); q.put(nk, r) }
                else archive(nk, r, "superseded")
                changed = true
            }
        }
        return changed
    }

    /** Кладёт строки в очередь и запускает отправку. Возвращает true, если действие сохранено на диске. */
    fun enqueue(base: String, rows: JSONArray, owner: JSONObject? = top.rootu.lampa.ProfileRuntime.ownerFor(base)): Boolean {
        if (base.isBlank()) return false
        var ok: Boolean
        synchronized(lock) {
            noteBase(base)
            val q = load()
            migrate(q)
            for (i in 0 until rows.length()) {
                val r = rows.optJSONObject(i) ?: continue
                val id = r.optString("id")
                if (id.isBlank()) continue
                val profile = owner?.optJSONObject("profile")?.optString("id").orEmpty()
                if (top.rootu.lampa.ProfileRuntime.enabled() && profile.isBlank()) return false
                val key = "$base|${profile.ifBlank { "u_default" }}|$id"
                if (owner != null) r.put("_owner", JSONObject(owner.toString()))
                r.put("_profile", profile.ifBlank { "u_default" })
                val old = q.optJSONObject(key)
                if (old != null) {
                    // то же действие (повтор) срок не продлевает; более старое не вытесняет ожидающее более новое; более позднее (в том числе перемотка после «досмотрено») заменяет
                    if (cmpPair(r.optLong("u"), evOf(r), old.optLong("u"), evOf(old)) <= 0) continue
                    supersede(key, evOf(old), evOf(r))
                }
                r.put("_base", base); r.put("_n", 0); r.put("_t", System.currentTimeMillis())
                q.put(key, r)
            }
            ok = store(q)
        }
        if (!ok) DLog.log("e", "outbox_enqueue_fail", "", "rows=" + rows.length())
        kick()
        return ok
    }

    /** Дослать накопленное (вызывается при открытии приложения и плеера). */
    fun flush(base: String) { synchronized(lock) { noteBase(base) }; kick() }

    /**
     * Выполнить cb, когда доставлены именно те события, что стоят в очереди для этого сервера сейчас. Если очередь для сервера пуста — сразу.
     * Отклонённое или истёкшее событие отменяет ожидание (это не доставка).
     */
    fun afterFlush(base: String, cb: () -> Unit) {
        val ownerId=top.rootu.lampa.ProfileRuntime.profileId()
        val guarded={ if(top.rootu.lampa.ProfileRuntime.profileId()==ownerId) cb() }
        var runNow = false
        synchronized(lock) {
            noteBase(base)
            val q = load(); migrate(q)
            val evs = HashSet<String>()
            for (k in q.keys()) { val r = q.optJSONObject(k) ?: continue; if (r.optString("_base") == base && r.optString("_profile", "u_default") == top.rootu.lampa.ProfileRuntime.profileId().ifBlank { "u_default" }) evs.add(k + "#" + evOf(r)) }
            if (evs.isEmpty()) runNow = true else {
                waiters.add(Waiter(evs, guarded))
                while (waiters.size > 20) waiters.removeAt(0)
            }
        }
        if (runNow) Thread { try { guarded() } catch (_: Exception) {} }.start() else kick()
    }

    private fun kick() { exec.execute { runFlush() } }

    private fun runFlush() {
        var again = false
        val snapshot: JSONObject = synchronized(lock) {
            val q = load()
            var ch = migrate(q)
            // срок хранения: отсчёт от постановки в очередь; истёкшее — в архив, не молча
            val ks = ArrayList<String>(); for (k in q.keys()) ks.add(k)
            for (k in ks) {
                val r = q.optJSONObject(k) ?: continue
                if (System.currentTimeMillis() - r.optLong("_t") > TTL_MS) { archive(k, r, "ttl"); q.remove(k); ch = true }
            }
            if (ch || memQueue != null) store(q)
            q
        }
        // файл очереди не прочитан: повторяем чтение позже, а не считаем очередь пустой
        if (synchronized(lock) { unreadable }) scheduleRetry()
        if (snapshot.length() == 0) { if (!synchronized(lock) { unreadable }) retryMs = 15_000L; checkWaiters(); return }
        // группируем по серверу; строки без адреса («на разборе») не отправляются
        val byBase = LinkedHashMap<String, MutableList<String>>()
        for (k in snapshot.keys()) {
            val r=snapshot.getJSONObject(k)
            val owner=r.optJSONObject("_owner")
            val credential=owner?.optString("progress_grant").orEmpty().ifBlank { owner?.optString("token").orEmpty() }
            val group=r.optString("_base")+"\n"+r.optString("_profile","u_default")+"\n"+credential
            byBase.getOrPut(group) { mutableListOf() }.add(k)
        }
        for ((group, keys) in byBase) {
            val base=group.substringBefore("\n")
            if (base.isEmpty()) continue
            val stored=snapshot.getJSONObject(keys.first()).optJSONObject("_owner")
            val active=top.rootu.lampa.ProfileRuntime.ownerFor(base)
            val profile=snapshot.getJSONObject(keys.first()).optString("_profile","u_default")
            val owner=if(active?.optJSONObject("profile")?.optString("id")==profile && stored?.optString("progress_grant").orEmpty().isBlank()) active else stored
            if(top.rootu.lampa.ProfileRuntime.enabled() && owner==null){again=true;continue}
            for (chunk in keys.chunked(50)) {
                val rows = JSONArray()
                for (k in chunk) {
                    val src = snapshot.getJSONObject(k)
                    rows.put(JSONObject(src.toString()).apply { remove("_base"); remove("_n"); remove("_t"); remove("ended"); remove("_owner"); remove("_profile"); remove("origin_unknown") })
                }
                var code: Int
                val acked = HashMap<String, String>()          // "h|event" -> статус из ответа сервера
                try {
                    val c = URL("$base/home/tl/set").openConnection() as HttpURLConnection
                    c.requestMethod = "POST"; c.doOutput = true; c.connectTimeout = 5000; c.readTimeout = 10000
                    c.setRequestProperty("Content-Type", "application/json")
                    val grant=owner?.optString("progress_grant").orEmpty()
                    if(grant.isNotBlank() && rows.let { a -> (0 until a.length()).all { a.getJSONObject(it).optString("op") in listOf("progress","completed") } }) c.setRequestProperty("X-Home-Progress",grant)
                    else owner?.optString("token")?.takeIf { it.isNotBlank() }?.let { c.setRequestProperty("X-Home-Session",it) }
                    c.outputStream.use { it.write(JSONObject().put("rows", rows).toString().toByteArray()) }
                    code = c.responseCode
                    val body = try { (if (code in 200..299) c.inputStream else c.errorStream)?.bufferedReader()?.use { it.readText() } ?: "" } catch (_: Exception) { "" }
                    val jb = try { JSONObject(body) } catch (_: Exception) { null }
                    jb?.optJSONArray("results")?.let { ra ->
                        for (i in 0 until ra.length()) {
                            val o = ra.optJSONObject(i) ?: continue
                            acked[o.optString("h") + "|" + (if (o.isNull("event")) "" else o.optString("event"))] = o.optString("status")
                        }
                    }
                    c.disconnect()
                } catch (_: Exception) { code = -1 }
                if (code != 200) DLog.log("w", "outbox_http", if (code == -1) "no_connection" else code.toString(), "rows=" + rows.length())

                synchronized(lock) {
                    val now = load()
                    for (k in chunk) {
                        val sent = snapshot.getJSONObject(k)
                        val cur = now.optJSONObject(k) ?: continue
                        if (evOf(cur) != evOf(sent)) { again = true; continue }      // за это время появилось новее — оно уйдёт следующим проходом
                        // подтверждение ищем по собственному событию (h + event), а не по номеру в ответе
                        val st = acked[sent.optString("h") + "|" + sent.optString("event")]
                        DLog.log(if (st == null || st == "invalid") "w" else "i", "outbox_ack", st ?: (if (code == -1) "net_error" else "no_ack:$code"), "")
                        when {
                            st == "accepted" || st == "duplicate" || st == "stale" -> { now.remove(k); markDelivered(k + "#" + evOf(cur)) }
                            st == "invalid" -> { archive(k, cur, "invalid"); now.remove(k) }
                            st == "retryable" -> again = true
                            code in 400..499 && code != 401 && code != 403 && code != 408 && code != 429 -> {         // сервер отклоняет весь запрос: ограниченное число попыток, затем в архив
                                val n = cur.optInt("_n") + 1
                                if (n >= MAX_REJECT) { archive(k, cur, "rejected:$code"); now.remove(k) } else { cur.put("_n", n); again = true }
                            }
                            else -> again = true       // нет ответа на это событие, success:false, сеть, 5xx: временная ошибка, событие остаётся до срока хранения
                        }
                    }
                    store(now)
                }
            }
        }
        checkWaiters()
        if (again) scheduleRetry() else retryMs = 15_000L
    }

    private fun scheduleRetry() {
        if (retryScheduled) return
        retryScheduled = true
        val d = retryMs
        retryMs = (retryMs * 2).coerceAtMost(300_000L)
        exec.schedule({ retryScheduled = false; runFlush() }, d, TimeUnit.MILLISECONDS)
    }
}
