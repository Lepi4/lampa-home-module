package top.rootu.lampa

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.app.SearchManager
import android.content.ComponentName
import android.content.Context
import android.content.DialogInterface
import android.content.DialogInterface.BUTTON_NEGATIVE
import android.content.DialogInterface.BUTTON_NEUTRAL
import android.content.DialogInterface.BUTTON_POSITIVE
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ResolveInfo
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.net.Uri
import android.os.Build
import android.os.Build.VERSION
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.util.Log
import android.view.Gravity
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.view.ViewTreeObserver
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.ArrayAdapter
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.addCallback
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatDelegate
import androidx.appcompat.content.res.AppCompatResources
import androidx.appcompat.widget.AppCompatEditText
import androidx.appcompat.widget.AppCompatImageButton
import androidx.appcompat.widget.SwitchCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import com.airbnb.lottie.LottieAnimationView
import com.google.android.material.floatingactionbutton.FloatingActionButton
import com.google.android.material.progressindicator.LinearProgressIndicator
import com.google.android.material.textfield.TextInputLayout
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import net.gotev.speech.GoogleVoiceTypingDisabledException
import net.gotev.speech.Logger
import net.gotev.speech.Speech
import net.gotev.speech.SpeechDelegate
import net.gotev.speech.SpeechRecognitionNotAvailable
import net.gotev.speech.SpeechUtil
import net.gotev.speech.ui.SpeechProgressView
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import org.xwalk.core.MyXWalkEnvironment
import org.xwalk.core.MyXWalkUpdater
import org.xwalk.core.XWalkInitializer
import org.xwalk.core.XWalkPreferences
import top.rootu.lampa.browser.Browser
import top.rootu.lampa.browser.SysView
import top.rootu.lampa.browser.XWalk
import top.rootu.lampa.channels.ChannelManager.getChannelDisplayName
import top.rootu.lampa.channels.WatchNext
import top.rootu.lampa.content.LampaProvider
import top.rootu.lampa.helpers.Backup
import top.rootu.lampa.helpers.Backup.loadFromBackup
import top.rootu.lampa.helpers.Backup.backupSettings
import top.rootu.lampa.helpers.Backup.validateStorageBackup
import top.rootu.lampa.helpers.Helpers
import top.rootu.lampa.helpers.Helpers.debugLogIntentData
import top.rootu.lampa.helpers.Helpers.dp2px
import top.rootu.lampa.helpers.Helpers.getJson
import top.rootu.lampa.helpers.Helpers.isAndroidTV
import top.rootu.lampa.helpers.Helpers.isTvContentProviderAvailable
import top.rootu.lampa.helpers.Helpers.isValidJson
import top.rootu.lampa.helpers.PermHelpers
import top.rootu.lampa.helpers.PermHelpers.hasMicPermissions
import top.rootu.lampa.helpers.PermHelpers.isInstallPermissionDeclared
import top.rootu.lampa.helpers.PermHelpers.verifyMicPermissions
import top.rootu.lampa.helpers.Prefs
import top.rootu.lampa.helpers.Prefs.FAV
import top.rootu.lampa.helpers.Prefs.addUrlHistory
import top.rootu.lampa.helpers.Prefs.appBrowser
import top.rootu.lampa.helpers.Prefs.appLang
import top.rootu.lampa.helpers.Prefs.appPlayer
import top.rootu.lampa.helpers.Prefs.playerKeepConnection
import top.rootu.lampa.helpers.Prefs.kodiPlaylistEnabled
import top.rootu.lampa.helpers.Prefs.appPrefs
import top.rootu.lampa.helpers.Prefs.appUrl
import top.rootu.lampa.helpers.Prefs.bookToRemove
import top.rootu.lampa.helpers.Prefs.clearPending
import top.rootu.lampa.helpers.Prefs.contToRemove
import top.rootu.lampa.helpers.Prefs.defPrefs
import top.rootu.lampa.helpers.Prefs.firstRun
import top.rootu.lampa.helpers.Prefs.histToRemove
import top.rootu.lampa.helpers.Prefs.lampaSource
import top.rootu.lampa.helpers.Prefs.lastPlayedPrefs
import top.rootu.lampa.helpers.Prefs.likeToRemove
import top.rootu.lampa.helpers.Prefs.lookToRemove
import top.rootu.lampa.helpers.Prefs.migrate
import top.rootu.lampa.helpers.Prefs.schdToRemove
import top.rootu.lampa.helpers.Prefs.thrwToRemove
import top.rootu.lampa.helpers.Prefs.tvPlayer
import top.rootu.lampa.helpers.Prefs.urlHistory
import top.rootu.lampa.helpers.Prefs.viewToRemove
import top.rootu.lampa.helpers.Prefs.wathToAdd
import top.rootu.lampa.helpers.Prefs.wathToRemove
import top.rootu.lampa.helpers.getAppVersion
import top.rootu.lampa.helpers.hideSystemUI
import top.rootu.lampa.helpers.isAmazonDev
import top.rootu.lampa.helpers.isSafeForUse
import top.rootu.lampa.helpers.isTvBox
import top.rootu.lampa.models.LAMPA_CARD_KEY
import top.rootu.lampa.models.LampaCard
import top.rootu.lampa.net.HttpHelper
import top.rootu.lampa.receivers.PlayerReportReceiver
import top.rootu.lampa.sched.Scheduler
import top.rootu.lampa.tmdb.TMDB
import java.util.Locale
import java.util.regex.Pattern
import androidx.core.content.edit
import androidx.core.view.isGone
import androidx.core.net.toUri
import top.rootu.lampa.helpers.Helpers.encodeUrlIfNeeded
import kotlin.time.Duration.Companion.milliseconds


class MainActivity : BaseActivity(),
    Browser.Listener,
    XWalkInitializer.XWalkInitListener, MyXWalkUpdater.XWalkUpdateListener {
    // Local properties
    private var mXWalkUpdater: MyXWalkUpdater? = null
    private var mXWalkInitializer: XWalkInitializer? = null
    private var browser: Browser? = null
    // Lampa Media Center: оболочка в этом же окне (только сборка mc)
    private var mcHost: McHostApi? = null
    private var browserInitComplete = false
    private var isMenuVisible = false
    // открыто как домашний экран (кнопка Home): назад на главной не выходит, Home возвращает на главную
    @Volatile var homeMode = false

    // фоновая музыка (Яндекс Музыка в плагине): не усыплять WebView и держать процесс
    private var backgroundAudio = false

    private var isPlayerLaunching = false // suppress WebView pauseTimers while our external player is open
    private var launchedOutAt = 0L // последний запуск другого экрана/приложения из Lampa (плеер, Vimu, TiviMate): после него автозапуск ТВ не нужен
    private var pausedAt = 0L // когда приложение ушло с переднего плана: по долгому отсутствию (сон) включаем автозапуск ТВ
    private lateinit var loaderView: LottieAnimationView
    private lateinit var resultLauncher: ActivityResultLauncher<Intent>
    private lateinit var speechLauncher: ActivityResultLauncher<Intent>
    private lateinit var progressIndicator: LinearProgressIndicator
    private lateinit var playerStateManager: PlayerStateManager
    private var playbackUiOwner = ""

    // Data class for menu items
    private data class MenuItem(
        val title: String,
        val action: String,
        val icon: Int
    )

    // Class for URL history
    private class UrlAdapter(context: Context) :
        ArrayAdapter<String>(
            context,
            R.layout.lampa_dropdown_item, // Custom dropdown layout
            android.R.id.text1, // ID of the TextView in the custom layout
            context.urlHistory.toMutableList() // Load URL history
        )

    companion object {
        // Constants
        const val VIDEO_COMPLETED_DURATION_MAX_PERCENTAGE = 90
        private const val TAG = "APP_MAIN"
        private const val RESULT_VIMU_ENDED = 2
        private const val RESULT_VIMU_START = 3
        private const val RESULT_VIMU_ERROR = 4
        private const val JS_SUCCESS = "SUCCESS"
        private const val JS_FAILURE = "FAILED"
        private const val IP4_DIG = "([01]?\\d?\\d|2[0-4]\\d|25[0-5])"
        private const val IP4_REGEX = "(${IP4_DIG}\\.){3}${IP4_DIG}"
        private const val IP6_DIG = "[0-9A-Fa-f]{1,4}"
        private const val IP6_REGEX =
            "((${IP6_DIG}:){7}${IP6_DIG}|(${IP6_DIG}:){1,7}:|:(:${IP6_DIG}){1,7}|(${IP6_DIG}::?){1,6}${IP6_DIG})"
        private const val DOMAIN_REGEX = "([-A-Za-z\\d]+\\.)+[-A-Za-z]{2,}"
        private const val URL_REGEX = "^https?://" + // Mandatory protocol
                // "^(https?://)?" + // Protocol (http or https, optional)
                "(\\[${IP6_REGEX}]|${IP4_REGEX}|${DOMAIN_REGEX})" +  // IPv6, IPv4, or domain
                "(:\\d+)?" +                      // Optional port
                "(/[-\\w@:%._+~#=&]*(/[-\\w@:%._+~#=&]*)*)?" + // Optional path (allows subpaths)
                "(\\?[\\w@:%._+~#=&-]*)?" +       // Optional query string
                "(#[\\w-]*)?" +                   // Optional fragment
                "$"

        // Player Packages
        private val MX_PACKAGES = setOf(
            "com.mxtech.videoplayer.ad", // Standard
            "com.mxtech.videoplayer.pro", // Pro
            "com.mxtech.videoplayer.beta", // Beta
        )
        private val UPLAYER_PACKAGES = setOf(
            "com.uapplication.uplayer",  // Standard
            "com.uapplication.uplayer.beta", // Beta
        )
        private val VIMU_PACKAGES = setOf(
            "net.gtvbox.videoplayer",  // Standard
            "net.gtvbox.vimuhd",       // ViMu HD
            "net.gtvbox.vimu",         // Legacy
        )
        private val DDD_PLAYER_PACKAGES = setOf(
            "top.rootu.dddplayer"
        )
        // Players on the Just+ nested playlist contract; beta builds add a suffix to the package
        private val JUSTPLUS_PLAYER_PREFIXES = listOf("com.justplus.player", "com.lampaua.player")

        private fun isJustPlusPlayer(name: String) = JUSTPLUS_PLAYER_PREFIXES.any { name.startsWith(it) }
		private val KODI_PACKAGES = setOf(
			"org.xbmc.kodi",
			"net.kodinerds.maven.kodi22",
            "net.kodinerds.maven.kodi23",
            "org.xbmc.fandangos",
		)
        private val EXO_PLAYER_PACKAGES = setOf(
            "com.google.android.exoplayer2.demo", // v2, Legacy
            "androidx.media3.demo.main", // v3, current
        )
        private val MPV_PACKAGES = setOf(
            "is.xyz.mpv",
            "is.xyz.mpv.nightly",
            "is.xyz.mpv.debug",
            "live.mehiz.mpvkt",
        )
        private val PLAYERS_BLACKLIST = setOf(
            "com.android.gallery3d",
            "com.android.tv.frameworkpackagestubs",
            "com.estrongs.android.pop",
            "com.estrongs.android.pop.pro",
            "com.ghisler.android.totalcommander",
            "com.google.android.apps.photos",
            "com.google.android.tv.frameworkpackagestubs",
            "com.instantbits.cast.webvideo",
            "com.lonelycatgames.xplore",
            "com.mitv.videoplayer",
            "com.mixplorer.silver",
            "com.opera.browser",
            "com.tcl.browser",
            "nextapp.fx",
            "org.droidtv.contentexplorer",
            "pl.solidexplorer2",
            // more to add...
        )
        private val URL_PATTERN = Pattern.compile(URL_REGEX)
        private val listenerMutex = Mutex()

        // Properties
        var LAMPA_URL: String = ""
        var SELECTED_PLAYER: String? = ""
        var SELECTED_BROWSER: String? =
            if (VERSION.SDK_INT < Build.VERSION_CODES.KITKAT) "XWalk" else ""
        var delayedVoidJsFunc = mutableListOf<List<String>>()
        // Lampa Media Center: страница Lampa полностью загрузилась (window.appready) — оболочка открывает карточки только после этого
        val mcDocument = McDocumentState()
        var mcReady: Boolean
            get() = mcDocument.ready
            set(value) { mcDocument.ready = value }
        var mcPongAt: Long
            get() = mcDocument.pongAt
            set(value) { mcDocument.pongAt = value }
        var playerTimeCode: String = "continue"
        var playerAutoNext: Boolean = true
        var keepPlayerConnection: Boolean = true
		var useKodiPlaylist: Boolean = true
        var proxyTmdbEnabled: Boolean = false
        var lampaActivity: String = "{}" // JSON
        lateinit var urlAdapter: ArrayAdapter<String>

        // Just+ Player report callback extras: the launch's activity and card
        private const val REPORT_ACTIVITY_EXTRA = "lampa_activity"
        private const val REPORT_CARD_EXTRA = "lampa_card"
        private var instance: java.lang.ref.WeakReference<MainActivity>? = null

        /** Lampa Media Center: «призрак» — окно Lampa прозрачно и не принимает нажатий, но живёт и прогрето (на части прошивок оно иначе перекрывает оболочку). */
        @Volatile var mcShellOnTop = false
        @Volatile var mcShellHidden = false     // оболочка спрятана: на экране была сама Lampa (карточка, список)
        @Volatile var mcRestoreLast = false     // окно пересоздано системой (нехватка памяти при просмотре): вернуть Lampa на последний экран
        @Volatile var mcPrefPlayer = ""      // настройка Lampa «Тип плеера»
        @Volatile var mcPrefTorrent = ""     // настройка Lampa «Тип плеера для торрентов»
        @Volatile var mcReturnToShell = false   // ролик/плеер запущен из оболочки: после закрытия плеера вернуться на оболочку, а не на главную Lampa
        @Volatile var mcHiddenAt = 0L   // когда оболочка ушла: нажатие «ОК», открывшее карточку, не должно сработать ещё и в Lampa
        fun mcGhost(on: Boolean) { instance?.get()?.takeIf { !it.isFinishing }?.applyGhost(on) }

        /**
         * Handled only by a live activity. A report reaching a process started just for it is dropped,
         * never kept for the next start: applied then, it could write over progress the cloud sync has
         * brought in meanwhile.
         */
        fun onPlayerReport(intent: Intent) {
            instance?.get()?.takeIf { !it.isFinishing }?.takeJustPlusReport(justPlusReport(intent))
        }

        /** com.justplus.player.result as JSON: everything handleJustPlusReport needs, nothing else. */
        private fun justPlusReport(intent: Intent): JSONObject {
            val positions = intent.getIntArrayExtra("positions_sec") ?: IntArray(0)
            // Durations come per visit in history; the last known one of each item wins.
            val durations = IntArray(positions.size) { -1 }
            @Suppress("DEPRECATION")
            intent.getParcelableArrayExtra("history")?.forEach { visit ->
                val v = visit as? Bundle ?: return@forEach
                val i = v.getInt("index", -1)
                val d = v.getInt("duration_sec", -1)
                if (i in durations.indices && d > 0) durations[i] = d
            }
            val index = intent.getIntExtra("index", -1)
            val duration = intent.getIntExtra("duration_sec", -1)
            if (index in durations.indices && duration > 0) durations[index] = duration
            return JSONObject().apply {
                put("key", intent.getStringExtra(REPORT_ACTIVITY_EXTRA) ?: lampaActivity)
                put("card", intent.getStringExtra(REPORT_CARD_EXTRA))
                put("uri", intent.getStringExtra("uri") ?: intent.dataString)
                put("index", index)
                put("end_by", intent.getStringExtra("end_by"))
                put("error", intent.getStringExtra("error_message"))
                put("positions", JSONArray(positions.toList()))
                put("durations", JSONArray(durations.toList()))
            }
        }
    }

    inline fun <reified T> T.logDebug(message: String) {
        if (BuildConfig.DEBUG)
            Log.d(T::class.simpleName, message)
    }

    // adb shell setprop log.tag.MainActivity DEBUG
    // usage: debugLog<MainActivity> { "..." }
    inline fun <reified T> debugLog(block: () -> String) {
        if (BuildConfig.DEBUG || Log.isLoggable(T::class.simpleName, Log.DEBUG)) {
            Log.d(T::class.simpleName, block())
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        top.rootu.lampa.player.DLog.ver = BuildConfig.VERSION_NAME
        LAMPA_URL = appUrl
        SELECTED_PLAYER = appPlayer
        trimCaches()
        // Media Center: страница Lampa должна пережить просмотр (иначе процесс отрисовки убивается, по возврату страница грузится заново и стек экранов теряется)
        keepPlayerConnection = playerKeepConnection || BuildConfig.APPLICATION_ID.endsWith(".mc")
		useKodiPlaylist = kodiPlaylistEnabled
        logDebug("onCreate SELECTED_BROWSER: $SELECTED_BROWSER")
        logDebug("onCreate LAMPA_URL: $LAMPA_URL")
        logDebug("onCreate SELECTED_PLAYER: $SELECTED_PLAYER")
        playerStateManager = PlayerStateManager(this).apply {
            purgeOldStates()
        }
        instance = java.lang.ref.WeakReference(this)
        // Система может уничтожить остановленное окно, пока играет плеер («low-mem»): по возврату оно создаётся заново с пустой страницей.
        // Не повторяем старую команду запуска (карточка/поиск), а возвращаем то, что было на экране.
        val mcRecreated = BuildConfig.APPLICATION_ID.endsWith(".mc") && savedInstanceState != null
        if (mcRecreated) {
            intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
            mcRestoreLast = mcShellHidden && !mcReturnToShell
            Log.i(TAG, "окно пересоздано системой: shellHidden=$mcShellHidden returnToShell=$mcReturnToShell")
        }

        setupActivity()
        setupBrowser()
        setupUI()
        setupIntents()
        if (BuildConfig.APPLICATION_ID.endsWith(".mc")) {
            try {
                mcHost = Class.forName("top.rootu.lampa.shell.ShellHost").getConstructor(MainActivity::class.java).newInstance(this) as McHostApi
                mcHost?.attach(if (mcRecreated) (mcShellHidden && !mcReturnToShell) else !mcIsHomeIntent(intent))
            } catch (e: Exception) { Log.e(TAG, "оболочка Media Center не создана", e) }
        }

        if (firstRun) {
            CoroutineScope(Dispatchers.Default).launch {
                logDebug("First run scheduleUpdate(sync: true)")
                Scheduler.scheduleUpdate(true)
            }
        }
    }

    // любой запуск другого экрана/приложения из Lampa (внешний плеер, TiviMate, ссылка): возврат из него не считается «выходом из сна»
    override fun startActivity(intent: Intent?, options: Bundle?) {
        launchedOutAt = android.os.SystemClock.elapsedRealtime()
        super.startActivity(intent, options)
    }

    override fun startActivityForResult(intent: Intent, requestCode: Int, options: Bundle?) {
        launchedOutAt = android.os.SystemClock.elapsedRealtime()
        super.startActivityForResult(intent, requestCode, options)
    }

    internal fun applyGhost(on: Boolean) {
        if (!BuildConfig.APPLICATION_ID.endsWith(".mc")) return
        runOnUiThread {
            try {
                val lp = window.attributes
                lp.alpha = if (on) 0f else 1f
                window.attributes = lp
                if (on) window.addFlags(android.view.WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or android.view.WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE)
                else window.clearFlags(android.view.WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or android.view.WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE)
            } catch (_: Exception) {}
        }
    }

    // вход «домой»/«из меню приложений» без команды: показываем оболочку; всё остальное (карточка, поиск, ТВ) открывается в Lampa
    private fun mcIsHomeIntent(i: Intent?): Boolean {
        if (i == null) return true
        if (i.action != Intent.ACTION_MAIN) return false
        val home = i.hasCategory(Intent.CATEGORY_HOME) || i.hasCategory(Intent.CATEGORY_LAUNCHER) || i.hasCategory(Intent.CATEGORY_LEANBACK_LAUNCHER)
        return home && !i.hasExtra("id") && !i.hasExtra("mc_js") && !i.hasExtra("remind_channel") && !i.hasExtra(android.app.SearchManager.QUERY)
    }

    private var trustedHomeNavigation=false
    private var mcNavigationGeneration = 0
    fun mcCancelNavigation() {
        mcNavigationGeneration++
        browser?.evaluateJavascript("window.__mc_nav_id=${mcNavigationGeneration}"){}
    }
    fun mcOpen(i: Intent) {
        mcCancelNavigation()
        val own=mcNavigationGeneration
        browser?.evaluateJavascript("window.__mc_shell_origin=window.Lampa&&Lampa.Activity.active();window.__mc_ret=1"){ }
        val hold=i.getBooleanExtra("mc_hold_shell",false)
        if(hold) browser?.evaluateJavascript("window.__mc_nav_origin=window.Lampa&&Lampa.Activity.active();window.__mc_nav_select=(document.querySelector('.selectbox')||{}).textContent||''"){}
        trustedHomeNavigation=true;try{onNewIntent(i)}finally{trustedHomeNavigation=false}
        if(hold) lifecycleScope.launch {
            repeat(600){
                delay(100)
                if(own!=mcNavigationGeneration)return@launch
                val check="(function(){if(!window.Lampa)return false;var a=Lampa.Activity.active();return !!((a&&a!==window.__mc_nav_origin&&a.component!=='main'&&a.activity&&a.activity.is_started)||document.body.classList.contains('search--open')||(Lampa.Select.opened()&&((document.querySelector('.selectbox')||{}).textContent||'')!==window.__mc_nav_select))})()"
                browser?.evaluateJavascript(check){r->if(r=="true"&&own==mcNavigationGeneration){mcNavigationGeneration++;mcHost?.hide()}}
            }
            if(own==mcNavigationGeneration){mcCancelNavigation();mcHost?.back();android.widget.Toast.makeText(this@MainActivity,"Не удалось открыть раздел. Попробуйте ещё раз",android.widget.Toast.LENGTH_LONG).show()}
        }
    }

    private var mcDocumentJob: Job? = null
    private var mcSetupGeneration = -1L

    /** Called on the UI thread before replacing the document and on root load start. */
    fun mcBeginDocument() {
        mcDocumentJob?.cancel()
        mcDocumentJob = null
        mcSetupGeneration = -1L
        mcDocument.begin(android.os.SystemClock.elapsedRealtime())
    }

    /** Оболочка нашла/сменила сервер: загружаем Lampa с нового адреса. */
    fun mcServerChanged(url: String) {
        runOnUiThread {
            if (url.isNotEmpty() && url != LAMPA_URL) {
                LAMPA_URL = url
                mcBeginDocument()
                if (browserInitComplete) browser?.loadUrl(url)
            }
        }
    }

    /** Discard the previous viewer's document and pending callbacks; keep the process boot ID. */
    fun mcUserChanged() {
        runOnUiThread {
            mcBeginDocument()
            if (browserInitComplete) browser?.loadUrl(LAMPA_URL)
            mcHost?.show()
        }
    }

    /** Подтянуть новые настройки профиля в страницу Lampa, не закрывая меню оболочки. */
    fun mcReloadUnderShell() {
        runOnUiThread {
            mcBeginDocument()
            if (browserInitComplete) browser?.loadUrl(LAMPA_URL)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        mcHost?.let { h ->
            if (mcIsHomeIntent(intent)) { mcCancelNavigation();h.onHome(); return }
            // «ТВ»: оболочка остаётся на экране, пока страница включает эфир (после выхода из эфира вы снова на ней, а не на главной Lampa)
            else if (TvLaunch.channel >= 0 || intent.getBooleanExtra("mc_hold_shell",false) || intent.getBooleanExtra("mc_keep_shell",false)) h.show() else h.hide()
        }
        // команда дежурного напоминаний: открыть эфир на нужном канале (страница Lampa делает это обычным путём, с настройками ТВ)
        intent.getStringExtra("remind_channel")?.let { ch ->
            val t = intent.getStringExtra("remind_title") ?: ""
            Handler(Looper.getMainLooper()).postDelayed({
                runVoidJsFunc("window.__tv && window.__tv.playChannel", org.json.JSONObject.quote(ch) + "," + org.json.JSONObject.quote(t))
            }, 900)
            return
        }
        setIntent(intent) // getIntent() should always return the most recent
        if (intent.hasCategory(Intent.CATEGORY_HOME)) {
            homeMode = true
            runVoidJsFunc("window.__homeEntered", "")
        }
        logDebug("onNewIntent() processIntent")
        processIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        mcHost?.onResume()
        try { top.rootu.lampa.player.ProgressOutbox.init(this); top.rootu.lampa.player.ProgressOutbox.flush(LAMPA_URL) } catch (_: Exception) {}
        if (mcReturnToShell) { mcReturnToShell = false; mcHost?.show() }
        if (intent?.hasCategory(Intent.CATEGORY_HOME) == true) homeMode = true
        // выход из сна: процесс жив, страница не перезагружается и обычный автозапуск ТВ не срабатывает — просим страницу выполнить его
        val away = if (pausedAt > 0) android.os.SystemClock.elapsedRealtime() - pausedAt else 0L
        val fromPlayer = isPlayerLaunching || (launchedOutAt > 0 && pausedAt - launchedOutAt in 0..15000L)
        // Включение приставки — не кнопка «Домой»: без этого остаётся спрятанная страница Lampa.
        if (BuildConfig.APPLICATION_ID.endsWith(".mc") && !fromPlayer && away > 2 * 60 * 1000L && TvLaunch.channel < 0) {
            Log.i(TAG, "после сна ${away / 1000} с показываем лаунчер")
            mcHost?.show()
            mcHost?.noteWake(away / 1000)
        }
        if (!fromPlayer && away > 5 * 60 * 1000L && browserInitComplete && !BuildConfig.APPLICATION_ID.endsWith(".mc")) {
            Log.i(TAG, "WakeStart: приложение было на заднем плане ${away / 1000} с — автозапуск ТВ")
            Handler(Looper.getMainLooper()).postDelayed({ runVoidJsFunc("window.__tv && window.__tv.wakeStart", "") }, 1800)
        }
        pausedAt = 0L
        launchedOutAt = 0L
        // команда «включить ТВ» из TvLaunchActivity (программа автоматизации): страница уже загружена — выполняем сразу,
        // иначе её заберёт автозапуск страницы при загрузке
        if (TvLaunch.channel >= 0 && browserInitComplete)
            Handler(Looper.getMainLooper()).postDelayed({ runVoidJsFunc("window.__tv && window.__tv.launchStart", "") }, 1000)
        endPlayerSession() // returned to foreground; player (if any) is closed
        hideSystemUI()
        if (!isTvBox) setupFab()
        // Try to initialize again when the user completed updating and
        // returned to current activity. The browser.onResume() will do nothing if
        // the initialization is proceeding or has already been completed.
        mXWalkInitializer?.initAsync()
        logDebug("onResume() browserInitComplete $browserInitComplete")
        if (browserInitComplete)
            browser?.resumeTimers()
        logDebug("onResume() isSafeForUse ${browser.isSafeForUse()}")
        if (browser.isSafeForUse()) {
            lifecycleScope.launch {
                logDebug("onResume() run syncBookmarks()")
                syncBookmarks()
            }
        }
        keptReports.values.toList().also { keptReports.clear() }.forEach(::handleJustPlusReport)
    }

    override fun onPause() {
        mcHost?.onPause()
        pausedAt = android.os.SystemClock.elapsedRealtime()
        // Keep JS timers (and the RCH socket heartbeat) alive while our external player is open,
        // but only when the user enabled it. Home/real backgrounding still pauses as before.
        // Media Center: Lampa живёт в фоне рядом с оболочкой, таймеры не останавливаем (pauseTimers действует на все WebView процесса)
        if (browserInitComplete && !backgroundAudio && !(isPlayerLaunching && keepPlayerConnection) && !BuildConfig.APPLICATION_ID.endsWith(".mc"))
            browser?.pauseTimers()
        super.onPause()
    }

    override fun onDestroy() {
        Log.w(TAG, "onDestroy: finishing=$isFinishing config=$isChangingConfigurations", Throwable("onDestroy"))
        try { mcHost?.destroy() } catch (_: Exception) {}
        mcDocumentJob?.cancel()
        if (instance?.get() === this) mcDocument.begin(android.os.SystemClock.elapsedRealtime())
        // A destroyed activity may linger before GC, so the weak reference alone would still hand it
        // reports. Only our own: a newer activity may already have registered itself.
        if (instance?.get() === this) instance = null
        PlaybackService.stop(this)
        MusicSession.release(this)
        if (musicReceiverRegistered) {
            try { unregisterReceiver(musicActionReceiver) } catch (_: Exception) {}
            musicReceiverRegistered = false
        }
        if (browserInitComplete) {
            browser?.apply {
                // Destroy only if not already destroyed
                if (!isDestroyed) {
                    destroy()
                }
            }
        }
        try {
            Speech.getInstance()?.shutdown()
        } catch (_: Exception) {
        }
        super.onDestroy()
    }

    // handle user pressed Home
    override fun onUserLeaveHint() {
        logDebug("onUserLeaveHint() isPlayerLaunching=$isPlayerLaunching")
        if (backgroundAudio && !homeMode) enterMusicPip()
        // Launching our external player also triggers onUserLeaveHint (startActivity sets the
        // userLeaving flag) BEFORE onPause. Skip pausing there so the RCH socket heartbeat
        // survives playback. Real Home/leave (isPlayerLaunching == false) still pauses & clears.
        if (browserInitComplete && !backgroundAudio && !(isPlayerLaunching && keepPlayerConnection) && !BuildConfig.APPLICATION_ID.endsWith(".mc"))
            browser?.apply {
                pauseTimers()
                clearCache(true)
            }
        super.onUserLeaveHint()
    }

    // handle configuration changes (language / screen orientation)
    override fun onConfigurationChanged(newConfig: Configuration) {
        logDebug("onConfigurationChanged()")
        super.onConfigurationChanged(newConfig)
        lifecycleScope.launch {
            delay(300.milliseconds) // Small delay for configuration to settle
            hideSystemUI()
            showFab(true)
        }
    }

    // пока на экране оболочка, клавиши пульта идут только ей (WebView Lampa под ней может забрать фокус сам)
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        mcHost?.let { h ->
            if (h.shown && event.keyCode != KeyEvent.KEYCODE_BACK) return h.dispatchKey(event)
            if (!h.shown && (event.keyCode == KeyEvent.KEYCODE_ENTER || event.keyCode == KeyEvent.KEYCODE_DPAD_CENTER || event.keyCode == KeyEvent.KEYCODE_NUMPAD_ENTER) &&
                android.os.SystemClock.elapsedRealtime() - mcHiddenAt < 900) return true
        }
        return super.dispatchKeyEvent(event)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        mcHost?.onResult(requestCode, resultCode, data)
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean {
        if (keyCode == KeyEvent.KEYCODE_MENU
            || keyCode == KeyEvent.KEYCODE_TV_CONTENTS_MENU
            || keyCode == KeyEvent.KEYCODE_TV_MEDIA_CONTEXT_MENU
        ) {
            logDebug("Menu key pressed")
            showMenuDialog()
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onKeyLongPress(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK) {
            logDebug("Back button long pressed")
            showMenuDialog()
            return true
        }
        return super.onKeyLongPress(keyCode, event)
    }

    override fun onBrowserInitCompleted() {
        browserInitComplete = true
        HttpHelper.userAgent = browser?.getUserAgentString() + " lampa_client"
        browser?.apply {
            setUserAgentString(HttpHelper.userAgent)
            setBackgroundColor(ContextCompat.getColor(baseContext, R.color.lampa_background))
            addJavascriptInterface(AndroidJS(this@MainActivity, this), "AndroidJS")
        }
        logDebug("onBrowserInitCompleted LAMPA_URL: $LAMPA_URL")
        launchLog("запуск приложения, сеть: ${if (netUp()) "есть" else "нет"}")
        if (LAMPA_URL.isEmpty() && BuildConfig.APPLICATION_ID.endsWith(".mc")) {
            // Media Center: адрес сервера выбирает оболочка (автопоиск), диалог не нужен; Lampa загрузится, когда адрес появится
            logDebug("onBrowserInitCompleted: ждём адрес от оболочки")
        } else if (LAMPA_URL.isEmpty()) {
            logDebug("onBrowserInitCompleted showUrlInputDialog")
            showUrlInputDialog()
        } else {
            logDebug("onBrowserInitCompleted load $LAMPA_URL")
            browser?.loadUrl(LAMPA_URL)
        }
    }

    override fun onBrowserPageFinished(view: ViewGroup, url: String) {
        logDebug("onBrowserPageFinished url: $url")
        if (url.startsWith("http")) {
            launchLog(if (offlineWatching) "страница открыта из кеша" else "страница загружена")
            loadRetried = false
            flushLaunchLog()
        }
        // Restore Lampa settings and reload if migrate flag set
        if (migrate) {
            migrateSettings()
        }
        if (view.visibility != View.VISIBLE) {
            view.visibility = View.VISIBLE
        }
        // Switch Loader (Note it control delayedVoidJsFunc)
        loaderView.visibility = View.GONE

        Log.d(TAG, "LAMPA onLoadFinished $url")

        lifecycleScope.launch {
            syncLanguage()
        }
        // Hack to skip reload from Back history
        if (url.trimEnd('/').equals(LAMPA_URL, true)) {
            val generation = mcDocument.generation
            // Some WebView versions deliver page-finished twice for one document.
            if (mcSetupGeneration == generation) return
            mcSetupGeneration = generation
            mcDocumentJob?.cancel()
            // 1s delay after deep link to load content and after storage listener setup
            val waitDelay = 1000L
            // Lazy Load Intent
            processIntent(intent, waitDelay)
            mcDocumentJob = lifecycleScope.launch {
                listenerCleanupAndSetup()
                delay(waitDelay.milliseconds)
                if (generation != mcDocument.generation) return@launch
                syncStorage() // Sync with Lampa settings
                syncBookmarks() // Sync Android TV Home user changes
                // Media Center: ждём готовности Lampa (window.appready) и сообщаем оболочке
                launch {                     // пульс: пока страница отвечает, mcPongAt обновляется; сторож оболочки следит за ним
                    while (isActive && generation == mcDocument.generation) {
                        browser?.evaluateJavascript("1") { _: String -> mcDocument.pong(generation, android.os.SystemClock.elapsedRealtime()) }
                        delay(5000)
                    }
                }
                launch {
                    var tries = 0
                    while (tries++ < 240 && !mcReady && generation == mcDocument.generation) {
                        browser?.evaluateJavascript("(function(){return !!window.appready})()") { r: String -> if (r == "true") mcDocument.markReady(generation) }
                        delay(500)
                    }
                    // окно было пересоздано во время просмотра: открываем последний экран Lampa (карточка, список) вместо главной
                    if (generation == mcDocument.generation && mcReady && mcRestoreLast) {
                        if (!mcShellHidden || mcReturnToShell) {
                            Log.i(TAG, "restore_last skipped: shell visible")
                            mcRestoreLast = false
                        } else {
                            mcRestoreLast = false
                            delay(1200)
                            runVoidJsFunc("(function(){try{var a=JSON.parse(localStorage.getItem('mc_last_activity')||'null');localStorage.removeItem('mc_last_activity');if(a&&a.component&&a.component!=='main'&&Lampa.Activity.all().length<2){window.__mc_ret=1;Lampa.Activity.push(a)}}catch(e){}})()", "")
                        }
                    }
                }
                // Process delayed functions with safe iteration
                val itemsToProcess = delayedVoidJsFunc.toList()
                delayedVoidJsFunc.clear() // Clear before processing
                for (item in itemsToProcess) {
                    runVoidJsFunc(item[0], item[1])
                }
                // Background update Android TV channels and recommendations
                withContext(Dispatchers.Default) {
                    delay(waitDelay.milliseconds)
                    Scheduler.scheduleUpdate(false) // false for one shot, true is onBoot
                }
            }
        }
    }

    private fun isAfterEndCreditsPosition(positionMillis: Long, duration: Long): Boolean {
        return duration > 0 && positionMillis >= duration * VIDEO_COMPLETED_DURATION_MAX_PERCENTAGE / 100
    }

    private fun setupActivity() {
        AppCompatDelegate.setDefaultNightMode(AppCompatDelegate.MODE_NIGHT_YES)
        enableEdgeToEdge()
        @Suppress("DEPRECATION")
        if (VERSION.SDK_INT <= Build.VERSION_CODES.TIRAMISU)
            overridePendingTransition(android.R.anim.fade_in, android.R.anim.fade_out)
        else // API > 33
            overrideActivityTransition(
                OVERRIDE_TRANSITION_OPEN,
                android.R.anim.fade_in,
                android.R.anim.fade_out
            )
        onBackPressedDispatcher.addCallback {
            if (mcHost?.shown == true) { mcHost?.back(); return@addCallback }
            // no Back with no focused webView workaround
            // страница сама решает: окна настроек/поиска закрываются контроллером Lampa, иначе обычный history.back().
            // Вызываем всегда, даже если у WebView нет истории (canGoBack() == false): иначе из настроек нельзя выйти
            runVoidJsFunc("(window.__homeBack || window.history.back.bind(window))", "")
            // Clear any pending intents that might cause reloads
            // intent = Intent() // Reset the intent
        }
    }

    private fun setupBrowser() {
        SELECTED_BROWSER = appBrowser
        if (!Helpers.isWebViewAvailable(this)
            || (SELECTED_BROWSER.isNullOrEmpty() && VERSION.SDK_INT < Build.VERSION_CODES.KITKAT)
        ) {
            SELECTED_BROWSER = "XWalk"
        }
        val wvvMajorVersion: Double = try {
            Helpers.getWebViewVersion(this).substringBefore(".").toDouble()
        } catch (_: NumberFormatException) {
            0.0
        }
        // Use WebView on RuStore builds and modern Androids by default
        if (Helpers.isWebViewAvailable(this)
            && SELECTED_BROWSER.isNullOrEmpty()
            && (BuildConfig.FLAVOR == "ruStore" || wvvMajorVersion > 53.589)
        ) {
            SELECTED_BROWSER = "SysView"
        }
        when (SELECTED_BROWSER) {
            "XWalk" -> {
                // Must call initAsync() before anything that involves the embedding
                // API, including invoking setContentView() with the layout which
                // holds the XWalkView object.
                mXWalkInitializer = XWalkInitializer(this, this)
                mXWalkInitializer?.initAsync()
                // Until onXWalkInitCompleted() is invoked, you should do nothing with the
                // embedding API except the following:
                // 1. Instantiate the XWalkView object
                // 2. Call XWalkPreferences.setValue()
                // 3. Call mXWalkView.setXXClient(), e.g., setUIClient
                // 4. Call mXWalkView.setXXListener(), e.g., setDownloadListener
                // 5. Call mXWalkView.addJavascriptInterface()
                XWalkPreferences.setValue(XWalkPreferences.REMOTE_DEBUGGING, true)
                XWalkPreferences.setValue(XWalkPreferences.ENABLE_JAVASCRIPT, true)
            }

            "SysView" -> {
                useSystemWebView()
            }

            else -> {
                setContentView(R.layout.activity_empty)
                showBrowserInputDialog()
            }
        }
        // https://developer.android.com/develop/background-work/background-tasks/scheduling/wakelock
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    private fun useCrossWalk() {
        setContentView(R.layout.activity_xwalk)
        loaderView = findViewById(R.id.loaderView)
        try {
            browser = XWalk(this, R.id.xWalkView)
            browser?.initialize()
            // Fix: On legacy Android versions (5-7) / Amlogic SoC, dynamically adding SurfaceView
            // after onCreate/onResume leaves the display surface unattached (window.innerWidth == 0).
            // A delayed REORDER_TO_FRONT pulse forces WindowManager to bind the surface.
            Handler(Looper.getMainLooper()).postDelayed({
                try {
                    val intent = Intent(this, MainActivity::class.java).apply {
                        addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)
                    }
                    startActivity(intent)
                } catch (ignored: Throwable) {
                }
            }, 1200)
        } catch (e: Exception) {
            Log.e("XWalk", "Init failed. Fallback to WebView.", e)
            useSystemWebView()
        }
    }

    private fun useSystemWebView() {
        setContentView(R.layout.activity_webview)
        loaderView = findViewById(R.id.loaderView)
        browser = SysView(this, R.id.webView)
        browser?.initialize()
    }

    private fun handleSpeechResult(result: androidx.activity.result.ActivityResult) {
        if (result.resultCode == RESULT_OK) {
            // There are no request codes
            val data: Intent? = result.data
            val spokenText: String? =
                data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.let { results ->
                    results[0]
                }
            // Do something with spokenText.
            if (spokenText != null) {
                runVoidJsFunc("window.voiceResult", "'" + spokenText.replace("'", "\\'") + "'")
            }
        }
    }

    // Some external video player api specs:
    // vlc https://wiki.videolan.org/Android_Player_Intents/
    // justplayer https://github.com/moneytoo/Player/issues/203
    // just+ player https://github.com/just-plus-player/just-plus-player/blob/master/docs/PLAYLIST_API.md
    // mxplayer https://mx.j2inter.com/api
    // mpv http://mpv-android.github.io/mpv-android/intent.html
    // vimu https://www.vimu.tv/player-api
    @Volatile private var pendingTlUpdates: Map<String, Long> = emptyMap()   // хеш серии -> время последнего действия в плеере (мс)

    // Итог плеера записывается в Lampa временем действия, а не временем закрытия: иначе забытый на паузе плеер перетирает чужой просмотр, сделанный позже
    private fun withActionTime(json: String): String {
        return try {
            val o = org.json.JSONObject(json)
            val u = pendingTlUpdates[o.optString("hash")] ?: return json
            o.put("received", true); o.put("updated", u); o.put("home_native", true); o.toString()
        } catch (_: Exception) { json }
    }

    @Volatile private var pendingCompletedUrls: List<String> = emptyList()   // серии, досмотренные до конца в этом сеансе (от встроенного плеера)

    private fun handlePlayerResult(result: androidx.activity.result.ActivityResult) {
        endPlayerSession() // player closed, returning result
        val data: Intent? = result.data
        pendingCompletedUrls = data?.getStringArrayListExtra("completed_urls")?.toList() ?: emptyList()
        pendingTlUpdates = try {
            val o = org.json.JSONObject(data?.getStringExtra("tl_updates") ?: "{}")
            o.keys().asSequence().associateWith { o.optLong(it) }
        } catch (_: Exception) { emptyMap() }
        val videoUrl: String = data?.data?.toString() ?: "null"
        val resultCode = result.resultCode

        if (BuildConfig.DEBUG) {
            Log.d(TAG, "Returned videoUrl: $videoUrl")
            when (resultCode) {
                RESULT_OK -> Log.d(TAG, "RESULT_OK")
                RESULT_CANCELED -> Log.d(TAG, "RESULT_CANCELED")
                RESULT_FIRST_USER -> Log.d(TAG, "RESULT_FIRST_USER")
                RESULT_VIMU_ENDED -> Log.d(TAG, "RESULT_VIMU_ENDED")
                RESULT_VIMU_START -> Log.d(TAG, "RESULT_VIMU_START")
                RESULT_VIMU_ERROR -> Log.e(TAG, "RESULT_VIMU_ERROR")
                else -> Log.w(TAG, "Undefined result code [$resultCode]")
            }
        }
        debugLogIntentData(TAG, data)

        data?.let { intent ->
            // кнопка «Внешний ↗» во встроенном плеере: записываем позицию и открываем VIMU с этого же места
            if (intent.getBooleanExtra("open_external", false)) {
                handleOpenExternal(intent, videoUrl)
                return
            }
            // Just+ Player: reports come through the result_callback only
            val action = intent.action
            if (action != null && isJustPlusPlayer(action) && action.endsWith(".result")) return
            when (intent.action) {
                // MX & Just Player
                "com.mxtech.intent.result.VIEW" ->
                    handleMxPlayerResult(intent, resultCode, videoUrl)
                // VLC
                "org.videolan.vlc.player.result" ->
                    handleVlcPlayerResult(intent, resultCode, videoUrl)
                // MPV
                "is.xyz.mpv.MPVActivity.result",
                "is.xyz.mpv.nightly.MPVActivity.result",
                "is.xyz.mpv.debug.MPVActivity.result",
                "live.mehiz.mpvkt.MPVActivity.result" ->
                    handleMpvPlayerResult(intent, resultCode, videoUrl)
                // UPlayer
                "com.uapplication.uplayer.result", "com.uapplication.uplayer.beta.result" ->
                    handleUPlayerResult(intent, resultCode, videoUrl)
                // ViMu
                "net.gtvbox.videoplayer.result", "net.gtvbox.vimuhd.result" ->
                    handleViMuPlayerResult(intent, resultCode, videoUrl)
                "top.rootu.dddplayer.intent.result.VIEW" ->
                    handleDddPlayerResult(intent, resultCode, videoUrl)
                // Others
                else -> handleGenericPlayerResult(intent, resultCode, videoUrl)
            }
        }
    }

    // Встроенный плеер закрылся по кнопке «Внешний ↗»: сохраняем позицию (как при обычном закрытии) и запускаем VIMU
    // с неё же. Если звук был выбран, просим у сервера поток с этой дорожкой по умолчанию (в API VIMU выбора дорожки нет).
    private fun handleOpenExternal(intent: Intent, videoUrl: String) {
        val pos = intent.getIntExtra("position", 0)
        val dur = intent.getIntExtra("duration", 0)
        val audioLabel = intent.getStringExtra("ext_audio_label").orEmpty()
        val audioLang = intent.getStringExtra("ext_audio_lang").orEmpty()
        val pkg = listOf("net.gtvbox.videoplayer", "net.gtvbox.vimuhd").firstOrNull {
            try { packageManager.getPackageInfo(it, 0); true } catch (_: Exception) { false }
        } ?: return
        lifecycleScope.launch {
            val st = playerStateManager.getState(lampaActivity)
            val url = videoUrl.takeUnless { it.isBlank() || it == "null" } ?: st.currentUrl ?: return@launch
            val upd = updateCurrentPlaylistItem(st.playlist, url, pos, dur, false) ?: return@launch
            playerStateManager.saveState(
                activityJson = lampaActivity,
                playlist = upd.first,
                currentIndex = upd.second,
                currentUrl = url,
                currentPosition = pos.toLong(),
                startIndex = st.startIndex,
                extras = st.extras
            )
            upd.first[upd.second].timeline?.let { timeline ->
                runVoidJsFunc("Lampa.Timeline.update", playerStateManager.convertTimelineToJsonString(timeline))
            }
            val state = playerStateManager.getState(lampaActivity)
            val currentItem = state.playlist.getOrNull(state.currentIndex) ?: return@launch
            val playJson = playerStateManager.getStateJson(state).apply {
                currentItem.title?.takeIf { it.isNotEmpty() }?.let { if (!has("title")) put("title", it) }
                currentItem.timeline?.let { tl ->
                    put("timeline", JSONObject().apply {
                        put("hash", tl.hash); put("time", tl.time); put("duration", tl.duration); put("percent", tl.percent)
                        tl.profile?.let { put("profile", it) }
                    })
                    put("position", tl.time.toLong())
                }
                put("home_player", false)
                // дорожка звука: поток kino.pub отдаём с нужной дорожкой по умолчанию
                if (audioLabel.isNotBlank() || audioLang.isNotBlank()) {
                    fun withAudio(u: String): String {
                        if (!u.contains("/kp/stream")) return u
                        val a = java.net.URLEncoder.encode(audioLabel.ifBlank { audioLang }, "UTF-8")
                        return u + (if (u.contains("?")) "&" else "?") + "audio=" + a
                    }
                    put("url", withAudio(optString("url")))
                    optJSONArray("playlist")?.let { arr ->
                        for (k in 0 until arr.length()) arr.optJSONObject(k)?.let { o -> o.put("url", withAudio(o.optString("url"))) }
                    }
                }
            }
            runPlayer(playJson, pkg, lampaActivity)
        }
    }

    private fun handleDddPlayerResult(intent: Intent, resultCode: Int, videoUrl: String) {
        when (resultCode) {
            RESULT_OK -> {
                val pos = intent.getLongExtra("position", 0L)
                val dur = intent.getLongExtra("duration", 0L)
                // val endBy = intent.getStringExtra("end_by") // "user" or "playback_completion"

                if (pos > 0 && dur > 0) {
                    val ended = isAfterEndCreditsPosition(pos, dur)
                    Log.i(TAG, "Playback stopped [position=$pos, duration=$dur, ended:$ended]")
                    resultPlayer(videoUrl, pos.toInt(), dur.toInt(), ended)
                } else if (pos == 0L && dur == 0L) {
                    // Возможно воспроизведение завершилось полностью
                    Log.i(TAG, "Playback completed (DDD)")
                    resultPlayer(videoUrl, 0, 0, true)
                }
            }
            RESULT_CANCELED -> Log.i(TAG, "Playback stopped by user")
            else -> Log.e(TAG, "Invalid state [resultCode=$resultCode]")
        }
    }
    private fun handleMxPlayerResult(intent: Intent, resultCode: Int, videoUrl: String) {
        when (resultCode) {
            RESULT_OK -> {
                when (intent.getStringExtra("end_by")) {
                    "playback_completion" -> {
                        Log.i(TAG, "Playback completed")
                        resultPlayer(videoUrl, 0, 0, true)
                    }

                    "user" -> {
                        val pos = intent.getIntExtra("position", 0)
                        val dur = intent.getIntExtra("duration", 0)
                        if (pos > 0 && dur > 0) {
                            val ended = isAfterEndCreditsPosition(pos.toLong(), dur.toLong())
                            Log.i(
                                TAG,
                                "Playback stopped [position=$pos, duration=$dur, ended:$ended]"
                            )
                            resultPlayer(videoUrl, pos, dur, ended)
                        } else {
                            Log.e(TAG, "Invalid state [position=$pos, duration=$dur]")
                        }
                    }

                    else -> Log.e(TAG, "Invalid state [endBy=${intent.getStringExtra("end_by")}]")
                }
            }

            RESULT_CANCELED -> Log.i(TAG, "Playback stopped by user")
            RESULT_FIRST_USER -> {
                Log.e(TAG, "Playback stopped by unknown error")
                if (intent.getStringExtra("end_by") == "error") {
                    val retry = JSONObject().put("attempt", intent.getStringExtra("home_auto_attempt").orEmpty())
                        .put("profile", intent.getStringExtra("home_auto_profile").orEmpty())
                        .put("startupFailed", intent.getBooleanExtra("home_auto_startup_failed", false))
                    runVoidJsFunc("(function(){try{window.__homeTorrentRetry&&window.__homeTorrentRetry($retry)}catch(e){}})", "")
                }
            }
            else -> Log.e(TAG, "Invalid state [resultCode=$resultCode]")
        }
    }

    /** Just+ reports received in the background, the latest per launch. */
    private val keptReports = LinkedHashMap<String, JSONObject>()

    // Applied in the foreground, so sync plugins send from a visible page; interval reports at once
    fun takeJustPlusReport(report: JSONObject) {
        if (lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) ||
            report.optString("end_by") == "interval" && keepPlayerConnection
        ) handleJustPlusReport(report)
        else keptReports[report.optString("key") + report.optString("card")] = report
    }

    // Just+ Player report: a position per item, -1 = never opened. The state stays: reports come mid-session
    fun handleJustPlusReport(report: JSONObject) {
        val endBy = report.optString("end_by")
        if (endBy != "completion" && endBy != "user" && endBy != "interval") {
            when (endBy) {
                "cancelled" -> Log.i(TAG, "Playback cancelled before start")
                "error" -> Log.e(TAG, "Playback error: ${report.optString("error")}")
                else -> Log.e(TAG, "Invalid state [endBy=$endBy]")
            }
            return
        }
        val key = report.optString("key", lampaActivity)
        val cardId = report.optString("card").takeIf { it.isNotEmpty() }
        val positions = report.optJSONArray("positions") ?: return
        val durations = report.optJSONArray("durations") ?: return
        // The report's own activity, not lampaActivity: that one may be stale or changed since
        lifecycleScope.launch {
            val state = playerStateManager.getState(key)
            val card = (state.extras[LAMPA_CARD_KEY] as? String)
                ?.let { getJson(it, LampaCard::class.java) } ?: getCardFromActivity(key)
            // Another card's state under the same key
            if (cardId != null && card?.id != null && cardId != card.id) {
                logDebug("Player report of another card [$cardId / ${card.id}]")
                return@launch
            }
            val playlist = state.playlist.toMutableList()
            if (playlist.isEmpty() || positions.length() != playlist.size) {
                logDebug("Player report does not fit the playlist [${positions.length()} / ${playlist.size}]")
                return@launch
            }
            // Серия отмечается только по собственной позиции из отчёта (JustPlusRules): не открытые плеером серии не меняются, диапазон старт→текущая доказательством не считается
            val reportItems = top.rootu.lampa.player.JustPlusRules.items(
                (0 until positions.length()).map { positions.optInt(it, -1) },
                (0 until positions.length()).map { durations.optInt(it, -1) },
                VIDEO_COMPLETED_DURATION_MAX_PERCENTAGE
            )
            for (it in reportItems) {
                if (it.index in playlist.indices) playlist[it.index] = createUpdatedPlaylistItem(playlist[it.index], it.posSec * 1000, it.durSec * 1000, it.ended)
            }
            val index = report.optInt("index", -1).takeIf { it in playlist.indices } ?: state.currentIndex
            // Only changed items: Lampa stamps every write as new progress
            playlist.forEachIndexed { i, item ->
                item.timeline?.takeIf { it.hash != "0" && it != state.playlist[i].timeline }?.let {
                    runVoidJsFunc("Lampa.Timeline.update", playerStateManager.convertTimelineToJsonString(it))
                }
            }
            markOpened(playlist, positions, index)
            val ended = playlist[index].timeline?.percent == 100
            Log.i(TAG, "Player report [index=$index, ended=$ended]")
            val saved = playerStateManager.saveState(
                activityJson = key,
                playlist = playlist,
                currentIndex = index,
                currentUrl = report.optString("uri").takeIf { it.isNotEmpty() } ?: playlist[index].url,
                currentPosition = positions.optInt(index, 0).coerceAtLeast(0) * 1000L,
                startIndex = state.startIndex,
                extras = state.extras
            )
            // Watch Next by this launch's card and activity
            if (isTvContentProviderAvailable && card != null) launch(Dispatchers.Default) {
                try {
                    when {
                        ended -> WatchNext.removeContinueWatch(card)
                        saved.currentItem != null && !saved.isEnded -> WatchNext.addLastPlayed(card, key)
                    }
                } catch (e: Exception) {
                    logDebug("Error in Watch Next: ${e.javaClass.simpleName} - ${e.message}")
                }
            }
        }
    }

    /**
     * The "viewed" eye of the online plugin (online.js): its playlist items carry `callback: file.mark`,
     * which Lampa's own player calls as each episode starts. An external player gets the playlist as
     * JSON, functions dropped, so only the episode clicked was ever marked. The playlist itself stays in
     * Lampa.PlayerPlaylist after the hand-over: call the mark of every item played in this session,
     * the current one last so it is the one the plugin resumes. By url only: Lampa rewrites the urls on
     * those very objects before the hand-over, so they are the strings this playlist holds, and a
     * season / episode fallback would mark another show's episode once its playlist has replaced this
     * one. Nothing to call once the page has been reloaded — the list is gone then.
     */
    private fun markOpened(
        playlist: List<PlayerStateManager.PlaylistItem>,
        positions: JSONArray,
        current: Int
    ) {
        val opened = JSONArray()
        (playlist.indices.filter { it != current } + current).forEach { i ->
            if (positions.optInt(i, -1) <= 0) return@forEach // never opened, or opened and not played
            opened.put(playlist[i].url)
        }
        if (opened.length() == 0) return
        runVoidJsFunc(
            "(function (opened) {" +
                    "var list = (window.Lampa && Lampa.PlayerPlaylist && Lampa.PlayerPlaylist.get()) || [];" +
                    "opened.forEach(function (url) {" +
                    "var item = list.find(function (p) { return p.url == url; });" +
                    "if (item && typeof item.callback == 'function') item.callback();" +
                    "});" +
                    "})",
            opened.toString()
        )
    }

    private fun handleVlcPlayerResult(intent: Intent, resultCode: Int, videoUrl: String) {
        if (resultCode == RESULT_OK) {
            val pos = intent.getLongExtra("extra_position", 0L)
            val dur = intent.getLongExtra("extra_duration", 0L)
            val url =
                if (videoUrl.isEmpty() || videoUrl == "null") intent.getStringExtra("extra_uri")
                    ?: videoUrl else videoUrl
            when {
                pos > 0L && dur > 0L -> {
                    val ended = isAfterEndCreditsPosition(pos, dur)
                    Log.i(TAG, "Playback stopped [position=$pos, duration=$dur, ended:$ended]")
                    resultPlayer(url, pos.toInt(), dur.toInt(), ended)
                }

                pos == 0L && dur == 0L -> {
                    Log.i(TAG, "Playback error?")
                    resultPlayer(url, 0, 0, false)
                }

                pos > 0L -> {
                    Log.i(TAG, "Playback stopped with no duration! ENDED")
                    resultPlayer(url, pos.toInt(), pos.toInt(), true)
                }
            }
        } else {
            Log.e(TAG, "Invalid state [resultCode=$resultCode]")
        }
    }

    private fun handleMpvPlayerResult(intent: Intent, resultCode: Int, videoUrl: String) {
        if (resultCode == RESULT_OK) {
            val pos = intent.getIntExtra("position", 0)
            val dur = intent.getIntExtra("duration", 0)
            when {
                dur > 0 -> {
                    val ended = isAfterEndCreditsPosition(pos.toLong(), dur.toLong())
                    Log.i(TAG, "Playback stopped [position=$pos, duration=$dur, ended:$ended]")
                    resultPlayer(videoUrl, pos, dur, ended)
                }

                dur == 0 && pos == 0 -> {
                    Log.i(TAG, "Playback completed")
                    resultPlayer(videoUrl, 0, 0, true)
                }
            }
        } else {
            Log.e(TAG, "MPV playback stopped or canceled [resultCode=$resultCode]")
        }
    }

    private fun handleUPlayerResult(intent: Intent, resultCode: Int, videoUrl: String) {
        when (resultCode) {
            RESULT_OK -> {
                val pos = intent.getLongExtra("position", 0L)
                val dur = intent.getLongExtra("duration", 0L)
                if (pos > 0L && dur > 0L) {
                    val ended =
                        intent.getBooleanExtra("isEnded", pos == dur) || isAfterEndCreditsPosition(
                            pos,
                            dur
                        )
                    Log.i(TAG, "Playback stopped [position=$pos, duration=$dur, ended=$ended]")
                    resultPlayer(videoUrl, pos.toInt(), dur.toInt(), ended)
                }
            }

            RESULT_CANCELED -> Log.e(
                TAG,
                "Playback Error. It isn't possible to get the duration or create the playlist."
            )

            else -> Log.e(TAG, "Invalid state [resultCode=$resultCode]")
        }
    }

    private fun handleViMuPlayerResult(intent: Intent, resultCode: Int, videoUrl: String) {
        when (resultCode) {
            RESULT_FIRST_USER -> {
                Log.i(TAG, "Playback completed")
                resultPlayer(videoUrl, 0, 0, true)
            }

            RESULT_CANCELED, RESULT_VIMU_START, RESULT_VIMU_ENDED -> {
                val pos = intent.getIntExtra("position", 0)
                val dur = intent.getIntExtra("duration", 0)
                if (pos > 0 && dur > 0) { // ViMu duration can be -1 on playback error
                    val ended = isAfterEndCreditsPosition(pos.toLong(), dur.toLong())
                    Log.i(TAG, "Playback stopped [position=$pos, duration=$dur, ended=$ended]")
                    resultPlayer(videoUrl, pos, dur, ended)
                }
            }

            RESULT_VIMU_ERROR -> Log.e(TAG, "Playback error")
            else -> Log.e(TAG, "Invalid state [resultCode=$resultCode]")
        }
    }

    private fun handleGenericPlayerResult(intent: Intent, resultCode: Int, videoUrl: String) {
        when (resultCode) {
            RESULT_FIRST_USER -> {
                Log.i(TAG, "Playback completed")
                resultPlayer(videoUrl, 0, 0, true)
            }

            RESULT_OK, RESULT_CANCELED, RESULT_VIMU_START, RESULT_VIMU_ENDED -> {
                val pos = intent.getIntExtra("position", 0)
                val dur = intent.getIntExtra("duration", 0)
                val ended = isAfterEndCreditsPosition(pos.toLong(), dur.toLong())
                Log.i(TAG, "Playback stopped [position=$pos, duration=$dur, ended=$ended]")
                resultPlayer(videoUrl, pos, dur, ended)
            }

            else -> Log.e(TAG, "Invalid state [resultCode=$resultCode]")
        }
    }

    private fun setupIntents() {
        resultLauncher =
            registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
                if(playbackUiOwner.isNotEmpty()&&playbackUiOwner!=ProfileRuntime.scopeKey("playback_ui"))endPlayerSession()
                else handlePlayerResult(result)
            }
        speechLauncher =
            registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
                handleSpeechResult(result)
            }
    }

    private fun setupUI() {
        hideSystemUI() // Must be invoked after setContentView!
    }

    override fun onXWalkInitStarted() {
        logDebug("onXWalkInitStarted()")
    }

    override fun onXWalkInitCancelled() {
        logDebug("onXWalkInitCancelled()")
        // Perform error handling here
        finish()
    }

    override fun onXWalkInitFailed() {
        logDebug("onXWalkInitFailed()")
        if (mXWalkUpdater == null) {
            mXWalkUpdater = MyXWalkUpdater(this, this)
        }
        setupXWalkApkUrl()
        mXWalkUpdater?.updateXWalkRuntime()
    }

    override fun onXWalkInitCompleted() {
        logDebug("onXWalkInitCompleted() isXWalkReady: ${mXWalkInitializer?.isXWalkReady}")
        if (mXWalkInitializer?.isXWalkReady == true) {
            useCrossWalk()
        }
    }

    override fun onXWalkUpdateCancelled() {
        logDebug("onXWalkUpdateCancelled()")
        // Perform error handling here
        finish()
    }

    private fun setupXWalkApkUrl() {
        val abi = MyXWalkEnvironment.getRuntimeAbi()
        val apkUrl = String.format(getString(R.string.xwalk_apk_link), abi)
        mXWalkUpdater!!.setXWalkApkUrl(apkUrl)
    }

    private fun migrateSettings() {
        lifecycleScope.launch {
            restoreStorage { callback ->
                if (callback.contains(JS_SUCCESS, true)) {
                    Log.d(TAG, "migrateSettings - Lampa settings restored. Restart.")
                    recreate()
                } else {
                    App.toast(R.string.settings_rest_fail)
                }
            }
            this@MainActivity.migrate = false
        }
    }

    suspend fun listenerCleanupAndSetup() {
        listenerMutex.withLock {
            cleanupListener()
            // Wait for cleanup to complete
            delay(1000.milliseconds) // Ensure cleanup completes
            // Verify cleanup
            browser?.evaluateJavascript(
                """
                (function() {
                    return {
                        listenerExists: typeof window._androidStorageListener !== 'undefined',
                        lampaReady: !!window.Lampa
                    };
                })()
                """.trimIndent()
            ) { result ->
                logDebug("Listener cleanup verification: $result")
            }
            setupListener()
        }
    }

    private fun setupListener() {
        logDebug("Setting up storage change listener...")
        browser?.evaluateJavascript(
            """
            (function() {
                // Only setup if not already exists
                if (typeof window._androidStorageListener === 'undefined') {
                    window._androidStorageListener = function(o) {
                        AndroidJS.storageChange(JSON.stringify(o));
                    };
                    if (Lampa && Lampa.Storage && Lampa.Storage.listener) {
                        try {
                            if (Lampa.Storage.listener.follow) {
                                console.log('Use listener.follow');
                                Lampa.Storage.listener.follow('change', window._androidStorageListener);
                            } else {
                                console.log('Use listener.add');
                                Lampa.Storage.listener.add('change', window._androidStorageListener);
                            }
                        } catch (e) {
                            console.error('Error adding listener:', e);
                        }
                        return 'LISTENER_ADDED';
                    }
                    return 'NO_LAMPA_STORAGE';
                }
                return 'LISTENER_EXISTS';
            })()
            """.trimIndent()
        ) { result ->
            logDebug("Listener setup result: $result")
        }
    }

    // Call this when the page is destroyed or navigated away
    fun cleanupListener() {
        logDebug("Starting listener cleanup...")
        browser?.evaluateJavascript(
            """
            (function() {
                if (typeof window._androidStorageListener !== 'undefined') {
                    if (Lampa && Lampa.Storage && Lampa.Storage.listener) {
                        try {
                            console.log('Use listener.remove');
                            Lampa.Storage.listener.remove('change', window._androidStorageListener);
                        } catch (e) {
                            console.error('Error removing listener:', e);
                        }
                    }
                    delete window._androidStorageListener;
                    console.log('Listener fully cleaned up');
                    return 'LISTENER_REMOVED';
                }
                return 'NO_LISTENER';
            })()
            """.trimIndent()
        ) { result ->
            logDebug("Listener cleanup result: $result")
        }
    }

    private fun syncLanguage() {
        runJsStorageChangeField("language") // apply App language
    }

    private fun syncStorage() {
        runJsStorageChangeField("activity", "{}") // get current lampaActivity
        runJsStorageChangeField("player_timecode") // for player params
        runJsStorageChangeField("playlist_next") // for player playlist
        runJsStorageChangeField("source") // get current catalog for Recs
        runJsStorageChangeField("account_use") // get bookmarks sync state
        runJsStorageChangeField("recomends_list", "[]") // force update recs
        runJsStorageChangeField("proxy_tmdb") // to get current baseUrlApiTMDB and baseUrlImageTMDB
    }

    fun getLampaTmdbUrls() {
        lifecycleScope.launch {
            browser?.evaluateJavascript(
                """
                (function() {
                    if(window.appready) {
                        console.log('Lampa ready, store baseUrlApiTMDB and baseUrlImageTMDB...');
                        AndroidJS.storageChange(JSON.stringify({name: 'baseUrlApiTMDB', value: Lampa.TMDB.api('')}))
                        AndroidJS.storageChange(JSON.stringify({name: 'baseUrlImageTMDB', value: Lampa.TMDB.image('')}))
                    } else {
                        console.log('Lampa not ready, wait load...');
                        Lampa.Listener.follow('app', function (e) {
                        if(e.type =='ready')
                            console.log('Lampa ready, store baseUrlApiTMDB and baseUrlImageTMDB...');
                            AndroidJS.storageChange(JSON.stringify({name: 'baseUrlApiTMDB', value: Lampa.TMDB.api('')}))
                            AndroidJS.storageChange(JSON.stringify({name: 'baseUrlImageTMDB', value: Lampa.TMDB.image('')}))
                        })
                    }
                    return '${JS_SUCCESS} setup TMDB URLs';
                })()
                """.trimIndent()
            ) { result -> logDebug(result.removeSurrounding("\"")) }

//            runVoidJsFunc(
//                "AndroidJS.storageChange",
//                "JSON.stringify({name: 'baseUrlApiTMDB', value: Lampa.TMDB.api('')})"
//            )
//            runVoidJsFunc(
//                "AndroidJS.storageChange",
//                "JSON.stringify({name: 'baseUrlImageTMDB', value: Lampa.TMDB.image('')})"
//            )
        }
    }

    // Function to sync bookmarks (Required only for Android TV 8+)
    // runVoidJsFunc("Lampa.Favorite.$action", "'$catgoryName', {id: $id}")
    // runVoidJsFunc("Lampa.Favorite.add", "'wath', ${Gson().toJson(card)}")
    private suspend fun syncBookmarks() = withContext(Dispatchers.Default) {
        if (!isTvContentProviderAvailable) return@withContext
        withContext(Dispatchers.Main) {
            runVoidJsFunc("Lampa.Favorite.init", "") // Initialize if no favorite
        }
        // logDebug("syncBookmarks() add to wath: ${App.context.wathToAdd}")
        App.context.wathToAdd.forEach { item ->
            val lampaCard = App.context.FAV?.card?.find { it.id == item.id } ?: item.card
            lampaCard?.let { card ->
                card.fixCard()
                val id = card.id?.toIntOrNull()
                id?.let {
                    val params =
                        if (card.type == "tv") "name: '${card.name}'" else "title: '${card.title}'"
                    withContext(Dispatchers.Main) {
                        runVoidJsFunc(
                            "Lampa.Favorite.add",
                            "'${LampaProvider.LATE}', {id: $id, type: '${card.type}', source: '${card.source}', img: '${card.img}', $params}"
                        )
                    }
                }
            }
            delay(500.milliseconds) // don't do it too fast
        }
        // Remove items from various categories
        listOf(
            LampaProvider.LATE to App.context.wathToRemove,
            LampaProvider.BOOK to App.context.bookToRemove,
            LampaProvider.LIKE to App.context.likeToRemove,
            LampaProvider.HIST to App.context.histToRemove,
            LampaProvider.LOOK to App.context.lookToRemove,
            LampaProvider.VIEW to App.context.viewToRemove,
            LampaProvider.SCHD to App.context.schdToRemove,
            LampaProvider.CONT to App.context.contToRemove,
            LampaProvider.THRW to App.context.thrwToRemove
        ).forEach { (category, items) ->
            // logDebug("syncBookmarks() remove from $category: $items")
            items.forEach { id ->
                withContext(Dispatchers.Main) {
                    runVoidJsFunc("Lampa.Favorite.remove", "'$category', {id: $id}")
                }
                delay(500.milliseconds) // don't do it too fast
            }
        }
        // don't do it again
        App.context.clearPending()
    }

    private fun dumpStorage(callback: (String) -> Unit) {
        val backupJavascript = """
            (function() {
                console.log('Backing up localStorage to App Prefs');
                try {
                    AndroidJS.clear();
                    let count = 0;
                    for (let i = 0; i < localStorage.length; i++) {
                        const key = localStorage.key(i);
                        AndroidJS.set(key, localStorage.getItem(key));
                        count++;
                    }
                    return '${JS_SUCCESS}. ' + count + ' items backed up.';
                } catch (error) {
                    return '${JS_FAILURE}: ' + error.message;
                }
            })()
            """.trimIndent()
        browser?.evaluateJavascript(backupJavascript) { result ->
            if (result.contains(JS_SUCCESS, true)) {
                logDebug("localStorage backed up. Result $result")
                callback(result) // Success
            } else {
                Log.e(TAG, "Failed to dump localStorage.")
                callback(result) // Failure
            }
        }
    }

    private fun restoreStorage(callback: (String) -> Unit) {
        val restoreJavascript = """
            (function() {
                console.log('Restoring localStorage from App Prefs');
                try {
                    AndroidJS.dump();
                    var len = AndroidJS.size();
                    for (i = 0; i < len; i++) {
                        var key = AndroidJS.key(i);
                        console.log('[' + key + ']');
                        localStorage.setItem(key, AndroidJS.get(key));
                    }
                    return '${JS_SUCCESS}. ' + len + ' items restored.';
                } catch (error) {
                    return '${JS_FAILURE}: ' + error.message;
                }
            })()
            """.trimIndent()
        browser?.evaluateJavascript(restoreJavascript) { result ->
            if (result.contains(JS_SUCCESS, true)) {
                logDebug("localStorage restored. Result $result")
                callback(result) // Success
            } else {
                Log.e(TAG, "Failed to restore localStorage.")
                callback(result) // Failure
            }
        }
    }

    private fun clearStorage() {
        browser?.evaluateJavascript("localStorage.clear()") { Log.d(TAG, "localStorage cleared") }
    }

    private fun processIntent(intent: Intent?, delay: Long = 0) {
        // Log intent data for debugging
        debugLogIntentData(TAG, intent)
        intent ?: return
        if(BuildConfig.APPLICATION_ID.endsWith(".mc")&&ProfileRuntime.enabled()&&!trustedHomeNavigation){
            val scope=ProfileRuntime.snapshot()
            val personalLink=intent.data!=null||intent.getBooleanExtra("continueWatch",false)||intent.hasExtra("media")
            if(personalLink&&(!scope.optBoolean("ready",false)||ProfileRuntime.isChild()))return
        }
        // Parse intent extras
        val sid = intent.getStringExtra("id") ?: intent.getIntExtra("id", -1)
            .toString() // Change to String
        val mediaType = intent.getStringExtra("media") ?: ""
        val source = intent.getStringExtra("source") ?: lampaSource.ifEmpty { "tmdb" }
        // Parse intent data
        intent.data?.let { uri ->
            parseUriData(intent, uri, delay)
        }
        // Handle PlayNext
        if (intent.getBooleanExtra("continueWatch", false) == true) {
            handleContinueWatch(intent, delay)
            // Handle opening a card
        } else if (sid != "-1" && mediaType.isNotEmpty()) {
            handleOpenCard(intent, sid, mediaType, source, delay)
        }
        // Handle search command
        intent.getStringExtra("cmd")?.let { cmd ->
            when (cmd) {
                "open_settings" -> showMenuDialog()
            }
        }
        // Handle an external search query (e.g. from a voice assistant or a
        // launcher): open LAMPA's own search screen prefilled with the query
        // via the public Lampa.Search API. If a search screen is already open
        // (a repeated external query), close it first — Lampa.Search.open() is
        // not idempotent and would stack a new search over the previous one.
        // runVoidJsFunc queues the call until the web app is ready, so a cold
        // start is fine.
        intent.getStringExtra(SearchManager.QUERY)?.let { query ->
            if (query.isNotBlank()) {
                val escaped = query.replace("\\", "\\\\").replace("'", "\\'")
                val opener = "(function(q){" +
                        "if(!window.Lampa||!Lampa.Search)return;" +
                        "try{if(document.body&&document.body.classList" +
                        ".contains('search--open'))Lampa.Search.close();}catch(e){}" +
                        "Lampa.Search.open({input:q});" +
                        "})"
                runVoidJsFunc(opener, "'$escaped'")
            }
        }
        // Сырой mc_js только из mcOpen (trustedHomeNavigation); внешний startActivity не получает флаг
        if (trustedHomeNavigation) {
            intent.getStringExtra("mc_js")?.let { fn ->
                if (fn.isNotBlank()) runVoidJsFunc(fn, intent.getStringExtra("mc_arg") ?: "")
            }
            intent.removeExtra("mc_js")
            intent.removeExtra("mc_arg")
        }
        // Fix initial focus
        if(!intent.getBooleanExtra("mc_hold_shell",false)&&!intent.getBooleanExtra("mc_keep_shell",false))browser?.setFocus()
    }

    // Helper function to handle URI data
    private fun parseUriData(
        intent: Intent,
        uri: Uri,
        delay: Long = 0L
    ) {
        if (uri.host?.contains("themoviedb.org") == true && uri.pathSegments.size >= 2) {
            val videoType = uri.pathSegments[0]
            val sid = "\\d+".toRegex().find(uri.pathSegments[1])?.value // Keep as String
            if (videoType in listOf(
                    "movie",
                    "tv"
                ) && sid?.toIntOrNull() != null
            ) { // Change comparison to String
                handleTmdbIntent(intent, videoType, sid, delay) // Pass id as String
            }
        }
        when (intent.action) {
            "GLOBALSEARCH" -> handleGlobalSearch(intent, uri, delay)
            else -> handleChannelIntent(uri, delay)
        }
    }

    // Helper function to handle TMDB intents
    private fun handleTmdbIntent(
        intent: Intent,
        videoType: String,
        sid: String,
        delay: Long = 0
    ) { // Change id to String
        val source = intent.getStringExtra("source") ?: "tmdb"
        val card = "{id: '$sid', source: 'tmdb'}" // Use String id in JSON
        lifecycleScope.launch {
            openLampaContent(
                "{id: '$sid', method: '$videoType', source: '$source', component: 'full', card: $card}",
                delay
            )
        }
    }

    // Helper function to handle global search
    // content://top.rootu.lampa.atvsearch/video/508883#Intent;action=GLOBALSEARCH
    private fun handleGlobalSearch(intent: Intent, uri: Uri, delay: Long = 0) {
        val sid = uri.lastPathSegment // Keep as String
        val videoType = intent.extras?.getString(SearchManager.EXTRA_DATA_KEY) ?: ""
        // Handle global search case
        if (videoType in listOf("movie", "tv") && sid?.toIntOrNull() != null)
            handleTmdbIntent(intent, videoType, sid, delay) // Pass id as String
    }

    // Helper function to handle channel intents
    private fun handleChannelIntent(uri: Uri, delay: Long = 0) {
        if (uri.encodedPath?.contains("update_channel") == true) {
            val channel = uri.encodedPath?.substringAfterLast("/") ?: ""
            val params = when (channel) {
                LampaProvider.RECS -> {
                    "{" +
                            "title: '${getString(R.string.title_main)} - ${
                                lampaSource.uppercase(
                                    Locale.getDefault()
                                )
                            }'," +
                            "component: 'main'," +
                            "source: '$lampaSource'," +
                            "url: ''" +
                            "}"
                }

                LampaProvider.LIKE, LampaProvider.BOOK, LampaProvider.HIST -> {
                    "{" +
                            "title: '${getChannelDisplayName(channel)}'," +
                            "component: '${if (channel == "book") "bookmarks" else "favorite"}'," +
                            "type: '$channel'," +
                            "url: ''," +
                            "page: 1" +
                            "}"
                }

                else -> ""
            }

            if (params.isNotEmpty()) {
                lifecycleScope.launch {
                    openLampaContent(params, delay)
                }
            }
        }
    }

    private fun handleContinueWatch(intent: Intent, delay: Long = 0) {
        lifecycleScope.launch {
            // fallback to lampaActivity?
            val activityJson = intent.getStringExtra("lampaActivity") ?: return@launch
            if (isValidJson(activityJson)) {
                openLampaContent(activityJson, delay) // needed to match state
                delay(delay.milliseconds) // need to sure content loaded and activity stored
                if (intent.getBooleanExtra("android.intent.extra.START_PLAYBACK", false)) {
                    val card = getCardFromActivity(activityJson) ?: return@launch
                    val state = playerStateManager.findStateByCard(card) ?: return@launch
                    // val matchingStates = playerStateManager.findMatchingStates(activityJson)
                    // playerStateManager.debugKeyMatching(activityJson)
                    when {
                        state.currentItem != null -> { // matchingStates.isNotEmpty()
                            // val state = matchingStates.maxByOrNull { it.lastUpdated }!!
                            val currentItem = state.playlist.getOrNull(state.currentIndex)
                            val playJsonObj = playerStateManager.getStateJson(state).apply {
                                // Set title
                                currentItem?.title?.takeIf { it.isNotEmpty() }?.let { title ->
                                    if (!has("title")) put("title", title)
                                }
                                // Apply current item's timeline to root
                                currentItem?.timeline?.let { timeline ->
                                    put("timeline", JSONObject().apply {
                                        put("hash", timeline.hash)
                                        put("time", timeline.time)
                                        put("duration", timeline.duration)
                                        put("percent", timeline.percent)
                                        timeline.profile?.let { put("profile", it) }
                                    })
                                    // Also ensure the current position is set
                                    put("position", timeline.time.toLong())
                                }
                                // Player-specific flag
                                put("from_state", true)
                            }
                            // logDebug("playJsonObj ${playJsonObj.toString(2)}")
                            runPlayer(playJsonObj, "", activityJson)
                        }

                        else -> {
                            logDebug("No matching state found for card")
                        }
                    }
                }
            }
        }
    }

    // Helper function to check card match
    private fun cardMatchesState(
        card: LampaCard,
        state: PlayerStateManager.PlaybackState
    ): Boolean {
        return (state.extras[LAMPA_CARD_KEY] as? String)?.let { storedJson ->
            getJson(storedJson, LampaCard::class.java)?.let { storedCard ->
                storedCard.id == card.id ||
                        (storedCard.title == card.title && storedCard.release_year == card.release_year)
            }
        } == true
    }

    // Helper function to handle opening a card
    private fun handleOpenCard(
        intent: Intent?,
        sid: String,
        mediaType: String,
        source: String,
        delay: Long = 0
    ) { // Change intID to String
        val cardJson = intent?.getStringExtra(LAMPA_CARD_KEY)
            ?: "{id: '$sid', source: '$source'}" // Use String id in JSON
        lifecycleScope.launch {
            openLampaContent(
                "{id: '$sid', method: '$mediaType', source: '$source', component: 'full', card: $cardJson}",
                delay
            )
        }
    }

    private suspend fun openLampaContent(json: String, delay: Long = 0) {
        runVoidJsFunc("window.start_deep_link = ", json)
        delay(delay.milliseconds)
        // runVoidJsFunc("Lampa.Controller.toContent", "")
        // force close all menus and dialogs (fix for keepPlayerConnection = true)
        runVoidJsFunc("(function(){if(Lampa.Select.opened())Lampa.Select.close();if($('.modal').length)Lampa.Modal.close();Lampa.Controller.toContent()})", "")
        runVoidJsFunc("Lampa.Activity.push", json)
        // TODO: Clear back stack after deep link
        // browser?.clearHistory()
    }

    private fun showMenuDialog() {
        // Media Center — это домашний экран: меню со служебными пунктами (адрес Lampa, браузер, «Завершить работу») ему не нужно, закрывать его нельзя
        if (BuildConfig.APPLICATION_ID.endsWith(".mc")) return
        // Define menu items
        val menuItems = mutableListOf(
            MenuItem(
                title = if (isTvContentProviderAvailable) {
                    getString(R.string.update_chan_title)
                } else if (isAndroidTV) {
                    getString(R.string.update_home_title)
                } else {
                    getString(R.string.close_menu_title)
                },
                action = "updateOrClose",
                icon = if (isAndroidTV) R.drawable.round_refresh_24 else R.drawable.round_close_24
            ),
            MenuItem(
                title = getString(R.string.change_url_title),
                action = "showUrlInputDialog",
                icon = R.drawable.round_link_24
            ),
            MenuItem(
                title = getString(R.string.change_engine),
                action = "showBrowserInputDialog",
                icon = R.drawable.round_explorer_24
            ),
            MenuItem(
                title = getString(R.string.backup_restore_title),
                action = "showBackupDialog",
                icon = R.drawable.round_settings_backup_restore_24
            ),
            MenuItem(
                title = getString(
                    if (keepPlayerConnection) R.string.keep_connection_disable
                    else R.string.keep_connection_enable
                ),
                action = "toggleKeepConnection",
                icon = R.drawable.round_link_24
            ),
			MenuItem(
                title = getString(
                    if (useKodiPlaylist) R.string.kodi_playlist_disable
                    else R.string.kodi_playlist_enable
                ),
                action = "toggleKodiPlaylist",
                icon = R.drawable.round_link_24
            ),
            MenuItem(
                title = getString(R.string.exit),
                action = "appExit",
                icon = R.drawable.round_exit_to_app_24
            )
        )

        // Hide CrossWalk switcher on RuStore builds
        if (!isInstallPermissionDeclared(this))
            menuItems.removeAt(2)

        // Set up the adapter
        val adapter = ImgArrayAdapter(
            this,
            menuItems.map { it.title }.toList(),
            menuItems.map { it.icon }.toList()
        )

        // Configure the dialog
        val dialog = AlertDialog.Builder(this).apply {
            setTitle(getString(R.string.menu_title))
            setAdapter(adapter) { dialog, which ->
                dialog.dismiss()
                when (menuItems[which].action) {
                    "updateOrClose" -> {
                        if (isAndroidTV) {
                            Scheduler.scheduleUpdate(false)
                        }
                    }

                    "showUrlInputDialog" -> {
                        App.toast(R.string.change_note)
                        showUrlInputDialog()
                    }

                    "showBrowserInputDialog" -> {
                        App.toast(R.string.change_note)
                        showBrowserInputDialog()
                    }

                    "showBackupDialog" -> showBackupDialog()
                    "toggleKeepConnection" -> {
                        keepPlayerConnection = !keepPlayerConnection
                        playerKeepConnection = keepPlayerConnection // persist to prefs
                        showMenuDialog() // reopen so the item reflects the new state
                    }

					"toggleKodiPlaylist" -> {
                        useKodiPlaylist = !useKodiPlaylist
                        kodiPlaylistEnabled = useKodiPlaylist
                        showMenuDialog()
                    }
                    "appExit" -> appExit()
                }
            }
            // Set dismiss listener
            setOnDismissListener {
                isMenuVisible = false
                showFab(true)
            }
        }.create()
        // Show full screen dialog
        showFullScreenDialog(dialog)
        isMenuVisible = true
    }

    // Function to handle backup all settings
    private fun backupAllSettings() {
        lifecycleScope.launch {
            dumpStorage { callback ->
                if (callback.contains(JS_SUCCESS, true)) { // .trim().removeSurrounding("\"")
                    val itemsCount = callback.substringAfter("$JS_SUCCESS.")
                        .substringBefore("items")
                        .trim()
                        .toIntOrNull() ?: 0
                    // Proceed with saving settings if dumpStorage successful
                    if (backupSettings(Prefs.APP_PREFERENCES) &&
                        backupSettings(Prefs.STORAGE_PREFERENCES) &&
                        validateStorageBackup(itemsCount)
                    ) {
                        App.toast(getString(R.string.settings_saved_toast, Backup.DIR.toString()))
                    } else {
                        App.toast(R.string.settings_save_fail)
                    }
                } else {
                    App.toast(R.string.settings_save_fail)
                }
            }
        }
    }

    // Function to handle restore app settings
    private fun restoreAppSettings() {
        if (loadFromBackup(Prefs.APP_PREFERENCES)) {
            App.toast(R.string.settings_restored)
            recreate()
        } else {
            App.toast(R.string.settings_rest_fail)
        }
    }

    // Function to handle restore Lampa settings
    private fun restoreLampaSettings() {
        lifecycleScope.launch {
            if (loadFromBackup(Prefs.STORAGE_PREFERENCES)) {
                restoreStorage { callback ->
                    if (callback.contains(JS_SUCCESS, true)) {
                        App.toast(R.string.settings_restored)
                        recreate()
                    } else {
                        App.toast(R.string.settings_rest_fail)
                    }
                }
            } else {
                App.toast(R.string.settings_rest_fail)
            }
        }
    }

    // Function to handle restore default settings
    private fun restoreDefaultSettings() {
        clearStorage()
        appPrefs.edit { clear() }
        defPrefs.edit { clear() }
        lastPlayedPrefs.edit { clear() }
        // clearUrlHistory()
        recreate()
    }

    private fun showBackupDialog() {

        // Define menu items
        val menuItems = listOf(
            MenuItem(
                title = getString(R.string.backup_all_title),
                action = "backupAllSettings",
                icon = R.drawable.round_settings_backup_restore_24
            ),
            MenuItem(
                title = getString(R.string.restore_app_title),
                action = "restoreAppSettings",
                icon = R.drawable.round_refresh_24
            ),
            MenuItem(
                title = getString(R.string.restore_storage_title),
                action = "restoreLampaSettings",
                icon = R.drawable.round_refresh_24
            ),
            MenuItem(
                title = getString(R.string.default_setting_title),
                action = "restoreDefaultSettings",
                icon = R.drawable.round_close_24
            )
        )

        // Set up the adapter
        val adapter = ImgArrayAdapter(
            this,
            menuItems.map { it.title }.toList(),
            menuItems.map { it.icon }.toList()
        )

        // Configure the dialog
        val dialog = AlertDialog.Builder(this).apply {
            setTitle(getString(R.string.backup_restore_title))
            setAdapter(adapter) { dialog, which ->
                dialog.dismiss()
                when (menuItems[which].action) {
                    "backupAllSettings" -> backupAllSettings()
                    "restoreAppSettings" -> restoreAppSettings()
                    "restoreLampaSettings" -> restoreLampaSettings()
                    "restoreDefaultSettings" -> restoreDefaultSettings()
                }
            }
        }.create()
        // Show the dialog
        showFullScreenDialog(dialog)
        // Set active row
        adapter.setSelectedItem(0)

        // Check storage permissions
        if (!PermHelpers.hasStoragePermissions(this)) {
            PermHelpers.verifyStoragePermissions(this)
        }
    }

    private fun showBrowserInputDialog() {

        val xWalkVersion = "53.589.4"
        var selectedIndex = 0

        // Determine available browser options
        val (menuItemsTitles, menuItemsActions, menuIcons) =
            if (Helpers.isWebViewAvailable(this)) {
                val webViewVersion = Helpers.getWebViewVersion(this)
                val webViewMajorVersion = try {
                    webViewVersion.substringBefore(".").toDouble()
                } catch (_: NumberFormatException) {
                    0.0
                }

                val isCrosswalkActive = SELECTED_BROWSER == "XWalk"

                val crosswalkTitle = if (isCrosswalkActive) {
                    "${getString(R.string.engine_crosswalk)} - ${getString(R.string.engine_active)} $xWalkVersion"
                } else {
                    if (webViewMajorVersion > 53.589) "${getString(R.string.engine_crosswalk_obsolete)} $xWalkVersion"
                    else "${getString(R.string.engine_crosswalk)} $xWalkVersion"
                }

                val webkitTitle = if (isCrosswalkActive) {
                    "${getString(R.string.engine_webkit)} $webViewVersion"
                } else {
                    "${getString(R.string.engine_webkit)} - ${getString(R.string.engine_active)} $webViewVersion"
                }

                val titles = listOf(crosswalkTitle, webkitTitle)
                val actions = listOf("XWalk", "SysView")
                val icons = listOf(R.drawable.round_explorer_24, R.drawable.round_explorer_24)
                selectedIndex = if (isCrosswalkActive) 0 else 1

                Triple(titles, actions, icons)
            } else { // No WebView
                val crosswalkTitle = if (SELECTED_BROWSER == "XWalk") {
                    "${getString(R.string.engine_crosswalk)} - ${getString(R.string.engine_active)} $xWalkVersion"
                } else {
                    "${getString(R.string.engine_crosswalk)} $xWalkVersion"
                }

                val titles = listOf(crosswalkTitle)
                val actions = listOf("XWalk")
                val icons = listOf(R.drawable.round_explorer_24)

                Triple(titles, actions, icons)
            }

        // Set up the adapter
        val adapter = ImgArrayAdapter(this, menuItemsTitles, menuIcons)

        // Configure the dialog
        val dialog = AlertDialog.Builder(this).apply {
            setTitle(getString(R.string.change_engine_title))
            setAdapter(adapter) { dialog, which ->
                dialog.dismiss()
                if (menuItemsActions[which] != SELECTED_BROWSER) {
                    appBrowser = menuItemsActions[which]
                    this@MainActivity.recreate()
                }
            }
        }.create()
        // Show the dialog
        showFullScreenDialog(dialog)
        // Set active row
        adapter.setSelectedItem(selectedIndex)
    }

    // ---- запуск без сети ----
    // Нет сети или сервер не отвечает: вместо окна ввода адреса показываем Lampa из кеша WebView; как только сеть появится и сервер
    // ответит, страница перезагружается в обычном режиме. Нет кеша (первый запуск) — остаётся прежнее окно ввода адреса.
    @Volatile private var offlineTried = false
    @Volatile private var offlineWatching = false
    @Volatile private var loadRetried = false

    private fun netUp(): Boolean = try {
        val cm = getSystemService(android.content.Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager
        @Suppress("DEPRECATION") cm.activeNetworkInfo?.isConnected == true
    } catch (_: Exception) { false }

    // Журнал запусков: последние 40 событий хранятся в приложении и при первой возможности отправляются на сервер (раздел «Журнал запусков» в панели).
    fun launchLog(msg: String) {
        try {
            val sp = getSharedPreferences("launch_log", MODE_PRIVATE)
            val arr = org.json.JSONArray(sp.getString("e", "[]"))
            arr.put(org.json.JSONObject().put("t", System.currentTimeMillis()).put("m", msg).put("net", netUp()))
            val keep = org.json.JSONArray()
            for (i in maxOf(0, arr.length() - 40) until arr.length()) keep.put(arr.get(i))
            sp.edit().putString("e", keep.toString()).apply()
        } catch (_: Exception) {
        }
    }

    private fun flushLaunchLog() {
        Thread {
            try {
                val sp = getSharedPreferences("launch_log", MODE_PRIVATE)
                val body = sp.getString("e", "[]") ?: return@Thread
                if (body == "[]" || LAMPA_URL.isEmpty()) return@Thread
                val id = android.provider.Settings.Secure.getString(contentResolver, android.provider.Settings.Secure.ANDROID_ID) ?: "x"
                val dev = android.os.Build.MODEL + "_" + id.takeLast(4)
                val c = java.net.URL("$LAMPA_URL/home/applog?dev=" + java.net.URLEncoder.encode(dev, "UTF-8")).openConnection() as java.net.HttpURLConnection
                c.requestMethod = "POST"; c.doOutput = true; c.connectTimeout = 4000; c.readTimeout = 4000; c.setRequestProperty("Content-Type", "application/json")
                c.outputStream.use { it.write(body.toByteArray()) }
                if (c.responseCode in 200..299) sp.edit().putString("e", "[]").apply()
                c.disconnect()
            } catch (_: Exception) {
            }
        }.start()
    }

    fun tryOfflineCache(view: android.webkit.WebView): Boolean {
        if (LAMPA_URL.isEmpty()) return false
        // после пробуждения ТВ сеть может подниматься несколько секунд: один раз повторяем обычную загрузку, прежде чем идти в кеш
        if (!loadRetried) {
            loadRetried = true
            launchLog("сервер не ответил (сеть: ${if (netUp()) "есть" else "нет"}), повтор через 4 с")
            view.postDelayed({ view.loadUrl(LAMPA_URL) }, 4000)
            return true
        }
        if (offlineTried) return false
        offlineTried = true
        launchLog("повтор не помог, открываю из кеша")
        return try {
            logDebug("tryOfflineCache: открываю из кеша $LAMPA_URL")
            view.settings.cacheMode = android.webkit.WebSettings.LOAD_CACHE_ELSE_NETWORK
            view.loadUrl(LAMPA_URL)
            watchServerReload(view)
            true
        } catch (e: Exception) {
            false
        }
    }

    private fun serverReachable(url: String): Boolean = try {
        val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
        c.connectTimeout = 3000; c.readTimeout = 3000; c.requestMethod = "GET"
        val ok = c.responseCode in 200..399
        c.disconnect(); ok
    } catch (_: Exception) { false }

    // пока показан кеш — раз в 10 секунд спрашиваем сервер; ответил (сеть вернулась или сервер включили) — перезагружаем в обычном режиме
    private fun watchServerReload(view: android.webkit.WebView) {
        if (offlineWatching) return
        offlineWatching = true
        Thread {
            try {
                var tries = 0
                while (tries++ < 720 && !isFinishing) {          // до двух часов
                    Thread.sleep(10_000)
                    if (serverReachable(LAMPA_URL)) {
                        runOnUiThread {
                            view.settings.cacheMode = android.webkit.WebSettings.LOAD_DEFAULT
                            offlineTried = false
                            view.loadUrl(LAMPA_URL)
                        }
                        break
                    }
                }
            } catch (_: Exception) {
            }
            offlineWatching = false
        }.start()
    }

    // Кеш не должен раздуваться: при запуске, если кеш приложения и WebView больше 300 МБ, удаляются самые старые файлы, а самые свежие
    // (около 25 МБ) остаются — очищать всё не нужно. У WebView удаляются только файлы записей кеша, служебные (индекс) не трогаются.
    private fun trimCaches() {
        Thread {
            try {
                val limit = 300L * 1024 * 1024
                val keep = 25L * 1024 * 1024
                val files = ArrayList<java.io.File>()
                fun walk(f: java.io.File, inWebCache: Boolean) {
                    if (f.isDirectory) {
                        val web = inWebCache || f.name == "Cache_Data" || f.name == "HTTP Cache"
                        f.listFiles()?.forEach { walk(it, web) }
                    } else if (!inWebCache || Regex("^[0-9a-f]{16}_[0-9s]$").matches(f.name)) {
                        files.add(f)
                    }
                }
                walk(cacheDir, false)
                walk(java.io.File(applicationInfo.dataDir, "app_webview"), false)
                var total = files.sumOf { it.length() }
                if (total > limit) {
                    logDebug("trimCaches: ${total / 1048576} МБ больше предела — оставляю самое свежее ~25 МБ")
                    var kept = 0L
                    for (f in files.sortedByDescending { it.lastModified() }) {
                        if (kept < keep) { kept += f.length(); continue }
                        try { total -= f.length(); f.delete() } catch (_: Exception) {}
                    }
                    logDebug("trimCaches: осталось ${total / 1048576} МБ")
                }
            } catch (_: Exception) {
            }
        }.start()
    }

    fun showUrlInputDialog(msg: String = "") {
        val mainActivity = this
        urlAdapter = UrlAdapter(mainActivity)
        val inputManager = getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager

        @SuppressLint("InflateParams")
        // Inflate the dialog view
        val view = layoutInflater.inflate(R.layout.dialog_input_url, null, false)
        val tilt = view.findViewById<TextInputLayout>(R.id.tiltLampaUrl)
        val input = view.findViewById<AutoCompleteTV>(R.id.etLampaUrl)

        // Build the dialog
        val builder = AlertDialog.Builder(mainActivity).apply {
            setTitle(R.string.input_url_title)
            setView(view)
            setPositiveButton(R.string.save) { _, _ -> handleSaveButtonClick(input) }
            setNegativeButton(R.string.cancel) { di, _ -> handleCancelButtonClick(di) }
            setNeutralButton(R.string.migrate) { _, _ -> } // Override later
        }

        // Show the dialog
        val dialog = builder.create()
            .apply {
                window?.apply {
                    // top position (no keyboard overlap)
                    attributes = attributes.apply {
                        gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
                        verticalMargin = 0.1F
                    }
                    // Automatically show the keyboard
                    setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE)
                }
            }

        showFullScreenDialog(dialog)

        // Set up the input field
        setupInputField(input, tilt, msg, dialog, inputManager)

        // Set up the migrate button
        dialog.getButton(BUTTON_NEUTRAL).setOnClickListener {
            handleMigrateButtonClick(dialog)
        }
    }

    // Helper function to set up the input field
    private fun setupInputField(
        input: AutoCompleteTV?,
        tilt: TextInputLayout?,
        msg: String,
        dialog: AlertDialog?,
        inputManager: InputMethodManager
    ) {
        input?.apply {
            setText(LAMPA_URL.ifEmpty { "http://lampa.mx" })
            if (msg.isNotEmpty()) {
                tilt?.isErrorEnabled = true
                tilt?.error = msg
            }
            setAdapter(urlAdapter)
            // Handle popup and overlayed buttons visibility
            onPopupVisibilityChanged = { isOverlay ->
                try {
                    dialog?.apply {
                        if (isOverlay) {
                            getButton(BUTTON_NEUTRAL)?.visibility = View.INVISIBLE
                            getButton(BUTTON_NEGATIVE)?.visibility = View.INVISIBLE
                        } else {
                            getButton(BUTTON_NEUTRAL)?.visibility = View.VISIBLE
                            getButton(BUTTON_NEGATIVE)?.visibility = View.VISIBLE
                        }
                    }
                } catch (_: Exception) {
                }
            }
            // Common setup for all API levels
            setOnItemClickListener { _, _, _, _ ->
                try {
                    dialog?.getButton(BUTTON_POSITIVE)?.requestFocus()
                } catch (_: Exception) {
                }
            }

            setOnKeyListener { view, keyCode, event ->
                try {
                    when {
                        // Handle ENTER key
                        keyCode == KeyEvent.KEYCODE_ENTER && event.action == KeyEvent.ACTION_UP -> {
                            if (isPopupShowing) {
                                dismissDropDown()
                            }
                            if (isAmazonDev)
                                inputManager.showSoftInput(this, 0)
                            else
                                inputManager.showSoftInput(this, InputMethodManager.SHOW_IMPLICIT)
                            true
                        }
                        // Handle BACK key
                        keyCode == KeyEvent.KEYCODE_BACK && event.action == KeyEvent.ACTION_UP -> {
                            if (!isPopupShowing) {
                                showDropDown()
                            }
                            inputManager.hideSoftInputFromWindow(view.windowToken, 0)
                            true
                        }
                        // For other keys or action phases
                        else -> false
                    }
                } catch (_: Exception) {
                    false
                }
            }

            setOnEditorActionListener { _, actionId, _ ->
                try {
                    when (actionId) {
                        EditorInfo.IME_ACTION_NEXT, EditorInfo.IME_ACTION_DONE -> {
                            inputManager.hideSoftInputFromWindow(windowToken, 0)
                            dialog?.getButton(BUTTON_POSITIVE)?.requestFocus()
                            true
                        }

                        else -> false
                    }
                } catch (_: Exception) {
                    false
                }
            }
        }
    }

    // Helper function to handle the save button click
    private fun handleSaveButtonClick(input: AutoCompleteTV?) {
        LAMPA_URL = input?.text.toString()
        if (isValidUrl(LAMPA_URL)) {
            Log.d(TAG, "URL '$LAMPA_URL' is valid")
            if (appUrl != LAMPA_URL) {
                appUrl = LAMPA_URL
                addUrlHistory(LAMPA_URL)
                browser?.loadUrl(LAMPA_URL)
                App.toast(R.string.change_url_press_back)
            } else {
                browser?.loadUrl(LAMPA_URL) // Reload current URL
            }
        } else {
            Log.d(TAG, "URL '$LAMPA_URL' is invalid")
            App.toast(R.string.invalid_url)
            showUrlInputDialog()
        }
        // hideSystemUI()
    }

    // Helper function to handle the cancel button click
    private fun handleCancelButtonClick(dialog: DialogInterface) {
        dialog.cancel()
        if (LAMPA_URL.isEmpty() && appUrl.isEmpty()) {
            appExit()
        } else {
            LAMPA_URL = appUrl
            // hideSystemUI()
        }
    }

    private fun addProgressIndicatorToDialog(dialog: AlertDialog) {
        // Access the dialog's root view
        val rootView = dialog.window?.decorView?.findViewById<ViewGroup>(android.R.id.content)
        // Create a LinearProgressIndicator
        progressIndicator = LinearProgressIndicator(this).apply {
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            )
            isIndeterminate = true // Set to indeterminate mode
            trackCornerRadius = dp2px(dialog.context, 2.0F)
            trackThickness = dp2px(dialog.context, 4.0F)
            visibility = LinearProgressIndicator.VISIBLE // Make it visible by default
        }
        // Add the progress indicator to the dialog's layout
        if (rootView is LinearLayout) {
            // rootView.addView(progressIndicator) // Add at the bottom
            rootView.addView(progressIndicator, 0) // Add at the top of the layout
        } else {
            // If the root view is not a LinearLayout, wrap it in a new LinearLayout
            val newLayout = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                layoutParams = ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
            }
            // Remove the rootView from its current parent (if it has one)
            (rootView?.parent as? ViewGroup)?.removeView(rootView)
            // Add the progress indicator and the existing rootView to the new layout
            newLayout.addView(progressIndicator) // Add to top
            newLayout.addView(rootView)
            // Set the new layout as the dialog's content view
            dialog.window?.setContentView(newLayout)
        }
    }

    // Function to control the visibility of the progress indicator
    private fun setProgressIndicatorVisibility(isVisible: Boolean) {
        if (::progressIndicator.isInitialized) {
            progressIndicator.visibility =
                if (isVisible) LinearProgressIndicator.VISIBLE else LinearProgressIndicator.GONE
        }
    }

    private fun handleMigrateButtonClick(dialog: AlertDialog) {
        // Add a LinearProgressIndicator to the dialog
        addProgressIndicatorToDialog(dialog)
        // Show the migrate progress
        setProgressIndicatorVisibility(true)
        lifecycleScope.launch {
            dumpStorage { callback ->
                setProgressIndicatorVisibility(false) // Hide the progress indicator
                if (callback.contains(JS_SUCCESS, true)) { // .trim().removeSurrounding("\"")
                    Log.d(TAG, "handleMigrateButtonClick: dumpStorage completed - $callback")
                    this@MainActivity.migrate = true
                    val input = dialog.findViewById<AutoCompleteTV>(R.id.etLampaUrl)
                    handleSaveButtonClick(input)
                    dialog.dismiss()
                } else {
                    Log.e(TAG, "handleMigrateButtonClick: dumpStorage failed - $callback")
                    // try use backup on Error
                    if (loadFromBackup(Prefs.STORAGE_PREFERENCES)) {
                        Log.d(TAG, "handleMigrateButtonClick: do migrate from backup")
                        this@MainActivity.migrate = true
                        val input = dialog.findViewById<AutoCompleteTV>(R.id.etLampaUrl)
                        handleSaveButtonClick(input)
                        dialog.dismiss()
                    } else App.toast(R.string.settings_migrate_fail)
                }
            }
        }
    }

    // Helper function to validate URLs
    private fun isValidUrl(url: String): Boolean {
        return URL_PATTERN.matcher(url).matches()
    }

    fun appExit() {
        if (BuildConfig.APPLICATION_ID.endsWith(".mc")) {
            mcHost?.show()
            return
        }
        browser?.apply {
            clearCache(true)
            destroy()
        }
        finishAffinity() // exitProcess(1)
    }

    fun setPlayerPackage(packageName: String, isIPTV: Boolean) {
        SELECTED_PLAYER = packageName.lowercase(Locale.getDefault())
        if (isIPTV)
            tvPlayer = SELECTED_PLAYER!!
        else
            appPlayer = SELECTED_PLAYER!!
    }

    @SuppressLint("InflateParams")
    fun runPlayer(jsonObject: JSONObject) {
        logDebug("runPlayer(jsonObject) - add lampaActivity to params")
        runPlayer(jsonObject, "", lampaActivity)
    }

    fun displaySpeechRecognizer() {
        // На ТВ-приставках микрофон пульта доступен только системному окну голосового ввода (Google): собственный AudioRecord слышит тишину
        // («NO_SPEECH_DETECTED»). Поэтому сначала запускаем системное окно, а встроенное остаётся запасным (если окна нет).
        if (isTvBox) {
            try {
                speechLauncher.launch(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
                    putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                    putExtra(RecognizerIntent.EXTRA_LANGUAGE, if (appLang.isNotBlank()) appLang else "ru-RU")
                    putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1)
                    putExtra(RecognizerIntent.EXTRA_CALLING_PACKAGE, packageName)
                })
                return
            } catch (_: Exception) {
                logDebug("system voice input not found, using built-in dialog")
            }
        }
        if (VERSION.SDK_INT < 18) {
            if (!SpeechRecognizer.isRecognitionAvailable(baseContext)) {
                logDebug("SpeechRecognizer not available!")
            } else {
                logDebug("SpeechRecognizer available!")
            }
            // Create an intent that can start the Speech Recognizer activity
            val intent = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
                putExtra(
                    RecognizerIntent.EXTRA_LANGUAGE_MODEL,
                    RecognizerIntent.LANGUAGE_MODEL_FREE_FORM
                )
                putExtra(RecognizerIntent.EXTRA_CALLING_PACKAGE, packageName)
            }
            // This starts the activity and populates the intent with the speech text.
            try {
                speechLauncher.launch(intent)
            } catch (_: Exception) {
                App.toast(R.string.not_found_speech, false)
            }
        } else {
            verifyMicPermissions(this)

            var dialog: AlertDialog? = null
            val view = layoutInflater.inflate(R.layout.dialog_search, null, false)
            val etSearch = view.findViewById<AppCompatEditText?>(R.id.etSearchQuery)
            val btnVoice = view.findViewById<AppCompatImageButton?>(R.id.btnVoiceSearch)
            val inputManager =
                getSystemService(INPUT_METHOD_SERVICE) as? InputMethodManager

            etSearch?.apply {
                setOnClickListener {
                    if (isAmazonDev)
                        inputManager?.showSoftInput(this, 0)
                    else
                        inputManager?.showSoftInput(this, InputMethodManager.SHOW_IMPLICIT)
                }
                imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI
            }?.setOnEditorActionListener(TextView.OnEditorActionListener { _, actionId, _ ->
                if (actionId == EditorInfo.IME_ACTION_SEARCH || actionId == EditorInfo.IME_ACTION_DONE) {
                    dialog?.getButton(BUTTON_POSITIVE)?.performClick()
                    return@OnEditorActionListener true
                }
                false
            })

            btnVoice?.apply {
                val context = this.context
                if (!hasMicPermissions(context)) {
                    this.isEnabled = false
                    etSearch?.requestFocus()
                } else {
                    this.isEnabled = true
                }
                setOnClickListener {
                    val dots = view.findViewById<LinearLayout>(R.id.searchDots)
                    val progress = view.findViewById<SpeechProgressView>(R.id.progress)
                    val heights = intArrayOf(40, 56, 38, 55, 35)
                    progress.setBarMaxHeightsInDp(heights)
                    if (hasMicPermissions(context)) {
                        etSearch?.hint = context.getString(R.string.search_voice_hint)
                        btnVoice.visibility = View.GONE
                        dots?.visibility = View.VISIBLE
                    } else {
                        App.toast(R.string.search_requires_record_audio)
                        btnVoice.visibility = View.VISIBLE
                        dots?.visibility = View.GONE
                        etSearch?.hint = context.getString(R.string.search_is_empty)
                        etSearch?.requestFocus()
                    }
                    // start Speech
                    startSpeech(
                        getString(R.string.search_voice_hint),
                        progress
                    ) { result, final, success ->
                        etSearch?.hint = ""
                        etSearch?.setText(result)
                        if (final) {
                            btnVoice.visibility = View.VISIBLE
                            dots?.visibility = View.GONE
                        }
                        if (final && success) {
                            dialog?.getButton(BUTTON_POSITIVE)?.requestFocus() //.performClick()
                        }
                    }
                }
            }

            dialog = AlertDialog.Builder(this)
                .setView(view)
                .setPositiveButton(android.R.string.ok) { _, _ ->
                    dialog?.dismiss()
                    val query = etSearch.text.toString()
                    if (query.isNotEmpty()) {
                        runVoidJsFunc(
                            "window.voiceResult",
                            "'" + query.replace("'", "\\'") + "'"
                        )
                    } else { // notify user
                        App.toast(R.string.search_is_empty)
                    }
                }
                .create()
                .apply {
                    window?.apply {
                        // top position (no keyboard overlap)
                        attributes = attributes.apply {
                            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
                            verticalMargin = 0.1F
                        }
                    }
                }
            // show fullscreen dialog
            showFullScreenDialog(dialog)
            // run voice search
            btnVoice?.performClick()
        }
    }

    @Suppress("DEPRECATION")
    private fun showFullScreenDialog(dialog: AlertDialog?) {
        dialog?.apply {
            // Set the dialog to be not focusable initially
            window?.setFlags(
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
            )
            if (VERSION.SDK_INT < Build.VERSION_CODES.R) {
                // Use systemUiVisibility for older APIs
                window?.decorView?.systemUiVisibility = (View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                        or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        or View.SYSTEM_UI_FLAG_FULLSCREEN
                        or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        or if (VERSION.SDK_INT >= Build.VERSION_CODES.KITKAT) {
                    View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                } else {
                    0
                })
                // make navbar translucent
                if (VERSION.SDK_INT >= Build.VERSION_CODES.KITKAT) {
                    window?.addFlags(WindowManager.LayoutParams.FLAG_TRANSLUCENT_NAVIGATION)
                }
            }
            // Show the dialog
            show()
            // Ensure the window is ready before accessing WindowInsetsController
            window?.decorView?.let { decorView ->
                if (VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    // Use WindowInsetsController for API 30+
                    decorView.viewTreeObserver.addOnWindowAttachListener(object :
                        ViewTreeObserver.OnWindowAttachListener {
                        override fun onWindowAttached() {
                            // Window is attached, now it's safe to access WindowInsetsController
                            decorView.windowInsetsController?.let { controller ->
                                controller.hide(WindowInsets.Type.statusBars() or WindowInsets.Type.navigationBars())
                                controller.systemBarsBehavior =
                                    WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                            }
                            // Remove the listener to avoid memory leaks
                            decorView.viewTreeObserver.removeOnWindowAttachListener(this)
                        }

                        override fun onWindowDetached() {
                            // No-op
                        }
                    })
                }
            }
            // Clear the not focusable flag after the dialog is shown
            window?.clearFlags(WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE)
        }
    }

    private fun startSpeech(
        msg: String,
        progress: SpeechProgressView,
        onSpeech: (result: String, final: Boolean, success: Boolean) -> Unit
    ): Boolean {
        val context = this.baseContext
        if (hasMicPermissions(context)) {
            try {
                // you must have android.permission.RECORD_AUDIO granted at this point
                Speech.init(context, packageName)?.apply {
                    val languageToLocaleMap = mapOf(
                        "ru" to "ru-RU",
                        "en" to "en-US",
                        "be" to "be-BY",
                        "uk" to "uk-UA",
                        "zh" to "zh-CN",
                        "bg" to "bg-BG",
                        "pt" to "pt-PT",
                        "cs" to "cs-CZ"
                    )
                    val langParts = appLang.split("-")
                    val langTag = if (langParts.size >= 2) {
                        "${langParts[0]}-${langParts[1]}"
                    } else {
                        languageToLocaleMap[langParts[0]] ?: appLang
                    }
                    // Optional IETF language tag (as defined by BCP 47), for example "en-US" required for
                    // https://developer.android.com/reference/android/speech/RecognizerIntent#EXTRA_LANGUAGE
                    // so Locale with Country must be provided (en_US ru_RU etc)
                    val locale = if (langTag.split("-").size >= 2) Locale(
                        langTag.split("-")[0],
                        langTag.split("-")[1]
                    ) else if (langTag.isNotEmpty()) Locale(langTag) else Locale.getDefault()
                    logDebug("appLang = $appLang")
                    logDebug("langTag = $langTag")
                    logDebug("locale = $locale")
                    setLocale(locale)
                    startListening(progress, object : SpeechDelegate {
                        private var success = true
                        override fun onStartOfSpeech() {
                            Log.i("speech", "speech recognition is now active. $msg")
                        }

                        override fun onSpeechRmsChanged(value: Float) {
                            //Log.d("speech", "rms is now: $value")
                        }

                        override fun onSpeechPartialResults(results: List<String>) {
                            val str = StringBuilder()
                            for (res in results) {
                                str.append(res).append(" ")
                            }
                            Log.i(
                                "speech",
                                "partial result: " + str.toString().trim { it <= ' ' })
                            onSpeech(str.toString().trim { it <= ' ' }, false, success)
                        }

                        override fun onSpeechResult(res: String) {
                            Log.i("speech", "result: $res")
                            if (res.isEmpty())
                                success = false
                            onSpeech(res, true, success)
                        }
                    })
                }
                if (BuildConfig.DEBUG)
                    Logger.setLogLevel(Logger.LogLevel.DEBUG)
                return true
            } catch (_: SpeechRecognitionNotAvailable) {
                Log.e("speech", "Speech recognition is not available on this device!")
                App.toast(R.string.search_no_voice_recognizer)
                // You can prompt the user if he wants to install Google App to have
                // speech recognition, and then you can simply call:
                SpeechUtil.redirectUserToGoogleAppOnPlayStore(context)
                // to redirect the user to the Google App page on Play Store
            } catch (_: GoogleVoiceTypingDisabledException) {
                Log.e("speech", "Google voice typing must be enabled!")
            }
        }
        return false
    }

    private fun showFab(show: Boolean = true) {
        val fab: FloatingActionButton? = findViewById(R.id.fab)
        if (show && !isTvBox && !BuildConfig.APPLICATION_ID.endsWith(".mc")) {
            fab?.show()
            lifecycleScope.launch {
                delay(15000.milliseconds)
                fab?.hide()
            }
        } else
            fab?.hide()
    }

    private fun setupFab() { // FAB
        val fab: FloatingActionButton? = findViewById(R.id.fab)
        fab?.apply {
            setImageDrawable(
                AppCompatResources.getDrawable(
                    context,
                    R.drawable.lampa_logo_round
                )
            )
            customSize = dp2px(context, 32f)
            setMaxImageSize(dp2px(context, 30f))
            backgroundTintList = ColorStateList.valueOf(
                ContextCompat.getColor(
                    context,
                    R.color.lampa_background
                )
            )
            setOnClickListener {
                showMenuDialog()
                showFab(false)
            }
        }
        if (!isMenuVisible) {
            showFab(true)
        }
    }

    private fun runJsStorageChangeField(name: String) {
        runVoidJsFunc(
            "AndroidJS.storageChange",
            "JSON.stringify({" +
                    "name: '${name}'," +
                    "value: Lampa.Storage.field('${name}')" +
                    "})"
        )
    }

    private fun runJsStorageChangeField(name: String, default: String) {
        runVoidJsFunc(
            "AndroidJS.storageChange",
            "JSON.stringify({" +
                    "name: '${name}'," +
                    "value: Lampa.Storage.get('${name}', '$default')" +
                    "})"
        )
    }

    /**
     * Toggle Lampa's own screensaver. We deliberately keep the page's JS timers running while
     * an external player is in front (RCH socket heartbeat), so Lampa's inactivity timer also
     * survives and fires its screensaver behind the player — burning CPU/network (the default
     * "aerial" type streams 4K clips).
     *
     * This is a runtime-only flag inside Lampa's Screensaver instance; nothing is written to
     * Lampa.Storage. If the process is killed during playback, the reloaded page enables the
     * screensaver by itself, and the user's own screensaver setting is a separate flag we never
     * touch.
     */
    private fun setLampaScreensaver(enabled: Boolean) {
        runVoidJsFunc("Lampa.Screensaver." + if (enabled) "enable" else "disable", "")
    }

    fun runVoidJsFunc(funcName: String, params: String, retry: Int = 0) {
        if (browserInitComplete && loaderView.isGone) {
            logDebug("runVoidJsFunc $funcName")
            val js = ("(function(){"
                    + "try {"
                    + funcName + "(" + params + ");"
                    + "return '${JS_SUCCESS}';"
                    + "} catch (e) {"
                    + "return '${JS_FAILURE}: ' + e.message;"
                    + "}"
                    + "})();")
            browser?.evaluateJavascript(
                js
            ) { r: String ->
                Log.i(
                    "runVoidJsFunc",
                    "$funcName($params) Result $r"
                )
                // страница ещё грузится (после возврата из плеера): запись о просмотре не должна теряться — повторяем, пока Lampa не готова
                if (r.contains(JS_FAILURE) && (funcName.startsWith("Lampa.Timeline") || funcName.startsWith("Lampa.Favorite")) && retry < 40) {
                    Handler(Looper.getMainLooper()).postDelayed({ runVoidJsFunc(funcName, params, retry + 1) }, 1500)
                }
            }
        } else {
            logDebug("runVoidJsFunc add to delayedVoidJsFunc $funcName")
            delayedVoidJsFunc.add(listOf(funcName, params))
        }
    }

    /**
     *  AI roxxx
     */

    @SuppressLint("InflateParams")
    fun runPlayer(jsonObject: JSONObject, launchPlayer: String = "", activity: String? = null) {
        try {
            val videoUrl = jsonObject.optString("url").takeIf { it.isNotBlank() } ?: run {
                App.toast(R.string.invalid_url, true)
                return
            }
            val timelineOnly = jsonObject.optBoolean("home_timeline_only", false)
            val playActivity = if (timelineOnly) JSONObject().apply {
                put("component", "home_file"); put("source", "home_file"); put("url", videoUrl)
            }.toString() else activity?.takeIf { it.isNotEmpty() } ?: lampaActivity

            Log.i(TAG, "runPlayer: url=${videoUrl.take(70)} keys=${jsonObject.keys().asSequence().toList()} home_player=${jsonObject.opt("home_player")} launchPlayer='$launchPlayer' appPlayer='${appPlayer}' prefs=$mcPrefPlayer/$mcPrefTorrent")
            // Pre-compute frequently used values
            val isIPTV = jsonObject.optBoolean("iptv", false)
            val isLIVE = jsonObject.optBoolean("need_check_live_stream", false)
            val isContinueWatch = jsonObject.optBoolean("from_state", false)
            // «Штатный» плеер Lampa (home_player=true) и ТВ — наш встроенный; «Android» — внешний (VIMU, если установлен)
            // Media Center: страница Lampa сообщает свои настройки плеера (AndroidJS.mcPlayerPrefs); если страница не пометила запуск сама,
            // решаем по ним: «встроенный» (не «android») — наш плеер без диалога; «android» — внешний с выбором
            val isTorrentLaunch = jsonObject.has("torrent_hash") || Regex("/stream[/?].*[?&]link=").containsMatchIn(videoUrl)
            val homePlayer = if (jsonObject.has("home_player")) jsonObject.optBoolean("home_player", false)
                else if (BuildConfig.APPLICATION_ID.endsWith(".mc") && !isIPTV && !isLIVE) {
                    val pref = (if (isTorrentLaunch) mcPrefTorrent else mcPrefPlayer)
                    Log.i(TAG, "mc плеер: torrent=$isTorrentLaunch pref=$pref url=${videoUrl.take(60)}")
                    pref.isNotEmpty() && pref != "android"
                } else false
            val selectedPlayer = if(ProfileRuntime.enabled()&&ProfileRuntime.isChild()) packageName else launchPlayer.takeIf { it.isNotBlank() }
                ?: (if (isIPTV || isLIVE) tvPlayer else appPlayer).takeIf { !it.isNullOrBlank() }
                ?: (if (isIPTV || isLIVE || homePlayer) null else listOf("net.gtvbox.videoplayer", "net.gtvbox.vimuhd")
                    .firstOrNull { pkg -> try { packageManager.getPackageInfo(pkg, 0); true } catch (_: Exception) { false } })
                ?: packageName
            val videoTitle =
                jsonObject.optString("title", if (isIPTV) "LAMPA TV" else "LAMPA video")
            // The card sent with the play request names this video; the stored activity is only as
            // fresh as the last storage event and can still be the page before.
            // Independent video/file sources must not inherit the card behind their popup.
            val card = if (timelineOnly) null
                else getCard(jsonObject.optJSONObject("card")) ?: getCardFromActivity(playActivity)

            // Headers handling
            var headers = prepareHeaders(jsonObject)

            val state = if (isContinueWatch && card != null) {
                // Get most recent state for card
                playerStateManager.findStateByCard(card)?.also {
                    headers = getHeadersFromState(it) ?: headers
                }
            } else {
                // Create new state
                val playlist = try {
                    when {
                        jsonObject.has("playlist") && (playerAutoNext || isIPTV) ->
                            playerStateManager.convertJsonToPlaylist(jsonObject.getJSONArray("playlist"))

                        else -> listOf(playerStateManager.convertJsonToPlaylistItem(jsonObject))
                    }
                } catch (e: Exception) {
                    logDebug("Error converting playlist: ${e.message}")
                    listOf(playerStateManager.convertJsonToPlaylistItem(jsonObject))
                }
                // safe start index
                val currentIndex = playlist.indexOfFirst { it.url == videoUrl }.coerceAtLeast(0)
                // fill required extras
                val extras = mutableMapOf<String, Any>().apply {
                    put("isIPTV", isIPTV)
                    put("isLIVE", isLIVE)
                    // прямой эфир из плагина ТВ: адрес «сейчас/далее» и размер буфера (сек)
                    jsonObject.optString("tv_now_url").takeIf { it.isNotBlank() }?.let { put("tv_now_url", it) }
                    if (jsonObject.has("tv_buffer")) put("tv_buffer", jsonObject.optInt("tv_buffer", -1))
                    if (jsonObject.has("tv_font")) put("tv_font", jsonObject.optInt("tv_font", 0))
                    if (jsonObject.has("tv_panel_alpha")) put("tv_panel_alpha", jsonObject.optInt("tv_panel_alpha", 70))
                    if (jsonObject.has("tv_astereo")) put("tv_astereo", jsonObject.optInt("tv_astereo", 1))
                    if (jsonObject.has("tv_center")) put("tv_center", jsonObject.optInt("tv_center", 0))
                    if (jsonObject.has("tv_afr")) put("tv_afr", jsonObject.optInt("tv_afr", 1))
                    jsonObject.optString("tv_dev").takeIf { it.isNotBlank() }?.let { put("tv_dev", it) }
                    // кино и сериалы: ключ тайтла для памяти звуковой дорожки на сервере, адрес сервера и предел качества профиля устройства
                    jsonObject.optString("audio_key").takeIf { it.isNotBlank() }?.let { put("audio_key", it) }
                    jsonObject.optString("home_base").takeIf { it.isNotBlank() }?.let { put("home_base", it) }
                    jsonObject.optString("home_tv_channel").takeIf { it.isNotBlank() }?.let { put("home_tv_channel", it) }
                    jsonObject.optString("home_auto_attempt").takeIf { it.isNotBlank() }?.let { put("home_auto_attempt", it) }
                    jsonObject.optString("home_auto_profile").takeIf { it.isNotBlank() }?.let { put("home_auto_profile", it) }
                    if (jsonObject.has("max_height")) put("max_height", jsonObject.optInt("max_height", 0))
                    // YouTube: звук отдельной дорожкой и сегменты рекламы, которые плеер перепрыгивает
                    jsonObject.optString("yt_audio").takeIf { it.isNotBlank() }?.let { put("yt_audio", it) }
                    jsonObject.optString("home_youtube_id").takeIf { it.isNotBlank() }?.let { put("home_youtube_id", it) }
                    jsonObject.optString("yt_skip").takeIf { it.isNotBlank() }?.let { put("yt_skip", it) }
                    jsonObject.optString("yt_quals").takeIf { it.isNotBlank() }?.let { put("yt_quals", it) }
                    jsonObject.optString("tl_meta").takeIf { it.isNotBlank() }?.let { put("tl_meta", it) }
                    jsonObject.optString("resume_playlist").takeIf { it.isNotBlank() }?.let { put("resume_playlist", it) }
                    if (jsonObject.has("vod_buf_mb")) put("vod_buf_mb", jsonObject.optInt("vod_buf_mb", 0))
                    jsonObject.optString("tv_surface").takeIf { it.isNotBlank() }?.let { put("tv_surface", it) }
                    // движок прямого эфира: пусто — ExoPlayer (HomePlayerActivity), mpv* — опытный плеер на libmpv
                    jsonObject.optString("tv_engine").takeIf { it.isNotBlank() }?.let { put("tv_engine", it) }
                    // NOTE: it used in PlayerStateManager for state match
                    activity?.let { put("lampaActivity", it) }
                    // Store headers as raw string array to extras
                    headers?.let { put("headers_array", it.asList()) }
                }
                // save new state
                playerStateManager.saveState(
                    activityJson = playActivity,
                    playlist = playlist,
                    currentIndex = currentIndex,
                    currentUrl = videoUrl,
                    startIndex = currentIndex,
                    extras = extras,
                    card = card
                )
            }
            state?.let {
                createBaseIntent(it)?.let {
                    // Get available players
                    val availablePlayers =
                        getAvailablePlayers(it).takeIf { it.isNotEmpty() } ?: run {
                            App.toast(R.string.no_player_activity_found, true)
                            return
                        }
                    // Check if selected player exists
                    if (selectedPlayer != null && availablePlayers.any {
                            it.activityInfo.packageName.equals(selectedPlayer, true)
                        }) {
                        // Configure and launch the selected player
                        configurePlayerIntent(
                            it,
                            selectedPlayer,
                            videoTitle,
                            isIPTV,
                            state,
                            headers,
                            playActivity
                        )
                        // Launch Player
                        launchPlayer(it)
                    } else {
                        // Show player selection dialog
                        Log.i(TAG, "runPlayer: ДИАЛОГ выбора плеера: selected='$selectedPlayer' homePlayer=$homePlayer доступно=${availablePlayers.map { p -> p.activityInfo.packageName }}")
                        showPlayerSelectionDialog(availablePlayers, jsonObject, isIPTV)
                    }
                }
            }
        } catch (e: Exception) {
            logDebug("Unexpected error: ${e.message}")
        }
    }

    private fun getHeadersFromState(state: PlayerStateManager.PlaybackState): Array<String>? {
        return (state.extras["headers_array"] as? List<*>)?.filterIsInstance<String>()
            ?.toTypedArray()
    }

    private fun createBaseIntent(
        state: PlayerStateManager.PlaybackState,
    ): Intent? {
        state.currentItem?.let { currentItem ->
            val safeUri = encodeUrlIfNeeded(currentItem.url).toUri()
            val intent = Intent(Intent.ACTION_VIEW).apply {
                data = safeUri
                setDataAndType(
                    safeUri,
                    /* if (currentItem.url.endsWith(".m3u8")) "application/vnd.apple.mpegurl" else */
                    "video/*"
                )
                flags = 0 // Clear any default flags
            }
            return intent
        }
        return null
    }

    private fun getAvailablePlayers(intent: Intent): List<ResolveInfo> {
        return if (VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            packageManager.queryIntentActivities(intent, PackageManager.MATCH_ALL)
                .filterNot { info ->
                    info.activityInfo.packageName.lowercase() in PLAYERS_BLACKLIST
                }
        } else {
            packageManager.queryIntentActivities(intent, 0) // PackageManager.MATCH_DEFAULT_ONLY
                .filterNot { info ->
                    info.activityInfo.packageName.lowercase() in PLAYERS_BLACKLIST
                }
        }
    }

    private fun configurePlayerIntent(
        intent: Intent,
        playerPackage: String,
        videoTitle: String,
        isIPTV: Boolean,
        state: PlayerStateManager.PlaybackState,
        headers: Array<String>? = null,
        activity: String = lampaActivity, // the one the state is saved under
    ) {
        val position = getPlaybackPosition(state)

        // Just+ Player: nested playlist API, any build of it (beta builds add a suffix to the package)
        if (isJustPlusPlayer(playerPackage.lowercase())) {
            configureJustPlusPlaylistIntent(intent, playerPackage, state, videoTitle, position, headers, activity)
            return
        }
        when (playerPackage.lowercase()) {
            // UPlayer
            in UPLAYER_PACKAGES -> {
                configureUPlayerIntent(
                    intent,
                    playerPackage,
                    state = state,
                    videoTitle,
                    position
                )
            }
            // DDD Video Player
            in DDD_PLAYER_PACKAGES -> {
                configureDddPlayerIntent(
                    intent,
                    playerPackage,
                    state = state,
                    videoTitle,
                    position,
                    headers = headers
                )
            }
            // MX Player
            in MX_PACKAGES, packageName -> {
                configureMxPlayerIntent(
                    intent,
                    playerPackage,
                    state = state,
                    videoTitle,
                    position,
                    headers = headers
                )
            }
			// Kodi
			in KODI_PACKAGES.takeIf { useKodiPlaylist } ?: emptySet() -> {
				configureKodiIntent(
					intent,
					playerPackage,
					state = state
				)
		    }
            // MPV
            in MPV_PACKAGES -> {
                configureMpvIntent(
                    intent,
                    playerPackage,
                    state = state,
                    videoTitle = videoTitle,
                    position,
                    headers = headers

                )
            }
            // VLC
            "org.videolan.vlc" -> {
                configureVlcIntent(
                    intent,
                    playerPackage,
                    // state = state,
                    videoTitle,
                    position,
                    // headers = headers
                )
            }
            // Just Player
            "com.brouken.player" -> {
                configureBroukenPlayerIntent(
                    intent,
                    playerPackage,
                    state = state,
                    videoTitle,
                    position,
                    // headers = headers
                )
            }
            // ViMu
            in VIMU_PACKAGES -> {
                configureViMuIntent(
                    intent,
                    playerPackage,
                    state = state,
                    videoTitle,
                    position,
                    isIPTV,
                    headers = headers
                )
            }
            // Exo Variants
            in EXO_PLAYER_PACKAGES -> {
                configureExoPlayerIntent(
                    intent,
                    playerPackage,
                    state = state,
                    videoTitle,
                    position,
                    // headers = headers
                )
            }
            // All others
            else -> { // Generic
                intent.setPackage(playerPackage)
                // Common title
                intent.putExtra(Intent.EXTRA_TITLE, videoTitle)
                intent.putExtra("title", videoTitle) // fallback
                // Try to add headers to unknown players as a fallback
                headers?.let { intent.putExtra("headers", it) }
                // App playback position
                when {
                    playerTimeCode == "continue" || playerTimeCode == "again" && position > 0 -> {
                        intent.putExtra("position", position.toInt())
                        // Add precise position if available from state
                        state.currentItem?.timeline?.time?.toLong()?.let {
                            intent.putExtra("precise_position", it)
                        }
                    }
                }
                // Handle quality variants if available
                state.currentItem?.quality?.takeIf { it.isNotEmpty() }?.let { qualities ->
                    intent.putExtra("quality_levels", qualities.keys.toTypedArray())
                    intent.putExtra(
                        "quality_urls",
                        qualities.values.map { it.toUri() }.toTypedArray()
                    )
                }
            }
        }
    }

    private fun prepareHeaders(jsonObject: JSONObject): Array<String>? {
        val headers = mutableListOf<String>()
        var hasHeaders = false

        // Process headers from JSON
        jsonObject.optJSONObject("headers")?.let { headersObj ->
            val keys = headersObj.keys()
            var hasCustomUserAgent = false

            while (keys.hasNext()) {
                val key = keys.next()
                when (key.lowercase(Locale.getDefault())) {
                    "user-agent" -> {
                        headers.add(key)
                        headers.add(headersObj.optString(key))
                        hasCustomUserAgent = true
                        hasHeaders = true
                    }

                    "content-length" -> continue  // Skip
                    else -> {
                        headers.add(key)
                        headers.add(headersObj.optString(key))
                        hasHeaders = true
                    }
                }
            }
            // Add default User-Agent only if not already specified
            if (!hasCustomUserAgent) {
                HttpHelper.userAgent?.let {
                    headers.add("User-Agent")
                    headers.add(it)
                    hasHeaders = true
                }
            }
        } ?: HttpHelper.userAgent?.let {  // No headers object case
            headers.add("User-Agent")
            headers.add(it)
            hasHeaders = true
        }

        return if (hasHeaders) headers.toTypedArray() else null
    }


    private fun getPlaybackPosition(state: PlayerStateManager.PlaybackState): Long {
        // "ask" needs the position too; every caller checks the mode
        return if (playerTimeCode != "again") {
            state.currentItem?.timeline?.let { timeline ->
                (timeline.time * 1000).toLong()
            } ?: state.currentPosition
        } else {
            0L
        }
    }

    // https://github.com/androidx/media/blob/release/demos/main/src/main/java/androidx/media3/demo/main/IntentUtil.java
    // https://github.com/androidx/media/blob/release/demos/main/src/main/java/androidx/media3/demo/main/PlayerActivity.java
    private fun configureExoPlayerIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long,
        // headers: Array<String>? = null
    ) {
        intent.apply {
            setPackage(playerPackage)
            putExtra("title", videoTitle)
            // putExtra("secure_uri", true)
            // headers?.let { putExtra("headers", it) }
            // Handle playback position
            when {
                playerTimeCode == "continue" && position > 0 ->
                    putExtra("position", position)

                playerTimeCode == "again" || (playerTimeCode == "continue" && position == 0L) ->
                    putExtra("position", 0L)
            }
            // Handle current video URL
            state.currentItem?.let { currentItem ->
                // Handle subtitles from state
                currentItem.subtitles?.takeIf { it.isNotEmpty() }?.let { subtitles ->
                    if (playerPackage == "com.google.android.exoplayer2.demo") {
                        putExtra(
                            "subtitle_uris", // arrayOf("http://example.com/sub.srt"))
                            subtitles.map { it.url.toUri().toString() }.toTypedArray()
                        )
                        putExtra(
                            "subtitle_labels", // arrayOf("English"))
                            subtitles.mapIndexed { index, item ->
                                item.label.takeIf { it.isNotEmpty() }
                                    ?: "Sub ${
                                        item.language?.uppercase().takeIf { !it.isNullOrEmpty() }
                                            ?: "${index + 1}"
                                    }"
                            }.toTypedArray()
                        )
                    } else { // v3 Subtitles (single track)
                        subtitles.firstOrNull()?.let { sub ->
                            putExtra("subtitle_uri", sub.url)
                            putExtra("subtitle_mime_type", "text/vtt") // Set actual MIME type
                            putExtra("subtitle_language", sub.language ?: "") // N/A
                        }
                    }
                }
            }
            // Handle playlist if available (Old format)
            if (state.playlist.size > 1) {
                if (playerPackage == "com.google.android.exoplayer2.demo") {
                    intent.putExtra(
                        "media_uris", // String[]
                        state.playlist.map { it.url.toUri().toString() }.toTypedArray()
                    )
                    intent.putExtra(
                        "media_titles", // String[]
                        state.playlist.mapIndexed { index, item ->
                            item.title ?: "Video ${index + 1}"
                        }
                            .toTypedArray()
                    )
                    intent.putExtra( // Int
                        "start_index",
                        state.currentIndex.coerceIn(0, state.playlist.size - 1)
                    )
                } else { // v3
                    // Playlist (indexed format)
                    state.playlist.forEachIndexed { index, item ->
                        intent.putExtra("uri_$index", item.url.toUri().toString())
                        intent.putExtra(
                            "title_$index",
                            item.title.takeIf { !it.isNullOrEmpty() } ?: "Video ${index + 1}")
                    }
                }
            }
        }
    }

    private fun configureDddPlayerIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long,
        headers: Array<String>? = null
    ) {
        intent.apply {
            setPackage(playerPackage)
            putExtra("title", videoTitle)
            putExtra("return_result", true)

            // Headers
            headers?.let { putExtra("headers", it) }

            // Position
            when {
                playerTimeCode == "continue" && position > 0 ->
                    putExtra("position", position.toInt())
                playerTimeCode == "again" ->
                    putExtra("position", 0)
            }

            // Playlist Logic
            if (state.playlist.size > 1) {
                val urls = ArrayList<Uri>()
                val titles = ArrayList<String>()
                val filenames = ArrayList<String>()
                val thumbnails = ArrayList<String>()
                val subtitlesList = ArrayList<Bundle>()

                state.playlist.forEach { item ->
                    urls.add(item.url.toUri())
                    titles.add(item.title ?: "")
                    filenames.add(item.url.toUri().lastPathSegment ?: "")
                    thumbnails.add(item.thumbnail ?: "")

                    // Per-item subtitles
                    val itemSubsBundle = Bundle()
                    item.subtitles?.takeIf { it.isNotEmpty() }?.let { subs ->
                        val subUris = subs.map { it.url.toUri() }.toTypedArray()
                        val subNames = subs.map { it.label.ifEmpty { "Sub" } }.toTypedArray()
                        itemSubsBundle.putParcelableArray("uris", subUris)
                        itemSubsBundle.putStringArray("names", subNames)
                    }
                    subtitlesList.add(itemSubsBundle)
                }

                state.currentItem?.let { item ->
                    setDataAndType(item.url.toUri(), "video/*")
                }

                // ВАЖНО: Передаем как TypedArray (Parcelable[]), так как IntentUtils использует getParcelableArray
                putExtra("video_list", urls.toTypedArray())
                putStringArrayListExtra("video_list.name", titles)
                putStringArrayListExtra("video_list.filename", filenames)
                putStringArrayListExtra("video_list.thumbnail", thumbnails)
                // ArrayList<Bundle> передается нормально через putParcelableArrayListExtra
                putParcelableArrayListExtra("video_list.subtitles", subtitlesList)

            } else {
                // Single Video Logic
                state.currentItem?.let { item ->
                    setDataAndType(item.url.toUri(), "video/*")
                    putExtra("filename", item.url.toUri().lastPathSegment)
                    putExtra("thumbnail", item.thumbnail ?: "")

                    // Subtitles for single video
                    item.subtitles?.takeIf { it.isNotEmpty() }?.let { subs ->
                        val subUris = subs.map { it.url.toUri() }.toTypedArray()
                        val subNames = subs.map { it.label.ifEmpty { "Sub" } }.toTypedArray()

                        putExtra("subs", subUris)
                        putExtra("subs.name", subNames)
                    }
                }
            }
        }
    }

    // Number of scan lines a quality label denotes (e.g. "1080p" -> 1080, "4K"/"UHD" -> 2160),
    // used to sort quality variants from highest to lowest before they are sent to the player.
    private fun qualityLines(label: String?): Int {
        if (label == null) return 0
        val normalized = label.lowercase(Locale.getDefault())
        if (normalized.contains("4k") || normalized.contains("uhd")) return 2160
        return label.filter { it.isDigit() }.toIntOrNull() ?: 0
    }

    // Just+ Player nested `playlist` contract: one Bundle with an item Bundle per episode.
    // The player answers through the result_callback (see handleJustPlusReport).
    private fun configureJustPlusPlaylistIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long,
        headers: Array<String>?,
        activity: String
    ) {
        val card = (state.extras[LAMPA_CARD_KEY] as? String)
            ?.let { getJson(it, LampaCard::class.java) }
        val cardImdbId: String? = card?.imdb_id?.takeIf { it.isNotEmpty() }
        val cardId = card?.tmdb_id?.takeIf { it.isNotEmpty() }
            ?: card?.id?.takeIf { it.isNotEmpty() }
        // A series: the root title is the show, each item's own name goes under it as episode_title
        val isSeries = card?.type == "tv" ||
                state.playlist.any { it.season != null || it.episode != null }
        // The root title is the card's name (a show's is `name`, a film's `title`), else what LAMPA sent
        val cardTitle = (if (isSeries) listOf(card?.name, card?.title) else listOf(card?.title, card?.name))
            .firstOrNull { !it.isNullOrEmpty() }
        val title = cardTitle ?: videoTitle
        // The widest logo in the app language, else in English, else none
        val logos = card?.images?.logos.orEmpty().filter { !it.file_path.isNullOrEmpty() }
        val logo = (logos.filter { it.iso_639_1 == appLang.substringBefore('-') }.takeIf { it.isNotEmpty() }
            ?: logos.filter { it.iso_639_1 == "en" })
            .maxByOrNull { it.aspect_ratio }
            ?.let { TMDB.imageUrl(it.file_path).replace("original", "w500") }
        // The best-voted backdrop without lettering, else the card's own backdrop
        val backdrop = card?.images?.backdrops.orEmpty()
            .filter { !it.file_path.isNullOrEmpty() && it.iso_639_1.isNullOrEmpty() }
            .maxByOrNull { it.vote_average }?.file_path
        val background = (backdrop ?: card?.backdrop_path)?.takeIf { it.isNotEmpty() }
            ?.let { TMDB.imageUrl(it).replace("original", "w1280") }
            ?: card?.background_image?.takeIf { it.isNotEmpty() }

        val items = state.playlist.mapIndexed { index, item ->
            Bundle().apply {
                // Encoded as the launch's data is (createBaseIntent): spaces, Cyrillic, brackets
                putString("uri", encodeUrlIfNeeded(item.url))
                item.title?.takeIf { it.isNotEmpty() && (isSeries || it != videoTitle) }
                    ?.let { putString("episode_title", it) }
                item.thumbnail?.takeIf { it.isNotEmpty() }?.let { putString("thumbnail", it) }
                (item.imdbId ?: cardImdbId)?.let { putString("imdb_id", it) }
                cardId?.let { putString("tmdb_id", it) }
                item.season?.let { putInt("season", it) }
                item.episode?.let { putInt("episode", it) }
                item.segments?.takeIf { it.isNotEmpty() }?.let { putString("segments", it) }
                // The start item from the resolved position; the others from their own timeline, so a
                // jump from the player's playlist resumes a half-watched episode (a finished one starts over)
                // "again": explicit 0, else the player resumes from its own saved position
                if (index == state.currentIndex) {
                    if (playerTimeCode == "again")
                        putInt("position_sec", 0)
                    else if (position > 0)
                        putInt("position_sec", (position / 1000).toInt())
                } else if (playerTimeCode != "again") {
                    item.timeline?.takeIf {
                        it.time > 0 && it.percent < VIDEO_COMPLETED_DURATION_MAX_PERCENTAGE
                    }?.let { putInt("position_sec", it.time.toInt()) }
                }
                item.quality?.takeIf { it.isNotEmpty() }?.let { q ->
                    putParcelableArray("qualities", q.keys.sortedByDescending { qualityLines(it) }
                        .map { label ->
                            Bundle().apply {
                                putString("label", label)
                                putString("uri", encodeUrlIfNeeded(q.getValue(label)))
                            }
                        }.toTypedArray())
                }
                item.subtitles?.takeIf { it.isNotEmpty() }?.let { subs ->
                    putParcelableArray("subtitles", subs.map { sub ->
                        Bundle().apply {
                            putString("uri", encodeUrlIfNeeded(sub.url))
                            putString("label", sub.label.ifEmpty { "Sub" })
                            sub.language?.let { putString("language", it) }
                        }
                    }.toTypedArray())
                }
            }
        }

        intent.apply {
            setPackage(playerPackage)
            putExtra("playlist", Bundle().apply {
                putString("title", title)
                logo?.let { putString("logo", it) }
                background?.let { putString("background", it) }
                putInt("start_index", state.currentIndex.coerceIn(0, items.lastIndex.coerceAtLeast(0)))
                headers?.let { putStringArray("headers", it) }
                // Lampa's "ask": the player asks Resume / Start over
                if (playerTimeCode == "ask") putBoolean("ask_resume", true)
                putParcelableArray("items", items.toTypedArray())
                // A report every 2 minutes while playing, the cadence of Lampa's own player: the one on
                // leaving can come too late to be kept (a task swiped from recents dies a second after it)
                if (keepPlayerConnection) putInt("report_interval_sec", 120)
                // Reports on every leave (Home included). Mutable, or the player's extras are dropped:
                // the default below Android 12 and for targetSdk < 31, explicit from 31 on. One per activity
                // and card: a new launch into the running player first reports the old session to the
                // old callback, and a shared one would by then carry the new activity's key.
                putParcelable(
                    "result_callback", PendingIntent.getBroadcast(
                        this@MainActivity, (activity + card?.id).hashCode(),
                        Intent(this@MainActivity, PlayerReportReceiver::class.java)
                            .putExtra(REPORT_ACTIVITY_EXTRA, activity)
                            .putExtra(REPORT_CARD_EXTRA, card?.id),
                        PendingIntent.FLAG_UPDATE_CURRENT or
                                (if (VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
                    )
                )
            })
        }
    }

	
	private fun configureKodiIntent(
		intent: Intent,
		playerPackage: String,
		state: PlayerStateManager.PlaybackState
	) {
		val item = state.currentItem ?: return
		val uri = item.url.toUri()
		
		val hasSubtitles = state.playlist.any { item ->
		    !item.subtitles.isNullOrEmpty()
		}

		intent.apply {
			setPackage(playerPackage)
			action = Intent.ACTION_VIEW
			
			if (state.playlist.size > 1 || hasSubtitles) {
				val hash = uri.getQueryParameter("link") ?: return
				val index = uri.getQueryParameter("index") ?: "0"
				
				val playlistUrl =
					"${uri.scheme}://${uri.authority}/playlist/${Uri.encode(uri.lastPathSegment)}.m3u?hash=$hash&index=$index"
				
				setDataAndType(
					playlistUrl.toUri(),
					"audio/x-mpegurl"
				)
			} else {
				setDataAndType(
					uri,
					"video/*"
				)
			}
		}
	}

    private fun configureMxPlayerIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long,
        headers: Array<String>? = null
    ) {
        intent.apply {
            component = if (playerPackage == packageName)
                ComponentName(packageName, "top.rootu.lampa.player.HomePlayerActivity")
            else ComponentName(playerPackage, "$playerPackage.ActivityScreen")
            putExtra("title", videoTitle)
            putExtra("sticky", false)
            if (playerPackage == packageName) {
                // опытный движок libmpv только для прямого эфира (и только на Android 8+, где работает библиотека);
                // кино и сериалы всегда идут в HomePlayerActivity на ExoPlayer
                val eng = state.extras["tv_engine"] as? String
                if (state.extras["isIPTV"] == true && eng != null && eng.startsWith("mpv") && VERSION.SDK_INT >= 26) {
                    component = ComponentName(packageName, "top.rootu.lampa.player.MpvTvActivity")
                    putExtra("tv_engine", eng)
                }
                putExtra("live", state.extras["isIPTV"] == true)
                (state.extras["tv_now_url"] as? String)?.let { putExtra("tv_now_url", it) }
                (state.extras["tv_buffer"] as? Number)?.let { putExtra("buffer_sec", it.toInt()) }
                (state.extras["tv_font"] as? Number)?.let { putExtra("tv_font", it.toInt()) }
                (state.extras["tv_panel_alpha"] as? Number)?.let { putExtra("tv_panel_alpha", it.toInt()) }
                (state.extras["tv_astereo"] as? Number)?.let { putExtra("tv_astereo", it.toInt()) }
                (state.extras["tv_center"] as? Number)?.let { putExtra("tv_center", it.toInt()) }
                (state.extras["tv_afr"] as? Number)?.let { putExtra("tv_afr", it.toInt()) }
                (state.extras["tv_dev"] as? String)?.let { putExtra("tv_dev", it) }
                (state.extras["audio_key"] as? String)?.let { putExtra("audio_key", it) }
                (state.extras["home_base"] as? String)?.let { putExtra("home_base", it) }
                (state.extras["home_tv_channel"] as? String)?.let { putExtra("home_tv_channel", it) }
                (state.extras["home_auto_attempt"] as? String)?.let { putExtra("home_auto_attempt", it) }
                (state.extras["home_auto_profile"] as? String)?.let { putExtra("home_auto_profile", it) }
                (state.extras["max_height"] as? Number)?.let { putExtra("max_height", it.toInt()) }
                (state.extras["yt_audio"] as? String)?.let { putExtra("yt_audio", it) }
                (state.extras["home_youtube_id"] as? String)?.let { putExtra("home_youtube_id", it) }
                (state.extras["yt_skip"] as? String)?.let { putExtra("yt_skip", it) }
                (state.extras["yt_quals"] as? String)?.let { putExtra("yt_quals", it) }
                (state.extras["tl_meta"] as? String)?.let { putExtra("tl_meta", it) }
                (state.extras["resume_playlist"] as? String)?.let { putExtra("resume_playlist", it) }
                (state.extras["vod_buf_mb"] as? Number)?.let { putExtra("vod_buf_mb", it.toInt()) }
                (state.extras["tv_surface"] as? String)?.let { putExtra("surface", it) }
                ProfileRuntime.attachOwner(this, (state.extras["home_base"] as? String) ?: this@MainActivity.appUrl)
            }
            headers?.let { putExtra("headers", it) }
            // Handle playback position
            when {
                playerTimeCode == "continue" && position > 0 ->
                    putExtra("position", position.toInt())

                playerTimeCode == "again" || (playerTimeCode == "continue" && position == 0L) ->
                    putExtra("position", 1)
            }
            // Handle playlist from state
            if (state.playlist.isNotEmpty()) {
                val urls = state.playlist.map { it.url }
                val titles = state.playlist.mapIndexed { index, item ->
                    item.title ?: "Item ${index + 1}" // Fallback to "Item 1", "Item 2", etc.
                }

                putExtra("video_list", urls.map(Uri::parse).toTypedArray())
                putExtra("video_list.name", titles.toTypedArray())
                putExtra("video_list_is_explicit", true)
            }
            // Handle subtitles from current item
            state.currentItem?.subtitles?.takeIf { it.isNotEmpty() }?.let { subtitles ->
                val subUrls = subtitles.map { it.url }
                val subTitles = subtitles.mapIndexed { index, item ->
                    item.label.takeIf { it.isNotEmpty() } ?: "Sub ${index + 1}"
                }

                putExtra("subs", subUrls.map(Uri::parse).toTypedArray())
                putExtra("subs.name", subTitles.toTypedArray())
            }
            putExtra("return_result", true)
        }
    }

    // VLC Player configuration with state integration
    // https://code.videolan.org/videolan/vlc-android/-/blob/master/application/resources/src/main/java/org/videolan/resources/Constants.kt
    // https://code.videolan.org/videolan/vlc-android/-/blob/master/application/vlc-android/src/org/videolan/vlc/gui/video/VideoPlayerActivity.kt
    private fun configureVlcIntent(
        intent: Intent,
        playerPackage: String,
        // state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long,
        // headers: Array<String>? = null
    ) {
        intent.apply {
            if (VERSION.SDK_INT > 32) {
                setPackage(playerPackage)
            } else {
                component = ComponentName(
                    playerPackage,
                    "$playerPackage.gui.video.VideoPlayerActivity"
                )
            }
            // Basic video info
            putExtra("title", videoTitle)
            // Headers
            // headers?.let { putExtra("http-headers", it) }
            // Handle playback position
            when {
                playerTimeCode == "continue" && position > 0 -> {
                    putExtra("from_start", false)
                    putExtra("position", position)
                }

                playerTimeCode == "again" || (playerTimeCode == "continue" && position == 0L) -> {
                    putExtra("from_start", true)
                    putExtra("position", 0L)
                }
            }
            // Add duration from current item's timeline if available
            // state.currentItem?.timeline?.duration?.toLong()?.let {
            //    if (it > 0) putExtra("extra_duration", it)
            // }
            // Handle subtitles from state
            // state.currentItem?.subtitles?.firstOrNull()?.let { firstSub ->
            // putExtra("subtitles_location", firstSub.url)
            // For VLC 3.5+ that supports multiple subtitles (this is for local URIs)
            // if (VERSION.SDK_INT >= 30 && state.currentItem?.subtitles?.size!! > 1) {
            //    putExtra(
            //        "subtitles_extra",
            //        state.currentItem?.subtitles?.drop(1)?.map { Uri.parse(it.url).toString() }?.toTypedArray()
            //    )
            // }
            // }
        }
    }

    // MPV Player configuration with state integration
    // https://github.com/pepeloni-away/mpv-android/blob/2b28598fd9f5ba8fd54652e3aee54b1b05ef936c/app/src/main/java/is/xyz/mpv/MPVActivity.kt#L966-L990
    private fun configureMpvIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String = "",
        position: Long,
        headers: Array<String>? = null
    ) {
        intent.apply {
            setPackage(playerPackage)
            // Resolve exact component for standard MPV packages to avoid launcher ambiguity
            try {
                if (playerPackage.startsWith("is.xyz.mpv")) {
                    component = ComponentName(playerPackage, "is.xyz.mpv.MPVActivity")
                }
            } catch (_: Exception) {
            }
            // Set media title
            if (videoTitle.isNotBlank()) {
                putExtra("title", videoTitle)
                putExtra(Intent.EXTRA_TITLE, videoTitle)
            }
            // Handle headers with MPV's required format
            headers?.let {
                val headerString = it.toList()
                    .chunked(2)
                    .joinToString("\r\n") { (k, v) -> "$k: $v" }
                putExtra("headers", headerString)
                putExtra("http-header-fields", headerString)
            }
            // Handle playback position
            // Crucial: Only pass position when resuming at > 0 ms.
            // Do NOT send position = 1 when starting from 0: MPV interprets this as --start=0.001
            // which forces seeking on unbuffered/live streams (e.g. TorrServer) and breaks playback.
            when {
                playerTimeCode == "continue" && position > 0 ->
                    putExtra("position", position.toInt())
            }
            // Handle subtitles from state
            state.currentItem?.subtitles?.takeIf { it.isNotEmpty() }?.let { subs ->
                // MPV can handle multiple subtitle tracks, ensure URLs are properly encoded
                val subUris = subs.map { encodeUrlIfNeeded(it.url).toUri() }.toTypedArray()
                putExtra("subs", subUris) // Parcelable[]
                // Add language information if available
                subs.mapNotNull { it.language }.takeIf { it.isNotEmpty() }?.let { langs ->
                    putExtra("subs_langs", langs.toTypedArray())
                }
            }
        }
    }

    private fun configureViMuIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long,
        isIPTV: Boolean,
        headers: Array<String>? = null
    ) {
        val vimuVersion = getAppVersion(this, playerPackage)?.versionNumber ?: 0L
        logDebug("ViMu ($playerPackage) version $vimuVersion")
        intent.apply {
            setPackage(playerPackage)
            headers?.let { putExtra("headers", it) }
            // Handle playlist
            when {
                state.playlist.size > 1 -> {
                    state.currentItem?.url?.let { url ->
                        setDataAndType(url.toUri(), "application/vnd.gtvbox.filelist")
                        configureViMuPlaylist(this, state, vimuVersion)
                    }
                }

                !state.playlist.isEmpty() -> {
                    configureSingleViMuItem(this, state, videoTitle, isIPTV)
                }
            }
            // Handle playback position
            when (playerTimeCode) {
                "continue", "again" -> {
                    putExtra("position", position.toInt())
                    putExtra("startfrom", position.toInt())
                }

                "ask" -> {
                    putExtra("forcedirect", true)
                    putExtra("forceresume", true)
                }
            }
        }
    }

    private fun configureViMuPlaylist(
        intent: Intent,
        state: PlayerStateManager.PlaybackState,
        vimuVersion: Long
    ) {
        val urls = state.playlist.map { it.url.toUri().toString() }
        val titles = state.playlist.mapIndexed { index, item ->
            item.title ?: "Item ${index + 1}" // Fallback to "Item 1", "Item 2", etc.
        }
        val safeIndex = state.currentIndex.coerceIn(0, urls.size - 1)

        if (vimuVersion >= 799L) { // 7.99+ version
            intent.apply {
                putStringArrayListExtra("asusfilelist", ArrayList(urls))
                putStringArrayListExtra("asusnamelist", ArrayList(titles))
                putExtra("startindex", safeIndex)
            }
        } else { // Legacy version
            intent.apply {
                putStringArrayListExtra(
                    "asusfilelist",
                    ArrayList(urls.subList(safeIndex, urls.size))
                )
                putStringArrayListExtra(
                    "asusnamelist",
                    ArrayList(titles.subList(safeIndex, urls.size))
                )
            }
        }
    }

    private fun configureSingleViMuItem(
        intent: Intent,
        state: PlayerStateManager.PlaybackState,
        fallbackTitle: String,
        isIPTV: Boolean
    ) {
        state.currentItem?.let { currentItem ->
            intent.apply {
                putExtra("forcename", currentItem.title ?: fallbackTitle)
                currentItem.subtitles?.takeIf { it.isNotEmpty() }?.let { subtitles ->
                    putStringArrayListExtra(
                        "asussrtlist",
                        subtitles.map { it.url.toUri().toString() }.toCollection(ArrayList())
                    )
                }
                if (isIPTV) {
                    putExtra("forcelive", true)
                } else {
                    removeExtra("forcelive")
                }
            }
        }
    }

    private fun configureBroukenPlayerIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long,
        // headers: Array<String>? = null,
        additionalExtras: Bundle? = null
    ) {
        intent.apply {
            setPackage(playerPackage)
            putExtra("title", videoTitle)
            // headers?.let { putExtra("headers", it) }
            // Handle playback position
            when {
                playerTimeCode == "continue" || playerTimeCode == "again" -> {
                    putExtra("position", position.toInt())
                }
            }
            // Handle current media item
            state.currentItem?.let { currentItem ->
                // Handle subtitles from state
                currentItem.subtitles?.takeIf { it.isNotEmpty() }?.let { subtitles ->
                    putExtra(
                        "subs",
                        subtitles.map { it.url.toUri() }.toTypedArray()
                    ) // Parcelable[]
                    putExtra("subs.name", subtitles.mapIndexed { index, item -> // String[]
                        item.label.takeIf { it.isNotEmpty() } ?: "Sub ${index + 1}"
                    }.toTypedArray())
                    // Add language codes if available
                    // subtitles.mapNotNull { it.language }.takeIf { it.isNotEmpty() }?.let { langs ->
                    //    putExtra("subs.lang", langs.toTypedArray())
                    // }
                }
            }
            // Additional custom extras
            additionalExtras?.let { putExtras(it) }
            // Common Brouken player flags
            putExtra("return_result", true)
        }
    }

    private fun configureUPlayerIntent(
        intent: Intent,
        playerPackage: String,
        state: PlayerStateManager.PlaybackState,
        videoTitle: String,
        position: Long
    ) {
        intent.apply {
            setPackage(playerPackage)
            putExtra("title", videoTitle)
            // Handle resume/restart
            when (playerTimeCode) {
                "continue", "again" -> putExtra("resume", position)
            }
            // Check for multi-quality streams or playlist
            val hasQualityVariants = state.currentItem?.quality?.isNotEmpty() == true
            val hasPlaylist = state.playlist.size > 1
            // Handle playlists or multi-quality streams
            if (hasQualityVariants || hasPlaylist) {
                configureUPlayerPlaylist(this, videoTitle, state)
            } else {
                // Single video fallback
                state.currentItem?.let { currentItem ->
                    putExtra("video", currentItem.url)
                }
            }
        }
    }

    private fun configureUPlayerPlaylist(
        intent: Intent,
        videoTitle: String,
        state: PlayerStateManager.PlaybackState
    ) {
        // Handle quality variants from current item
        state.currentItem?.quality?.let { qualityMap ->
            if (qualityMap.isNotEmpty()) {
                intent.apply {
                    // Set Title
                    putStringArrayListExtra("titleList", arrayListOf(videoTitle))
                    // Add Quality
                    putStringArrayListExtra("videoGroupList", ArrayList(qualityMap.keys))
                    qualityMap.forEach { (key, url) ->
                        putStringArrayListExtra(key, arrayListOf(url))
                    }
                    // Find and set current index
                    val qualityIndex = qualityMap.values.indexOfFirst { url ->
                        url == state.currentItem?.url
                    }.takeIf { it != -1 } ?: 0
                    putExtra("groupPosition", qualityIndex)
                }
                return
            }
        }
        // Fallback to normal playlist handling
        if (state.playlist.size > 1) {
            // Get first item's hash from state
            val firstHash = state.playlist.firstOrNull()?.timeline?.hash ?: "0"
            if (firstHash != "0") {
                intent.putExtra("playlistTitle", firstHash)
            }

            val urls = ArrayList<String>()
            val titles = ArrayList<String>()

            state.playlist.forEach { item ->
                urls.add(item.url.toUri().toString())
                titles.add(item.title ?: "Item ${state.playlist.indexOf(item) + 1}")
            }

            intent.apply {
                putStringArrayListExtra("videoList", urls)
                putStringArrayListExtra("titleList", titles)
                putExtra("playlistPosition", state.currentIndex)
            }
        }
    }

    /**
     * The external player is gone (closed, or it never launched) — drop everything we were
     * holding for its sake. Idempotent: both onResume() and handlePlayerResult() get here.
     */
    // ---- окно «картинка в картинке» с текущим треком, пока приложение свёрнуто ----
    private val musicActionReceiver = object : android.content.BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            val cmd = intent?.getStringExtra("cmd") ?: return
            runVoidJsFunc("window.__ymCmd", "'$cmd'")
        }
    }
    private var musicReceiverRegistered = false
    @Volatile private var musicPlayingNow = true

    private fun pipParams(): android.app.PictureInPictureParams? {
        if (VERSION.SDK_INT < Build.VERSION_CODES.O) return null
        fun action(icon: Int, title: String, cmd: String, req: Int): android.app.RemoteAction {
            val pi = android.app.PendingIntent.getBroadcast(
                this, req, Intent("app.lampac.home.MUSIC").setPackage(packageName).putExtra("cmd", cmd),
                android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT
            )
            return android.app.RemoteAction(android.graphics.drawable.Icon.createWithResource(this, icon), title, title, pi)
        }
        val actions = listOf(
            action(android.R.drawable.ic_media_previous, "Предыдущий", "prev", 1),
            if (musicPlayingNow) action(android.R.drawable.ic_media_pause, "Пауза", "pause", 2)
            else action(android.R.drawable.ic_media_play, "Играть", "play", 3),
            action(android.R.drawable.ic_media_next, "Следующий", "next", 4)
        )
        return android.app.PictureInPictureParams.Builder()
            .setAspectRatio(android.util.Rational(16, 9))
            .setActions(actions)
            .build()
    }

    private fun enterMusicPip() {
        if (VERSION.SDK_INT < Build.VERSION_CODES.O) return
        try {
            if (!musicReceiverRegistered) {
                val f = android.content.IntentFilter("app.lampac.home.MUSIC")
                if (VERSION.SDK_INT >= 33) registerReceiver(musicActionReceiver, f, RECEIVER_NOT_EXPORTED)
                else registerReceiver(musicActionReceiver, f)
                musicReceiverRegistered = true
            }
            pipParams()?.let { enterPictureInPictureMode(it) }
        } catch (e: Exception) {
            logDebug("enterMusicPip failed: ${e.message}")
        }
    }

    fun setMusicPlaying(playing: Boolean) {
        musicPlayingNow = playing
        if (VERSION.SDK_INT >= Build.VERSION_CODES.O && backgroundAudio) {
            try { pipParams()?.let { setPictureInPictureParams(it) } } catch (_: Exception) {}
        }
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: android.content.res.Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        runVoidJsFunc("window.__ymPip", if (isInPictureInPictureMode) "true" else "false")
    }

    // Плагин сообщает, что играет музыка: после сворачивания приложение продолжает играть
    fun setBackgroundAudio(on: Boolean) {
        runOnUiThread {
            if (backgroundAudio == on) return@runOnUiThread
            backgroundAudio = on
            browser?.setKeepVisible(on)
            if (on) PlaybackService.start(this)
            else if (!isPlayerLaunching) PlaybackService.stop(this)
        }
    }

    private fun endPlayerSession() {
        if (!isPlayerLaunching) return
        isPlayerLaunching = false
        if (!backgroundAudio) {
            browser?.setKeepVisible(false) // restore normal visibility handling
            PlaybackService.stop(this) // no longer need to hold the process foreground
        }
        // Unconditional: enable() is the page's own default state, so this also heals the case
        // where keepPlayerConnection got toggled off while the player was in front.
        setLampaScreensaver(true)
    }

    private fun launchPlayer(intent: Intent, policyChecked: Boolean = false) {
        if(ProfileRuntime.enabled() && !policyChecked){
            val owner=ProfileRuntime.snapshot()
            val meta=try { org.json.JSONArray(intent.getStringExtra("tl_meta") ?: "[]") } catch (_: Exception) { org.json.JSONArray() }
            val body=org.json.JSONObject().put("card",meta.optJSONObject(0)?.optString("c").orEmpty()).put("youtube_id",intent.getStringExtra("home_youtube_id").orEmpty()).put("tv_channel",(intent.getStringExtra("home_tv_channel") ?: intent.getStringExtra("tv_now_url")?.let { android.net.Uri.parse(it).getQueryParameter("id") }).orEmpty())
            Thread {
                var allowed=false
                try {
                    val c=java.net.URL(owner.optString("server_base")+"/home/play/authorize").openConnection() as java.net.HttpURLConnection
                    c.requestMethod="POST";c.doOutput=true;c.connectTimeout=3000;c.readTimeout=12000;c.setRequestProperty("Content-Type","application/json");c.setRequestProperty("X-Home-Session",owner.optString("token"))
                    c.outputStream.use { it.write(body.toString().toByteArray()) };allowed=c.responseCode==200;c.disconnect()
                } catch (_: Exception) { }
                runOnUiThread { if(ProfileRuntime.contextJson()!=owner.toString())return@runOnUiThread;if(allowed)launchPlayer(intent,true)else App.toast("Просмотр не разрешён в этом профиле",true) }
            }.start()
            return
        }
        playbackUiOwner=ProfileRuntime.scopeKey("playback_ui")
        try {
            debugLogIntentData(TAG, intent)
            // Mark that the upcoming onPause() is caused by launching our external player,
            // so we can keep the WebView JS timers (RCH socket heartbeat) alive.
            isPlayerLaunching = true
            // Media Center: запоминаем текущий экран Lampa — если систему не хватит памяти и окно пересоздадут, вернём его (localStorage переживает перезагрузку страницы)
            if (BuildConfig.APPLICATION_ID.endsWith(".mc")) runVoidJsFunc("(function(){try{localStorage.setItem('mc_last_activity',localStorage.getItem('activity')||'')}catch(e){}})", "")
            // Keep the page reported as visible to the web engine so Lampa doesn't pause its
            // own timers on DOM visibilitychange while the player is in front.
            if (keepPlayerConnection) {
                browser?.setKeepVisible(true)
                // Foreground service keeps the process out of the OEM/Android background
                // freezer so the WebView RCH socket survives while the player is on top.
                PlaybackService.start(this)
                // ...and with the timers alive, Lampa would start its screensaver mid-playback.
                setLampaScreensaver(false)
            }
            resultLauncher.launch(intent)
        } catch (e: Exception) {
            endPlayerSession() // no player launched -> onPause won't be a player launch
            logDebug("Failed to launch player: ${e.message}")
            App.toast(R.string.no_launch_player, true)
        }
    }

    private fun showPlayerSelectionDialog(
        players: List<ResolveInfo>,
        jsonObject: JSONObject,
        isIPTV: Boolean
    ) {
        val mainActivity = this
        val listAdapter = AppListAdapter(mainActivity, players)
        val playerChooser = AlertDialog.Builder(mainActivity)

        @SuppressLint("InflateParams")
        val appTitleView = LayoutInflater.from(mainActivity).inflate(R.layout.app_list_title, null)
        val switch = appTitleView.findViewById<SwitchCompat>(R.id.useDefault)
        playerChooser.setCustomTitle(appTitleView)

        playerChooser.setAdapter(listAdapter) { dialog, which ->
            val setDefaultPlayer = switch.isChecked
            val selectedPlayer = listAdapter.getItemPackage(which)

            if (setDefaultPlayer) {
                setPlayerPackage(selectedPlayer, isIPTV)
            }

            dialog.dismiss()
            runPlayer(jsonObject, selectedPlayer)
        }

        val playerChooserDialog = playerChooser.create()
        showFullScreenDialog(playerChooserDialog)
        playerChooserDialog.listView.requestFocus()
    }

    /**
     * Processes playback results and updates the player state accordingly.
     *
     * This function handles:
     * 1. Identifying the current playing item in the playlist
     * 2. Updating its playback position and completion status
     * 3. Marking previous items as completed (if auto-next is enabled)
     * 4. Sending timeline updates to the UI
     * 5. Sending updates to WatchNext channel on Android TV
     * 6. Clearing state when playback ends
     *
     * @param endedVideoUrl The URL of the video that ended playback (may be blank/null for current item)
     * @param positionMillis The current playback position in milliseconds
     * @param durationMillis The total duration of the media in milliseconds
     * @param ended Boolean indicating whether playback has fully completed
     *
     */
    private fun resultPlayer(
        endedVideoUrl: String,
        positionMillis: Int,
        durationMillis: Int,
        ended: Boolean
    ) {
        // Skip invalid updates where position and duration are 0 but playback hasn't ended
        if (!ended && positionMillis == 0 && durationMillis == 0) {
            logDebug("Skipping invalid update - zero position/duration for non-ended playback")
            return
        }
        lifecycleScope.launch {
            // Get current state and resolve which video URL we're processing
            val currentState = playerStateManager.getState(lampaActivity)
            val videoUrl = endedVideoUrl.takeUnless { it.isBlank() || it == "null" }
                ?: currentState.currentUrl
                ?: return@launch  // Exit if no valid URL found
            // Find and update the current playlist item
            val (updatedPlaylist, foundIndex) = updateCurrentPlaylistItem(
                currentState.playlist,
                videoUrl,
                positionMillis,
                durationMillis,
                ended
            ) ?: return@launch  // Exit if current item not found
            // Persist the updated state
            playerStateManager.saveState(
                activityJson = lampaActivity,
                playlist = updatedPlaylist,
                currentIndex = foundIndex,
                currentUrl = videoUrl,
                currentPosition = positionMillis.toLong(),
                startIndex = currentState.startIndex, // Maintain original starting point
                extras = currentState.extras // Don't loose extras
            )
            // Серии, которые реально досмотрели до конца (автопереход во встроенном плеере), отмечаем по одной.
            // Раньше отмечался весь диапазон между стартом и текущей серией: ручной прыжок E1→E6 закрывал E2–E5, которые не открывались.
            val proven = pendingCompletedUrls; pendingCompletedUrls = emptyList()
            proven.forEach { u ->
                val idx = updatedPlaylist.indexOfFirst { isCurrentPlaybackItem(it, u) }
                if (idx >= 0 && idx != foundIndex) {
                    updatedPlaylist[idx] = createCompletedPlaylistItem(updatedPlaylist[idx])
                    updatedPlaylist[idx].timeline?.let { tl ->
                        runVoidJsFunc("Lampa.Timeline.update", withActionTime(playerStateManager.convertTimelineToJsonString(tl)))
                    }
                }
            }
            // Notify UI of current item's timeline update
            updatedPlaylist[foundIndex].timeline?.let { timeline ->
                runVoidJsFunc(
                    "Lampa.Timeline.update",
                    withActionTime(playerStateManager.convertTimelineToJsonString(timeline))
                )
            }
            // Update PlayNext (must finish before clearState)
            // Critical section - ensure ordering
            if (ended) {
                val updateJob = launch(Dispatchers.Default) {
                    updatePlayNext(true) // Runs in background
                }
                updateJob.join() // Wait for completion before clearing state
                playerStateManager.clearState(lampaActivity)
            } else {
                launch(Dispatchers.Default) {
                    updatePlayNext(false) // Fire-and-forget for non-ended case
                }
            }
        }
    }

    /**
     * Updates the current playing item in the playlist and returns the modified playlist with found index
     */
    private fun updateCurrentPlaylistItem(
        playlist: List<PlayerStateManager.PlaylistItem>,
        videoUrl: String,
        positionMillis: Int,
        durationMillis: Int,
        ended: Boolean
    ): Pair<MutableList<PlayerStateManager.PlaylistItem>, Int>? {
        val updatedPlaylist = playlist.toMutableList()
        val foundIndex =
            updatedPlaylist.indexOfFirst { isCurrentPlaybackItem(it, videoUrl) }.takeIf { it >= 0 }
                ?: return null

        updatedPlaylist[foundIndex] = createUpdatedPlaylistItem(
            item = updatedPlaylist[foundIndex],
            positionMillis = positionMillis,
            durationMillis = durationMillis,
            ended = ended
        )

        return updatedPlaylist to foundIndex
    }

    /**
     * Marks items as completed from startIndex to currentIndex-1 (excludes current item)
     * and sends timeline updates for each marked item
     */
    private fun updatePreviousItemsCompletion(
        playlist: MutableList<PlayerStateManager.PlaylistItem>,
        startIndex: Int,
        currentIndex: Int
    ) {
        getCompletionRange(startIndex, currentIndex, playlist.lastIndex).forEach { index ->
            playlist[index] = createCompletedPlaylistItem(playlist[index])

            playlist[index].timeline?.let { timeline ->
                runVoidJsFunc(
                    "Lampa.Timeline.update",
                    playerStateManager.convertTimelineToJsonString(timeline)
                )
                logDebug("Marked item $index as completed (100%)")
            }
        }
    }

    /**
     * Creates a range of indices to mark as completed (startIndex to currentIndex-1)
     */
    private fun getCompletionRange(
        startIndex: Int,
        currentIndex: Int,
        lastValidIndex: Int
    ): IntRange {
        val safeStart = startIndex.coerceIn(0, lastValidIndex)
        val safeEnd = currentIndex.coerceIn(0, lastValidIndex)

        return if (safeStart <= safeEnd) {
            safeStart until safeEnd  // Excludes currentIndex
        } else {
            safeEnd + 1..safeStart   // Reverse range, excludes currentIndex
        }
    }

    /**
     * Creates a new playlist item with updated timeline
     */
    private fun createUpdatedPlaylistItem(
        item: PlayerStateManager.PlaylistItem,
        positionMillis: Int,
        durationMillis: Int,
        ended: Boolean
    ): PlayerStateManager.PlaylistItem {
        val percent = if (durationMillis > 0) (positionMillis.toLong() * 100L / durationMillis.toLong()).coerceIn(0L, 100L).toInt() else 0
        return item.copy(
            timeline = PlayerStateManager.PlaylistItem.Timeline(
                hash = item.timeline?.hash ?: "0",
                time = if (ended) 0.0 else positionMillis / 1000.0,
                duration = if (ended) 0.0 else durationMillis / 1000.0,
                percent = if (ended) 100 else percent
            )
        )
    }

    /**
     * Creates a playlist item marked as 100% completed
     */
    private fun createCompletedPlaylistItem(item: PlayerStateManager.PlaylistItem): PlayerStateManager.PlaylistItem {
        return item.copy(
            timeline = PlayerStateManager.PlaylistItem.Timeline(
                hash = item.timeline?.hash ?: "0",
                time = 0.0, // item.timeline?.duration ?: 0.0,
                duration = 0.0, // item.timeline?.duration ?: 0.0,
                percent = 100
            )
        )
    }

    /**
     * Determines if a playlist item matches the currently playing video URL.
     */
    private fun isCurrentPlaybackItem(
        item: PlayerStateManager.PlaylistItem,
        videoUrl: String
    ): Boolean {
        val normalizedInputUrl = videoUrl.toUri().toString()

        return item.url.toUri().toString() == normalizedInputUrl ||
                item.quality?.values?.any { qualityUrl ->
                    qualityUrl.isNotEmpty() && qualityUrl.toUri()
                        .toString() == normalizedInputUrl
                } == true
    }

    private fun getCardFromActivity(activityJson: String?): LampaCard? {
        return try {
            getCard(JSONObject(activityJson ?: return null).optJSONObject("movie"))
        } catch (e: JSONException) {
            logDebug("Invalid activity JSON: ${e.message}")
            null
        }
    }

    private fun getCard(cardJson: JSONObject?): LampaCard? {
        return getJson(cardJson?.toString() ?: return null, LampaCard::class.java)?.apply {
            fixCard()
        }
    }

    /**
     * Updates Watch Next on Android TV.
     */
    private suspend fun updatePlayNext(ended: Boolean) = withContext(Dispatchers.Default) {
        if (!isTvContentProviderAvailable) return@withContext
        try {
            // The card runPlayer stored with this state came with the play request; the activity can be
            // the page before.
            val card = (playerStateManager.getState(lampaActivity).extras[LAMPA_CARD_KEY] as? String)
                ?.let { getJson(it, LampaCard::class.java) }
                ?: getCardFromActivity(lampaActivity) ?: return@withContext
            // Get current playback state
            // val state = playerStateManager.getState(lampaActivity)
            // Get state by matching card (must be added with saveState!)
            val state = playerStateManager.findStateByCard(card) ?: return@withContext
            // playerStateManager.debugKeyMatching(lampaActivity)
            when {
                ended -> { // Case 1: Playback ended - remove from Continue Watching
                    logDebug("PlayNext: remove ${card.id} and clearState [ended]")
                    WatchNext.removeContinueWatch(card) // FIXME: don't remove if added to PlayNext by user
                    playerStateManager.clearState(lampaActivity)
                }
                // Case 2: Valid ongoing playback - update Continue Watching
                state.currentItem != null && !state.isEnded -> {
                    logDebug("PlayNext: Updating ${card.id}")
                    WatchNext.addLastPlayed(card, lampaActivity)
                }

                else -> { // Case 3: No valid state - just log
                    logDebug("PlayNext: No valid playback state for ${card.id}")
                }
            }
        } catch (e: Exception) {
            logDebug("Error in updatePlayNext: ${e.javaClass.simpleName} - ${e.message}")
        }
    }
}
