package top.rootu.lampa

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

// Одна попытка пробуждения: оболочка, нажатие «ТВ», launchStart, плеер.
// Адрес сервера в журнал не пишется. Пока плеер не отметился, попытка не считается закрытой.
object WakeTrace {
    @Volatile var attempt: String? = null
        private set
    @Volatile private var base: String = ""
    @Volatile private var dev: String = ""
    private val sent = HashSet<String>()

    fun begin(id: String, server: String, device: String, sleepSec: Long) {
        synchronized(this) {
            sent.clear()
            attempt = id
            base = server.trimEnd('/')
            dev = device
        }
        post("reload", sleepSec, -1, 0)
    }

    fun mark(stage: String, ackMs: Long = -1, shell: Int = 0) {
        if (attempt == null) return
        synchronized(this) { if (!sent.add(stage)) return }
        post(stage, -1, ackMs, shell)
    }

    private fun post(stage: String, sleepSec: Long, ackMs: Long, shell: Int) {
        val id = attempt ?: return
        val root = base
        if (!(root.startsWith("http://") || root.startsWith("https://"))) return
        val body = JSONObject()
            .put("attempt", id)
            .put("dev", dev)
            .put("stage", stage)
            .put("sleep_s", sleepSec)
            .put("ack_ms", ackMs)
            .put("shell", shell)
            .toString()
        Thread {
            var c: HttpURLConnection? = null
            try {
                c = URL("$root/home/shell/wake").openConnection() as HttpURLConnection
                c.requestMethod = "POST"
                c.connectTimeout = 4000
                c.readTimeout = 4000
                c.instanceFollowRedirects = false
                c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                c.setRequestProperty("User-Agent", "McShell")
                c.doOutput = true
                c.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
                c.responseCode
            } catch (_: Exception) {
            } finally { try { c?.disconnect() } catch (_: Exception) {} }
        }.apply { isDaemon = true; name = "wake" }.start()
    }
}
