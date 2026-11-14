package top.rootu.lampa

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import android.util.Log

// Одна попытка пробуждения: оболочка, нажатие «ТВ», launchStart, плеер.
// Адрес сервера в журнал не пишется. Пока плеер не отметился, попытка не считается закрытой.
object WakeTrace {
    private data class Attempt(val id: String, val root: String, val dev: String, val headers: Map<String, String>)
    @Volatile private var active: Attempt? = null
    val attempt: String? get() = active?.id
    private val sent = HashSet<String>()

    fun begin(id: String, server: String, device: String, sleepSec: Long) {
        val root = server.trimEnd('/')
        val context = Attempt(id, root, device, ProfileRuntime.requestHeaders(ProfileRuntime.ownerFor(root)).toMap())
        synchronized(this) {
            sent.clear()
            active = context
        }
        post(context, "reload", sleepSec, -1, 0)
    }

    fun mark(stage: String, ackMs: Long = -1, shell: Int = 0) {
        val context = synchronized(this) {
            val current = active ?: return
            if (!sent.add(stage)) return
            current
        }
        post(context, stage, -1, ackMs, shell)
    }

    private fun post(context: Attempt, stage: String, sleepSec: Long, ackMs: Long, shell: Int) {
        val root = context.root
        if (!(root.startsWith("http://") || root.startsWith("https://"))) return
        val body = JSONObject()
            .put("attempt", context.id)
            .put("dev", context.dev)
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
                context.headers.forEach { (key, value) -> c.setRequestProperty(key, value) }
                c.doOutput = true
                c.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
                val status = c.responseCode
                if (status !in 200..299) Log.w("HomeWake", "stage=$stage status=$status")
            } catch (error: Exception) {
                Log.w("HomeWake", "stage=$stage failed=${error.javaClass.simpleName}")
            } finally { try { c?.disconnect() } catch (_: Exception) {} }
        }.apply { isDaemon = true; name = "wake" }.start()
    }
}
