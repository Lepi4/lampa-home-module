package top.rootu.lampa

import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.SharedPreferences
import android.os.Build
import android.util.Log
import android.webkit.JavascriptInterface
import androidx.annotation.RequiresApi
import androidx.core.content.edit
import androidx.core.net.toUri
import com.google.gson.Gson
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONException
import org.json.JSONObject
import java.net.URI
import java.util.Locale
import top.rootu.lampa.browser.Browser
import top.rootu.lampa.channels.LampaChannels
import top.rootu.lampa.channels.PluginChannels
import top.rootu.lampa.channels.LampaChannels.updateChanByName
import top.rootu.lampa.channels.WatchNext.updateWatchNext
import top.rootu.lampa.content.LampaProvider
import top.rootu.lampa.helpers.Helpers.debugLog
import top.rootu.lampa.helpers.Helpers.filterValidCubBookmarks
import top.rootu.lampa.helpers.Helpers.isTvContentProviderAvailable
import top.rootu.lampa.helpers.Helpers.isValidJson
import top.rootu.lampa.helpers.Prefs.appUrl
import top.rootu.lampa.helpers.Prefs.appLang
import top.rootu.lampa.helpers.Prefs.lampaSource
import top.rootu.lampa.helpers.Prefs.saveAccountBookmarks
import top.rootu.lampa.helpers.Prefs.saveFavorite
import top.rootu.lampa.helpers.Prefs.saveRecs
import top.rootu.lampa.helpers.Prefs.storagePrefs
import top.rootu.lampa.helpers.Prefs.syncEnabled
import top.rootu.lampa.helpers.Prefs.tmdbApiUrl
import top.rootu.lampa.helpers.Prefs.tmdbImgUrl
import top.rootu.lampa.helpers.AppAttestation
import top.rootu.lampa.net.Http
import top.rootu.lampa.recs.RecsService
import top.rootu.lampa.tmdb.TMDB
import kotlin.system.exitProcess

class AndroidJS(private val mainActivity: MainActivity, private val browser: Browser) {

    private val store: SharedPreferences get() = App.context.storagePrefs

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun homeProfileBootstrap(): String = ProfileRuntime.bootstrap(mainActivity, mainActivity.appUrl)

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun homeProfileContext(): String = ProfileRuntime.contextJson()

    // Local properties
    private var lastEventHash: String? = null
    private var reqResponse: MutableMap<String, String> = HashMap()
    private var keys: Array<String?>? = null
    private var values: Array<String?>? = null
    private var dumped = false

    companion object {
        // Constants
        private const val TAG = "AndroidJS"
        private const val UPDATE_DELAY = 5000L // in ms, wait before update TV channel
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun storageChange(json: String?) {
        val hash = json.hashCode().toString()
        if (hash == lastEventHash) {
            debugLog(TAG, "Ignoring duplicate storage change event: $json")
            return
        }
        lastEventHash = hash
        json?.let {
            val eo: JSONObject = if (json == "\"\"") {
                JSONObject()
            } else {
                JSONObject(json)
            }
            if (!eo.has("name") || !eo.has("value")) return

            when (eo.optString("name")) {
                "activity" -> {
                    MainActivity.lampaActivity = eo.optString("value", "{}")
                    debugLog(TAG, "lampaActivity stored: ${MainActivity.lampaActivity}")
                }

                "player_timecode" -> {
                    MainActivity.playerTimeCode = eo.optString("value", MainActivity.playerTimeCode)
                    debugLog(TAG, "playerTimeCode stored: ${MainActivity.playerTimeCode}")
                }

                "playlist_next" -> {
                    MainActivity.playerAutoNext = eo.optString("value", "true") == "true"
                    debugLog(TAG, "playerAutoNext stored: ${MainActivity.playerAutoNext}")
                }

                "language" -> {
                    val newLang = eo.optString("value", "ru")
                    if (newLang != "undefined" && mainActivity.appLang != newLang) {
                        App.setAppLanguage(mainActivity, newLang)
                        // mainActivity.appLang = newLang
                        // mainActivity.runOnUiThread { mainActivity.recreate() }
                        debugLog(TAG, "language changed to $newLang")
                    } else {
                        debugLog(TAG, "language not changed [${mainActivity.appLang}]")
                    }
                }

                "source" -> {
                    mainActivity.lampaSource = eo.optString("value", mainActivity.lampaSource)
                    debugLog(TAG, "lampaSource stored: ${mainActivity.lampaSource}")
                }

                "protocol" -> {
                    if (MainActivity.proxyTmdbEnabled) {
                        debugLog(TAG, "protocol changed. run getLampaTmdbUrls()")
                        mainActivity.getLampaTmdbUrls()
                    } else {
                        debugLog(TAG, "protocol changed. TMDB Proxy disabled, nothing to do.")
                    }
                }

                "proxy_tmdb" -> {
                    val newState = eo.optString("value", "true") == "true"
                    MainActivity.proxyTmdbEnabled = newState
                    debugLog(TAG, "proxyTmdbEnabled set to $newState")
                    if (MainActivity.proxyTmdbEnabled) {
                        mainActivity.getLampaTmdbUrls()
                    } else {
                        // store defaults to prefs
                        debugLog(TAG, "Apply default TMDB URLs")
                        mainActivity.tmdbApiUrl = TMDB.APIURL
                        mainActivity.tmdbImgUrl = TMDB.IMGURL
                    }
                }

                "baseUrlApiTMDB" -> {
                    val newUrl = eo.optString("value", TMDB.APIURL)
                    if (newUrl.startsWith("http", true) &&
                        !newUrl.contains(mainActivity.tmdbApiUrl, true)
                    ) {
                        mainActivity.tmdbApiUrl = newUrl
                        debugLog(
                            TAG,
                            "baseUrlApiTMDB changed, tmdbApiUrl ${mainActivity.tmdbApiUrl}"
                        )
                    } else {
                        debugLog(
                            TAG,
                            "baseUrlApiTMDB changed, tmdbApiUrl not changed: ${mainActivity.tmdbApiUrl}"
                        )
                    }
                }

                "baseUrlImageTMDB" -> {
                    val newUrl = eo.optString("value", TMDB.IMGURL)
                    if (newUrl.startsWith("http", true) &&
                        !newUrl.contains(mainActivity.tmdbImgUrl, true)
                    ) {
                        mainActivity.tmdbImgUrl = newUrl
                        debugLog(
                            TAG,
                            "baseUrlImageTMDB changed, tmdbImgUrl ${mainActivity.tmdbImgUrl}"
                        )
                    } else {
                        debugLog(
                            TAG,
                            "baseUrlImageTMDB changed, tmdbImgUrl not changed: ${mainActivity.tmdbImgUrl}"
                        )
                    }
                }

                "favorite" -> {
                    val json = eo.optString("value", "")
                    if (isValidJson(json)) {
                        mainActivity.saveFavorite(json)
                        debugLog(TAG, "favorite JSON saved to prefs")
                    } else {
                        Log.e(TAG, "Not valid JSON in favorite")
                    }
                }

                "account_use" -> {
                    val use = eo.optBoolean("value", false)
                    debugLog(TAG, "set syncEnabled $use")
                    mainActivity.syncEnabled = use
                }

                "recomends_list" -> {
                    val json = eo.optString("value", "")
                    if (isValidJson(json)) {
                        mainActivity.saveRecs(json)
                        debugLog(TAG, "recomends_list JSON saved to prefs")
                    } else {
                        Log.e(TAG, "Not valid JSON in recomends_list")
                    }
                }

                else -> { // no op
                }
            }
        }
    }


    // Lampa Media Center: общий с оболочкой идентификатор устройства (в обычной сборке пусто)
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun mcDeviceId(): String = try { mainActivity.getSharedPreferences("mc", android.content.Context.MODE_PRIVATE).getString("dev", "") ?: "" } catch (_: Exception) { "" }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun appVersion(): String {
        return BuildConfig.VERSION_NAME + "-" + BuildConfig.VERSION_CODE
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun exit() {
        try {
            mainActivity.runOnUiThread { mainActivity.appExit() }
        } catch (_: Exception) {
            exitProcess(1)
        }
    }

    // Отметки просмотра со страницы (kinopub.js) попадают в ту же устойчивую очередь, что и отчёты плеера: повторы, срок хранения, подтверждение по событию.
    // true — действие сохранено на диске.
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun tlEnqueue(json: String): Boolean {
        return try {
            top.rootu.lampa.player.ProgressOutbox.init(mainActivity)
            top.rootu.lampa.player.ProgressOutbox.enqueue(MainActivity.LAMPA_URL, org.json.JSONArray(json))
        } catch (_: Exception) { false }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun tlPendingOwned(context: String): String {
        return try {
            val owner=JSONObject(context)
            val current=top.rootu.lampa.ProfileRuntime.snapshot()
            val base=MainActivity.LAMPA_URL.trimEnd('/')
            if(owner.optString("server_base").trimEnd('/')!=base ||
                owner.optJSONObject("profile")?.optString("id")!=current.optJSONObject("profile")?.optString("id") ||
                owner.optString("token")!=current.optString("token"))
                return JSONObject().put("error","owner_changed").toString()
            top.rootu.lampa.player.ProgressOutbox.init(mainActivity)
            val profile=owner.optJSONObject("profile")?.optString("id").orEmpty().ifBlank{"u_default"}
            val pending=top.rootu.lampa.player.ProgressOutbox.pendingFor(base,profile)
            if(pending)top.rootu.lampa.player.ProgressOutbox.flush(base)
            JSONObject().put("pending",pending).toString()
        } catch (_: Exception) {JSONObject().put("error","queue_unavailable").toString()}
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun tlEnqueueOwned(json: String, context: String): Boolean {
        return try {
            val owner=org.json.JSONObject(context)
            if(owner.optString("server_base").trimEnd('/')!=MainActivity.LAMPA_URL.trimEnd('/'))return false
            top.rootu.lampa.player.ProgressOutbox.init(mainActivity)
            top.rootu.lampa.player.ProgressOutbox.enqueue(MainActivity.LAMPA_URL, org.json.JSONArray(json), owner)
        } catch (_: Exception) { false }
    }
    // Диагностический журнал: страница сообщает переходы состояния (lvl i|w|e); журнал ограничен по сроку и объёму, см. DLog
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun dlog(lvl: String, code: String, key: String, x: String) {
        try { top.rootu.lampa.player.ProgressOutbox.init(mainActivity); top.rootu.lampa.player.DLog.setBase(MainActivity.LAMPA_URL); top.rootu.lampa.player.DLog.log(lvl, code, key, x) } catch (_: Exception) {}
    }

    // Lampa Media Center: страница сообщает выбранные в Lampa типы плеера (inner / android), чтобы решение «встроенный или внешний» принималось по ним
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun mcPlayerPrefs(player: String, torrent: String) {
        MainActivity.mcPrefPlayer = player
        MainActivity.mcPrefTorrent = torrent
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun mcSetAfr(enabled: Boolean) {
        top.rootu.lampa.player.PlayerSettingsStore.acceptAfr(mainActivity, enabled)
    }
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun mcChooseAfr(enabled: Boolean) = top.rootu.lampa.player.PlayerSettingsStore.chooseAfr(mainActivity, enabled)
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun mcGetAfr(): Boolean = mainActivity.getSharedPreferences("home_player", android.content.Context.MODE_PRIVATE).getBoolean("afr", true)

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Throws(JSONException::class)
    fun openTorrentLink(url: String, jsonString: String): Boolean {
        val jsonData = if (jsonString == "\"\"") JSONObject() else JSONObject(jsonString)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            if (url.startsWith("magnet", ignoreCase = true)) {
                data = url.toUri()
            } else {
                setDataAndType(url.toUri(), "application/x-bittorrent")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            addCategory(Intent.CATEGORY_BROWSABLE)

            jsonData.optString("title").takeIf { it.isNotEmpty() }?.let { title ->
                putExtra("title", title)
                putExtra("displayName", title)
                putExtra("forcename", title)
            }

            jsonData.optString("poster").takeIf { it.isNotEmpty() }?.let { poster ->
                putExtra("poster", poster)
            }

            jsonData.optString("media").takeIf { it.isNotEmpty() }?.let { category ->
                putExtra("category", category)
            }

            jsonData.optJSONObject("data")?.let { dataObj ->
                putExtra("data", dataObj.toString())
            }
        }

        mainActivity.runOnUiThread {
            try {
                mainActivity.startActivity(intent)
            } catch (e: Exception) {
                Log.e(TAG, "Failed to open torrent link", e)
                App.toast(R.string.no_torrent_activity_found, true)
            }
        }
        // Force update Recs to filter viewed
        CoroutineScope(Dispatchers.Default).launch {
            delay(UPDATE_DELAY)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                LampaChannels.updateRecsChannel()
            } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                RecsService.updateRecs()
            }
        }

        return true
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun openYoutube(str: String) {
        val intent = Intent(
            Intent.ACTION_VIEW,
            "https://www.youtube.com/watch?v=$str".toUri()
        )
        mainActivity.runOnUiThread {
            try {
                mainActivity.startActivity(intent)
            } catch (e: Exception) {
                Log.e(TAG, e.message, e)
                App.toast(R.string.no_youtube_activity_found, true)
            }
        }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun openBrowser(url: String) {
        val intent = Intent(Intent.ACTION_VIEW, url.toUri())
        mainActivity.runOnUiThread {
            try {
                mainActivity.startActivity(intent)
            } catch (e: ActivityNotFoundException) {
                Log.e(TAG, "No browser found: ${e.message}")
                App.toast(R.string.no_activity_found, true)
            } catch (e: Exception) {
                Log.e(TAG, "Browser launch failed: ${e.message}", e)
                App.toast(R.string.generic_error, true)
            }
        }
    }

    // Установленные программы с ярлыком запуска (ТВ-приложения и обычные): [{p: пакет, n: название}]
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun getInstalledApps(): String {
        val pm = mainActivity.packageManager
        val out = org.json.JSONArray()
        val seen = HashSet<String>()
        for (cat in listOf(Intent.CATEGORY_LEANBACK_LAUNCHER, Intent.CATEGORY_LAUNCHER)) {
            val list = try { pm.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(cat), 0) } catch (_: Exception) { emptyList() }
            for (ri in list) {
                val pkg = ri.activityInfo.packageName
                if (pkg == mainActivity.packageName || !seen.add(pkg)) continue
                out.put(org.json.JSONObject().put("p", pkg).put("n", ri.loadLabel(pm).toString()))
            }
        }
        return out.toString()
    }

    // Дежурный напоминаний «включить передачу»: живёт в приложении, пока оно запущено (страница на паузе под плеером)
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun tvRemindStart(base: String, dev: String): Boolean {
        RemindWatcher.start(mainActivity, base, dev)
        return true
    }

    // Окно ввода адреса сервера (доступно и из настроек Lampa, запасной вариант на случай смены адреса)
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun showServerDialog(): Boolean {
        mainActivity.runOnUiThread { mainActivity.showUrlInputDialog() }
        return true
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun getServerUrl(): String = top.rootu.lampa.MainActivity.LAMPA_URL

    // Команда «включить ТВ» от точки входа TvLaunchActivity: посмотреть (не забирая) и забрать; -1 — команды нет
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun tvLaunchPeek(): Int = TvLaunch.channel

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun tvLaunchTake(): Int { val c = TvLaunch.channel; TvLaunch.channel = -1; return c }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun wakeMark(stage: String) { if (stage == "launch") WakeTrace.mark("launch") }

    // Значок программы как data:image/png (для пункта меню)
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun getAppIcon(pkg: String): String {
        return try {
            val dr = mainActivity.packageManager.getApplicationIcon(pkg)
            val bmp = android.graphics.Bitmap.createBitmap(96, 96, android.graphics.Bitmap.Config.ARGB_8888)
            val cv = android.graphics.Canvas(bmp)
            dr.setBounds(0, 0, 96, 96)
            dr.draw(cv)
            val bos = java.io.ByteArrayOutputStream()
            bmp.compress(android.graphics.Bitmap.CompressFormat.PNG, 90, bos)
            "data:image/png;base64," + android.util.Base64.encodeToString(bos.toByteArray(), android.util.Base64.NO_WRAP)
        } catch (_: Exception) { "" }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun launchApp(pkg: String): Boolean {
        val pm = mainActivity.packageManager
        val intent = pm.getLeanbackLaunchIntentForPackage(pkg) ?: pm.getLaunchIntentForPackage(pkg) ?: return false
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        mainActivity.runOnUiThread {
            try { mainActivity.startActivity(intent) } catch (e: Exception) { Log.e(TAG, "launchApp: ${e.message}") }
        }
        return true
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun clearDefaultPlayer() {
        mainActivity.runOnUiThread {
            mainActivity.setPlayerPackage("", false)
            mainActivity.setPlayerPackage("", true)
            App.toast(R.string.select_player_reset)
        }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun httpReq(str: String, returnI: Int) {
        debugLog(TAG, "httpReq JSON $str")
        try {
            val jsonObject = JSONObject(str)
            Http.disableH2(jsonObject.optBoolean("disableH2", false))

            // Extract all parameters upfront
            val url = jsonObject.optString("url").takeIf { it.isNotEmpty() }
                ?: throw IllegalArgumentException("URL cannot be empty")
            val data = jsonObject.opt("post_data")
            val returnHeaders = jsonObject.optBoolean("returnHeaders", false)
             // Optional dataType: "base64" forces binary-safe response encoding.
            val dataType = jsonObject.optString("dataType").takeIf { it.isNotEmpty() }
            val timeout = jsonObject.optInt("timeout", 15000).coerceAtLeast(1000)
            val headers = jsonObject.optJSONObject("headers")?.let {
                JSONObject(it.toString()) // Create a copy to avoid modifying the original
            } ?: JSONObject()

            var contentType = jsonObject.optString("contentType")
            var requestContent = ""

            if (data != null) {
                if (data is String) {
                    requestContent = data.toString()
                    contentType = try {
                        JSONObject(requestContent)
                        "application/json"
                    } catch (_: JSONException) {
                        "application/x-www-form-urlencoded"
                    }
                } else if (data is JSONObject) {
                    contentType = "application/json"
                    requestContent = data.toString()
                }
            }
            // Ensure Content-Type header is properly set
            if (requestContent.isNotEmpty()) {
                if (!headers.has("Content-Type")) {
                    if (headers.has("Content-type")) {
                        contentType = headers.optString("Content-type", contentType)
                        headers.remove("Content-type")
                    }
                    if (headers.has("content-type")) {
                        contentType = headers.optString("content-type", contentType)
                        headers.remove("content-type")
                    }
                    headers.putSafe("Content-Type", contentType)
                }
            }
            injectLampaClientHeaders(url, headers)
            val finalRequestContent = requestContent
            val finalHeaders = headers

            class LampaAsyncTask : AsyncTask<Void?, Int?, String>("LampaAsyncTask") {
                override fun doInBackground(vararg params: Void?): String {
                    val http = Http()
                    return try {
                        val responseJSON = if (finalRequestContent.isEmpty()) {
                            // GET
                            http.Get(url, finalHeaders, timeout, dataType)
                        } else {
                            // POST
                            http.Post(url, finalRequestContent, finalHeaders, timeout, dataType)
                        }
                        reqResponse[returnI.toString()] = if (returnHeaders) {
                            responseJSON.toString()
                        } else {
                            responseJSON.optString("body", "")
                        }
                        "complite"
                    } catch (e: Exception) {
                        val errorJson = JSONObject().apply {
                            putSafe("status", http.lastErrorCode)
                            putSafe("message", "request error: ${e.message ?: "unknown error"}")
                        }
                        reqResponse[returnI.toString()] = errorJson.toString()
                        debugLog(TAG, "error: ${e.message ?: "unknown error"}")
                        "error"
                    }
                }

                override fun onPostExecute(result: String?) {
                    mainActivity.runOnUiThread {
                        val js = ("Lampa.Android.httpCall("
                                + returnI.toString() + ", '"
                                + result
                            ?.replace("\\", "\\\\")
                            ?.replace("'", "\\'")
                            ?.replace("\n", "\\\n")
                                + "')")
                        browser.evaluateJavascript(js) { debugLog(TAG, js) }
                    }
                }
            }
            LampaAsyncTask().execute()
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun getResp(str: String): String? {
        var string: String? = ""
        if (reqResponse.containsKey(str)) {
            string = reqResponse[str]
            reqResponse.remove(str)
        }
        return string
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun isHomeMode(): Boolean = mainActivity.homeMode

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun setNowPlaying(json: String) {
        mainActivity.runOnUiThread { MusicSession.update(mainActivity, json) }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun setBackgroundAudio(on: Boolean) {
        mainActivity.setBackgroundAudio(on)
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun openPlayer(link: String, jsonStr: String) {
        debugLog(TAG, "openPlayer: $link json:$jsonStr")
        val jsonObject = try {
            JSONObject(jsonStr.ifEmpty { "{}" }).apply {
                if (!has("url")) {
                    putSafe("url", link)
                }
            }
        } catch (_: Exception) {
            JSONObject().apply { putSafe("url", link) }
        }

        mainActivity.runOnUiThread { mainActivity.runPlayer(jsonObject) }

        // Force update Recs to filter viewed
        CoroutineScope(Dispatchers.Default).launch {
            delay(UPDATE_DELAY)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                LampaChannels.updateRecsChannel()
            } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                RecsService.updateRecs()
            }
        }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun setProxyPAC(link: String): Boolean {
        return Http.setProxyPAC(link)
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun getProxyPAC(): String {
        return Http.getProxyPAC()
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun voiceStart() {
        // Голосовой ввод с последующей передачей результата через JS
        mainActivity.runOnUiThread {
            try {
                mainActivity.displaySpeechRecognizer()
            } catch (e: Exception) {
                e.printStackTrace()
                // Очищаем поле ввода
                mainActivity.runVoidJsFunc("window.voiceResult", "''")
            }
        }
    }

    /**
     * Saves valid bookmarks after filtering out invalid ones.
     *
     * @param json The JSON string containing bookmarks.
     */
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun saveBookmarks(json: String?) {
        debugLog(TAG, "saveBookmarks fired!")
        CoroutineScope(Dispatchers.Default).launch {
            // Filter out invalid CubBookmark objects
            val validBookmarks = filterValidCubBookmarks(json)
            if (validBookmarks.isNotEmpty()) {
                debugLog(TAG, "saveBookmarks - found ${validBookmarks.size} valid elements")
                // Save the valid bookmarks
                mainActivity.saveAccountBookmarks(Gson().toJson(validBookmarks))
            } else {
                Log.e(TAG, "saveBookmarks - no valid CUB bookmarks found in the JSON")
            }
        }
    }

    @RequiresApi(Build.VERSION_CODES.LOLLIPOP)
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun updateChannel(where: String?) {
        // https://github.com/yumata/lampa-source/blob/e5505b0e9cf5f95f8ec49bddbbb04086fccf26c8/src/app.js#L203
        if (where != null && isTvContentProviderAvailable) {
            debugLog(TAG, "updateChannel [$where]")
            when (where) {
                LampaProvider.HIST,
                LampaProvider.BOOK,
                LampaProvider.LIKE,
                LampaProvider.LOOK,
                LampaProvider.VIEW,
                LampaProvider.SCHD,
                LampaProvider.CONT,
                LampaProvider.THRW -> {
                    CoroutineScope(Dispatchers.Default).launch {
                        delay(UPDATE_DELAY)
                        updateChanByName(where)
                    }
                }

                LampaProvider.LATE -> {
                    // Handle add to Watch Next from Lampa
                    CoroutineScope(Dispatchers.Default).launch {
                        delay(UPDATE_DELAY)
                        updateWatchNext()
                    }
                }
            }
        }
    }

    /** Returns queue acceptance, not completion of the TV provider write. */
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun publishPluginChannel(json: String?): Boolean = PluginChannels.publish(json)

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    fun clearPluginChannel(id: String?): Boolean = PluginChannels.clear(id)

    // https://stackoverflow.com/a/41560207
    // https://copyprogramming.com/howto/android-webview-savestate
    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Synchronized
    fun dump() {
        check(!dumped) { "already dumped" }
        val map = store.all
        val size = map?.size ?: 0
        keys = arrayOfNulls(size)
        values = arrayOfNulls(size)
        for ((cur, key) in map!!.keys.withIndex()) {
            keys!![cur] = key
            values!![cur] = (map[key] as String?)!!
        }
        dumped = true
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Synchronized
    fun size(): Int {
        check(dumped) { "dump() first" }
        return keys!!.size
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Synchronized
    fun key(i: Int): String? {
        check(dumped) { "dump() first" }
        return keys!![i]
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Synchronized
    fun value(i: Int): String? {
        check(dumped) { "dump() first" }
        return values!![i]
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Synchronized
    operator fun get(key: String?): String? {
        return store.getString(key, null)
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Synchronized
    operator fun set(key: String?, value: String?) {
        check(!dumped) { "already dumped" }
        store.edit { putString(key, value) }
    }

    @JavascriptInterface
    @org.xwalk.core.JavascriptInterface
    @Synchronized
    fun clear() {
        store.edit { clear() }
        keys = null
        values = null
        dumped = false
    }

    @Synchronized
    override fun toString(): String {
        return store.all.toString()
    }

    private fun attachHomeSession(url: String, headers: JSONObject) {
        val server = MainActivity.LAMPA_URL.trim().trimEnd('/')
        if (server.isEmpty()) return
        val request = try { URI(url.trim()) } catch (_: Exception) { return }
        val home = try { URI(server) } catch (_: Exception) { return }
        if (request.host.isNullOrEmpty() || home.host.isNullOrEmpty()) return
        if (!request.scheme.equals(home.scheme, ignoreCase = true)) return
        if (!request.host.equals(home.host, ignoreCase = true)) return
        fun portOf(uri: URI): Int = when {
            uri.port != -1 -> uri.port
            uri.scheme.equals("https", ignoreCase = true) -> 443
            else -> 80
        }
        if (portOf(request) != portOf(home)) return
        val token = ProfileRuntime.ownerFor(server)?.optString("token").orEmpty()
        if (token.isBlank()) return
        headers.putIfAbsentIgnoreCase("X-Home-Session", token)
    }

    private fun injectLampaClientHeaders(url: String, headers: JSONObject) {
        attachHomeSession(url, headers)
        if (!AppAttestation.shouldInjectClientHeaders(url, MainActivity.LAMPA_URL)) return

        AppAttestation.buildClientHeaders(mainActivity).forEach { (key, value) ->
            headers.putIfAbsentIgnoreCase(key, value)
        }

        val referer = MainActivity.LAMPA_URL.trim().trimEnd('/')
        if (referer.isNotEmpty()) {
            headers.putIfAbsentIgnoreCase("Referer", referer)
        }

        debugLog(
            TAG,
            "httpReq injected client headers: " +
                "${AppAttestation.HEADER_CLIENT}=${AppAttestation.clientId()}, " +
                "${AppAttestation.HEADER_REPO}=${BuildConfig.REPO_ID}, " +
                "Referer=$referer, " +
                "${AppAttestation.HEADER_CERT_SHA256}=${AppAttestation.signingCertSha256(mainActivity)}"
        )
    }

    private fun JSONObject.putIfAbsentIgnoreCase(key: String, value: Any) {
        if (hasIgnoreCase(key)) return
        putSafe(key, value)
    }

    private fun JSONObject.hasIgnoreCase(key: String): Boolean {
        val needle = key.lowercase(Locale.ROOT)
        val it = keys()
        while (it.hasNext()) {
            if (it.next().lowercase(Locale.ROOT) == needle) return true
        }
        return false
    }

    private fun JSONObject.putSafe(key: String, value: Any) = try {
        put(key, value)
    } catch (_: JSONException) { /* Ignore */
    }
}
