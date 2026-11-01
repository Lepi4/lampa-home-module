package top.rootu.lampa

import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import top.rootu.lampa.player.HomePlayerActivity
import java.lang.ref.WeakReference
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * Дежурный напоминаний «включить передачу»: живёт в процессе приложения, а не на странице Lampa (страница стоит на паузе, пока
 * сверху открыт видеоплеер). Раз в 15 секунд читает с сервера напоминания этого устройства; за 2 минуты до начала передачи:
 *  - идёт фильм/сериал (ExoPlayer) — закрывает плеер и просит Lampa открыть эфир на нужном канале;
 *  - открыта сама Lampa — то же;
 *  - идёт эфир в плеере mpv — ничего не делает, плеер сам переключает канал (mpvAlive).
 * Из сна и из других приложений не будит: запуск экрана возможен, только пока приложение на виду.
 */
object RemindWatcher {
    @Volatile private var started = false
    @Volatile private var base = ""
    @Volatile private var dev = ""
    @Volatile var mpvAlive = false
    @Volatile var videoPlayer: WeakReference<HomePlayerActivity>? = null

    fun start(act: MainActivity, baseUrl: String, devId: String) {
        base = baseUrl.trimEnd('/')
        dev = devId
        if (started) return
        started = true
        val ctx = act.applicationContext
        Thread { loop(ctx) }.apply { isDaemon = true; name = "RemindWatcher" }.start()
    }

    private fun get(url: String, owner: JSONObject?): String {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 4000; c.readTimeout = 5000
        c.setRequestProperty("User-Agent", "McShell TV reminders")
        ProfileRuntime.requestHeaders(owner).forEach { (key,value) -> c.setRequestProperty(key,value) }
        return try { c.inputStream.bufferedReader().use { it.readText() } } finally { c.disconnect() }
    }

    private fun loop(ctx: Context) {
        while (true) {
            try { if (base.isNotEmpty() && !mpvAlive) tick(ctx) } catch (_: Exception) {}
            try { Thread.sleep(15_000) } catch (_: InterruptedException) { return }
        }
    }

    private fun tick(ctx: Context) {
        val server = base
        val owner = ProfileRuntime.ownerFor(server)
        if (ProfileRuntime.enabled() && owner == null) return
        val profile = owner?.optJSONObject("profile")?.optString("id")
        val q = if (dev.isBlank()) "" else "dev=" + URLEncoder.encode(dev, "UTF-8")
        val arr = JSONObject(get("$server/tv/remind?$q", owner)).optJSONArray("list") ?: return
        val now = System.currentTimeMillis() / 1000
        var lastId: String? = null
        var lastTitle = ""
        for (i in 0 until arr.length()) {
            val r = arr.getJSONObject(i)
            val s = r.optLong("s")
            if (now < s - 120) continue
            val id = r.optString("id")
            try { get("$server/tv/remind/done?$q&id=$id&s=$s", owner) } catch (_: Exception) { continue }   // не удалось пометить — в следующий раз
            lastId = id; lastTitle = r.optString("t")   // если сошлись две сразу — включаем более позднюю
        }
        if (lastId == null) return
        val id = lastId
        val title = lastTitle
        Handler(Looper.getMainLooper()).post {
            val current = ProfileRuntime.ownerFor(server)
            if (base == server && current?.optJSONObject("profile")?.optString("id") == profile) fire(ctx, id, title)
        }
    }

    private fun fire(ctx: Context, id: String, title: String) {
        val vp = videoPlayer?.get()
        if (vp != null) vp.closeForReminder()
        val open = Runnable {
            try {
                ctx.startActivity(
                    Intent(ctx, MainActivity::class.java)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)
                        .putExtra("remind_channel", id)
                        .putExtra("remind_title", title)
                )
            } catch (e: Exception) {
                android.util.Log.i("RemindWatcher", "не удалось открыть Lampa: ${e.message}")
            }
        }
        // даём плееру закрыться, потом выводим Lampa вперёд
        if (vp != null) Handler(Looper.getMainLooper()).postDelayed(open, 700) else open.run()
    }
}
