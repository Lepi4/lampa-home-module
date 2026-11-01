package top.rootu.lampa

import android.content.Context
import android.content.Intent
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID

/** Process lifetime is the cold-start boundary. Tokens and the selected adult are never restored from disk. */
object ProfileRuntime {
    val bootId: String = UUID.randomUUID().toString().replace("-", "")
    private val lock = Any()
    @Volatile private var state = JSONObject().put("ready", false).put("profiles_enabled", true)
    private fun base(url: String) = url.trim().trimEnd('/')

    fun device(context: Context): String {
        val prefs = context.getSharedPreferences("mc", Context.MODE_PRIVATE)
        synchronized(lock) {
            prefs.getString("dev", null)?.let { return it }
            val id = "mc" + UUID.randomUUID().toString().replace("-", "").take(12)
            prefs.edit().putString("dev", id).apply()
            return id
        }
    }
    @Volatile private var publicRowsCleared=false
    private fun clearPublicRows(context: Context) {
        if(publicRowsCleared||!enabled()||!BuildConfig.APPLICATION_ID.endsWith(".mc"))return
        publicRowsCleared=true
        if(android.os.Build.VERSION.SDK_INT<26)return
        try {
            val resolver=context.contentResolver
            val tables=listOf(androidx.tvprovider.media.tv.TvContractCompat.Channels.CONTENT_URI,androidx.tvprovider.media.tv.TvContractCompat.WatchNextPrograms.CONTENT_URI)
            for(table in tables){val ids=ArrayList<Long>();resolver.query(table,arrayOf("_id","package_name"),null,null,null)?.use { c->while(c.moveToNext())if(c.getString(1)==context.packageName)ids.add(c.getLong(0)) };for(id in ids)resolver.delete(android.content.ContentUris.withAppendedId(table,id),null,null)}
        }catch (_: Exception) { }
    }
    fun snapshot(): JSONObject = synchronized(lock) { JSONObject(state.toString()) }
    fun contextJson(): String = snapshot().toString()
    fun enabled(): Boolean = snapshot().optBoolean("profiles_enabled", false)
    fun profileId(): String = snapshot().optJSONObject("profile")?.optString("id").orEmpty()
    fun isChild(): Boolean = snapshot().optJSONObject("profile")?.optBoolean("is_child", false) == true
    fun settings(): JSONObject = snapshot().optJSONObject("profile")?.optJSONObject("settings") ?: JSONObject()
    fun scopeKey(name: String): String {
        val s = snapshot()
        if (!s.optBoolean("profiles_enabled", false)) return name
        val id = s.optJSONObject("profile")?.optString("id").orEmpty()
        // Before bootstrap, never open the previous adult's unscoped private store.
        val serverId = s.optString("server_id").ifBlank { UUID.nameUUIDFromBytes(s.optString("server_base").toByteArray()).toString() }
        return "home_user:" + serverId + ":" + id.ifBlank { "locked" } + ":" + name
    }
    private fun call(server: String, path: String, body: JSONObject, owner: JSONObject? = null): JSONObject {
        val c = URL(base(server) + path).openConnection() as HttpURLConnection
        try {
            c.requestMethod = "POST"; c.doOutput = true; c.connectTimeout = 3000; c.readTimeout = 6000
            c.setRequestProperty("Content-Type", "application/json")
            c.setRequestProperty("User-Agent", "McShell local profiles")
            owner?.optString("token")?.takeIf { it.isNotBlank() }?.let { c.setRequestProperty("X-Home-Session", it) }
            c.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            val code = c.responseCode
            val text = (if (code in 200..299) c.inputStream else c.errorStream)?.bufferedReader()?.use { it.readText() }.orEmpty()
            if (code == 404 && path == "/home/session/bootstrap")
                return JSONObject().put("ready", true).put("profiles_enabled", false).put("legacy", true)
            val result = try { JSONObject(text) } catch (_: Exception) { JSONObject().put("error", "profile_response_invalid") }
            if (code !in 200..299 && !result.has("error")) result.put("error", "http_" + code)
            return result
        } finally { c.disconnect() }
    }
    private fun prepared(server: String, response: JSONObject): JSONObject {
        val next = JSONObject(response.toString()).put("server_base", base(server)).put("profiles_enabled", response.optBoolean("profiles_enabled", !response.optBoolean("legacy", false)))
        next.put("ready", response.optBoolean("legacy", false) || response.optString("token").isNotBlank())
        if (response.has("token")) next.put("expires_at", System.currentTimeMillis() + response.optLong("expires_in", 86400) * 1000)
        return next
    }
    // Called on the JavaScript bridge/IO thread, not on the Android main thread.
    fun bootstrap(context: Context, server: String, pin: String = ""): String = synchronized(lock) {
        if (state.optString("server_base") == base(server) && state.optBoolean("ready", false) && (state.optBoolean("legacy",false)||state.optLong("expires_at")>System.currentTimeMillis()+30000)) return state.toString()
        try {
            val request = JSONObject().put("device", device(context)).put("boot", bootId)
            if (pin.isNotEmpty()) request.put("pin", pin)
            val sameProcess = state.optString("server_base") == base(server) && state.optJSONObject("profile") != null
            if (sameProcess && pin.isEmpty() && state.optString("token").isNotBlank()) {
                refresh(server)
                if (state.optBoolean("ready",false)) return state.toString()
            }
            if(sameProcess) request.put("profile_id",state.getJSONObject("profile").getString("id"))
            state = prepared(server, call(server, if(sameProcess) "/home/session/login" else "/home/session/bootstrap", request))
            clearPublicRows(context)
        } catch (_: Exception) {
            state = JSONObject(state.toString()).put("ready", false).put("profiles_enabled", true).put("server_base", base(server)).put("error", "profile_server_unavailable")
        }
        state.toString()
    }
    fun switch(context: Context, server: String, id: String, pin: String = ""): String = synchronized(lock) {
        val old = JSONObject(state.toString())
        if (!old.optBoolean("ready", false)) return JSONObject().put("error", "session_required").toString()
        try {
            val request = JSONObject().put("profile_id", id)
            if (pin.isNotEmpty()) request.put("pin", pin)
            val result = call(server, "/home/session/switch", request, old)
            if (result.optString("token").isNotBlank()) state = prepared(server, result)
            result.toString()
        } catch (_: Exception) { JSONObject().put("error", "profile_server_unavailable").toString() }
    }
    fun refresh(server: String): String = synchronized(lock) {
        if(state.optString("token").isBlank())return state.toString()
        try {
            val response=call(server,"/home/session/renew",JSONObject().put("boot",bootId),state)
            if(response.optString("token").isNotBlank()) state=prepared(server,response)
            else if(response.optString("error")=="session_required") state=JSONObject(state.toString()).put("ready",false).put("error","session_required")
        } catch (_: Exception) { }
        state.toString()
    }
    fun resetServer() = synchronized(lock) { state = JSONObject().put("ready", false).put("profiles_enabled", true) }
    fun ownerFor(server: String): JSONObject? {
        val s = snapshot()
        return if (s.optBoolean("ready", false) && s.optString("server_base") == base(server)) s else null
    }
    fun attachOwner(intent: Intent, server: String) {
        ownerFor(server)?.let { intent.putExtra("home_user_context", it.toString()) }
    }
    fun requestHeaders(owner: JSONObject?): Map<String, String> {
        val token = owner?.optString("token").orEmpty()
        return if (token.isBlank()) emptyMap() else mapOf("X-Home-Session" to token)
    }
}
