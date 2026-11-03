package top.rootu.lampa.player

import android.content.Context
import org.json.JSONObject
import top.rootu.lampa.ProfileRuntime
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

object PlayerSettingsStore {
    private fun prefs(context: Context) = context.getSharedPreferences("home_player", Context.MODE_PRIVATE)
    fun afr(context: Context) = prefs(context).getBoolean("afr", true)

    @Synchronized fun chooseAfr(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean("afr", enabled).putBoolean("afr_pending_sync", true).apply()
        syncAfr(context)
    }

    // An old server snapshot must not undo a choice made in the native player.
    @Synchronized fun acceptAfr(context: Context, enabled: Boolean) {
        val sp = prefs(context)
        if (sp.getBoolean("afr_pending_sync", false) && afr(context) != enabled) return
        sp.edit().putBoolean("afr", enabled).remove("afr_pending_sync").apply()
    }

    @Volatile private var syncingAfr = false
    @Synchronized fun syncAfr(context: Context) {
        if (syncingAfr || !prefs(context).getBoolean("afr_pending_sync", false)) return
        val owner = ProfileRuntime.snapshot()
        val base = owner.optString("server_base").trimEnd('/')
        val device = context.getSharedPreferences("mc", Context.MODE_PRIVATE).getString("dev", "").orEmpty()
        if (base.isBlank() || device.isBlank() || owner.optString("token").isBlank()) return
        val enabled = afr(context)
        syncingAfr = true
        Thread {
            var c: HttpURLConnection? = null
            try {
                c = URL("$base/home/dev/set?dev=${URLEncoder.encode(device, "UTF-8")}&k=afr&v=${if (enabled) 1 else 0}").openConnection() as HttpURLConnection
                c.connectTimeout = 4000; c.readTimeout = 4000
                ProfileRuntime.requestHeaders(owner).forEach { (k, v) -> c.setRequestProperty(k, v) }
                if (c.responseCode == 200) {
                    val result = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                    android.util.Log.i("HomeSettings", "afr sync ok=${result.optBoolean("ok")} enabled=$enabled")
                }
                // Keep pending until a server snapshot acknowledges the same value.
            } catch (_: Exception) { } finally {
                c?.disconnect(); syncingAfr = false
                // Coalesce quick repeated choices without losing the latest one.
                if (afr(context) != enabled) syncAfr(context)
            }
        }.start()
    }
}
