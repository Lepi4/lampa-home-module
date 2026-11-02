package top.rootu.lampa.player

import top.rootu.lampa.R
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.BaseAdapter
import android.widget.FrameLayout
import android.widget.ListView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.Tracks
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.source.MergingMediaSource
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.exoplayer.trackselection.DefaultTrackSelector
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Встроенный плеер «Lampa Home». Говорит на протоколе MX Player: принимает title / position /
 * video_list / subs / headers и по закрытию возвращает end_by / position / duration, поэтому
 * приложение обрабатывает его так же, как внешний плеер.
 *
 * Пульт: OK — пауза и панель, ←/→ — перемотка (при удержании шаг растёт), ↓ — меню
 * (звук, субтитры, качество, скорость, формат), Назад — закрыть.
 */
class HomePlayerActivity : Activity() {
    private companion object {
        const val RESULT_ACTION = "com.mxtech.intent.result.VIEW"
        const val HIDE_DELAY = 4500L
        const val SEEK_COMMIT_DELAY = 650L
    }

    private lateinit var player: ExoPlayer
    private lateinit var trackSelector: DefaultTrackSelector
    private lateinit var playerView: PlayerView
    private lateinit var spinner: TextView
    private var stoppedAt = 0L
    private var epgStarted = false
    private lateinit var overlay: FrameLayout
    private lateinit var titleView: TextView
    private lateinit var timeView: TextView
    private lateinit var endsView: TextView
    private lateinit var bar: KnobBar
    private lateinit var controlsRow: LinearLayout
    private lateinit var transportRow: LinearLayout
    private lateinit var kickerView: TextView
    private lateinit var statsView: TextView
    private lateinit var endsTopView: TextView
    private lateinit var firstCtl: TextView
    private lateinit var aspectBtn: TextView
    private lateinit var speedBtn: TextView
    private lateinit var qualityBtn: TextView
    private lateinit var audioBtn: TextView
    private lateinit var subsBtn: TextView
    private lateinit var statusView: TextView
    private lateinit var playBtn: TextView
    private lateinit var descView: TextView
    private val seekButtons = ArrayList<TextView>()
    private var live = false
    private lateinit var frameRate: PlaybackFrameRateController
    private val ptsRate = PtsFrameRateEstimator()
    private val drc = DrcAudioProcessor()
    private var firstFrameSeen = false
    private var bufferStartedAt = 0L
    private var spinnerWasShown = false
    private var tvNowUrl: String? = null
    @Volatile private var epgMap: JSONObject? = null
    private var digits = ""
    private var chNames: List<String> = emptyList()
    private var chUris: List<String> = emptyList()
    private var panel: LinearLayout? = null
    private var chList: ListView? = null
    private var panelOpen = false
    private var liveRetries = 0
    private var decoderRetried = false
    private lateinit var srcBtn: TextView
    private var forcedIdx = -1          // позиция в списке, где выбран источник вручную
    private var forcedOrig: MediaItem? = null
    private var forcedSrc = -1
    private var tvDev: String = ""
    private val saveLast = Runnable { reportLastChannel() }
    private val logoMap = HashMap<String, String>()
    private val srcMap = HashMap<String, String>()      // id канала -> тип первого источника (ts — спутник, hls — интернет)
    private val logoFailed = java.util.Collections.synchronizedSet(HashSet<String>())
    private val logoCache = android.util.LruCache<String, android.graphics.Bitmap>(120)
    private lateinit var detLogo: android.widget.ImageView
    private lateinit var detName: TextView
    private lateinit var detNow: TextView
    private lateinit var detDesc: TextView
    private lateinit var detNext: TextView
    private val dayCache = HashMap<String, Pair<Long, org.json.JSONArray>>()
    private var detailPos = -1
    private val detailRun = Runnable { showDetail(detailPos) }
    private val commitDigits = Runnable {
        val n = digits.toIntOrNull()
        digits = ""
        statusView.text = ""
        if (n != null && n >= 1 && n <= player.mediaItemCount) player.seekTo(n - 1, C.TIME_UNSET)
    }

    private val ui = Handler(Looper.getMainLooper())
    private val clock = SimpleDateFormat("HH:mm", Locale.getDefault())

    private var names: List<String> = emptyList()
    private var startIndex = 0
    private var menuMode = false
    private var pendingSeek = -1L
    private var seekStreak = 0
    private var lastSeekKeyAt = 0L
    private var speedIdx = 1
    private var resizeIdx = 0
    private var finished = false

    // «Внешний ↗»: плеер закрывается и отдаёт управление приложению, оно запускает VIMU с этого же места (и той же дорожкой звука)
    private lateinit var extBtn: TextView
    private var extHandoff = false
    // память выбранной звуковой дорожки по тайтлу (на сервере, общая для всех устройств): применяется при продолжении и в следующей серии
    private var audioKey = ""
    private val startupAt = android.os.SystemClock.elapsedRealtime()
    private var homeBase = ""
    @Volatile private var homeOwner: JSONObject? = null
    private fun homeConnection(url: String): HttpURLConnection {
        val c=URL(url).openConnection() as HttpURLConnection
        if(homeBase.isNotBlank() && url.startsWith(homeBase+"/")) top.rootu.lampa.ProfileRuntime.requestHeaders(homeOwner).forEach { (k,v)->c.setRequestProperty(k,v) }
        return c
    }
    private var memAudioLabel = ""
    private var memAudioLang = ""
    private var memAudioLoaded = false
    private var audioApplied = false
    private var userPickedAudio = false
    private var extAudioLabel = ""
    private var extAudioLang = ""
    private var completed = false

    // ---- отчёт о просмотре прямо с плеера: позиция каждой серии уходит на сервер, не завися от страницы Lampa ----
    // (пока плеер открыт, таймеры WebView остановлены, а итог при закрытии мог не дойти — серии и автопереходы терялись)
    private var tlMeta: JSONArray? = null          // по элементу плейлиста: {id, h, c} — ключи строки timecode
    private var resumeList: JSONArray? = null      // общий список серий раздачи [{u,t,h}], один на запуск: окно для «Продолжить» собирается из него для текущей серии
    private var tlCur = 0                          // индекс элемента, который играет сейчас
    private val tlDur = HashMap<Int, Long>()       // известная длительность элементов, мс
    private val tlPos = HashMap<Int, Long>()       // последняя известная позиция, мс
    private val tlSentPos = HashMap<Int, Long>()   // позиция, которая уже отправлена: повтор той же позиции новым событием не считается
    private val tlSession = System.currentTimeMillis()   // идентификатор сеанса просмотра (защита от запоздавшего отчёта того же сеанса)
    private val tlLastU = HashMap<String, Long>()           // хеш серии -> время её последнего действия (мс): страница Lampa получает его вместе с итогом, а не «время закрытия плеера»
    private var tlSeq = 0L                                  // номер действия внутри сеанса: event = сеанс:номер, повтор отправки несёт тот же event
    private val tlDoneSent = HashSet<Int>()                 // серии, уже отправленные как «досмотрено» в этом сеансе: повтор не создаёт нового действия
    private val tlAutoDone = ArrayList<String>()   // серии, реально досмотренные до конца с автопереходом: приложение отмечает только их
    private val RESUME_MAX = 30_000
    private val tlTick = object : Runnable {
        override fun run() {
            if (finished || !::player.isInitialized) return
            // На паузе нет повторных событий; её позиция отправляется один раз
            // в onPlayWhenReadyChanged, затем повторно только при изменении.
            if (player.isPlaying) { tlRemember(); tlSend(listOf(tlCur), false) }
            ui.postDelayed(this, 10_000)
        }
    }

    private fun tlRemember() {
        if (!::player.isInitialized) return
        val i = player.currentMediaItemIndex
        val d = player.duration
        if (d != C.TIME_UNSET && d > 0) tlDur[i] = d
        tlPos[i] = player.currentPosition.coerceAtLeast(0)
    }

    // Откуда запущено воспроизведение, для журнала: хост:порт и первый сегмент пути (без адреса целиком и параметров), плюс тип файла/потока
    private fun srcTag(u: Uri?): String {
        return try {
            if (u == null) return "?"
            val host = (u.host ?: "local") + (if (u.port > 0) ":" + u.port else "")
            val seg = u.pathSegments?.firstOrNull()?.take(16) ?: ""
            val last = u.lastPathSegment ?: ""
            val ext = if (last.contains('.')) last.substringAfterLast('.').take(5).lowercase() else ""
            host + (if (seg.isNotEmpty()) "/$seg" else "") + (if (ext.isNotEmpty()) " ." + ext else "")
        } catch (_: Exception) { "?" }
    }

    // «Продолжить» для текущей серии: свои сведения + окно списка раздачи вокруг неё (5 назад, 45 вперёд), урезанное по размеру так,
    // чтобы текущая и следующая серии остались (сначала уходят дальние элементы). Размер согласован с серверным пределом.
    private fun buildResume(r0: JSONObject?): JSONObject? {
        r0 ?: return null
        val r = JSONObject(r0.toString())
        val list = resumeList
        val pi = if (r.has("pi")) r.optInt("pi", -1) else -1
        r.remove("pi")
        if (list != null && pi in 0 until list.length() && r.optString("src") == "url") {
            if (r.optString("url").isBlank()) r.put("url", list.optJSONObject(pi)?.optString("u") ?: "")
            val from = maxOf(0, pi - 5); val to = minOf(list.length(), pi + 45)
            val pl = JSONArray()
            for (k in from until to) list.optJSONObject(k)?.let { pl.put(it) }
            var idx = pi - from
            while (pl.length() > 2 && r.toString().length + pl.toString().length > RESUME_MAX) {
                if (idx > 0) { pl.remove(0); idx-- } else pl.remove(pl.length() - 1)
            }
            r.put("pl", pl); r.put("idx", idx)
        }
        return r
    }

    // idxs — элементы для отправки; ended — серия закончилась целиком (автопереход или конец списка)
    private fun tlSend(idxs: List<Int>, ended: Boolean, force: Boolean = false) {
        val meta = tlMeta ?: return
        val base = homeBase.ifBlank { return }
        val rows = JSONArray()
        val commit = ArrayList<() -> Unit>()                  // состояние «уже отправлено» фиксируем, только когда действие надёжно сохранено в очереди на диске
        for (i in idxs) {
            val m = meta.optJSONObject(i) ?: continue
            // An item may end before its first progress tick (e.g. resume near the credits).
            // Its window still knows the duration after the player has advanced to the next item.
            val durKnown = (tlDur[i] ?: if (ended && i in 0 until player.currentTimeline.windowCount)
                player.currentTimeline.getWindow(i, androidx.media3.common.Timeline.Window()).durationMs else null)
                ?.takeIf { it > 0 }
            if (!force && durKnown == null) continue
            val dur = durKnown ?: 0L
            val pos = if (ended && dur > 0) dur else (tlPos[i] ?: if (force) 0L else continue)
            if (dur > 0) { tlDur[i] = dur; if (ended) tlPos[i] = pos }
            if (!force && pos < 5_000 && !ended) continue
            // неподвижная позиция — не новое действие, даже при закрытии/уходе в фон (force): иначе плеер, забытый на паузе, перетирает чужой просмотр новой датой
            if (!ended && tlSentPos[i] != null && Math.abs(pos - tlSentPos[i]!!) < 1_000) continue
            // единое правило (сервер и страница — то же): дошли до конца, ≥90% или титры: остаток не больше min(10 мин, 12% длительности) после половины
            val done = (ended && dur > 0) || (dur > 0 && (pos >= dur * 0.90 || (dur - pos <= minOf(600_000L, dur * 12 / 100) && pos >= dur / 2)))
            if (done && i in tlDoneSent) continue                        // «досмотрено» уже отправлено: повтор того же состояния — не действие
            val nowU = System.currentTimeMillis()
            val hh = m.optString("h")
            commit.add { if (done) tlDoneSent.add(i) else tlDoneSent.remove(i); tlSentPos[i] = pos; tlLastU[hh] = nowU }
            rows.put(JSONObject().apply {
                put("id", m.optString("id")); put("h", m.optString("h")); put("c", m.optString("c"))
                put("t", pos / 1000); put("d", dur / 1000)          // «досмотрено» хранится как отдельное действие (op), а позиция остаётся настоящей
                put("p", if (done) 100.0 else if (dur > 0) Math.round(pos * 1000.0 / dur) / 10.0 else 0.0)
                put("u", nowU); put("op", if (done) "completed" else "progress")
                put("session", tlSession.toString()); put("ended", ended || done); put("event", "$tlSession:${++tlSeq}")
                buildResume(m.optJSONObject("r"))?.let { put("resume", it) }          // сведения для «Продолжить»: сервер пишет их вместе с прогрессом в одной транзакции
            })
        }
        if (rows.length() == 0) return
        // очередь на диске: при обрыве сети отчёт дождётся следующей отправки (в том числе из следующего просмотра)
        ProgressOutbox.init(this)
        if (ProgressOutbox.enqueue(base, rows, homeOwner)) commit.forEach { it() }
    }

    // после закрытия: свести просмотр с Кинопабом (то же, что делает страница через 3–20 с)
    private fun tlSyncKino() {
        val base = homeBase.ifBlank { return }
        // после отправки очереди (иначе мост прочитает ещё старую позицию)
        ProgressOutbox.afterFlush(base) { try { homeConnection("$base/kp/sync/run").apply { connectTimeout = 5000; readTimeout = 60000 }.responseCode } catch (_: Exception) {} }
    }
    private var errored = false
    private val speeds = floatArrayOf(0.75f, 1f, 1.25f, 1.5f, 2f)
    private val resizeModes = intArrayOf(
        AspectRatioFrameLayout.RESIZE_MODE_FIT,
        AspectRatioFrameLayout.RESIZE_MODE_ZOOM,
        AspectRatioFrameLayout.RESIZE_MODE_FILL
    )
    private val resizeNames = arrayOf("Вписать", "Заполнить (обрезка)", "Растянуть")

    private val hideOverlay = Runnable { setOverlay(false) }
    private val commitSeek = Runnable {
        if (pendingSeek >= 0) {
            player.seekTo(pendingSeek)
            pendingSeek = -1
            seekStreak = 0
        }
    }
    private var skipRanges: List<Pair<Long, Long>> = emptyList()
    // YouTube: доступные качества (высота, кодек, адрес видео) и звук; переключение пересобирает источник на той же позиции
    private var ytQuals: List<Triple<Int, String, String>> = emptyList()
    private var ytAudioUrl = ""
    private var ytVideoUrl = ""

    private fun pickYtQuality() {
        val names = ytQuals.map { "${it.first}p · ${if (it.second == "avc1") "H.264" else "VP9"}" }
        val cur = ytQuals.indexOfFirst { it.third == ytVideoUrl }.coerceAtLeast(0)
        show("Качество", names, cur) { i ->
            val q = ytQuals[i]
            if (q.third == ytVideoUrl) return@show
            val pos = player.currentPosition
            ytVideoUrl = q.third
            val http = DefaultHttpDataSource.Factory().setAllowCrossProtocolRedirects(true).setConnectTimeoutMs(15_000).setReadTimeoutMs(30_000)
            val pf = ProgressiveMediaSource.Factory(http)
            val title = intent.getStringExtra("title") ?: ""
            val vi = MediaItem.Builder().setUri(Uri.parse(q.third)).setMediaMetadata(MediaMetadata.Builder().setTitle(title).build()).build()
            player.setMediaSource(MergingMediaSource(pf.createMediaSource(vi), pf.createMediaSource(MediaItem.fromUri(ytAudioUrl))), pos)
            player.prepare(); player.play()
            Toast.makeText(this, "Качество: ${q.first}p", Toast.LENGTH_SHORT).show()
        }
    }
    private var lastSkipAt = -1L

    private val ticker = object : Runnable {
        override fun run() {
            refreshProgress()
            if (skipRanges.isNotEmpty() && ::player.isInitialized && player.isPlaying) {
                val pos = player.currentPosition
                val r = skipRanges.firstOrNull { pos >= it.first && pos < it.second - 400 }
                if (r != null && r.first != lastSkipAt) {
                    lastSkipAt = r.first
                    player.seekTo(r.second)
                    Toast.makeText(this@HomePlayerActivity, "Пропущена вставка (${(r.second - r.first) / 1000} с)", Toast.LENGTH_SHORT).show()
                }
            }
            ui.postDelayed(this, 500)
        }
    }

    // дежурный напоминаний закрывает этот плеер, когда подошло время включить передачу
    fun closeForReminder() { runOnUiThread { try { closePlayer() } catch (_: Exception) { finish() } } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        homeBase = (intent.getStringExtra("home_base") ?: "").trimEnd('/')
        homeOwner = try { intent.getStringExtra("home_user_context")?.let { JSONObject(it) } } catch (_: Exception) { null }
        top.rootu.lampa.RemindWatcher.videoPlayer = java.lang.ref.WeakReference(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()
        val data = intent?.data
        if (data == null) {
            setResult(RESULT_FIRST_USER)
            finish()
            return
        }
        buildUi()
        frameRate = PlaybackFrameRateController(this)
        setupPlayer(data)
        ui.post(ticker)
        setOverlay(true)
    }

    // ---------- интерфейс ----------

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun label(text: String, size: Float, color: Int = Color.WHITE, bold: Boolean = false) =
        TextView(this).apply {
            this.text = text
            textSize = size
            setTextColor(color)
            if (bold) typeface = Typeface.DEFAULT_BOLD
            setShadowLayer(6f, 0f, 2f, Color.BLACK)
        }

    // ползунок прогресса: тонкая линия с «шариком»
    private inner class KnobBar(ctx: android.content.Context) : View(ctx) {
        var progress = 0
        private val track = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply { color = Color.argb(90, 255, 255, 255) }
        private val fill = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#F9A825") }
        private val knob = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; setShadowLayer(6f, 0f, 2f, Color.BLACK) }
        override fun onDraw(c: android.graphics.Canvas) {
            val h = height.toFloat(); val w = width.toFloat()
            val r = dp(8).toFloat(); val th = dp(3).toFloat()
            val cy = h / 2f
            val x = r + (w - 2 * r) * progress / 1000f
            c.drawRoundRect(r, cy - th / 2, w - r, cy + th / 2, th, th, track)
            c.drawRoundRect(r, cy - th / 2, x, cy + th / 2, th, th, fill)
            c.drawCircle(x, cy, r, knob)
        }
        fun set(p: Int) { val v = p.coerceIn(0, 1000); if (v != progress) { progress = v; invalidate() } }
    }

    // кнопка панели: прозрачная, при фокусе подсвечивается
    private fun ctl(text: String, size: Float, onClick: () -> Unit): TextView {
        val normal = GradientDrawable().apply { setColor(Color.TRANSPARENT); cornerRadius = dp(10).toFloat() }
        val focused = GradientDrawable().apply { setColor(Color.parseColor("#F9A825")); cornerRadius = dp(10).toFloat() }
        return TextView(this).apply {
            this.text = text
            textSize = size
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
            maxLines = 1
            setSingleLine(true)
            minWidth = dp(48)
            setPadding(dp(14), dp(10), dp(14), dp(10))
            background = normal
            isFocusable = true
            isFocusableInTouchMode = false
            isClickable = true
            setOnFocusChangeListener { v, has ->
                (v as TextView).setTextColor(if (has) Color.BLACK else Color.WHITE)
                v.background = if (has) focused else normal
            }
            setOnClickListener { onClick() }
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { setMargins(dp(2), 0, dp(2), 0) }
        }
    }

    // большая круглая кнопка «пауза/играть»
    private fun roundBtn(text: String, onClick: () -> Unit): TextView {
        val size = dp(60)
        val normal = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(Color.argb(190, 12, 14, 32)); setStroke(dp(2), Color.argb(230, 150, 175, 255)) }
        val focused = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(Color.parseColor("#F9A825")); setStroke(dp(2), Color.WHITE) }
        return TextView(this).apply {
            this.text = text
            textSize = 24f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
            background = normal
            isFocusable = true
            isFocusableInTouchMode = false
            isClickable = true
            setOnFocusChangeListener { v, has ->
                (v as TextView).setTextColor(if (has) Color.BLACK else Color.WHITE)
                v.background = if (has) focused else normal
            }
            setOnClickListener { onClick() }
            layoutParams = LinearLayout.LayoutParams(size, size).apply { setMargins(dp(10), 0, dp(10), 0) }
        }
    }

    private fun button(text: String, onClick: () -> Unit): TextView {
        val normal = GradientDrawable().apply { setColor(Color.argb(150, 20, 20, 30)); cornerRadius = dp(8).toFloat() }
        val focused = GradientDrawable().apply { setColor(Color.parseColor("#F9A825")); cornerRadius = dp(8).toFloat() }
        return TextView(this).apply {
            this.text = text
            textSize = 16f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
            maxLines = 1
            setSingleLine(true)
            setPadding(dp(14), dp(10), dp(14), dp(10))
            background = normal
            isFocusable = true
            isFocusableInTouchMode = false
            isClickable = true
            setOnFocusChangeListener { v, has ->
                (v as TextView).setTextColor(if (has) Color.BLACK else Color.WHITE)
                v.background = if (has) focused else normal
            }
            setOnClickListener { onClick() }
            val lp = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            lp.setMargins(dp(5), 0, dp(5), 0)
            layoutParams = lp
        }
    }

    private lateinit var rootFrame: FrameLayout
    private lateinit var cover: View
    private var curTexture = false
    private var globalTexture = false

    private val spinFrames = arrayOf("◜", "◝", "◞", "◟")
    private var spinIdx = 0
    private val spinTick = object : Runnable {
        override fun run() {
            if (!::spinner.isInitialized || spinner.visibility != View.VISIBLE || finished) return
            spinIdx = (spinIdx + 1) % spinFrames.size
            spinner.text = spinFrames[spinIdx]
            ui.postDelayed(this, 250)
        }
    }

    private fun showSpinner(show: Boolean) {
        if (!::spinner.isInitialized) return
        if (show) {
            if (bufferStartedAt > 0L) spinnerWasShown = true
            if (spinner.visibility != View.VISIBLE) { spinner.visibility = View.VISIBLE; ui.removeCallbacks(spinTick); ui.postDelayed(spinTick, 250) }
        } else {
            spinner.visibility = View.GONE
            ui.removeCallbacks(spinTick)
        }
    }

    private val delayedSpinner = Runnable {
        if (!finished && ::player.isInitialized && player.playWhenReady && player.playbackState == Player.STATE_BUFFERING)
            showSpinner(true)
    }

    private fun updateBufferIndicator(state: Int) {
        ui.removeCallbacks(delayedSpinner)
        if (state != Player.STATE_BUFFERING) {
            if (bufferStartedAt > 0L) {
                val elapsed = android.os.SystemClock.elapsedRealtime() - bufferStartedAt
                val bucket = when { elapsed < 350 -> "short"; elapsed < 2000 -> "medium"; else -> "long" }
                DLog.log("i", "buffer_episode", bucket, "ms=$elapsed shown=$spinnerWasShown live=$live")
                bufferStartedAt = 0L
            }
            showSpinner(false)
        } else {
            if (bufferStartedAt == 0L) { bufferStartedAt = android.os.SystemClock.elapsedRealtime(); spinnerWasShown = false }
            if (!player.playWhenReady) showSpinner(false)
            else if (!firstFrameSeen) showSpinner(true)
            else ui.postDelayed(delayedSpinner, 350)
        }
    }

    private fun makePlayerView(texture: Boolean): PlayerView =
        (if (texture) layoutInflater.inflate(R.layout.player_view_texture, null) as PlayerView else PlayerView(this)).apply {
            useController = false
            resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
            setShutterBackgroundColor(Color.BLACK)
            // при переключении канала держим последний кадр вместо чёрного экрана
            setKeepContentOnPlayerReset(true)
            keepScreenOn = true
        }

    // Вывод картинки выбирается автоматически по типу развёртки канала и запоминается:
    //  • чересстрочный (спутник 1080i/576i, декодер выдаёт 50 полей/с) — аппаратный слой: он склеивает поля, через GPU они мигают;
    //  • прогрессивный 25 кадров/с — через GPU (TextureView): на аппаратном слое у Amlogic кадры идут рывками.
    // Настройка «Вывод видео» = GPU принудительно для всех каналов.
    private fun learnedChannels(): MutableSet<String> =
        HashSet(getSharedPreferences("home_player", MODE_PRIVATE).getStringSet("tex_ch_v2", emptySet()) ?: emptySet())

    private fun saveLearned(s: Set<String>) {
        getSharedPreferences("home_player", MODE_PRIVATE).edit().putStringSet("tex_ch_v2", s).apply()
    }

    private fun wantTexture(id: String?): Boolean = globalTexture || (id != null && learnedChannels().contains(id))

    private var detectCh: String? = null
    private var detectKind = 0
    private var detectN = 0

    // раз в секунду: сколько кадров показано. ~25 — прогрессивное, ~50 — поля чересстрочного
    private fun detectScan(perSec: Int) {
        if (!live || globalTexture || !::player.isInitialized) return
        // A 50 fps stream shown while the previous channel still holds 24 Hz is not progressive 25 fps.
        // Classify output only after the new channel has acquired its own display cadence.
        if (frameRate.needsFrames) return
        val id = player.currentMediaItem?.localConfiguration?.uri?.lastPathSegment ?: return
        if (id != detectCh) { detectCh = id; detectKind = 0; detectN = 0 }
        val kind = when (perSec) { in 22..28 -> 1; in 46..54 -> 2; else -> 0 }
        if (kind == 0 || player.playbackState != Player.STATE_READY) { detectN = 0; detectKind = 0; return }
        if (kind == detectKind) detectN++ else { detectKind = kind; detectN = 1 }
        if (detectN != 4) return
        val learned = learnedChannels()
        if (kind == 1 && !curTexture) {
            learned.add(id); saveLearned(learned); swapSurface(true, restartDecoder = true)
            android.util.Log.d("HomePlayer", "вывод: $id прогрессивный → GPU")
        } else if (kind == 2 && curTexture) {
            learned.remove(id); saveLearned(learned); swapSurface(false, restartDecoder = true)
            android.util.Log.d("HomePlayer", "вывод: $id чересстрочный → аппаратный слой")
        }
    }

    private fun swapSurface(texture: Boolean, restartDecoder: Boolean = false) {
        if (texture == curTexture || !::player.isInitialized) return
        val restart = restartDecoder && player.playbackState == Player.STATE_READY
        val wasPlaying = player.playWhenReady
        val itemIndex = player.currentMediaItemIndex
        // Amlogic can freeze a running decoder when its output switches between SurfaceView
        // and TextureView. Stop it before replacing the output; playlist and AFR stay intact.
        if (restart) player.stop()
        curTexture = texture
        val old = playerView
        val nv = makePlayerView(texture)
        nv.resizeMode = old.resizeMode
        rootFrame.addView(nv, 0, FrameLayout.LayoutParams(-1, -1))
        // Attach the new output before detaching the old one. A temporary dummy surface
        // can leave Amlogic's running decoder without frame callbacks after the swap.
        PlayerView.switchTargetView(player, old, nv)
        rootFrame.removeView(old)
        playerView = nv
        DLog.log("i", "video_surface", if (texture) "texture" else "surface")
        if (restart) {
            player.seekToDefaultPosition(itemIndex)
            player.prepare()
            player.playWhenReady = wasPlaying
            android.util.Log.i("HomePlayer", "surface_decoder_restart texture=$texture item=$itemIndex")
        }
    }

    private fun buildUi() {
        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        // способ вывода картинки: surface (по умолчанию, аппаратный слой) или texture (через GPU — выручает там,
        // где у приставки неровно ходят кадры); переключается extra "surface" или настройкой home_player/surface
        // режим не запоминается в самом плеере: его каждый раз передаёт настройка «Вывод видео» (tv_surface), по умолчанию — аппаратный
        val surf = intent.getStringExtra("surface") ?: "surface"
        globalTexture = surf == "texture"
        curTexture = globalTexture
        playerView = makePlayerView(curTexture)
        rootFrame = root
        root.addView(playerView, FrameLayout.LayoutParams(-1, -1))
        // чёрная подложка до первого кадра: новый декодер сначала показывает зелёный экран
        cover = View(this).apply { setBackgroundColor(Color.BLACK) }
        root.addView(cover, FrameLayout.LayoutParams(-1, -1))

        overlay = FrameLayout(this)
        // «стеклянные» затемнения сверху и снизу
        overlay.addView(View(this).apply {
            background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(Color.argb(225, 0, 0, 0), Color.argb(120, 0, 0, 0), Color.TRANSPARENT))
        }, FrameLayout.LayoutParams(-1, dp(200), Gravity.TOP))
        overlay.addView(View(this).apply {
            background = GradientDrawable(GradientDrawable.Orientation.BOTTOM_TOP, intArrayOf(Color.argb(240, 0, 0, 0), Color.argb(170, 0, 0, 0), Color.TRANSPARENT))
        }, FrameLayout.LayoutParams(-1, dp(270), Gravity.BOTTOM))

        // верх слева: вид передачи, крупное название, подпись
        val top = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        kickerView = label("", 12f, Color.parseColor("#C8C8C8"), true).apply { letterSpacing = 0.2f }
        titleView = label("", 30f, bold = true).apply { setSingleLine(true); ellipsize = android.text.TextUtils.TruncateAt.END }
        descView = label("", 16f, Color.parseColor("#DDDDDD")).apply { maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END }
        top.addView(kickerView, LinearLayout.LayoutParams(-1, -2))
        top.addView(titleView, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(2) })
        top.addView(descView, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(4) })
        overlay.addView(top, FrameLayout.LayoutParams(resources.displayMetrics.widthPixels * 6 / 10, -2, Gravity.TOP or Gravity.START).apply {
            setMargins(dp(44), dp(30), 0, 0)
        })

        // верх справа: параметры потока и время окончания
        val stats = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.END }
        statsView = label("", 12.5f, Color.parseColor("#CCCCCC")).apply { gravity = Gravity.END }
        endsTopView = label("", 13f, Color.parseColor("#BBBBBB")).apply { gravity = Gravity.END }
        stats.addView(statsView, LinearLayout.LayoutParams(-2, -2))
        stats.addView(endsTopView, LinearLayout.LayoutParams(-2, -2).apply { topMargin = dp(6) })
        overlay.addView(stats, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.END).apply { setMargins(0, dp(34), dp(150), 0) })

        val bottom = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        // полоса прогресса с ползунком и подписями времени
        bar = KnobBar(this)
        timeView = label("0:00", 15f)
        endsView = label("", 15f).apply { gravity = Gravity.END }
        val timeRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        timeRow.addView(timeView, LinearLayout.LayoutParams(0, -2, 1f))
        timeRow.addView(endsView, LinearLayout.LayoutParams(0, -2, 1f))

        // кнопки: слева формат и скорость, по центру перемотка, справа качество, звук, субтитры, источник
        controlsRow = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER }
        transportRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        val leftG = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL or Gravity.START }
        val midG = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        val rightG = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL or Gravity.END }

        playBtn = roundBtn("⏸") { togglePause() }
        val prevB = ctl("⏮", 20f) { step(-1) }
        prevEpBtn = prevB
        val backB = ctl("−30с", 16f) { seekBy(-30_000) }.apply { contentDescription = "Назад на 30 секунд" }
        val fwdB = ctl("+30с", 16f) { seekBy(30_000) }.apply { contentDescription = "Вперёд на 30 секунд" }
        val nextB = ctl("⏭", 20f) { step(1) }
        nextEpBtn = nextB
        seekButtons.addAll(listOf(backB, fwdB))
        prevB.contentDescription = "Предыдущая серия"; nextB.contentDescription = "Следующая серия"

        aspectBtn = ctl("⛶", 20f) { pickResize() }
        firstCtl = aspectBtn
        aspectBtn.contentDescription = "Формат изображения"
        speedBtn = ctl("1×", 15f) { pickSpeed() }
        speedBtn.contentDescription = "Скорость воспроизведения"
        leftG.addView(aspectBtn); leftG.addView(speedBtn)

        midG.addView(prevB); midG.addView(backB); midG.addView(playBtn); midG.addView(fwdB); midG.addView(nextB)

        qualityBtn = ctl("Авто", 15f) { pickQuality() }
        audioBtn = ctl("♪", 15f) { pickAudio() }.apply { maxWidth = dp(170); ellipsize = android.text.TextUtils.TruncateAt.END; contentDescription = "Звуковая дорожка" }
        subsBtn = ctl("SUB", 15f) { pickText() }.apply { contentDescription = "Субтитры" }
        srcBtn = ctl("⇄", 20f) { pickSource() }.apply { contentDescription = "Источник канала" }
        srcBtn.visibility = View.GONE
        extBtn = ctl("↗", 20f) { openExternal() }.apply { contentDescription = "Внешний плеер" }
        rightG.addView(srcBtn); rightG.addView(qualityBtn); rightG.addView(audioBtn); rightG.addView(subsBtn); rightG.addView(extBtn)

        transportRow.addView(midG, LinearLayout.LayoutParams(-2, -2))
        val settingsRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        settingsRow.addView(leftG); settingsRow.addView(rightG)
        controlsRow.addView(transportRow, LinearLayout.LayoutParams(-1, -2))
        controlsRow.addView(settingsRow, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(4) })

        bottom.addView(bar, LinearLayout.LayoutParams(-1, dp(18)))
        bottom.addView(timeRow, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(2) })
        bottom.addView(controlsRow, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8) })
        overlay.addView(bottom, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM).apply {
            setMargins(dp(44), 0, dp(44), dp(22))
        })

        // шрифты списка: 0 — мелкий, 1 — средний, 2 — крупный
        val fontLevel = intent.getIntExtra("tv_font", 0).coerceIn(0, 2)
        // мелкий уровень рассчитан на 25–35 каналов на экране: одна строка на канал
        // размеры увеличены на 25% (крупнее, в списке помещается меньше каналов)
        val nameSp = floatArrayOf(12.75f, 18f, 25.5f)[fontLevel]
        val progSp = floatArrayOf(11.25f, 15f, 21f)[fontLevel]
        val logoDp = intArrayOf(22, 36, 51)[fontLevel]

        panel = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            // непрозрачность списка каналов из настройки ТВ (проценты; без настройки — как раньше, ≈93%)
            setBackgroundColor(Color.argb(intent.getIntExtra("tv_panel_alpha", 93).coerceIn(20, 100) * 255 / 100, 12, 12, 24))
            visibility = View.GONE
            setPadding(dp(10), dp(8), dp(10), dp(6))
        }
        // левая колонка: каналы с логотипами и программой «сейчас»
        val left = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        chList = ListView(this).apply {
            divider = null
            isFocusable = true
            setSelector(GradientDrawable().apply { setColor(Color.argb(120, 249, 168, 37)); cornerRadius = dp(6).toFloat() })
            adapter = object : BaseAdapter() {
                override fun getCount() = chNames.size
                override fun getItem(position: Int) = chNames[position]
                override fun getItemId(position: Int) = position.toLong()
                override fun getView(position: Int, convertView: View?, parent: ViewGroup?): View {
                    val row = (convertView as? LinearLayout) ?: LinearLayout(this@HomePlayerActivity).apply {
                        orientation = LinearLayout.HORIZONTAL
                        gravity = Gravity.CENTER_VERTICAL
                        setPadding(dp(6), dp(1), dp(6), dp(1))
                        addView(android.widget.ImageView(this@HomePlayerActivity).apply { scaleType = android.widget.ImageView.ScaleType.FIT_CENTER },
                            LinearLayout.LayoutParams(dp(logoDp * 3 / 2), dp(logoDp)).apply { rightMargin = dp(6) })
                        addView(label("", nameSp, bold = true).apply { setSingleLine(true); includeFontPadding = false; ellipsize = android.text.TextUtils.TruncateAt.END; setShadowLayer(0f, 0f, 0f, 0) },
                            LinearLayout.LayoutParams(0, -2, 0.42f))
                        addView(label("", progSp, Color.parseColor("#AAAAAA")).apply { setSingleLine(true); includeFontPadding = false; ellipsize = android.text.TextUtils.TruncateAt.END; setShadowLayer(0f, 0f, 0f, 0) },
                            LinearLayout.LayoutParams(0, -2, 0.58f))
                        // значок источника: спутник или интернет
                        addView(label("", nameSp).apply { gravity = Gravity.CENTER; setShadowLayer(0f, 0f, 0f, 0) }, LinearLayout.LayoutParams(dp(30), -2))
                    }
                    val sid = Uri.parse(chUris[position]).lastPathSegment ?: ""
                    val n = epgMap?.optJSONObject(sid)?.optJSONObject("n")
                    val cur = position == player.currentMediaItemIndex
                    (row.getChildAt(1) as TextView).apply { text = (if (cur) "▶ " else "") + chNames[position]; setTextColor(if (cur) Color.parseColor("#F9A825") else Color.WHITE) }
                    (row.getChildAt(2) as TextView).text = if (n != null) "${fmtClock(n.optLong("s"))} ${n.optString("t")}" else ""
                    loadLogo(row.getChildAt(0) as android.widget.ImageView, logoMap[sid], chNames[position])
                    (row.getChildAt(3) as TextView).text = when (srcMap[sid]) { "ts" -> "\uD83D\uDCE1"; "hls" -> "\uD83C\uDF10"; else -> "" }
                    return row
                }
            }
            setOnItemClickListener { _, _, pos, _ -> player.seekTo(pos, C.TIME_UNSET); hidePanel() }
            // круговая прокрутка: с первого канала вверх — на последний, с последнего вниз — на первый
            setOnKeyListener { v, keyCode, ev ->
                val lv = v as ListView
                val n = chNames.size
                if (ev.action == KeyEvent.ACTION_DOWN && n > 1) {
                    if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT || keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) {
                        // страницы выровнены: первая строка страницы всегда одна и та же; выбор переходит на ту же строку соседней страницы
                        val page = chPageSize(lv)
                        val cur = lv.selectedItemPosition.coerceAtLeast(0)
                        val row = cur % page
                        val pg = cur / page
                        val lastPg = (n - 1) / page
                        val toPg = if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) (if (pg >= lastPg) 0 else pg + 1) else (if (pg <= 0) lastPg else pg - 1)
                        chSelectPaged(lv, (toPg * page + row).coerceAtMost(n - 1))
                        true
                    }
                    else if (keyCode == KeyEvent.KEYCODE_DPAD_UP || keyCode == KeyEvent.KEYCODE_DPAD_DOWN) {
                        val cur = lv.selectedItemPosition.coerceAtLeast(0)
                        val page = chPageSize(lv)
                        val to = if (keyCode == KeyEvent.KEYCODE_DPAD_DOWN) (if (cur >= n - 1) 0 else cur + 1) else (if (cur <= 0) n - 1 else cur - 1)
                        // внутри страницы выбор идёт обычным образом; на границе страница листается целиком
                        if (to / page == cur / page) false else { chSelectPaged(lv, to); true }
                    }
                    else false
                } else false
            }
            onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
                override fun onItemSelected(p: android.widget.AdapterView<*>?, v: View?, pos: Int, id: Long) { scheduleDetail(pos) }
                override fun onNothingSelected(p: android.widget.AdapterView<*>?) {}
            }
        }
        left.addView(chList, LinearLayout.LayoutParams(-1, 0, 1f))
        left.addView(label("OK — включить · удержание — программа и автопереключение · ← → страницы", 8.4f, Color.parseColor("#888888")).apply { includeFontPadding = false }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(3) })
        panel!!.addView(left, LinearLayout.LayoutParams(0, -1, 0.5f))

        // правая колонка: программа выбранного канала с описанием
        val right = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(18), dp(4), dp(8), 0) }
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        detLogo = android.widget.ImageView(this).apply { scaleType = android.widget.ImageView.ScaleType.FIT_CENTER }
        head.addView(detLogo, LinearLayout.LayoutParams(dp(logoDp + 14), dp(logoDp + 14)).apply { rightMargin = dp(10) })
        detName = label("", nameSp + 3f, bold = true)
        head.addView(detName, LinearLayout.LayoutParams(0, -2, 1f))
        right.addView(head, LinearLayout.LayoutParams(-1, -2))
        detNow = label("", nameSp + 1f, Color.parseColor("#F9A825"), true)
        right.addView(detNow, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(12) })
        detDesc = label("", progSp + 1f, Color.parseColor("#DDDDDD"))
        right.addView(detDesc, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(6) })
        right.addView(label("Далее", progSp + 1f, Color.parseColor("#888888"), true), LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(14) })
        detNext = label("", progSp + 1f, Color.parseColor("#CCCCCC"))
        right.addView(detNext, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(4) })
        panel!!.addView(right, LinearLayout.LayoutParams(0, -1, 0.5f))
        overlay.addView(panel, FrameLayout.LayoutParams(dp(900), -1, Gravity.START))

        statusView = label("", 20f).apply { gravity = Gravity.CENTER }
        overlay.addView(statusView, FrameLayout.LayoutParams(-2, -2, Gravity.CENTER))
        // бегунок загрузки поверх застывшего кадра (не зависит от показа панели)
        // лёгкий бегунок: 4 обновления в секунду вместо анимации на каждый кадр (на слабых приставках постоянная перерисовка
        // поверх видео забивала GPU, и пульт отвечал с задержкой, пока канал ждёт данные)
        spinner = TextView(this).apply {
            text = "◜"
            textSize = 44f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
            visibility = View.GONE
        }
        root.addView(spinner, FrameLayout.LayoutParams(dp(56), dp(56), Gravity.CENTER))

        root.addView(overlay, FrameLayout.LayoutParams(-1, -1))
        seekHint = TextView(this).apply {
            setTextColor(Color.WHITE)
            textSize = 24f
            typeface = Typeface.DEFAULT_BOLD
            setPadding(dp(22), dp(10), dp(22), dp(10))
            background = GradientDrawable().apply { setColor(Color.argb(190, 0, 0, 0)); cornerRadius = dp(12).toFloat() }
            visibility = View.GONE
        }
        root.addView(seekHint, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(70) })
        setContentView(root)
    }

    private fun hideSystemBars() {
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
        if (hasFocus) armChannelTimeout() else { ui.removeCallbacks(closeChannels); channelOk.cancel(); liveOk.cancel() }
    }

    // ---------- плеер ----------

    private fun setupPlayer(data: Uri) {
        firstFrameSeen = false
        val headers = HashMap<String, String>()
        intent.getStringArrayExtra("headers")?.let { h ->
            var i = 0
            while (i + 1 < h.size) { headers[h[i]] = h[i + 1]; i += 2 }
        }
        val http = DefaultHttpDataSource.Factory()
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(15_000)
            .setReadTimeoutMs(30_000)
        headers["User-Agent"]?.let { http.setUserAgent(it) }
        headers.remove("User-Agent")
        if (headers.isNotEmpty()) http.setDefaultRequestProperties(headers)

        live = intent.getBooleanExtra("live", false)
        val playerPrefs = getSharedPreferences("home_player", MODE_PRIVATE)
        val afr = playerPrefs.getBoolean("afr", intent.getIntExtra("tv_afr", 1) != 0)
        frameRate.configure(afr, live)
        android.util.Log.i("HomeAFR", "afr_config enabled=$afr live=$live")
        drc.mode = playerPrefs.getInt("drc", 0).coerceIn(0, 2)
        tvNowUrl = intent.getStringExtra("tv_now_url")
        tvDev = intent.getStringExtra("tv_dev") ?: ""
        if (live) {
            seekButtons.forEach { it.visibility = View.GONE } // в эфире нет перемотки
            playBtn.visibility = View.GONE
            speedBtn.visibility = View.GONE
            extBtn.visibility = View.GONE
            srcBtn.visibility = View.VISIBLE
            // В эфире нижняя видеополоса не имеет смысла: канал и EPG уже
            // показывают текущее состояние, а линия выглядела как перемотка.
            bar.visibility = View.GONE
            if (!epgStarted) { epgStarted = true; startEpgPolling() }
        }
        // размер буфера для прямого эфира: 0 — минимальная задержка, иначе N секунд
        val bufSec = intent.getIntExtra("buffer_sec", -1)
        val loadControl = if (bufSec >= 0) {
            val ms = bufSec * 1000
            DefaultLoadControl.Builder()
                .setBufferDurationsMs(maxOf(ms, 1000), maxOf(ms, 1000) + 8000, if (ms == 0) 250 else ms, if (ms == 0) 500 else ms)
                .setPrioritizeTimeOverSizeThresholds(true)
                .build()
        } else if (!live) {
            // фильмы и серии: запас 40–120 с вперёд, 30 с назад (перемотка назад не качает заново);
            // настройка «буфер фильмов» (extra vod_buf_mb) поднимает лимит по объёму до выбранного, но не выше безопасной доли кучи приложения
            val reqMb = intent.getIntExtra("vod_buf_mb", 0)
            val heapSafe = (Runtime.getRuntime().maxMemory() * 0.8).toLong() - 64L * 1024 * 1024
            val bigBytes = if (reqMb > 0) minOf(reqMb * 1024L * 1024L, heapSafe) else 0L
            val big = bigBytes > 160L * 1024 * 1024       // меньше стандартного смысла нет
            android.util.Log.i("HomePlayer", "буфер кино: запрошено ${reqMb} МБ, куча ${Runtime.getRuntime().maxMemory() / 1048576} МБ, итог ${if (big) (bigBytes / 1048576).toString() + " МБ" else "стандартный"}")
            DefaultLoadControl.Builder()
                .setBufferDurationsMs(40_000, if (big) 900_000 else 120_000, 2_500, 6_000)
                .setBackBuffer(30_000, true)
                .setPrioritizeTimeOverSizeThresholds(true)
                .apply { if (big) setTargetBufferBytes(bigBytes.toInt()) }
                .build()
        } else DefaultLoadControl.Builder().build()

        // туннельное воспроизведение (кадры идут через аппаратный тракт декодера): популярное лекарство от неровного хода
        // кадров на Amlogic; включается extra "tunnel" (true/false), запоминается в настройках
        val sp = getSharedPreferences("home_player", MODE_PRIVATE)
        if (intent.hasExtra("tunnel")) sp.edit().putBoolean("tunnel", intent.getBooleanExtra("tunnel", false)).apply()
        val tunnel = sp.getBoolean("tunnel", false)
        // диагностика: extra "noaudio" отключает звук (проверка, не рвёт ли кадры неровный ход аудио-часов)
        val noAudio = intent.getBooleanExtra("noaudio", false)
        trackSelector = DefaultTrackSelector(this).apply {
            parameters = buildUponParameters()
                .setTrackTypeDisabled(C.TRACK_TYPE_AUDIO, noAudio)
                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
                .setTunnelingEnabled(tunnel && drc.mode == 0)
                .setPreferredAudioLanguages("rus", "ru")
                .setAllowVideoMixedMimeTypeAdaptiveness(true)
                .apply {
                    // кино и сериалы: не «авто с разгоном», а лучшая дорожка в пределах профиля устройства (FullHD-телевизору 4K не нужен);
                    // вручную качество по-прежнему переключается в меню плеера
                    if (!live) {
                        setForceHighestSupportedBitrate(true)
                        if(memQuality==0){trackSelector.parameters=trackSelector.buildUponParameters().clearOverridesOfType(C.TRACK_TYPE_VIDEO).build();return}
        val limit=intent.getIntExtra("max_height",0)
        val mh=memQuality?.takeIf { it>0 }?.let { if(limit>0)minOf(limit,it)else it }?:limit
                        // класс качества считаем по ширине: кадр 1920x804 — это 1080p, а не «804p»
                        if (mh > 0) setMaxVideoSize(mh * 16 / 9 + 8, Int.MAX_VALUE)
                    }
                }
                .build()
        }
        player = ExoPlayer.Builder(
            this,
            // декодер при смене канала создаётся заново (иначе Amlogic «сыплет» картинку после переключения)
            NoReuseRenderersFactory(this, drc)
                .setEnableDecoderFallback(true)
                // программные декодеры FFmpeg подключаются, когда аппаратного нет (MP2 и др.)
                .setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_ON)
        )
            .setVideoChangeFrameRateStrategy(C.VIDEO_CHANGE_FRAME_RATE_STRATEGY_OFF)
            .setTrackSelector(trackSelector)
            .setLoadControl(loadControl)
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(http))
            .setSeekBackIncrementMs(10_000)
            .setSeekForwardIncrementMs(10_000)
            .build()
        playerView.player = player
        if (live) swapSurface(wantTexture(data.lastPathSegment))
        player.addAnalyticsListener(androidx.media3.exoplayer.util.EventLogger())
        player.addAnalyticsListener(object : androidx.media3.exoplayer.analytics.AnalyticsListener {
            override fun onVideoInputFormatChanged(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime,
                format: androidx.media3.common.Format, decoderReuseEvaluation: androidx.media3.exoplayer.DecoderReuseEvaluation?) {
                if (eventTime.windowIndex == player.currentMediaItemIndex) offerFrameRate(format)
                DLog.log("i", "video_format", format.frameRate.toString(), "size=${format.width}x${format.height} codec=${format.codecs}")
            }
            override fun onVideoDecoderInitialized(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime,
                decoderName: String, initializedTimestampMs: Long, initializationDurationMs: Long) {
                DLog.log("i", "video_decoder", decoderName, "init_ms=$initializationDurationMs")
            }
            override fun onAudioSinkError(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime, audioSinkError: Exception) {
                DLog.log("w", "audio_sink_error", audioSinkError.javaClass.simpleName, "drc=${drc.mode}")
            }
        })
        player.setVideoFrameMetadataListener { presentationTimeUs, _, format, _ ->
            if (frameRate.needsFrames && format.frameRate <= 0f) {
                val epoch = frameRate.generation
                ptsRate.frame(presentationTimeUs, epoch)?.let { fps -> ui.post {
                    if (!finished) frameRate.offer(fps * player.playbackParameters.speed, "pts", epoch)
                } }
            }
        }
        player.setSeekParameters(androidx.media3.exoplayer.SeekParameters.CLOSEST_SYNC)
        // скорость запоминается между запусками
        if (!live) {
            val saved = getSharedPreferences("home_player", MODE_PRIVATE).getFloat(top.rootu.lampa.ProfileRuntime.scopeKey("speed"), 1f)
            val si = speeds.indexOfFirst { it == saved }
            if (si >= 0) { speedIdx = si; player.setPlaybackSpeed(saved) }
        }

        val uris: List<Uri> = (intent.getParcelableArrayExtra("video_list")
            ?.filterIsInstance<Uri>()?.takeIf { it.isNotEmpty() }) ?: listOf(data)
        names = intent.getStringArrayExtra("video_list.name")?.toList() ?: emptyList()
        chUris = uris.map { it.toString() }
        chNames = uris.mapIndexed { i, _ -> names.getOrNull(i) ?: "${i + 1}" }
        val title = intent.getStringExtra("title") ?: ""
        startIndex = uris.indexOfFirst { it == data }.coerceAtLeast(0)

        val subs = intent.getParcelableArrayExtra("subs")?.filterIsInstance<Uri>() ?: emptyList()
        val subNames = intent.getStringArrayExtra("subs.name")

        val items = uris.mapIndexed { i, u ->
            val b = MediaItem.Builder().setUri(u)
                .setMediaMetadata(MediaMetadata.Builder().setTitle(names.getOrNull(i) ?: title).build())
            if (i == startIndex && subs.isNotEmpty()) {
                b.setSubtitleConfigurations(subs.mapIndexed { si, su ->
                    MediaItem.SubtitleConfiguration.Builder(su)
                        .setMimeType(subMime(su))
                        .setLabel(subNames?.getOrNull(si) ?: "Субтитры ${si + 1}")
                        .setLanguage(null)
                        .build()
                })
            }
            b.build()
        }
        val ytAudio = intent.getStringExtra("yt_audio")
        ytAudioUrl = ytAudio ?: ""
        ytVideoUrl = uris.firstOrNull()?.toString() ?: ""
        try {
            val qa = org.json.JSONArray(intent.getStringExtra("yt_quals") ?: "[]")
            ytQuals = (0 until qa.length()).map { qa.getJSONObject(it).let { o -> Triple(o.getInt("h"), o.optString("v"), o.getString("url")) } }
        } catch (_: Exception) {}
        if (!live && !ytAudio.isNullOrBlank() && items.size == 1) {
            // YouTube отдаёт видео и звук разными файлами: склеиваем на лету
            val pf = ProgressiveMediaSource.Factory(http)
            player.setMediaSource(
                MergingMediaSource(pf.createMediaSource(items[0]), pf.createMediaSource(MediaItem.fromUri(ytAudio))),
                intent.getIntExtra("position", 0).toLong().coerceAtLeast(0)
            )
        } else
        player.setMediaItems(items, startIndex, intent.getIntExtra("position", 0).toLong().coerceAtLeast(0))
        // рекламные вставки (SponsorBlock): [[с, по], …] в секундах
        try {
            val arr = org.json.JSONArray(intent.getStringExtra("yt_skip") ?: "[]")
            skipRanges = (0 until arr.length()).map { arr.getJSONArray(it).let { r -> (r.getDouble(0) * 1000).toLong() to (r.getDouble(1) * 1000).toLong() } }
        } catch (_: Exception) {}
        audioKey = intent.getStringExtra("audio_key") ?: ""
        ProgressOutbox.init(this); ProgressOutbox.flush(homeBase)       // дослать то, что не ушло в прошлый раз
        tlMeta = try { intent.getStringExtra("tl_meta")?.let { JSONArray(it) } } catch (_: Exception) { null }
        resumeList = try { intent.getStringExtra("resume_playlist")?.let { JSONArray(it) } } catch (_: Exception) { null }
        if(homeOwner!=null && tlMeta!=null) Thread {
            try {
                val ids=JSONArray();val seen=HashSet<String>();val meta=tlMeta!!
                for(i in 0 until meta.length()) { val id=meta.getJSONObject(i).optString("id");if(id.isNotBlank()&&seen.add(id))ids.put(id) }
                val c=homeConnection("$homeBase/home/play/grant");c.requestMethod="POST";c.doOutput=true;c.connectTimeout=3000;c.readTimeout=6000;c.setRequestProperty("Content-Type","application/json")
                c.outputStream.use { it.write(JSONObject().put("card",meta.optJSONObject(0)?.optString("c")).put("identities",ids).toString().toByteArray()) }
                if(c.responseCode==200){val r=JSONObject(c.inputStream.bufferedReader().use { it.readText() });val grant=r.optString("grant");if(grant.isNotBlank())homeOwner=JSONObject(homeOwner.toString()).put("progress_grant",grant)}
                c.disconnect()
            } catch (_: Exception) { }
        }.start()

        DLog.init(this); DLog.setBase(homeBase)
        DLog.log("i", "player_start", (if (live) "live" else if (tlMeta != null) "vod" else "vod_nometa") + " " + srcTag(data), "items=" + (tlMeta?.length() ?: 0) + " list=" + (resumeList?.length() ?: 0) + " title=" + title.take(40))
        if (!live && tlMeta != null && homeBase.isNotBlank()) { tlCur = startIndex; ui.postDelayed(tlTick, 10_000) }
        if (!live && audioKey.isNotBlank() && homeBase.isNotBlank()) { loadAudioMemory(); loadViewMemory() }
        player.addListener(object : Player.Listener {
            override fun onTracksChanged(tracks: androidx.media3.common.Tracks) { applyAudioMemory(); applyTextMemory(); applyDefaultQuality() }

            override fun onPlaybackStateChanged(state: Int) {
                statusView.text = if (state == Player.STATE_BUFFERING && !live) "Загрузка…" else ""
                updateBufferIndicator(state)
                if (state == Player.STATE_ENDED) {
                    if (live) reconnect() else { completed = true; closePlayer() }
                }
                if (state == Player.STATE_READY) { liveRetries = 0; updateTitle() }
            }

            override fun onRenderedFirstFrame() {
                firstFrameSeen = true
                ui.removeCallbacks(delayedSpinner)
                cover.visibility = View.GONE
                updateBufferIndicator(player.playbackState)
            }

            override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                // автопереход на следующую серию: прошлая досмотрена
                if (reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO) {
                    try { player.getMediaItemAt(tlCur).localConfiguration?.uri?.toString()?.let { tlAutoDone.add(it) } } catch (_: Exception) {}
                    if (tlMeta != null) tlSend(listOf(tlCur), true)
                }
                tlCur = player.currentMediaItemIndex
                qualityApplied = false; lastQualityClass = 0      // у каждой серии свой набор дорожек: качество по умолчанию выбираем заново
                if (beginFrameRateItem()) firstFrameSeen = false
                if (!firstFrameSeen) showSpinner(true)
                if (live && !firstFrameSeen) cover.visibility = View.VISIBLE
                if (live) swapSurface(wantTexture(mediaItem?.localConfiguration?.uri?.lastPathSegment))
                // ручной выбор источника действует только на этом канале: ушли с него — снова «авто»
                if (forcedIdx >= 0 && player.currentMediaItemIndex != forcedIdx) {
                    forcedOrig?.let { o -> try { player.replaceMediaItem(forcedIdx, o) } catch (_: Exception) {} }
                    forcedIdx = -1; forcedOrig = null; forcedSrc = -1
                }
                ui.removeCallbacks(saveLast)
                if (live) ui.postDelayed(saveLast, 5000) // канал считается просмотренным, если на нём задержались
                updateTitle()
                setOverlay(true)
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                playBtn.text = if (isPlaying) "⏸" else "▶"
                ui.removeCallbacks(hideOverlay)
                // пошло воспроизведение — панель прячется через несколько секунд
                if (isPlaying && overlay.visibility == View.VISIBLE && !menuMode) ui.postDelayed(hideOverlay, HIDE_DELAY)
            }

            override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
                updateBufferIndicator(player.playbackState)
                if (!playWhenReady && !live && !finished && firstFrameSeen && tlMeta != null) {
                    tlRemember()
                    tlSend(listOf(tlCur), false, force = true)
                }
            }

            override fun onPlaybackParametersChanged(playbackParameters: androidx.media3.common.PlaybackParameters) {
                if (!live) { beginFrameRateItem(); applyFrameRate() }
            }

            override fun onPlayerError(error: PlaybackException) {
                if (live && liveRetries < 30) {
                    DLog.log("w", "live_retry", error.errorCodeName, "attempt=${liveRetries + 1} buffer=${player.totalBufferedDuration}")
                    reconnect(); return
                }
                val decoderIssue = error.errorCode == PlaybackException.ERROR_CODE_DECODER_INIT_FAILED ||
                    error.errorCode == PlaybackException.ERROR_CODE_DECODING_FAILED ||
                    error.errorCode == PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES
                if (decoderIssue && !decoderRetried) {
                    decoderRetried = true
                    val pos = player.currentPosition
                    player.prepare(); player.seekTo(pos); player.play()
                    return
                }
                errored = true
                DLog.log("e", "player_error", error.errorCodeName, (if (live) "live " else "vod ") + "decoderRetried=" + decoderRetried)
                DLog.flush()
                Toast.makeText(this@HomePlayerActivity, "Ошибка воспроизведения: ${error.errorCodeName}", Toast.LENGTH_LONG).show()
                closePlayer()
            }
        })
        beginFrameRateItem()
        player.prepare()
        player.playWhenReady = true
        startStatsLog()
        updateTitle()
        if (live) ui.postDelayed(saveLast, 5000)
    }

    // Диагностика плавности (logcat, тег HomePlayerStats): показано/потеряно кадров за секунду и запас буфера
    private var statsRendered = 0
    private var statsDropped = 0
    private var statsToken = 0
    // Диагностика тормозов: задачи главного потока, державшие интерфейс дольше 80 мс (logcat, тег HomePlayerSlow)
    private var slowT0 = 0L
    private fun startSlowLog() {
        android.os.Looper.getMainLooper().setMessageLogging { s ->
            if (s.startsWith(">>>>>")) slowT0 = android.os.SystemClock.uptimeMillis()
            else if (s.startsWith("<<<<<") && slowT0 > 0) {
                val d = android.os.SystemClock.uptimeMillis() - slowT0
                if (d >= 80) android.util.Log.d("HomePlayerSlow", "${d}ms ${s.take(160)}")
                slowT0 = 0
            }
        }
    }

    private fun startStatsLog() {
        startSlowLog()
        val tok = ++statsToken
        statsRendered = 0; statsDropped = 0
        val tick = object : Runnable {
            override fun run() {
                if (finished || tok != statsToken || !::player.isInitialized) return
                try {
                    val c = player.videoDecoderCounters
                    if (c != null) {
                        c.ensureUpdated()
                        val r = c.renderedOutputBufferCount; val d = c.droppedBufferCount + c.skippedOutputBufferCount
                        android.util.Log.d("HomePlayerStats", "rendered/s=${r - statsRendered} dropped/s=${d - statsDropped} " +
                            "buffer=${player.totalBufferedDuration}ms state=${player.playbackState} playing=${player.isPlaying} pwr=${player.playWhenReady} suppress=${player.playbackSuppressionReason} ch=${player.currentMediaItem?.localConfiguration?.uri?.lastPathSegment} " +
                            "v=${player.videoFormat?.let { "${it.width}x${it.height} ${it.codecs} ${it.frameRate}fps" }} a=${player.audioFormat?.let { "${it.sampleMimeType} ${it.channelCount}ch" }}")
                        detectScan(r - statsRendered)
                        statsRendered = r; statsDropped = d
                    }
                } catch (_: Throwable) {}
                ui.postDelayed(this, 1000)
            }
        }
        ui.postDelayed(tick, 1000)
    }

    private fun beginFrameRateItem(): Boolean {
        val speed = if (live) 1f else player.playbackParameters.speed
        val changed = frameRate.beginItem("${player.currentMediaItemIndex}:$speed")
        if (changed) ptsRate.reset()
        return changed
    }

    private fun offerFrameRate(format: androidx.media3.common.Format?) {
        if (format == null || !::frameRate.isInitialized) return
        frameRate.offer(format.frameRate * player.playbackParameters.speed, "format")
    }

    private fun applyFrameRate() { offerFrameRate(player.videoFormat) }

    private fun subMime(u: Uri): String {
        val p = (u.path ?: "").lowercase(Locale.ROOT)
        return when {
            p.endsWith(".srt") -> MimeTypes.APPLICATION_SUBRIP
            p.endsWith(".ass") || p.endsWith(".ssa") -> MimeTypes.TEXT_SSA
            else -> MimeTypes.TEXT_VTT
        }
    }

    private fun updateTitle() {
        val idx = player.currentMediaItemIndex
        val t = names.getOrNull(idx)
            ?: player.currentMediaItem?.mediaMetadata?.title?.toString()
            ?: intent.getStringExtra("title") ?: ""
        titleView.text = t
        // кнопки «предыдущая / следующая серия» — только когда в списке больше одной серии; подпись показывает, что именно произойдёт
        if (!live && ::prevEpBtn.isInitialized) {
            val many = player.mediaItemCount > 1
            prevEpBtn.visibility = if (many) View.VISIBLE else View.GONE
            nextEpBtn.visibility = if (many) View.VISIBLE else View.GONE
            prevEpBtn.text = "⏮"; nextEpBtn.text = "⏭"
            prevEpBtn.alpha = if (player.hasPreviousMediaItem()) 1f else 0.35f
            nextEpBtn.alpha = if (player.hasNextMediaItem()) 1f else 0.35f
        }
        kickerView.text = when {
            live -> "ПРЯМОЙ ЭФИР"
            player.mediaItemCount > 1 -> "ВИДЕО  ·  ${player.currentMediaItemIndex + 1} ИЗ ${player.mediaItemCount}"
            else -> "ВИДЕО"
        }
        updateEpgText()
    }

    // эфир оборвался (канал перезапустился, сеть) — переподключаемся к тому же каналу
    private fun reconnect() {
        liveRetries++
        statusView.text = "Переподключение…"
        ui.postDelayed({
            if (!finished) { player.seekToDefaultPosition(); player.prepare(); player.play() }
        }, 1500)
    }

    // логотип канала (пикон) из программы передач: загружаем в фоне и кэшируем
    // логотипа нет (в программе передач их нет у ~30% каналов) — рисуем значок с первыми буквами названия
    private fun placeholderLogo(name: String): android.graphics.Bitmap {
        val clean = name.replace(Regex("^\\d+\\.\\s*"), "").trim()
        val words = clean.split(Regex("[\\s!._-]+")).filter { it.isNotEmpty() }
        var letters = words.take(3).joinToString("") { it.take(1) }.uppercase()
        if (letters.length < 2) letters = clean.take(2).uppercase()
        val key = "ph:$clean"
        logoCache.get(key)?.let { return it }
        val w = 120; val h = 80
        val bmp = android.graphics.Bitmap.createBitmap(w, h, android.graphics.Bitmap.Config.ARGB_8888)
        val cv = android.graphics.Canvas(bmp)
        val hue = ((clean.hashCode() and 0x7fffffff) % 360).toFloat()
        val bg = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply { color = Color.HSVToColor(floatArrayOf(hue, 0.55f, 0.55f)) }
        cv.drawRoundRect(android.graphics.RectF(0f, 0f, w.toFloat(), h.toFloat()), 14f, 14f, bg)
        val tp = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE; textAlign = android.graphics.Paint.Align.CENTER; typeface = Typeface.DEFAULT_BOLD
            textSize = if (letters.length > 2) 34f else 42f
        }
        cv.drawText(letters, w / 2f, h / 2f + tp.textSize * 0.35f, tp)
        logoCache.put(key, bmp)
        return bmp
    }

    private fun loadLogo(iv: android.widget.ImageView, url: String?, name: String = "") {
        iv.tag = url ?: ("ph:" + name)
        if (url.isNullOrEmpty()) {
            if (name.isNotEmpty()) iv.setImageBitmap(placeholderLogo(name)) else iv.setImageDrawable(null)
            return
        }
        val cached = logoCache.get(url)
        if (cached != null) { iv.setImageBitmap(cached); return }
        // адрес уже не раз не открывался — сразу значок с буквами
        if (logoFailed.contains(url)) { if (name.isNotEmpty()) iv.setImageBitmap(placeholderLogo(name)); return }
        iv.setImageDrawable(null)
        Thread {
            var bmp: android.graphics.Bitmap? = null
            try {
                // переходы вручную: адреса логотипов часто http, а сервер переводит на https (301)
                var cur = url
                for (hop in 0 until 5) {
                    val c = URL(cur).openConnection() as HttpURLConnection
                    c.connectTimeout = 6000; c.readTimeout = 8000
                    c.instanceFollowRedirects = false
                    val code = c.responseCode
                    if (code in 300..399) {
                        val loc = c.getHeaderField("Location") ?: break
                        cur = URL(URL(cur), loc).toString()
                        c.disconnect()
                        continue
                    }
                    bmp = c.inputStream.use { android.graphics.BitmapFactory.decodeStream(it) }
                    break
                }
            } catch (_: Exception) {
            }
            if (bmp != null) {
                logoCache.put(url, bmp)
                ui.post { if (iv.tag == url) iv.setImageBitmap(bmp) }
            } else {
                logoFailed.add(url)
                ui.post { if (iv.tag == url && name.isNotEmpty()) iv.setImageBitmap(placeholderLogo(name)) }
            }
        }.start()
    }

    private fun scheduleDetail(pos: Int) {
        detailPos = pos
        ui.removeCallbacks(detailRun)
        ui.postDelayed(detailRun, 220)
    }

    // правая колонка панели: «сейчас» с описанием и ближайшие передачи
    private fun showDetail(pos: Int) {
        val uri = chUris.getOrNull(pos) ?: return
        val sid = Uri.parse(uri).lastPathSegment ?: return
        detName.text = chNames.getOrNull(pos) ?: ""
        loadLogo(detLogo, logoMap[sid], chNames.getOrNull(pos) ?: "")
        val cached = dayCache[sid]
        if (cached != null && System.currentTimeMillis() - cached.first < 5 * 60_000) { renderDay(cached.second); return }
        detNow.text = "Загрузка…"; detDesc.text = ""; detNext.text = ""
        val base = (tvNowUrl ?: return).substringBefore("/tv/now")
        Thread {
            try {
                val c = homeConnection("$base/tv/day?id=$sid")
                c.connectTimeout = 6000; c.readTimeout = 8000
                val arr = JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("programs") ?: org.json.JSONArray()
                dayCache[sid] = Pair(System.currentTimeMillis(), arr)
                ui.post { if (detailPos == pos) renderDay(arr) }
            } catch (_: Exception) {
                ui.post { if (detailPos == pos) { detNow.text = "Программа недоступна"; detDesc.text = ""; detNext.text = "" } }
            }
        }.start()
    }

    private fun renderDay(arr: org.json.JSONArray) {
        val now = System.currentTimeMillis() / 1000
        var cur = -1
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            if (o.optLong("s") <= now && o.optLong("e") > now) { cur = i; break }
        }
        if (cur < 0) {
            detNow.text = "Нет данных о программе"; detDesc.text = ""
            detNext.text = (0 until minOf(arr.length(), 8)).joinToString("\n") { arr.getJSONObject(it).let { o -> fmtClock(o.optLong("s")) + "  " + o.optString("t") } }
            return
        }
        val o = arr.getJSONObject(cur)
        detNow.text = "Сейчас · ${fmtClock(o.optLong("s"))}–${fmtClock(o.optLong("e"))}\n${o.optString("t")}"
        val desc = o.optString("d")
        detDesc.text = if (desc.isNotBlank() && desc != "null") desc else "Описание отсутствует"
        val sb = StringBuilder()
        var k = cur + 1
        while (k < arr.length() && k <= cur + 8) {
            val p = arr.getJSONObject(k)
            sb.append(fmtClock(p.optLong("s"))).append("  ").append(p.optString("t")).append('\n')
            k++
        }
        detNext.text = sb.toString().trimEnd()
    }

    // следующий/предыдущий канал по кольцу
    private fun stepChannel(dir: Int) {
        val n = player.mediaItemCount
        if (n < 2) return
        val to = ((player.currentMediaItemIndex + dir) % n + n) % n
        player.seekTo(to, C.TIME_UNSET)
        if (panelOpen) chList?.let { l -> chSelectPaged(l, to) }
    }

    private var okLong = false
    private var scheduleLoading = false
    private var scheduleDialog: AlertDialog? = null
    private val channelOk by lazy {
        TvOkPress({ run, delay -> ui.postDelayed(run, delay) }, { ui.removeCallbacks(it) }, {
            val pos = chList?.selectedItemPosition ?: -1
            if (panelOpen && pos in chUris.indices) { player.seekTo(pos, C.TIME_UNSET); hidePanel() }
        }, {
            val pos = chList?.selectedItemPosition ?: -1
            if (panelOpen && pos in chUris.indices) showSchedule(pos)
        })
    }
    private val liveOk by lazy {
        TvOkPress({ run, delay -> ui.postDelayed(run, delay) }, { ui.removeCallbacks(it) }, {
            if (live && !panelOpen && !menuMode && !finished) showPanel()
        }, {
            if (live && !panelOpen && !menuMode && !finished) setOverlay(true, true)
        })
    }

    override fun onKeyLongPress(keyCode: Int, event: KeyEvent): Boolean {
        if (live && !panelOpen && (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER)) {
            okLong = true
            setOverlay(true, true)
            return true
        }
        return super.onKeyLongPress(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean {
        if (live && (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER)) {
            if (okLong) { okLong = false; return true }
            if (!panelOpen && !menuMode && event.isTracking && !event.isCanceled) { showPanel(); return true }
        }
        return super.onKeyUp(keyCode, event)
    }

    // слои с информацией (шапка, прогресс, кнопки) — всё, кроме списка каналов и статуса
    private fun setInfoVisible(v: Boolean) {
        for (i in 0 until overlay.childCount) {
            val c = overlay.getChildAt(i)
            if (c !== panel && c !== statusView) c.visibility = if (v) View.VISIBLE else View.GONE
        }
    }

    // Список каналов постраничный: на экране всегда целые страницы (первый канал — первый), без плавной прокрутки
    private fun chRowH(lv: ListView): Int { val c = lv.getChildAt(0) ?: return 0; return c.height + lv.dividerHeight }
    private fun chPageSize(lv: ListView): Int {
        val rh = chRowH(lv); if (rh <= 0) return 8
        return ((lv.height - lv.paddingTop - lv.paddingBottom) / rh).coerceAtLeast(1)
    }
    /** Выбирает канал pos; его страница показана целиком с начала (pos не уезжает наверх). */
    private fun chSelectPaged(lv: ListView, pos: Int) {
        val p = chPageSize(lv); val rh = chRowH(lv)
        val top = (pos / p) * p
        lv.setSelectionFromTop(pos, (pos - top) * rh)
    }

    private val closeChannels = Runnable {
        if (panelOpen) hidePanel()
    }

    private fun armChannelTimeout() {
        ui.removeCallbacks(closeChannels)
        if (panelOpen && !scheduleLoading && scheduleDialog?.isShowing != true && hasWindowFocus() && !finished) ui.postDelayed(closeChannels, 10_000L)
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val started = android.os.SystemClock.uptimeMillis()
        if (panelOpen && event.action == KeyEvent.ACTION_DOWN) armChannelTimeout()
        val ok = event.keyCode == KeyEvent.KEYCODE_DPAD_CENTER || event.keyCode == KeyEvent.KEYCODE_ENTER
        // Route resting TV keys before PlayerView/focused children can consume
        // them. Opening the list must not depend on Android key tracking/focus.
        val openList = event.keyCode == KeyEvent.KEYCODE_DPAD_UP || event.keyCode == KeyEvent.KEYCODE_DPAD_DOWN || event.keyCode == KeyEvent.KEYCODE_DPAD_LEFT
        val handled = if (live && ok && liveOk.isPressed) {
            if (event.action == KeyEvent.ACTION_UP) liveOk.release(event.isCanceled)
            true
        } else if (live && !panelOpen && !menuMode && (ok || openList)) {
            if (ok) when (event.action) {
                KeyEvent.ACTION_DOWN -> liveOk.press()
                KeyEvent.ACTION_UP -> liveOk.release(event.isCanceled)
            } else if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0) showPanel()
            true
        } else if (live && panelOpen && ok) {
            when (event.action) {
                KeyEvent.ACTION_DOWN -> channelOk.press()
                KeyEvent.ACTION_UP -> channelOk.release(event.isCanceled)
            }
            true
        } else super.dispatchKeyEvent(event)
        TvInputLog.record(event, started, handled, "exo", currentFocus)
        return handled
    }

    private fun showPanel() {
        val p = panel ?: return
        setOverlay(false)
        setInfoVisible(false)
        panelOpen = true
        android.util.Log.d("TvPanel", "engine=exo visible=true")
        p.visibility = View.VISIBLE
        overlay.visibility = View.VISIBLE
        (chList?.adapter as? BaseAdapter)?.notifyDataSetChanged()
        val cl = chList
        if (cl != null) {
            val cur = player.currentMediaItemIndex
            cl.setSelectionFromTop(cur, 0)                        // чтобы появились строки и можно было измерить высоту
            cl.post { chSelectPaged(cl, cur) }
        }
        scheduleDetail(player.currentMediaItemIndex)
        chList?.requestFocus()
        armChannelTimeout()
    }

    private fun hidePanel() {
        channelOk.cancel()
        ui.removeCallbacks(closeChannels)
        panelOpen = false
        android.util.Log.d("TvPanel", "engine=exo visible=false")
        panel?.visibility = View.GONE
        overlay.visibility = View.INVISIBLE
        chList?.clearFocus()
    }

    // программа выбранного канала на сутки
    private fun showSchedule(pos: Int) {
        if (scheduleLoading || scheduleDialog?.isShowing == true) return
        val u = tvNowUrl ?: return
        val sid = Uri.parse(chUris.getOrNull(pos) ?: return).lastPathSegment ?: return
        val base = u.substringBefore("/tv/now")
        val dev = java.net.URLEncoder.encode(tvDev, "UTF-8")
        scheduleLoading = true; ui.removeCallbacks(closeChannels)
        Thread {
            try {
                val cached = dayCache[sid]
                val arr = if (cached != null && System.currentTimeMillis() - cached.first < 5 * 60_000) cached.second else {
                    val c = homeConnection("$base/tv/day?id=$sid")
                    c.connectTimeout = 6000; c.readTimeout = 8000
                    try { JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("programs") ?: JSONArray() } finally { c.disconnect() }
                }
                val marked = HashSet<Long>()
                try {
                    val c = homeConnection("$base/tv/remind?dev=$dev"); c.connectTimeout = 4000; c.readTimeout = 5000
                    val list = try { JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("list") } finally { c.disconnect() }
                    if (list != null) for (i in 0 until list.length()) list.getJSONObject(i).let { if (it.optString("id") == sid) marked.add(it.optLong("s")) }
                } catch (_: Exception) { }
                val now = System.currentTimeMillis() / 1000
                val programs = (0 until arr.length()).map { arr.getJSONObject(it) }.filter { it.optLong("e") > now }
                fun rows() = programs.map { o -> (if (marked.contains(o.optLong("s"))) "⏰ " else if (o.optLong("s") <= now) "▶ " else "") + fmtClock(o.optLong("s")) + "  " + o.optString("t") }
                ui.post {
                    scheduleLoading = false
                    if (!panelOpen || finished || isFinishing) return@post
                    if (programs.isEmpty()) { Toast.makeText(this, "Программы нет", Toast.LENGTH_SHORT).show(); armChannelTimeout(); return@post }
                    val scheduleAdapter = android.widget.ArrayAdapter(this, android.R.layout.simple_list_item_1, ArrayList(rows()))
                    val dlg = AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                        .setTitle((chNames.getOrNull(pos) ?: "") + " · OK — автопереключение")
                        .setAdapter(scheduleAdapter, null).create()
                    scheduleDialog = dlg
                    dlg.setOnDismissListener { scheduleDialog = null; armChannelTimeout() }
                    dlg.show()
                    android.util.Log.d("TvPanel", "engine=exo schedule=true")
                    dlg.listView?.setSelection(if (programs.size > 1 && programs[0].optLong("s") <= now) 1 else 0)
                    var busy = false
                    dlg.listView?.setOnItemClickListener { _, _, index, _ ->
                        val item = programs.getOrNull(index) ?: return@setOnItemClickListener
                        val start = item.optLong("s")
                        if (busy) return@setOnItemClickListener
                        if (start <= System.currentTimeMillis() / 1000) { Toast.makeText(this, "Передача уже идёт", Toast.LENGTH_SHORT).show(); return@setOnItemClickListener }
                        busy = true
                        Thread {
                            try {
                                val title = java.net.URLEncoder.encode(item.optString("t"), "UTF-8")
                                val name = java.net.URLEncoder.encode(chNames.getOrNull(pos) ?: "", "UTF-8")
                                val c = homeConnection("$base/tv/remind/toggle?dev=$dev&id=$sid&s=$start&t=$title&n=$name"); c.connectTimeout = 5000; c.readTimeout = 5000
                                val result = try { JSONObject(c.inputStream.bufferedReader().use { it.readText() }) } finally { c.disconnect() }
                                ui.post {
                                    busy = false
                                    if (result.optBoolean("on")) marked.add(start) else marked.remove(start)
                                    if (dlg.isShowing) { scheduleAdapter.clear(); scheduleAdapter.addAll(rows()); scheduleAdapter.notifyDataSetChanged(); dlg.listView.setSelection(index) }
                                    Toast.makeText(this, if (result.optBoolean("on")) "Автопереключение в ${fmtClock(start - 120)}" else "Автопереключение отменено", Toast.LENGTH_SHORT).show()
                                }
                            } catch (_: Exception) { ui.post { busy = false; Toast.makeText(this, "Нет связи с сервером", Toast.LENGTH_SHORT).show() } }
                        }.start()
                    }
                }
            } catch (_: Exception) {
                ui.post { scheduleLoading = false; Toast.makeText(this, "Не удалось загрузить программу", Toast.LENGTH_SHORT).show(); armChannelTimeout() }
            }
        }.start()
    }

    // запоминаем канал на сервере: меню «Телевидение» в Lampa включит его сразу
    private fun reportLastChannel() {
        if (!live) return
        val sid = streamId() ?: return
        val base = (tvNowUrl ?: return).substringBefore("/tv/now")
        val dev = java.net.URLEncoder.encode(tvDev, "UTF-8")
        Thread {
            try {
                val c = homeConnection("$base/tv/last?set=$sid&dev=$dev")
                c.connectTimeout = 4000; c.readTimeout = 4000
                c.inputStream.close()
            } catch (_: Exception) {
            }
        }.start()
    }

    private fun streamId(): String? =
        player.currentMediaItem?.localConfiguration?.uri?.lastPathSegment

    private fun fmtClock(ts: Long): String = clock.format(Date(ts * 1000))

    private fun updateEpgText() {
        if (!live) { descView.text = ""; return }
        val e = epgMap?.optJSONObject(streamId() ?: "")
        val n = e?.optJSONObject("n")
        val x = e?.optJSONObject("x")
        descView.text = when {
            n != null -> "Сейчас: ${n.optString("t")} (${fmtClock(n.optLong("s"))}–${fmtClock(n.optLong("e"))})" +
                (if (x != null) "   ·   Далее: ${fmtClock(x.optLong("s"))} ${x.optString("t")}" else "")
            else -> ""
        }
    }

    // «сейчас/далее» обновляются раз в минуту, пока смотрим канал
    private fun startEpgPolling() {
        val u = (tvNowUrl ?: return).substringBefore("/tv/now") + "/tv/channels"
        Thread {
            while (!finished) {
                try {
                    val c = homeConnection(u)
                    c.connectTimeout = 6000; c.readTimeout = 10000
                    val txt = c.inputStream.bufferedReader().use { it.readText() }
                    val arr = JSONObject(txt).optJSONArray("channels")
                    val m = JSONObject()
                    if (arr != null) for (i in 0 until arr.length()) {
                        val ch = arr.getJSONObject(i)
                        val id = ch.optString("id")
                        val e = JSONObject()
                        ch.optJSONObject("n")?.let { e.put("n", it) }
                        ch.optJSONObject("x")?.let { e.put("x", it) }
                        m.put(id, e)
                        ch.optString("logo").takeIf { it.isNotBlank() && it != "null" }?.let { logoMap[id] = it }
                        ch.optString("src").takeIf { it.isNotBlank() && it != "null" }?.let { srcMap[id] = it }
                    }
                    epgMap = m
                    ui.post { updateEpgText(); (chList?.adapter as? BaseAdapter)?.notifyDataSetChanged() }
                } catch (_: Exception) {
                }
                try { Thread.sleep(60_000) } catch (_: InterruptedException) { return@Thread }
            }
        }.start()
    }

    // ---------- панель ----------

    private fun setOverlay(show: Boolean, menu: Boolean = false) {
        ui.removeCallbacks(hideOverlay)
        menuMode = show && menu
        overlay.visibility = if (show) View.VISIBLE else View.INVISIBLE
        if (show) setInfoVisible(true)
        // фокус на кнопках только в режиме меню, иначе система сама подсвечивает первую кнопку
        controlsRow.descendantFocusability = if (menu) ViewGroup.FOCUS_AFTER_DESCENDANTS else ViewGroup.FOCUS_BLOCK_DESCENDANTS
        if (show && menu) (if (live) firstCtl else playBtn).requestFocus()
        else if (show) currentFocus?.clearFocus()
        if (show) {
            refreshProgress()
            if (player.isPlaying && !menu) ui.postDelayed(hideOverlay, HIDE_DELAY)
            else if (menu) ui.postDelayed(hideOverlay, HIDE_DELAY * 2)
        }
    }

    private fun fmt(ms: Long): String {
        val s = (ms / 1000).coerceAtLeast(0)
        val h = s / 3600; val m = (s % 3600) / 60; val sec = s % 60
        return if (h > 0) String.format(Locale.US, "%d:%02d:%02d", h, m, sec) else String.format(Locale.US, "%d:%02d", m, sec)
    }

    private fun refreshProgress() {
        if (!::player.isInitialized) return
        updateStats()
        if (live) {
            val n = epgMap?.optJSONObject(streamId() ?: "")?.optJSONObject("n")
            val nowS = System.currentTimeMillis() / 1000
            timeView.text = fmtClock(nowS)
            if (n != null && n.optLong("e") > n.optLong("s")) {
                bar.set((((nowS - n.optLong("s")) * 1000) / (n.optLong("e") - n.optLong("s"))).toInt())
                endsView.text = "до ${fmtClock(n.optLong("e"))}"
            } else { bar.set(0); endsView.text = "" }
            endsTopView.text = ""
            return
        }
        val dur = player.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: 0L
        val pos = if (pendingSeek >= 0) pendingSeek else player.currentPosition
        timeView.text = fmt(pos)
        bar.set(if (dur > 0) ((pos * 1000) / dur).toInt() else 0)
        if (dur > 0) {
            endsView.text = "-" + fmt(dur - pos)
            val rest = ((dur - pos) / player.playbackParameters.speed).toLong()
            endsTopView.text = "Закончится в ${clock.format(Date(System.currentTimeMillis() + rest))}"
        } else { endsView.text = ""; endsTopView.text = "" }
    }

    // класс качества по ширине кадра (широкоформатное кино обрезано по высоте: 1920x804 — это 1080p)
    private fun qClass(w: Int, h: Int): Int = when {
        w >= 3200 || h >= 2000 -> 2160
        w >= 2400 || h >= 1300 -> 1440
        w >= 1800 || h >= 1000 -> 1080
        w >= 1200 || h >= 700 -> 720
        w >= 700 || h >= 460 -> 480
        else -> h
    }

    // кодек, разрешение, канал связи и запас буфера; подписи кнопок качества, звука и скорости
    private fun updateStats() {
        val vf = player.videoFormat
        val parts = ArrayList<String>()
        if (vf != null) {
            val br = if (vf.bitrate > 0) String.format(Locale.US, " (%.1f Мбит/с)", vf.bitrate / 1_000_000.0) else ""
            parts.add("${codecName(vf.sampleMimeType)} ${vf.width}x${vf.height}$br")
        }
        val bw = androidx.media3.exoplayer.upstream.DefaultBandwidthMeter.getSingletonInstance(this).bitrateEstimate
        val buf = player.totalBufferedDuration / 1000
        statsView.text = (if (parts.isNotEmpty()) parts[0] + "\n" else "") +
            String.format(Locale.US, "Канал: %.1f Мбит/с   Буфер: %d с", bw / 1_000_000.0, buf)

        val auto = player.trackSelectionParameters.overrides.none { it.key.type == C.TRACK_TYPE_VIDEO }
        qualityBtn.text = if (vf != null && vf.height > 0) (if (auto) "${qClass(vf.width, vf.height)}p" else "${qClass(vf.width, vf.height)}p ✓") else "Авто"
        speedBtn.text = "${speeds[speedIdx]}×".replace(".0×", "×")
        var audioName = ""
        for (g in player.currentTracks.groups) {
            if (g.type != C.TRACK_TYPE_AUDIO) continue
            for (i in 0 until g.length) if (g.isTrackSelected(i)) {
                val f = g.getTrackFormat(i)
                audioName = (f.label?.takeIf { it.isNotBlank() } ?: langName(f.language)).ifBlank { codecName(f.sampleMimeType) }
            }
        }
        audioBtn.text = "♪ " + audioName.ifBlank { "Звук" }
        val textOn = !player.trackSelectionParameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT) &&
            player.currentTracks.groups.any { it.type == C.TRACK_TYPE_TEXT && it.isSelected }
        subsBtn.text = if (textOn) "SUB •" else "SUB"
        subsBtn.contentDescription = if (textOn) "Субтитры включены" else "Субтитры выключены"
    }

    // ---------- управление ----------

    private fun togglePause() {
        if (player.isPlaying) player.pause() else player.play()
        setOverlay(true, menuMode)
    }

    private lateinit var seekHint: TextView
    private lateinit var prevEpBtn: TextView
    private lateinit var nextEpBtn: TextView
    private val hideSeekHint = Runnable { if (::seekHint.isInitialized) seekHint.visibility = View.GONE }

    private fun seekBy(delta: Long) {
        val dur = player.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: Long.MAX_VALUE
        val base = if (pendingSeek >= 0) pendingSeek else player.currentPosition
        pendingSeek = (base + delta).coerceIn(0, if (dur == Long.MAX_VALUE) Long.MAX_VALUE else dur - 1000)
        ui.removeCallbacks(commitSeek)
        ui.postDelayed(commitSeek, SEEK_COMMIT_DELAY)
        if (menuMode || overlay.visibility == View.VISIBLE) {
            setOverlay(true, menuMode)      // перемотка кнопками панели — панель остаётся
        } else {
            // кнопки влево/вправо без панели: просто прыжок и короткая плашка с новым временем
            val total = if (dur == Long.MAX_VALUE) "" else " / " + fmt(dur)
            seekHint.text = (if (delta >= 0) "▶▶  +" else "◀◀  −") + (Math.abs(delta) / 1000) + " с   ·   " + fmt(pendingSeek) + total
            seekHint.visibility = View.VISIBLE
            ui.removeCallbacks(hideSeekHint)
            ui.postDelayed(hideSeekHint, 1400)
        }
    }

    private fun step(dir: Int) {
        if (!live && tlMeta != null && player.mediaItemCount > 1 &&
            ((dir > 0 && player.hasNextMediaItem()) || (dir < 0 && player.hasPreviousMediaItem()))) {
            tlRemember()
            val dur = tlDur[tlCur] ?: 0L
            val pos = tlPos[tlCur] ?: 0L
            val done = dur > 0 && (pos >= dur * 0.90 || (dur - pos <= minOf(600_000L, dur * 12 / 100) && pos >= dur / 2))
            tlSend(listOf(tlCur), done, force = true)
        }
        if (dir > 0 && player.hasNextMediaItem()) player.seekToNextMediaItem()
        else if (dir < 0 && player.hasPreviousMediaItem()) player.seekToPreviousMediaItem()
        else if (!live) {
            if (player.mediaItemCount > 1) Toast.makeText(this, if (dir > 0) "Это последняя серия" else "Это первая серия", Toast.LENGTH_SHORT).show()
            else seekBy(if (dir > 0) 60_000 else -60_000)
        }
    }

    // Назад в эфире: закрывает плеер одним нажатием. Список каналов открывается
    // DPAD вверх/влево или MENU; это не требует второго BACK и не оставляет
    // пульт в промежуточном состоянии на Shield.
    private fun handleLiveBack(event: KeyEvent) {
        if (event.repeatCount > 0) return
        android.util.Log.d("HomePlayer", "back panel=$panelOpen menu=$menuMode")
        when {
            menuMode -> setOverlay(true)
            panelOpen -> hidePanel()
            else -> closePlayer()
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (live && (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_ESCAPE)) { handleLiveBack(event); return true }
        when (keyCode) {
            KeyEvent.KEYCODE_BACK, KeyEvent.KEYCODE_ESCAPE -> {
                if (menuMode) { setOverlay(true); return true }
                closePlayer(); return true
            }
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_SPACE -> { togglePause(); return true }
            KeyEvent.KEYCODE_MEDIA_PLAY -> { player.play(); setOverlay(true, menuMode); return true }
            KeyEvent.KEYCODE_MEDIA_PAUSE -> { player.pause(); setOverlay(true, menuMode); return true }
            KeyEvent.KEYCODE_MEDIA_STOP -> { closePlayer(); return true }
            KeyEvent.KEYCODE_MEDIA_NEXT, KeyEvent.KEYCODE_CHANNEL_UP -> { if (live) stepChannel(1) else step(1); return true }
            KeyEvent.KEYCODE_MEDIA_PREVIOUS, KeyEvent.KEYCODE_CHANNEL_DOWN -> { if (live) stepChannel(-1) else step(-1); return true }
            KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> { if (live) stepChannel(1) else seekBy(30_000); return true }
            KeyEvent.KEYCODE_MEDIA_REWIND -> { if (live) stepChannel(-1) else seekBy(-30_000); return true }
            KeyEvent.KEYCODE_MENU -> { setOverlay(true, true); return true }
        }
        if (panelOpen) {
            when (keyCode) {
                KeyEvent.KEYCODE_BACK, KeyEvent.KEYCODE_ESCAPE -> { hidePanel(); return true }
                KeyEvent.KEYCODE_MEDIA_FAST_FORWARD, KeyEvent.KEYCODE_MEDIA_NEXT, KeyEvent.KEYCODE_CHANNEL_UP -> { stepChannel(1); return true }
                KeyEvent.KEYCODE_MEDIA_REWIND, KeyEvent.KEYCODE_MEDIA_PREVIOUS, KeyEvent.KEYCODE_CHANNEL_DOWN -> { stepChannel(-1); return true }
            }
            return super.onKeyDown(keyCode, event)
        }
        if (live) {
            // цифры — номер канала
            if (keyCode in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9) {
                digits += (keyCode - KeyEvent.KEYCODE_0).toString()
                statusView.text = "Канал $digits"
                ui.removeCallbacks(commitDigits)
                ui.postDelayed(commitDigits, 1500)
                return true
            }
            if (!menuMode) when (keyCode) {
                KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT -> { showPanel(); return true }
                KeyEvent.KEYCODE_DPAD_RIGHT -> { setOverlay(true); return true }
                KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER -> {
                    // короткое нажатие — список каналов (в onKeyUp), удержание — меню плеера (звук, субтитры, качество…)
                    event.startTracking()
                    return true
                }
            }
        }
        if (!menuMode) {
            when (keyCode) {
                KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER -> { togglePause(); return true }
                KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT -> {
                    val now = System.currentTimeMillis()
                    seekStreak = if (now - lastSeekKeyAt < 400) seekStreak + 1 else 0
                    lastSeekKeyAt = now
                    // короткое нажатие — 30 секунд без вызова меню; удержание и быстрые нажатия ускоряют ход
                    val stepMs = when {
                        seekStreak < 8 -> 15_000L
                        seekStreak < 16 -> 60_000L
                        else -> 180_000L
                    }
                    seekBy(if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT) -stepMs else stepMs)
                    return true
                }
                KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_UP -> { setOverlay(true, true); return true }
            }
        } else if (keyCode == KeyEvent.KEYCODE_DPAD_UP && (live || currentFocus?.parent?.parent === transportRow)) {
            setOverlay(true); return true
        }
        if (menuMode) {
            ui.removeCallbacks(hideOverlay)
            ui.postDelayed(hideOverlay, HIDE_DELAY * 2)
        }
        return super.onKeyDown(keyCode, event)
    }

    // ---------- выбор дорожек ----------

    private fun langName(code: String?): String {
        if (code.isNullOrBlank() || code == "und") return ""
        val l = Locale(code)
        val n = l.getDisplayLanguage(Locale("ru"))
        return if (n.isNullOrBlank() || n == code) code else n.replaceFirstChar { it.uppercase() }
    }

    private fun codecName(mime: String?): String = when {
        mime == null -> ""
        mime.contains("eac3") -> "E-AC3"
        mime.contains("ac3") -> "AC3"
        mime.contains("dts") || mime.contains("vnd.dts") -> "DTS"
        mime.contains("truehd") -> "TrueHD"
        mime.contains("opus") -> "Opus"
        mime.contains("aac") || mime.contains("mp4a") -> "AAC"
        mime.contains("mpeg") -> "MP3"
        mime.contains("hevc") -> "HEVC"
        mime.contains("avc") -> "H.264"
        mime.contains("av01") -> "AV1"
        mime.contains("vp9") -> "VP9"
        else -> mime.substringAfter('/').uppercase()
    }

    private fun trackOptions(type: Int, describe: (androidx.media3.common.Format) -> String): List<Triple<String, Tracks.Group, Int>> {
        val out = ArrayList<Triple<String, Tracks.Group, Int>>()
        for (g in player.currentTracks.groups) {
            if (g.type != type) continue
            for (i in 0 until g.length) {
                if (!g.isTrackSupported(i)) continue
                out.add(Triple(describe(g.getTrackFormat(i)), g, i))
            }
        }
        return out
    }

    private fun show(title: String, labels: List<String>, checked: Int, onPick: (Int) -> Unit) {
        ui.removeCallbacks(hideOverlay)
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle(title)
            .setSingleChoiceItems(labels.toTypedArray(), checked) { d, which -> d.dismiss(); onPick(which); setOverlay(true, true) }
            .setOnCancelListener { setOverlay(true, true) }
            .show()
    }

    // источники канала из Channel Manager: «авто» (его логика) или конкретная дорожка
    private fun pickSource() {
        val idx = player.currentMediaItemIndex
        val uri = player.currentMediaItem?.localConfiguration?.uri ?: return
        val sid = uri.lastPathSegment ?: return
        val base = "${uri.scheme}://${uri.authority}"
        Thread {
            try {
                val c = homeConnection("$base/tsb/sources/$sid")
                c.connectTimeout = 5000; c.readTimeout = 6000
                val arr = JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("sources")
                // какой источник играет сейчас: Channel Manager отдаёт это в /tsb/status
                var activeIdx = -1
                try {
                    val sc = homeConnection("$base/tsb/status")
                    sc.connectTimeout = 3000; sc.readTimeout = 3000
                    val ss = JSONObject(sc.inputStream.bufferedReader().use { it.readText() }).optJSONArray("sessions")
                    if (ss != null) for (k in 0 until ss.length()) { val so = ss.getJSONObject(k); if (so.optString("ch_id") == sid) activeIdx = so.optInt("source_idx", -1) }
                } catch (_: Exception) {}
                val labels = ArrayList<String>()
                val autoName = if (activeIdx >= 0 && arr != null && activeIdx < arr.length()) " — сейчас: " + arr.getJSONObject(activeIdx).optString("name") else ""
                labels.add("Авто (выбирает Channel Manager)$autoName")
                if (arr != null) for (i in 0 until arr.length()) {
                    val o = arr.getJSONObject(i)
                    labels.add("${if (i == activeIdx) "▶ " else ""}${i + 1}. ${o.optString("name")} · ${o.optString("type").uppercase()}${if (i == activeIdx) "  (играет сейчас)" else ""}")
                }
                ui.post {
                    if (labels.size <= 2) { Toast.makeText(this, "У этого канала один источник", Toast.LENGTH_SHORT).show(); return@post }
                    val cur = if (forcedIdx == idx && forcedSrc >= 0) forcedSrc + 1 else 0
                    show("Источник", labels, cur) { pick ->
                        val orig = if (forcedIdx == idx && forcedOrig != null) forcedOrig!! else player.getMediaItemAt(idx)
                        val item = if (pick == 0) orig
                        else orig.buildUpon().setUri(Uri.parse("$base/stream/$sid?src=${pick - 1}")).build()
                        forcedIdx = if (pick == 0) -1 else idx
                        forcedOrig = if (pick == 0) null else orig
                        forcedSrc = pick - 1
                        player.replaceMediaItem(idx, item)
                        player.prepare()
                        player.play()
                        setOverlay(false)
                    }
                }
            } catch (_: Exception) {
                ui.post { Toast.makeText(this, "Не удалось получить источники", Toast.LENGTH_SHORT).show() }
            }
        }.start()
    }

    private fun audioDescribe(f: androidx.media3.common.Format): String {
        val parts = listOfNotNull(
            langName(f.language).ifBlank { null },
            f.label?.takeIf { it.isNotBlank() },
            codecName(f.sampleMimeType).ifBlank { null },
            if (f.channelCount > 0) (if (f.channelCount > 2) "${f.channelCount - 1}.1" else "${f.channelCount}.0") else null
        )
        return parts.joinToString(" · ").ifBlank { "Дорожка" }
    }

    // дорожки без дублей: один и тот же звук нередко приходит в нескольких группах — в списке остаётся одна запись (выбранная в приоритете)
    private fun audioOptions(): List<Triple<String, Tracks.Group, Int>> {
        val byLabel = LinkedHashMap<String, Triple<String, Tracks.Group, Int>>()
        for (o in trackOptions(C.TRACK_TYPE_AUDIO) { audioDescribe(it) }) {
            val ex = byLabel[o.first]
            if (ex == null || (!ex.second.isTrackSelected(ex.third) && o.second.isTrackSelected(o.third))) byLabel[o.first] = o
        }
        return byLabel.values.toList()
    }

    private var memText: JSONObject? = null
    private var textApplied = false
    private var userPickedText = false
    private var memQuality: Int? = null
    private fun loadViewMemory() {
        if(live||audioKey.isBlank()||homeBase.isBlank())return
        Thread {
            try {
                val c=homeConnection("$homeBase/home/view/preferences?card="+java.net.URLEncoder.encode(audioKey,"UTF-8"));c.connectTimeout=3000;c.readTimeout=3000
                val prefs=JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONObject("preferences");c.disconnect()
                ui.post { memText=prefs;if(prefs?.has("quality_height")==true){memQuality=prefs.optInt("quality_height");qualityApplied=false;applyDefaultQuality()};applyTextMemory() }
            }catch (_: Exception) { }
        }.start()
    }
    private fun saveViewMemory(value: JSONObject) {
        if(live||audioKey.isBlank()||homeBase.isBlank())return
        Thread {
            try {
                val c=homeConnection("$homeBase/home/view/preferences?card="+java.net.URLEncoder.encode(audioKey,"UTF-8"));c.requestMethod="POST";c.doOutput=true;c.connectTimeout=3000;c.readTimeout=3000;c.setRequestProperty("Content-Type","application/json")
                c.outputStream.use { it.write(value.toString().toByteArray()) };c.inputStream.close();c.disconnect()
            }catch (_: Exception) { }
        }.start()
    }
    private fun applyTextMemory() {
        val m=memText?:return
        if(textApplied||userPickedText||!m.has("text_enabled")||!::player.isInitialized)return
        val b=trackSelector.buildUponParameters()
        if(!m.optBoolean("text_enabled")){textApplied=true;trackSelector.parameters=b.setTrackTypeDisabled(C.TRACK_TYPE_TEXT,true).build();return}
        val opts=trackOptions(C.TRACK_TYPE_TEXT){f->listOfNotNull(f.label?.takeIf { it.isNotBlank() },langName(f.language).ifBlank { null }).distinct().joinToString(" · ").ifBlank { "Субтитры" }}
        if(opts.isEmpty())return
        val hit=opts.firstOrNull { it.first==m.optString("text_label") }?:opts.firstOrNull { m.optString("text_lang").isNotBlank()&&it.second.getTrackFormat(it.third).language==m.optString("text_lang") }
        if(hit==null && m.optBoolean("text_enabled"))return
        textApplied=true
        if(!m.optBoolean("text_enabled")){trackSelector.parameters=b.setTrackTypeDisabled(C.TRACK_TYPE_TEXT,true).build();return}
        if(hit!=null)trackSelector.parameters=b.setTrackTypeDisabled(C.TRACK_TYPE_TEXT,false).setOverrideForType(TrackSelectionOverride(hit.second.mediaTrackGroup,listOf(hit.third))).build()
    }
    private fun loadAudioMemory() {
        Thread {
            try {
                val c = homeConnection("$homeBase/home/audio?key=" + java.net.URLEncoder.encode(audioKey, "UTF-8"))
                c.connectTimeout = 3000; c.readTimeout = 3000
                val o = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                ui.post {
                    memAudioLabel = o.optString("label"); memAudioLang = o.optString("lang"); memAudioLoaded = true
                    applyAudioMemory()
                }
            } catch (_: Exception) { ui.post { memAudioLoaded = true } }
        }.start()
    }

    // выбрать запомненную дорожку, как только они появились (один раз, если пользователь сам не выбрал)
    private var qualityApplied = false
    private var lastQualityClass = 0
    private var userPickedQuality = false

    // Авто-подбор ExoPlayer стартует с 720p и до 1080p/4K не дорастает. Поэтому по умолчанию берём лучшее качество в пределах профиля
    // устройства (extra max_height; без него — максимум), а «Авто» остаётся пунктом меню качества.
    private fun applyDefaultQuality() {
        if (userPickedQuality || live || !::player.isInitialized) return
        val opts = trackOptions(C.TRACK_TYPE_VIDEO) { "" }
        if (opts.isEmpty()) return
        val bestClass = opts.maxOf { qClass(it.second.getTrackFormat(it.third).width, it.second.getTrackFormat(it.third).height) }
        if (qualityApplied && bestClass <= lastQualityClass) return
        lastQualityClass = bestClass
        qualityApplied = true
        if (opts.size < 2 && bestClass < 1080) return
        if(memQuality==0){trackSelector.parameters=trackSelector.buildUponParameters().clearOverridesOfType(C.TRACK_TYPE_VIDEO).build();return}
        val limit=intent.getIntExtra("max_height",0)
        val mh=memQuality?.takeIf { it>0 }?.let { if(limit>0)minOf(limit,it)else it }?:limit
        val capW = if (mh > 0) mh * 16 / 9 + 8 else Int.MAX_VALUE
        fun score(t: Triple<String, Tracks.Group, Int>): Long {
            val f = t.second.getTrackFormat(t.third)
            return qClass(f.width, f.height) * 100_000_000L + f.height * 10_000L + (f.bitrate.coerceAtLeast(0) / 1000)
        }
        val inside = opts.filter { val f = it.second.getTrackFormat(it.third); f.width <= 0 || f.width <= capW }
        val best = (if (inside.isNotEmpty()) inside else opts).maxByOrNull { score(it) } ?: return
        if (!best.second.isTrackSelected(best.third))
            trackSelector.parameters = trackSelector.buildUponParameters()
                .setOverrideForType(TrackSelectionOverride(best.second.mediaTrackGroup, listOf(best.third))).build()
    }

    private fun applyAudioMemory() {
        if (audioApplied || userPickedAudio || !memAudioLoaded || memAudioLabel.isBlank() || !::player.isInitialized) return
        val opts = audioOptions()
        if (opts.isEmpty()) return
        val hit = opts.firstOrNull { it.first == memAudioLabel }
            ?: opts.firstOrNull { memAudioLang.isNotBlank() && it.second.getTrackFormat(it.third).language == memAudioLang && it.first.substringBefore(" · ") == memAudioLabel.substringBefore(" · ") }
        if (hit == null) return
        audioApplied = true
        if (!hit.second.isTrackSelected(hit.third)) {
            trackSelector.parameters = trackSelector.buildUponParameters()
                .setOverrideForType(TrackSelectionOverride(hit.second.mediaTrackGroup, listOf(hit.third))).build()
        }
    }

    private fun saveAudioMemory(label: String, lang: String) {
        if (audioKey.isBlank() || homeBase.isBlank()) return
        Thread {
            try {
                val u = "$homeBase/home/audio/set?key=" + java.net.URLEncoder.encode(audioKey, "UTF-8") +
                    "&label=" + java.net.URLEncoder.encode(label, "UTF-8") + "&lang=" + java.net.URLEncoder.encode(lang, "UTF-8")
                val c = homeConnection(u)
                c.connectTimeout = 3000; c.readTimeout = 3000
                c.inputStream.close()
            } catch (_: Exception) {}
        }.start()
    }

    private fun pickAudio() {
        val opts = audioOptions()
        val cur = opts.indexOfFirst { it.second.isTrackSelected(it.third) }
        show("Звук", opts.map { it.first }, cur.coerceAtLeast(0)) { i ->
            val (label, g, ti) = opts[i]
            userPickedAudio = true
            trackSelector.parameters = trackSelector.buildUponParameters()
                .setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, listOf(ti))).build()
            saveAudioMemory(label, g.getTrackFormat(ti).language ?: "")
        }
    }

    private fun pickText() {
        val opts = trackOptions(C.TRACK_TYPE_TEXT) { f ->
            listOfNotNull(f.label?.takeIf { it.isNotBlank() }, langName(f.language).ifBlank { null })
                .distinct().joinToString(" · ").ifBlank { "Субтитры" }
        }
        val labels = listOf("Выключены") + opts.map { it.first }
        val off = player.trackSelectionParameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT) ||
            opts.none { it.second.isTrackSelected(it.third) }
        val cur = if (off) 0 else opts.indexOfFirst { it.second.isTrackSelected(it.third) } + 1
        show("Субтитры", labels, cur) { i ->
            userPickedText=true
            val selected=if(i>0)opts[i-1]else null
            saveViewMemory(JSONObject().put("text_enabled",i>0).put("text_label",selected?.first.orEmpty()).put("text_lang",selected?.second?.getTrackFormat(selected.third)?.language.orEmpty()))
            val b = trackSelector.buildUponParameters()
            if (i == 0) {
                b.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
            } else {
                val (_, g, ti) = opts[i - 1]
                b.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
                    .setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, listOf(ti)))
            }
            trackSelector.parameters = b.build()
        }
    }

    private fun pickQuality() {
        if (ytQuals.size > 1 && ytAudioUrl.isNotBlank()) { pickYtQuality(); return }
        val opts = trackOptions(C.TRACK_TYPE_VIDEO) { f ->
            val res = if (f.height > 0) "${qClass(f.width, f.height)}p" else "Видео"
            val br = if (f.bitrate > 0) String.format(Locale.US, " · %.1f Мбит/с", f.bitrate / 1_000_000.0) else ""
            res + " · " + codecName(f.sampleMimeType) + br
        }.sortedByDescending { it.second.getTrackFormat(it.third).let { f -> qClass(f.width, f.height) * 10000 + f.height } }
        if (opts.size < 2) { Toast.makeText(this, "Качество одно: ${opts.firstOrNull()?.first ?: "—"}", Toast.LENGTH_SHORT).show(); return }
        val auto = player.trackSelectionParameters.overrides.none { it.key.type == C.TRACK_TYPE_VIDEO }
        val cur = if (auto) 0 else opts.indexOfFirst { it.second.isTrackSelected(it.third) } + 1
        show("Качество", listOf("Авто") + opts.map { it.first }, cur) { i ->
            userPickedQuality = true
            val picked = if (i == 0) null else opts[i - 1].second.getTrackFormat(opts[i - 1].third)
            saveViewMemory(JSONObject().put("quality_height", if (picked == null) 0 else qClass(picked.width, picked.height)))
            val b = trackSelector.buildUponParameters()
            if (i == 0) b.clearOverridesOfType(C.TRACK_TYPE_VIDEO)
            else {
                val (_, g, ti) = opts[i - 1]
                b.setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, listOf(ti)))
            }
            trackSelector.parameters = b.build()
        }
    }

    private fun pickSpeed() {
        show("Скорость", speeds.map { "${it}×" }, speedIdx) { i ->
            speedIdx = i
            player.setPlaybackSpeed(speeds[i])
            getSharedPreferences("home_player", MODE_PRIVATE).edit().putFloat(top.rootu.lampa.ProfileRuntime.scopeKey("speed"), speeds[i]).apply()
        }
    }

    private fun pickResize() {
        show("Формат", resizeNames.toList(), resizeIdx) { i ->
            resizeIdx = i
            playerView.resizeMode = resizeModes[i]
        }
    }

    // ---------- завершение ----------

    private fun openExternal() {
        if (live || !::player.isInitialized || finished) return
        val installed = listOf("net.gtvbox.videoplayer", "net.gtvbox.vimuhd").any {
            try { packageManager.getPackageInfo(it, 0); true } catch (_: Exception) { false }
        }
        if (!installed) { Toast.makeText(this, "Внешний плеер (VIMU) не установлен", Toast.LENGTH_SHORT).show(); return }
        // выбранная сейчас дорожка звука: по ней приложение попросит поток с этой дорожкой по умолчанию
        for (g in player.currentTracks.groups) {
            if (g.type != C.TRACK_TYPE_AUDIO) continue
            for (i in 0 until g.length) if (g.isTrackSelected(i)) {
                val f = g.getTrackFormat(i)
                extAudioLabel = f.label ?: ""
                extAudioLang = f.language ?: ""
            }
        }
        player.pause()
        extHandoff = true
        closePlayer()
    }

    private fun closePlayer() {
        if (finished) return
        finished = true
        updateBufferIndicator(Player.STATE_IDLE)
        ui.removeCallbacksAndMessages(null)
        val res = Intent(RESULT_ACTION)
        if (::player.isInitialized && !live && tlMeta != null) {
            tlRemember(); tlSend(listOf(tlCur), completed, force = true); tlSyncKino()
        }
        res.putStringArrayListExtra("completed_urls", tlAutoDone)
        res.putExtra("home_auto_attempt", intent.getStringExtra("home_auto_attempt"))
        res.putExtra("home_auto_profile", intent.getStringExtra("home_auto_profile"))
        res.putExtra("home_auto_startup_failed", errored && !firstFrameSeen && android.os.SystemClock.elapsedRealtime()-startupAt < 20_000)
        try { res.putExtra("tl_updates", JSONObject().apply { tlLastU.forEach { (h, u) -> put(h, u) } }.toString()) } catch (_: Exception) {}
        if (::player.isInitialized) {
            val uri = player.currentMediaItem?.localConfiguration?.uri ?: intent.data
            res.data = uri
            val pos = (if (pendingSeek >= 0) pendingSeek else player.currentPosition).coerceAtLeast(0)
            val dur = player.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: 0L
            when {
                errored -> res.putExtra("end_by", "error")
                completed -> res.putExtra("end_by", "playback_completion")
                else -> {
                    res.putExtra("end_by", "user")
                    res.putExtra("position", pos.toInt())
                    res.putExtra("duration", dur.toInt())
                    if (extHandoff) {
                        res.putExtra("open_external", true)
                        res.putExtra("ext_audio_label", extAudioLabel)
                        res.putExtra("ext_audio_lang", extAudioLang)
                    }
                }
            }
            player.release()
        }
        if (::frameRate.isInitialized) frameRate.release()
        setResult(if (errored) RESULT_FIRST_USER else if (live) RESULT_CANCELED else RESULT_OK, res)
        finish()
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // Home в лаунчере — выходим из плеера на главную Lampa
        if (!finished && !isInPictureInPictureMode) closePlayer()
    }

    // уже закрытый плеер (Home, запуск другого приложения) не должен снова «ожить» чёрным экраном
    override fun onResume() {
        super.onResume()
        if (finished) {
            finish()
            try {
                startActivity(Intent(this, top.rootu.lampa.MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP))
            } catch (_: Exception) {
            }
        }
    }

    override fun onStop() {
        channelOk.cancel()
        liveOk.cancel()
        ui.removeCallbacks(closeChannels)
        DLog.flush()
        super.onStop()
        updateBufferIndicator(Player.STATE_IDLE)
        // уход из приложения (Home) — не оставляем играть в фоне
        if (::player.isInitialized && !finished) {
            player.pause()
            // уход в фон (Home, чужое окно): фиксируем остановку один раз, дальше тики на паузе молчат
            if (!live && tlMeta != null) { tlRemember(); tlSend(listOf(tlCur), false, force = true) }
        }
        stoppedAt = android.os.SystemClock.elapsedRealtime()
        if (::frameRate.isInitialized) frameRate.suspend()
    }

    // после сна приставки декодер и поверхность часто «залипают» на последнем кадре: в эфире пересоздаём плеер заново
    override fun onStart() {
        super.onStart()
        if (live && !finished && ::player.isInitialized && stoppedAt > 0L) {
            val idx = player.currentMediaItemIndex.coerceIn(0, (chUris.size - 1).coerceAtLeast(0))
            stoppedAt = 0L
            try {
                player.release()
                setupPlayer(Uri.parse(chUris[idx]))
            } catch (e: Exception) {
                android.util.Log.d("HomePlayer", "restart after sleep failed: ${e.message}")
            }
        }
        if (::frameRate.isInitialized) frameRate.resume()
        armChannelTimeout()
    }

    override fun onDestroy() {
        ui.removeCallbacksAndMessages(null)
        if (top.rootu.lampa.RemindWatcher.videoPlayer?.get() === this) top.rootu.lampa.RemindWatcher.videoPlayer = null
        if (::player.isInitialized && !finished) player.release()
        if (::frameRate.isInitialized) frameRate.release()
        super.onDestroy()
    }
}
