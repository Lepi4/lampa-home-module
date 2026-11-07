package top.rootu.lampa.shell

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.drawable.BitmapDrawable
import android.net.ConnectivityManager
import android.os.Build
import android.provider.Settings
import android.util.Base64
import android.util.LruCache
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import top.rootu.lampa.player.DLog
import org.json.JSONArray
import org.json.JSONObject
import top.rootu.lampa.MainActivity
import top.rootu.lampa.McHostApi
import top.rootu.lampa.R
import top.rootu.lampa.helpers.Prefs.appUrl
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.Inet4Address
import java.net.URL
import java.util.UUID
import java.util.concurrent.Executors

// Lampa Media Center: домашний экран-оболочка внутри окна Lampa. Страница из assets лежит поверх WebView Lampa (тот же процесс,
// то же окно, одна задача): Lampa грузится и работает под оболочкой без прозрачных окон, порядок задач больше ни на что не влияет.
// Карточка, плеер, поиск открываются в самой Lampa: оболочка просто скрывается, «Назад»/выход возвращают её.
class ShellHost(private val a: MainActivity) : McHostApi {
    private val appUpdater by lazy { ServerAppUpdater(a) { shown } }
    private lateinit var web: WebView
    private val pool = Executors.newFixedThreadPool(48)
    private val iconCache = LruCache<String, String>(200)
    private val ui = android.os.Handler(android.os.Looper.getMainLooper())
    private var pausedAt = 0L
    private var lastRestartAt = 0L
    private var destroyed = false
    override var shown = true
        private set

    @SuppressLint("SetJavaScriptEnabled")
    override fun attach(startHidden: Boolean) {
        // Документ стартует вместе с оболочкой: watchdog восстановления должен видеть startedAt,
        // даже если корень Lampa не загрузился (сервер недоступен на холодном старте) — иначе
        // needsRecovery() не срабатывает и приложение не восстанавливается до ручного перезапуска.
        a.mcBeginDocument()
        web = WebView(a)
        web.setBackgroundColor(Color.BLACK)
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = true
            @Suppress("DEPRECATION") allowUniversalAccessFromFileURLs = true
            @Suppress("DEPRECATION") allowFileAccessFromFileURLs = true
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            cacheMode = WebSettings.LOAD_DEFAULT
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) web.setRendererPriorityPolicy(WebView.RENDERER_PRIORITY_IMPORTANT, false)
        web.settings.userAgentString = web.settings.userAgentString + " McShell"     // сервер по этой метке отличает оболочку от чужих страниц
        web.webChromeClient = WebChromeClient()
        web.webViewClient = WebViewClient()
        web.addJavascriptInterface(Bridge(), "MC")
        a.addContentView(web, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        web.loadUrl("file:///android_asset/shell/index.html")
        if (startHidden) hide() else show()
    }

    private var hiddenAt = 0L

    override fun show() {
        MainActivity.mcShellHidden = false
        MainActivity.mcRestoreLast = false
        a.runVoidJsFunc("(function(){try{localStorage.removeItem('mc_last_activity')}catch(e){}})()", "")
        ui.post {
            val was = shown
            shown = true
            web.visibility = View.VISIBLE
            web.requestFocus()
            try { web.resumeTimers() } catch (_: Exception) {}
            // вернулись на оболочку после Lampa/плеера: пусть обновит ленты («Продолжить», «Мои сериалы»)
            if (!was && hiddenAt > 0) js("window.__mc && window.__mc.resume && window.__mc.resume(${(android.os.SystemClock.elapsedRealtime() - hiddenAt) / 1000})")
        }
    }

    override fun hide() {
        MainActivity.mcHiddenAt = android.os.SystemClock.elapsedRealtime()
        MainActivity.mcShellHidden = true
        hiddenAt = MainActivity.mcHiddenAt
        ui.post {
            shown = false
            web.visibility = View.GONE
            try { a.findViewById<View>(R.id.webView)?.requestFocus() } catch (_: Exception) {}
        }
    }

    override fun onHome() { show(); js("window.__mc && window.__mc.home && window.__mc.home()") }

    // A recovery decision belongs to the current document and a fresh health
    // response, never to a cached success from before the profile/server change.
    private val probing = java.util.concurrent.atomic.AtomicBoolean(false)
    private var probeDelay = 15_000L
    @Volatile private var nextProbeAt = 0L

    private fun probeServer(now: Long, generation: Long) {
        if (now < nextProbeAt || !probing.compareAndSet(false, true)) return
        val url = a.appUrl
        Thread {
            var ok = false
            try {
                val c = java.net.URL(url.trimEnd('/') + "/home/health").openConnection() as java.net.HttpURLConnection
                c.connectTimeout = 4000; c.readTimeout = 4000; c.requestMethod = "GET"
                try {
                    if (c.responseCode == 200) {
                        val health = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                        ok = health.optBoolean("ok") && health.optString("module") == "HomeMedia"
                    }
                } finally { c.disconnect() }
            } catch (_: Exception) {}
            ui.post {
                probing.set(false)
                if (destroyed || generation != MainActivity.mcDocument.generation) return@post
                if (ok) probeDelay = 15_000L else probeDelay = (probeDelay * 2).coerceAtMost(120_000L)
                val checkedAt = android.os.SystemClock.elapsedRealtime()
                nextProbeAt = checkedAt + probeDelay
                if (ok && checkedAt - lastRestartAt > 60_000L && MainActivity.mcDocument.acceptsRecovery(generation, checkedAt)) {
                    val reason = if (MainActivity.mcReady) "no_pong" else "not_ready"
                    DLog.log("w", "watchdog_restart", reason, "fresh health; document=$generation")
                    DLog.flush()
                    Bridge().restartLampa()
                } else if (!ok) {
                    DLog.log("w", "watchdog_hold", "server_down", "document=$generation")
                }
            }
        }.apply { isDaemon = true }.start()
    }

    private val watchdog = object : Runnable {
        override fun run() {
            try {
                val now = android.os.SystemClock.elapsedRealtime()
                if (!destroyed && now - lastRestartAt > 60_000 && MainActivity.mcDocument.needsRecovery(now))
                    probeServer(now, MainActivity.mcDocument.generation)
            } catch (_: Exception) {}
            ui.postDelayed(this, 15_000)
        }
    }

    override fun onResume() {
        appUpdater.resume()
        val away = if (pausedAt > 0) (android.os.SystemClock.elapsedRealtime() - pausedAt) / 1000 else 0L
        try { web.resumeTimers() } catch (_: Exception) {}
        ui.removeCallbacks(watchdog); ui.postDelayed(watchdog, 20_000)
        if (shown) js("window.__mc && window.__mc.resume && window.__mc.resume($away)")
    }

    override fun onPause() {
        appUpdater.pause()
        try { DLog.flush() } catch (_: Exception) {}
        pausedAt = android.os.SystemClock.elapsedRealtime()
        ui.removeCallbacks(watchdog)
    }

    override fun onResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (requestCode == 7702 || requestCode == 7703) { appUpdater.result(requestCode); return }
        if (requestCode == 7701) {
            val t = if (resultCode == android.app.Activity.RESULT_OK) data?.getStringArrayListExtra(android.speech.RecognizerIntent.EXTRA_RESULTS)?.firstOrNull() ?: "" else ""
            js("window.__mc && window.__mc.onVoice && window.__mc.onVoice(${JSONObject.quote(t)})")
        }
    }

    override fun back() { a.mcCancelNavigation();js("window.__mc && window.__mc.back && window.__mc.back()") }

    override fun dispatchKey(event: KeyEvent): Boolean = web.dispatchKeyEvent(event)

    override fun destroy() {
        destroyed = true
        appUpdater.destroy()
        ui.removeCallbacks(watchdog)
        pool.shutdownNow()
        try { web.destroy() } catch (_: Exception) {}
    }

    private fun js(code: String) {
        a.runOnUiThread { try { web.evaluateJavascript(code, null) } catch (_: Exception) {} }
    }

    inner class Bridge {
        @JavascriptInterface fun profileBoot(pin: String): String {
            val previous = top.rootu.lampa.ProfileRuntime.snapshot()
            val result = top.rootu.lampa.ProfileRuntime.bootstrap(a, a.appUrl, pin)
            val next=top.rootu.lampa.ProfileRuntime.snapshot()
            if (next.optBoolean("ready", false)&&(!previous.optBoolean("ready",false)||previous.optString("token")!=next.optString("token"))) a.mcUserChanged()
            return result
        }
        @JavascriptInterface fun profileRefresh(): String = top.rootu.lampa.ProfileRuntime.refresh(a.appUrl)
        private val refreshing=java.util.concurrent.atomic.AtomicBoolean(false)
        @JavascriptInterface fun profileRefreshAsync(generation:Int) {
            if(!refreshing.compareAndSet(false,true))return
            pool.execute {
                try { val next=top.rootu.lampa.ProfileRuntime.refresh(a.appUrl)
                    js("window.__mc&&window.__mc.profileRefreshed&&window.__mc.profileRefreshed($next,$generation)")
                } finally {refreshing.set(false)}
            }
        }
        @JavascriptInterface fun profileContext(): String = top.rootu.lampa.ProfileRuntime.contextJson()
        @JavascriptInterface fun profileSwitch(id: String, pin: String): String {
            val result = top.rootu.lampa.ProfileRuntime.switch(a, a.appUrl, id, pin)
            if (try { JSONObject(result).optString("token").isNotBlank() } catch (_: Exception) { false }) a.mcUserChanged()
            return result
        }

        @JavascriptInterface fun server(): String = a.appUrl
        @JavascriptInterface fun appVersion(): String =
            top.rootu.lampa.BuildConfig.VERSION_NAME + " / " + top.rootu.lampa.BuildConfig.VERSION_CODE
        @JavascriptInterface fun setAfrMode(enabled: Boolean) {
            top.rootu.lampa.player.PlayerSettingsStore.acceptAfr(a, enabled)
        }
        @JavascriptInterface fun afrMode(): Boolean = top.rootu.lampa.player.PlayerSettingsStore.afr(a)
        @JavascriptInterface fun chooseAfrMode(enabled: Boolean) = top.rootu.lampa.player.PlayerSettingsStore.chooseAfr(a, enabled)
        @JavascriptInterface fun syncAfrPending() = top.rootu.lampa.player.PlayerSettingsStore.syncAfr(a)
        @JavascriptInterface fun tunneling(): Boolean = a.getSharedPreferences("home_player", Context.MODE_PRIVATE).getBoolean("tunnel", false)
        @JavascriptInterface fun setTunneling(enabled: Boolean) { a.getSharedPreferences("home_player", Context.MODE_PRIVATE).edit().putBoolean("tunnel", enabled).apply() }

        // диагностический журнал оболочки (тот же буфер, что у плеера и страницы Lampa)
        @JavascriptInterface fun dlog(lvl: String, code: String, key: String, x: String) {
            try { DLog.init(a); DLog.setBase(a.appUrl); DLog.log(lvl, code, key, x) } catch (_: Exception) {}
        }

        @JavascriptInterface fun setServer(url: String) { val u = url.trim().trimEnd('/'); if (u != a.appUrl) top.rootu.lampa.ProfileRuntime.resetServer(); a.appUrl = u; a.mcServerChanged(u) }

        // постоянный идентификатор этого устройства для записи на сервере
        @JavascriptInterface fun deviceId(): String {
            val sp = a.getSharedPreferences("mc", Context.MODE_PRIVATE)
            var id = sp.getString("dev", null)
            if (id == null) {
                id = "mc" + UUID.randomUUID().toString().replace("-", "").substring(0, 12)
                sp.edit().putString("dev", id).apply()
            }
            return id!!
        }

        @JavascriptInterface fun deviceName(): String = Build.MODEL ?: "Android"

        // Префиксы /24 всех текущих IPv4, не только активной сети: на первом запуске activeNetwork часто ещё пуст.
        private fun lanPrefixes(): LinkedHashSet<String> {
            val prefixes = LinkedHashSet<String>()
            try {
                val cm = a.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
                val nets = LinkedHashSet<android.net.Network>()
                cm.activeNetwork?.let { nets.add(it) }
                cm.allNetworks?.forEach { nets.add(it) }
                for (n in nets) {
                    cm.getLinkProperties(n)?.linkAddresses?.forEach { la ->
                        val ad = la.address
                        if (ad is Inet4Address && !ad.isLoopbackAddress && !ad.isLinkLocalAddress) {
                            val host = ad.hostAddress ?: return@forEach
                            prefixes.add(host.substringBeforeLast('.'))
                        }
                    }
                }
            } catch (_: Exception) {}
            return prefixes
        }

        // Поиск сервера: адрес из подсказки, затем все адреса своей подсети /24 по порту 9118. Находки приходят по одной.
        // На первой установке сеть часто поднимается на пару секунд позже экрана — без ожидания список адресов пуст и поиск сразу «ничего не нашёл».
        @JavascriptInterface fun discover(hintsJson: String) {
            pool.execute {
                val found = HashSet<String>()
                val hosts = LinkedHashSet<String>()
                try { JSONArray(hintsJson).let { arr -> for (i in 0 until arr.length()) hosts.add(arr.getString(i)) } } catch (_: Exception) {}
                var prefixes = lanPrefixes()
                var waited = 0
                while (prefixes.isEmpty() && waited < 8) {
                    try { Thread.sleep(1000) } catch (_: Exception) {}
                    waited++
                    prefixes = lanPrefixes()
                }
                for (p in prefixes) for (i in 1..254) hosts.add("http://$p.$i:9118")
                val total = hosts.size
                val latch = java.util.concurrent.CountDownLatch(total)
                for (h in hosts) try { pool.execute {
                    try {
                        val base = if (h.startsWith("http")) h else "http://$h"
                        val c = URL("$base/home/whoami").openConnection() as HttpURLConnection
                        try {
                            c.connectTimeout = 800; c.readTimeout = 1200
                            c.setRequestProperty("Accept", "application/json")
                            if (c.responseCode == 200) {
                                val o = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                                if (o.optString("app") == "lampa-home" && found.add(o.optString("id"))) {
                                    o.put("url", base)
                                    js("window.__mc && window.__mc.onFound(${o})")
                                }
                            }
                        } finally { c.disconnect() }
                    } catch (_: Exception) {
                    } finally { latch.countDown() }
                } } catch (_: java.util.concurrent.RejectedExecutionException) { latch.countDown() }
                try { latch.await(40, java.util.concurrent.TimeUnit.SECONDS) } catch (_: Exception) {}
                js("window.__mc && window.__mc.onScanDone(${found.size})")
            }
        }

        // Список приложений: [{p: пакет, l: название}]
        @JavascriptInterface fun apps(): String {
            val pm = a.packageManager
            val out = JSONArray()
            val seen = HashSet<String>()
            fun add(cat: String) {
                val i = Intent(Intent.ACTION_MAIN).addCategory(cat)
                for (ri in pm.queryIntentActivities(i, 0)) {
                    val p = ri.activityInfo.packageName
                    if (p == a.packageName || !seen.add(p)) continue
                    out.put(JSONObject().put("p", p).put("l", ri.loadLabel(pm).toString()))
                }
            }
            add(Intent.CATEGORY_LEANBACK_LAUNCHER)
            add(Intent.CATEGORY_LAUNCHER)
            return out.toString()
        }

        // Иконка приложения: PNG 96 пикселей в base64 (с кэшем)
        @JavascriptInterface fun icon(pkg: String): String {
            iconCache.get(pkg)?.let { return it }
            return try {
                val d = a.packageManager.getApplicationIcon(pkg)
                val bmp = Bitmap.createBitmap(96, 96, Bitmap.Config.ARGB_8888)
                val cv = Canvas(bmp)
                if (d is BitmapDrawable && d.bitmap != null) cv.drawBitmap(Bitmap.createScaledBitmap(d.bitmap, 96, 96, true), 0f, 0f, null)
                else { d.setBounds(0, 0, 96, 96); d.draw(cv) }
                val bo = ByteArrayOutputStream()
                bmp.compress(Bitmap.CompressFormat.PNG, 100, bo)
                val s = Base64.encodeToString(bo.toByteArray(), Base64.NO_WRAP)
                iconCache.put(pkg, s); s
            } catch (_: Exception) { "" }
        }

        @JavascriptInterface fun launch(pkg: String) {
            a.runOnUiThread {
                val i = a.packageManager.getLeanbackLaunchIntentForPackage(pkg) ?: a.packageManager.getLaunchIntentForPackage(pkg)
                if (i != null) try { a.startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: Exception) {}
            }
        }

        // «ТВ»: тот же вход, что у автоматизации (TvLaunchActivity включает эфир на последнем канале)
        @JavascriptInterface fun tv() {
            a.runOnUiThread { try { a.startActivity(Intent("app.lampac.home.action.TV").setPackage(a.packageName)) } catch (_: Exception) {} }
        }

        @JavascriptInterface fun drcMode(): Int =
            a.getSharedPreferences("home_player", Context.MODE_PRIVATE).getInt("drc", 0).coerceIn(0, 2)

        @JavascriptInterface fun audioPassthrough(): Boolean =
            a.getSharedPreferences("home_player", Context.MODE_PRIVATE).getBoolean("audio_passthrough", false)

        @JavascriptInterface fun setAudioPassthrough(enabled: Boolean) {
            a.getSharedPreferences("home_player", Context.MODE_PRIVATE).edit().putBoolean("audio_passthrough", enabled).apply()
        }

        @JavascriptInterface fun setDrcMode(mode: Int): Int {
            val value = mode.coerceIn(0, 2)
            a.getSharedPreferences("home_player", Context.MODE_PRIVATE).edit().putInt("drc", value).apply()
            return value
        }

        @JavascriptInterface fun settings() {
            a.runOnUiThread { try { a.startActivity(Intent(Settings.ACTION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: Exception) {} }
        }

        // Прогрев больше не нужен: Lampa грузится в этом же окне под оболочкой
        @JavascriptInterface fun warm() {}

        // Открыть Lampa. cmd: {} — главная, {id,media,source} — карточка, {q:"текст"} — поиск, {js:"функция", arg:"аргументы"} — вызвать функцию страницы
        @JavascriptInterface fun lampa(cmd: String) {
            a.runOnUiThread {
                val o = try { JSONObject(cmd) } catch (_: Exception) { JSONObject() }
                top.rootu.lampa.TvLaunch.channel = -1   // «Lampa», карточка и поиск не включают эфир: ждущая команда ТВ сбрасывается
                val i = Intent(a, MainActivity::class.java)
                if (o.has("id")) {
                    i.putExtra("id", o.optString("id")); i.putExtra("media", o.optString("media", "movie")); i.putExtra("source", o.optString("source", "tmdb"))
                    // карточка открыта из оболочки: «Назад» вернёт на неё
                    if (!o.has("js")) { i.putExtra("mc_js", "(function(){window.__mc_ret=1})"); i.putExtra("mc_arg", "") }
                }
                if (o.has("q")) i.putExtra(android.app.SearchManager.QUERY, o.optString("q"))
                if (o.has("js")) { i.putExtra("mc_js", o.optString("js")); i.putExtra("mc_arg", o.optString("arg", "")) }
                // запуск ролика/плеера: оболочка остаётся на экране, пока не откроется плеер, и возвращается после него
                if (o.optBoolean("keep")) {MainActivity.mcReturnToShell = true;i.putExtra("mc_keep_shell",true)}
                else if(o.has("id")||o.has("q")||o.has("js"))i.putExtra("mc_hold_shell",true)
                else hide()
                a.mcOpen(i)
            }
        }

        // Перезапуск Lampa (не оболочки): окно пересоздаётся, страница Lampa загружается заново
        @JavascriptInterface fun restartLampa() {
            a.runOnUiThread {
                lastRestartAt = android.os.SystemClock.elapsedRealtime()
                a.mcBeginDocument()
                // пересозданное окно стартует с главной оболочки, а не с последней команды Lampa
                try { a.intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME); a.recreate() } catch (_: Exception) {}
            }
        }

        @JavascriptInterface fun reloadLampa() { a.mcReloadUnderShell() }
        @JavascriptInterface fun cancelNavigation(){a.runOnUiThread{a.mcCancelNavigation()}}

        // Голосовой поиск: системное окно распознавания, результат придёт в window.__mc.onVoice(текст)
        @JavascriptInterface fun voice() {
            a.runOnUiThread {
                try {
                    val i = Intent(android.speech.RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                        .putExtra(android.speech.RecognizerIntent.EXTRA_LANGUAGE_MODEL, android.speech.RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                        .putExtra(android.speech.RecognizerIntent.EXTRA_LANGUAGE, "ru-RU")
                        .putExtra(android.speech.RecognizerIntent.EXTRA_PROMPT, "Что искать?")
                    a.startActivityForResult(i, 7701)
                } catch (_: Exception) { js("window.__mc && window.__mc.onVoice && window.__mc.onVoice('')") }
            }
        }

        // Lampa загружена и готова принять команду (карточка откроется без заставки)
        @JavascriptInterface fun lampaReady(): Boolean = MainActivity.mcReady

        @JavascriptInterface fun log(msg: String) { android.util.Log.i("MediaCenter", msg) }
    }
}
