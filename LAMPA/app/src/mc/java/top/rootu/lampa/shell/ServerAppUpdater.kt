package top.rootu.lampa.shell

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import androidx.appcompat.app.AlertDialog
import androidx.core.content.FileProvider
import org.json.JSONObject
import top.rootu.lampa.BuildConfig
import top.rootu.lampa.MainActivity
import top.rootu.lampa.ServerUpdatePolicy
import top.rootu.lampa.helpers.Prefs.appUrl
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

class ServerAppUpdater(private val a: MainActivity, private val mayShow: () -> Boolean) {
    private val ui = Handler(Looper.getMainLooper())
    private val policy = ServerUpdatePolicy()
    private var foreground = false
    private var destroyed = false
    private var checking = false
    private var downloading = false
    private var dialog: AlertDialog? = null
    private var pending: File? = null
    private var offered: JSONObject? = null
    private var waitingPermission = false
    private var installing = false
    private var installerWasPaused = false
    private var permissionWasPaused = false
    private var restorePending = false
    private val saved = a.getSharedPreferences("server-app-update", Context.MODE_PRIVATE)
    private val tick = object : Runnable {
        override fun run() {
            if (foreground && !destroyed) {
                checkServer()
                ui.postDelayed(this, 60_000L)
            }
        }
    }
    init {
        cleanup(a)
        try {
            val j = JSONObject(saved.getString("pending", "") ?: "")
            val file = File(a.cacheDir,"server-updates/lmpc-${j.getInt("version_code")}.apk")
            if (j.getInt("version_code") > BuildConfig.VERSION_CODE && file.exists() && saved.getString("phase","")=="permission" &&
                saved.getString("server","")==a.appUrl.trimEnd('/') && (Build.VERSION.SDK_INT < 26 || a.packageManager.canRequestPackageInstalls()) &&
                System.currentTimeMillis() - saved.getLong("at",0) in 0..600_000L) {
                pending = file; offered = j; restorePending = true
            } else saved.edit().clear().apply()
        } catch (_: Exception) { saved.edit().clear().apply() }
    }
    fun resume() {
        foreground = true
        if (installing && installerWasPaused) { clearPending(); offered?.let { decline(it) } }
        if (restorePending) {
            restorePending = false
            val file = pending; val j = offered
            if (file != null && j != null) Thread {
                var valid = false
                try { validateApk(file,j); valid = true } catch (_: Exception) {}
                ui.post { if (destroyed) return@post; if (valid) { if (foreground) install() else restorePending = true } else clearPending() }
            }.apply { isDaemon = true }.start()
        } else if (waitingPermission && permissionWasPaused) {
            if (Build.VERSION.SDK_INT < 26 || a.packageManager.canRequestPackageInstalls()) install()
            else { clearPending(); offered?.let { decline(it) } }
        }
        ui.removeCallbacks(tick); ui.post(tick)
    }
    fun pause() { if (installing) installerWasPaused = true; if (waitingPermission) permissionWasPaused = true; foreground = false; ui.removeCallbacks(tick) }
    fun destroy() { destroyed = true; pause(); dialog?.dismiss(); dialog = null }
    private fun decline(j: JSONObject) = policy.decline(j.optInt("version_code"), SystemClock.elapsedRealtime())
    private fun checkServer() {
        if (checking || downloading || pending != null || dialog != null || !mayShow() || a.appUrl.isBlank()) return
        checking = true
        val server = a.appUrl.trimEnd('/')
        Thread {
            var latest: JSONObject? = null
            try {
                val c = URL("$server/home/app-updates/latest").openConnection() as HttpURLConnection
                c.connectTimeout = 4000; c.readTimeout = 5000
                try { if (c.responseCode == 200) latest = JSONObject(c.inputStream.bufferedReader().use { it.readText() }) } finally { c.disconnect() }
            } catch (_: Exception) {}
            ui.post {
                checking = false
                val j = latest ?: return@post
                if (destroyed || !foreground || !mayShow() || server != a.appUrl.trimEnd('/')) return@post
                if (!j.optBoolean("available") || j.optString("package") != a.packageName ||
                    !policy.shouldOffer(BuildConfig.VERSION_CODE, j.optInt("version_code"), SystemClock.elapsedRealtime())) return@post
                offered = j
                dialog = AlertDialog.Builder(a).setTitle("Найдена новая версия")
                    .setMessage(j.optString("version_name"))
                    .setPositiveButton("ОК") { _, _ -> dialog = null; download(j, server) }
                    .setNegativeButton("ОТМЕНА") { _, _ -> decline(j); dialog = null }
                    .setOnCancelListener { decline(j); dialog = null }.create()
                dialog?.show()
            }
        }.apply { isDaemon = true }.start()
    }
    @Suppress("DEPRECATION")
    private fun validateApk(file: File, j: JSONObject) {
        check(file.length() == j.getLong("bytes"))
        val hash = file.inputStream().use { input ->
            val md = MessageDigest.getInstance("SHA-256"); val buffer = ByteArray(65536)
            while (true) { val n = input.read(buffer); if (n < 0) break; md.update(buffer, 0, n) }; md.digest().joinToString("") { "%02x".format(it) }
        }
        check(hash.equals(j.getString("sha256"), true))
        val flags = android.content.pm.PackageManager.GET_SIGNATURES
        val apk = a.packageManager.getPackageArchiveInfo(file.absolutePath, flags) ?: error("Invalid APK")
        val current = a.packageManager.getPackageInfo(a.packageName, flags)
        check(apk.packageName == a.packageName && apk.versionCode == j.getInt("version_code") && apk.versionCode > BuildConfig.VERSION_CODE)
        val signed = apk.signatures?.map { it.toCharsString() }?.toSet()
        check(!signed.isNullOrEmpty() && signed == current.signatures?.map { it.toCharsString() }?.toSet())
    }
    private fun download(j: JSONObject, server: String) {
        if (downloading) return
        val name = j.optString("file")
        if (!Regex("LMPC-[a-zA-Z0-9.-]+-\\d+\\.apk").matches(name)) return
        downloading = true
        android.widget.Toast.makeText(a, "Загружаю обновление…", android.widget.Toast.LENGTH_SHORT).show()
        Thread {
            val dir = File(a.cacheDir, "server-updates").apply { mkdirs() }
            val file = File(dir, "lmpc-${j.optInt("version_code")}.apk")
            var success = false
            try {
                val expected = j.getLong("bytes"); check(expected in 1..150_000_000L)
                val c = URL("$server/home/app-updates/apk/$name").openConnection() as HttpURLConnection
                c.connectTimeout = 5000; c.readTimeout = 15000
                try {
                    check(c.responseCode == 200)
                    c.inputStream.use { input -> file.outputStream().use { output ->
                        val buffer = ByteArray(65536); var total = 0L
                        while (true) { val n = input.read(buffer); if (n < 0) break; total += n; check(total <= expected); output.write(buffer, 0, n) }
                    } }
                } finally { c.disconnect() }
                validateApk(file, j); success = true
            } catch (_: Exception) { file.delete() }
            ui.post {
                downloading = false
                if (destroyed || !foreground || !mayShow() || server != a.appUrl.trimEnd('/')) { file.delete(); return@post }
                if (!success) { decline(j); android.widget.Toast.makeText(a,"Не удалось загрузить или проверить обновление",android.widget.Toast.LENGTH_LONG).show(); return@post }
                pending = file
                saved.edit().putString("pending",j.toString()).putLong("at",System.currentTimeMillis()).putString("server",server).apply()
                install()
            }
        }.apply { isDaemon = true }.start()
    }
    private fun install() {
        if (installing) return
        val file = pending ?: return
        try {
            if (Build.VERSION.SDK_INT >= 26 && !a.packageManager.canRequestPackageInstalls()) {
                waitingPermission = true
                permissionWasPaused = false
                saved.edit().putString("phase","permission").apply()
                a.startActivityForResult(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${a.packageName}")), 7703)
                return
            }
            waitingPermission = false
            val uri = FileProvider.getUriForFile(a, a.packageName + ".update_provider", file)
            installing = true
            installerWasPaused = false
            saved.edit().putString("phase","installing").apply()
            a.startActivityForResult(Intent(Intent.ACTION_INSTALL_PACKAGE).setData(uri)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION).putExtra(Intent.EXTRA_RETURN_RESULT, true), 7702)
        } catch (_: Exception) { clearPending(); offered?.let { decline(it) } }
    }
    private fun clearPending() { pending?.delete(); pending = null; waitingPermission = false; installing = false; installerWasPaused = false; permissionWasPaused = false; saved.edit().clear().apply() }
    fun result(request: Int) {
        if (request == 7703) {
            if (Build.VERSION.SDK_INT < 26 || a.packageManager.canRequestPackageInstalls()) install()
            else { clearPending(); offered?.let { decline(it) } }
        } else if (request == 7702) { clearPending(); offered?.let { decline(it) } }
    }
    companion object {
        fun cleanup(context: Context) {
            File(context.cacheDir,"server-updates").listFiles()?.forEach { file ->
                val version = Regex("lmpc-(\\d+)\\.apk").matchEntire(file.name)?.groupValues?.get(1)?.toIntOrNull()
                if (version != null && (version <= BuildConfig.VERSION_CODE || System.currentTimeMillis() - file.lastModified() > 14L*24*60*60*1000) && file.delete())
                    android.util.Log.i("MediaCenterUpdate", "removed cached APK $version")
            }
        }
    }
}
class ServerUpdateCleanupReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) { ServerAppUpdater.cleanup(context) }
}
