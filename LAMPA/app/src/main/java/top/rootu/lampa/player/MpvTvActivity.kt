package top.rootu.lampa.player

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.BaseAdapter
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.TextView
import android.widget.Toast
import dev.jdtech.mpv.MPVLib
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Плеер прямого эфира на движке libmpv (FFmpeg) вместо ExoPlayer.
 *
 * Включается настройкой ТВ «Движок эфира» (extra tv_engine: mpv | mpv_embed | mpv_hw | mpv_sw); по умолчанию эфир идёт через
 * HomePlayerActivity на ExoPlayer, так что откат — переключить настройку обратно. Кино и сериалы всегда на ExoPlayer.
 *
 * Управление как у штатного плеера эфира: вверх/вниз/влево/короткое ОК и «Назад» — список каналов с логотипами и программой
 * передач; CH+/CH−, перемотка вперёд/назад — следующий/предыдущий канал; вправо — плашка «сейчас/далее»; цифры — номер канала;
 * два «Назад» подряд — выход; МЕНЮ — статистика движка; долгое ОК — деинтерлейс вкл/выкл.
 */
class MpvTvActivity : Activity(), SurfaceHolder.Callback, MPVLib.EventObserver {
    companion object {
        // Вызовы mpv, которые могут надолго блокироваться (отсоединение поверхности, остановка, уничтожение), идут в отдельном потоке:
        // на главном они вешали выход («серый экран, звук идёт», Activity destroy timeout). Новый плеер ждёт завершения прежней остановки.
        @Volatile private var mpvExec: java.util.concurrent.ExecutorService? = null
        private fun newExec(): java.util.concurrent.ExecutorService =
            java.util.concurrent.Executors.newSingleThreadExecutor { r -> Thread(r, "mpv-teardown").apply { isDaemon = true } }
    }
    private var exec: java.util.concurrent.ExecutorService? = null
    private var finishing = false
    private var fromLaunch = false   // запущен через TvLaunchActivity: «Назад» из списка сразу выходит
    private lateinit var surfaceView: SurfaceView
    private lateinit var infoBox: LinearLayout
    private lateinit var nameView: TextView
    private lateinit var descView: TextView
    private lateinit var statsView: TextView
    private lateinit var statusView: TextView
    private lateinit var panel: LinearLayout
    private lateinit var chList: ListView
    private lateinit var detLogo: ImageView
    private lateinit var detName: TextView
    private lateinit var detNow: TextView
    private lateinit var detDesc: TextView
    private lateinit var detNext: TextView
    private val ui = Handler(Looper.getMainLooper())
    private val clock = SimpleDateFormat("HH:mm", Locale.getDefault())

    private var uris: List<String> = emptyList()
    private var names: List<String> = emptyList()
    private var cur = 0
    private var tvNowUrl: String? = null
    private var tvDev = ""
    private var sessionDrc: MpvSessionDrc? = null
    private var engine = "mpv"
    private var hwdec = "mediacodec-copy"

    private var statsOn = false
    private var panelOpen = false
    private var deint = false
    private var digits = ""
    private var okLong = false
    private var lastBackAt = 0L
    private var surfaceReady = false
    private var started = false
    private var destroyed = false

    // программа передач и логотипы (как у штатного плеера)
    @Volatile private var epgMap: JSONObject? = null
    private val logoMap = HashMap<String, String>()
    private val srcMap = HashMap<String, String>()      // id канала -> тип первого источника (ts — спутник, hls — интернет)
    private var forcedSrcIdx = -1                       // источник, выбранный вручную на текущем канале (-1 — «авто»)
    private val logoFailed = java.util.Collections.synchronizedSet(HashSet<String>())
    private val logoCache = android.util.LruCache<String, Bitmap>(120)
    private val dayCache = HashMap<String, Pair<Long, JSONArray>>()
    private var detailPos = -1
    private val detailRun = Runnable { showDetail(detailPos) }
    private var logoDp = 36
    private var nameSp = 18f
    private var progSp = 15f

    // список каналов листается страницами: на странице только целые строки, остаток высоты остаётся пустым
    private var rowPx = 0
    private var baseRowPx = 0
    private var pageSize = 8
    private var pageStart = 0
    private lateinit var hintView: TextView

    // напоминания «включить передачу» (хранятся на сервере отдельно для устройства): окно программ канала и опрос списка
    private lateinit var progMenu: LinearLayout
    private lateinit var progTitle: TextView
    private lateinit var progList: ListView
    private var progOpen = false
    private var progSid = ""
    private var progItems: List<JSONObject> = emptyList()
    private val reminded = java.util.Collections.synchronizedSet(HashSet<String>())   // "id|начало"
    private val panelOk by lazy { TvOkPress({ run, delay -> ui.postDelayed(run, delay) }, { ui.removeCallbacks(it) }, { onOkShort() }, { onOkLong() }) }

    // сторож: время воспроизведения должно идти; если стоит дольше порога — перезапускаем тот же канал
    private var lastPos = -1.0
    private var lastMoveAt = 0L
    private var loadedAt = 0L
    private var reconnects = 0
    private var lastLog = 0L
    private var bufferWait = 3
    private var afrDone = 0f
    private var afrOn = true
    private var spdifActive = false
    private var shotDone = false

    // Автоматическая частота кадров (тот же подбор, что у штатного плеера): режим экрана, кратный частоте кадров видео;
    // 25 к/с эфира показываем на 50 Гц. Включается настройкой AFR приложения (по умолчанию да).
    private fun applyFrameRate(fps: Float) {
        try {
            if (!afrOn || !getSharedPreferences("home_player", MODE_PRIVATE).getBoolean("afr", true)) return
            if (Math.abs(fps - afrDone) < 0.01f) return
            val kmin = if (fps in 24.5f..26f) 2 else 1
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                try {
                    val sf = surfaceView.holder.surface?.takeIf { it.isValid }
                    if (sf != null) {
                        if (android.os.Build.VERSION.SDK_INT >= 31)
                            sf.setFrameRate(fps * kmin, android.view.Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE, android.view.Surface.CHANGE_FRAME_RATE_ALWAYS)
                        else sf.setFrameRate(fps * kmin, android.view.Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE)
                    }
                } catch (_: Throwable) {
                    // метод заявлен, но отсутствует на некоторых прошивках — остаётся переключение режима ниже
                }
            }
            if (android.os.Build.VERSION.SDK_INT >= 23) {
                @Suppress("DEPRECATION")
                val d = windowManager.defaultDisplay
                val cm = d.mode
                val kc = Math.round(cm.refreshRate / fps)
                if (kc >= kmin && Math.abs(cm.refreshRate - fps * kc) < 0.15f) {
                    afrDone = fps
                    android.util.Log.i("MpvTv", "AFR: ${fps} к/с, экран уже ${cm.refreshRate} Гц — без изменений")
                    return
                }
                var best: android.view.Display.Mode? = null
                var bestErr = 1e9f
                for (m in d.supportedModes) {
                    if (m.physicalWidth != cm.physicalWidth || m.physicalHeight != cm.physicalHeight) continue
                    val r = m.refreshRate
                    if (r < fps * kmin - 0.5f) continue
                    val k = Math.max(kmin, Math.round(r / fps))
                    val err = Math.abs(r - fps * k) + (k - 1) * 0.05f
                    if (err < bestErr) { bestErr = err; best = m }
                }
                if (best != null && best.modeId != cm.modeId && bestErr < 0.5f) {
                    afrDone = fps
                    val lp = window.attributes
                    lp.preferredDisplayModeId = best.modeId
                    window.attributes = lp
                    android.util.Log.i("MpvTv", "AFR: ${fps} к/с → ${best.refreshRate} Гц (режим ${best.modeId})")
                } else android.util.Log.i("MpvTv", "AFR: ${fps} к/с, подходящего режима нет (экран ${cm.refreshRate} Гц)")
            }
        } catch (e: Throwable) {
            android.util.Log.i("MpvTv", "AFR error: ${e.message}")
        }
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    // цвет текста строки списка: обычный; под курсором (строка выбрана) — белый и канал, и передача, чтобы читалось на оранжевой полосе
    private fun selWhite(normal: Int) = android.content.res.ColorStateList(
        arrayOf(intArrayOf(android.R.attr.state_selected), intArrayOf()), intArrayOf(Color.WHITE, normal))

    private val playbackOwner by lazy { try { org.json.JSONObject(intent.getStringExtra("home_user_context") ?: "{}") } catch (_: Exception) { org.json.JSONObject() } }
    private fun homeConnection(url: String): HttpURLConnection {
        val c=URL(url).openConnection() as HttpURLConnection
        val base=playbackOwner.optString("server_base")
        if(base.isNotBlank()&&url.startsWith(base+"/"))top.rootu.lampa.ProfileRuntime.requestHeaders(playbackOwner).forEach { (k,v)->c.setRequestProperty(k,v) }
        return c
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        top.rootu.lampa.RemindWatcher.mpvAlive = true   // напоминания в эфире mpv обрабатывает сам плеер
        fromLaunch = System.currentTimeMillis() - top.rootu.lampa.TvLaunch.at < 120_000
        top.rootu.lampa.TvLaunch.at = 0
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        engine = intent.getStringExtra("tv_engine") ?: intent.getStringExtra("engine") ?: "mpv"
        hwdec = when (engine) {
            "mpv_hw", "mpv_embed" -> "mediacodec"
            "mpv_sw" -> "no"
            else -> "mediacodec-copy"
        }
        tvNowUrl = intent.getStringExtra("tv_now_url")
        tvDev = intent.getStringExtra("tv_dev") ?: ""

        val data: Uri? = intent.data
        val list = intent.getParcelableArrayExtra("video_list")?.filterIsInstance<Uri>()?.takeIf { it.isNotEmpty() }
            ?: listOfNotNull(data)
        uris = list.map { it.toString() }
        val given = intent.getStringArrayExtra("video_list.name")?.toList() ?: emptyList()
        names = uris.mapIndexed { i, _ -> given.getOrNull(i) ?: "${i + 1}" }
        cur = if (data != null) uris.indexOf(data.toString()).coerceAtLeast(0) else 0

        buildUi()
        initMpv()
        startEpgPolling()
        startRemindPolling()
    }

    private fun label(text: String, size: Float, color: Int = Color.WHITE, bold: Boolean = false) =
        TextView(this).apply {
            this.text = text
            textSize = size
            setTextColor(color)
            if (bold) typeface = Typeface.DEFAULT_BOLD
            setShadowLayer(6f, 0f, 2f, Color.BLACK)
        }

    // ---------------------------------------------------------------- интерфейс
    private fun buildUi() {
        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(this)
        root.addView(surfaceView, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        // плашка канала: название и «сейчас/далее», показывается при переключении и по «вправо»
        infoBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; visibility = View.GONE }
        nameView = label("", 22f, bold = true)
        descView = label("", 15f, Color.parseColor("#DDDDDD"))
        infoBox.addView(nameView, LinearLayout.LayoutParams(-2, -2))
        infoBox.addView(descView, LinearLayout.LayoutParams(-2, -2).apply { topMargin = dp(4) })
        root.addView(infoBox, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.START).apply { setMargins(dp(40), dp(30), dp(40), 0) })

        statsView = label("", 13f, Color.parseColor("#DDDDDD")).apply { visibility = View.GONE }
        root.addView(statsView, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.START).apply { setMargins(dp(40), 0, 0, dp(30)) })
        statusView = label("", 20f).apply { gravity = Gravity.CENTER }
        root.addView(statusView, FrameLayout.LayoutParams(-2, -2, Gravity.CENTER))

        // масштаб списка (настройка ТВ «Шрифт списка каналов») и прозрачность (процент непрозрачности, по умолчанию 70)
        val fontLevel = intent.getIntExtra("tv_font", 0).coerceIn(0, 2)
        nameSp = floatArrayOf(12.75f, 18f, 25.5f)[fontLevel]
        progSp = floatArrayOf(11.25f, 15f, 21f)[fontLevel]
        logoDp = intArrayOf(22, 36, 51)[fontLevel]
        val alphaPct = intent.getIntExtra("tv_panel_alpha", 70).coerceIn(20, 100)

        panel = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(Color.argb(alphaPct * 255 / 100, 12, 12, 24))
            visibility = View.GONE
            setPadding(dp(10), dp(8), dp(10), dp(6))
        }
        val left = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        // строки списка каналов на 15% мельче правой колонки
        val lk = 0.85f
        val listLogo = (logoDp * lk).toInt()
        baseRowPx = dp(listLogo + 8)
        rowPx = baseRowPx
        chList = ListView(this).apply {
            divider = null
            isFocusable = true
            isVerticalScrollBarEnabled = false
            setSelector(GradientDrawable().apply { setColor(Color.argb(120, 249, 168, 37)); cornerRadius = dp(6).toFloat() })
            adapter = object : BaseAdapter() {
                override fun getCount() = maxOf(0, minOf(pageSize, names.size - pageStart))
                override fun getItem(pos: Int) = names[pageStart + pos]
                override fun getItemId(pos: Int) = (pageStart + pos).toLong()
                override fun getView(pos: Int, convertView: View?, parent: ViewGroup?): View {
                    val position = pageStart + pos
                    val row = (convertView as? LinearLayout) ?: LinearLayout(this@MpvTvActivity).apply {
                        layoutParams = android.widget.AbsListView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, rowPx)
                        orientation = LinearLayout.HORIZONTAL
                        gravity = Gravity.CENTER_VERTICAL
                        setPadding(dp(6), dp(1), dp(6), dp(1))
                        addView(ImageView(this@MpvTvActivity).apply { scaleType = ImageView.ScaleType.FIT_CENTER },
                            LinearLayout.LayoutParams(dp(listLogo * 3 / 2), dp(listLogo)).apply { rightMargin = dp(6) })
                        addView(label("", nameSp * lk, bold = true).apply { setSingleLine(true); includeFontPadding = false; ellipsize = android.text.TextUtils.TruncateAt.END; setShadowLayer(0f, 0f, 0f, 0) },
                            LinearLayout.LayoutParams(0, -2, 0.42f))
                        addView(label("", progSp * 1.1f * lk, Color.parseColor("#9ED8FF"), bold = true).apply { setSingleLine(true); includeFontPadding = false; ellipsize = android.text.TextUtils.TruncateAt.END; setShadowLayer(0f, 0f, 0f, 0) },
                            LinearLayout.LayoutParams(0, -2, 0.58f))
                        // значок источника: спутник или интернет
                        addView(label("", nameSp * lk).apply { gravity = Gravity.CENTER; setShadowLayer(0f, 0f, 0f, 0) }, LinearLayout.LayoutParams(dp(30), -2))
                    }
                    row.layoutParams = android.widget.AbsListView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, rowPx)
                    val lh = minOf(dp(listLogo), rowPx - dp(3))
                    (row.getChildAt(0) as ImageView).layoutParams = LinearLayout.LayoutParams(lh * 3 / 2, lh).apply { rightMargin = dp(6) }
                    val sid = Uri.parse(uris[position]).lastPathSegment ?: ""
                    val n = epgMap?.optJSONObject(sid)?.optJSONObject("n")
                    val isCur = position == cur
                    (row.getChildAt(1) as TextView).apply { text = (if (isCur) "▶ " else "") + names[position]; setTextColor(selWhite(if (isCur) Color.parseColor("#F9A825") else Color.WHITE)) }
                    (row.getChildAt(2) as TextView).apply {
                        text = if (n != null) "${fmtClock(n.optLong("s"))} ${n.optString("t")}" else ""
                        setTextColor(selWhite(if (isCur) Color.parseColor("#F9A825") else Color.parseColor("#9ED8FF")))   // у текущего канала подсвечена и его передача
                    }
                    loadLogo(row.getChildAt(0) as ImageView, logoMap[sid], names[position])
                    (row.getChildAt(3) as TextView).text = when (srcMap[sid]) { "ts" -> "\uD83D\uDCE1"; "hls" -> "\uD83C\uDF10"; else -> "" }
                    return row
                }
            }
            setOnItemClickListener { _, _, pos, _ -> loadChannel(pageStart + pos); hidePanel() }
            // на краю страницы вверх/вниз — соседняя страница, по кругу (с первого канала вверх — на последний, с последнего вниз — на первый)
            setOnKeyListener { v, keyCode, ev ->
                val lv = v as ListView
                val n = names.size
                if (ev.action == KeyEvent.ACTION_DOWN && n > 1) {
                    val sel = lv.selectedItemPosition.coerceAtLeast(0)
                    if (keyCode == KeyEvent.KEYCODE_DPAD_UP && sel <= 0) { goTo(if (pageStart + sel - 1 < 0) n - 1 else pageStart + sel - 1); true }
                    else if (keyCode == KeyEvent.KEYCODE_DPAD_DOWN && sel >= (lv.adapter?.count ?: 1) - 1) { goTo(if (pageStart + sel + 1 >= n) 0 else pageStart + sel + 1); true }
                    else false
                } else false
            }
            onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
                override fun onItemSelected(p: android.widget.AdapterView<*>?, v: View?, pos: Int, id: Long) { scheduleDetail(pageStart + pos) }
                override fun onNothingSelected(p: android.widget.AdapterView<*>?) {}
            }
            // сколько строк помещается целиком — столько и показываем; остаток высоты остаётся пустым
            addOnLayoutChangeListener { _, _, t, _, b, _, _, _, _ ->
                val h = b - t
                if (h > 0 && baseRowPx > 0) {
                    val ns = maxOf(1, h / baseRowPx) + 2   // плотнее: на две строки больше
                    val np = h / ns
                    if (ns != pageSize || np != rowPx) {
                        pageSize = ns
                        rowPx = np
                        ui.post { if (names.isNotEmpty()) goTo(if (panelOpen) minOf(pageStart + maxOf(0, selectedItemPosition), names.size - 1) else cur) }
                    }
                }
            }
        }
        left.addView(chList, LinearLayout.LayoutParams(-1, 0, 1f))
        hintView = label("", 8.4f, Color.parseColor("#888888")).apply { includeFontPadding = false }
        left.addView(hintView, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(3) })
        panel.addView(left, LinearLayout.LayoutParams(0, -1, 0.5f))

        val right = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(18), dp(4), dp(8), 0) }
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        detLogo = ImageView(this).apply { scaleType = ImageView.ScaleType.FIT_CENTER }
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
        panel.addView(right, LinearLayout.LayoutParams(0, -1, 0.5f))
        root.addView(panel, FrameLayout.LayoutParams(dp(900), -1, Gravity.START))

        // окно программ выбранного канала (долгое ОК на канале): удержание ОК на передаче ставит/снимает напоминание
        progMenu = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.argb(maxOf(alphaPct * 255 / 100, 235), 12, 12, 24))
            visibility = View.GONE
            setPadding(dp(18), dp(12), dp(18), dp(8))
        }
        progTitle = label("", nameSp + 3f, bold = true)
        progMenu.addView(progTitle, LinearLayout.LayoutParams(-1, -2))
        progMenu.addView(label("ОК — автопереключение за 2 минуты до начала · ← назад", 8.4f, Color.parseColor("#888888")),
            LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(2); bottomMargin = dp(6) })
        progList = ListView(this).apply {
            divider = null
            isFocusable = true
            setSelector(GradientDrawable().apply { setColor(Color.argb(120, 249, 168, 37)); cornerRadius = dp(6).toFloat() })
            adapter = object : BaseAdapter() {
                override fun getCount() = progItems.size
                override fun getItem(position: Int) = progItems[position]
                override fun getItemId(position: Int) = position.toLong()
                override fun getView(position: Int, convertView: View?, parent: ViewGroup?): View {
                    val tv = (convertView as? TextView) ?: label("", nameSp, bold = true).apply {
                        setSingleLine(true); ellipsize = android.text.TextUtils.TruncateAt.END
                        setPadding(dp(8), dp(5), dp(8), dp(5)); setShadowLayer(0f, 0f, 0f, 0)
                    }
                    val o = progItems[position]
                    val s = o.optLong("s"); val e = o.optLong("e")
                    val now = System.currentTimeMillis() / 1000
                    val live = s <= now && e > now
                    val rem = reminded.contains("$progSid|$s")
                    tv.text = (if (rem) "⏰ " else if (live) "▶ " else "") + fmtClock(s) + "   " + o.optString("t")
                    tv.setTextColor(when { rem -> Color.parseColor("#9ED8FF"); live -> Color.parseColor("#F9A825"); else -> Color.WHITE })
                    return tv
                }
            }
        }
        progMenu.addView(progList, LinearLayout.LayoutParams(-1, 0, 1f))
        root.addView(progMenu, FrameLayout.LayoutParams(dp(900), -1, Gravity.START))
        setContentView(root)
    }

    private fun initMpv() {
        // прежний экземпляр mpv должен быть уничтожен до создания нового
        mpvExec?.let { old ->
            try { old.shutdown(); old.awaitTermination(4, java.util.concurrent.TimeUnit.SECONDS) } catch (_: Exception) {}
        }
        exec = newExec(); mpvExec = exec
        MPVLib.create(applicationContext)
        fun o(k: String, v: String) { MPVLib.setOptionString(k, v) }
        if (engine == "mpv_embed") {
            // кадры из аппаратного декодера идут прямо на поверхность экрана, как у ExoPlayer: без копирования и без GPU
            o("vo", "mediacodec_embed")
        } else {
            o("vo", "gpu")
            o("gpu-context", "android")
            o("opengl-es", "yes")
        }
        o("hwdec", hwdec)
        o("hwdec-codecs", "h264,hevc,mpeg4,mpeg2video,vp8,vp9,av1")
        val audioOutput = AudioOutputSettings.read(this)
        val spdif = AudioOutputSettings.mpvCodecs(this, audioOutput)
        spdifActive = spdif.isNotBlank()
        // DRC is bypassed while passthrough is enabled; its saved mode is kept.
        sessionDrc = MpvSessionDrc.create(this, audioOutput.effectiveDrc)
        o("ao", if (sessionDrc != null) "audiotrack" else "audiotrack,opensles")
        sessionDrc?.let { o("audiotrack-session-id", it.sessionId.toString()) }
        o("audio-spdif", spdif)
        android.util.Log.i("HomeAudio", "mpv_config passthrough=${audioOutput.passthrough} codecs=$spdif drc_saved=${audioOutput.savedDrc} drc_effective=${audioOutput.effectiveDrc}")
        afrOn = getSharedPreferences("home_player", MODE_PRIVATE).getBoolean("afr", intent.getIntExtra("tv_afr", 1) != 0)
        android.util.Log.i("HomeAFR", "afr_config enabled=$afrOn live=true engine=mpv")
        o("alang", "rus,ru")
        o("sub", "no")
        o("keep-open", "no")
        // буфер из настройки ТВ «Буфер видео» (extra buffer_sec, секунды; без настройки — 3 с): перед стартом и после обрыва
        // набираем столько секунд, чтобы сгладить неровный приход потока; 0 («минимальная задержка») даёт 1 с
        val bufSec = intent.getIntExtra("buffer_sec", -1)
        val wait = if (bufSec < 0) 3 else maxOf(bufSec, 1)
        bufferWait = wait
        o("cache", "yes")
        o("cache-pause", "yes")
        o("cache-pause-initial", "yes")
        o("cache-pause-wait", wait.toString())
        o("cache-secs", (wait + 10).toString())
        o("demuxer-readahead-secs", (wait + 5).toString())
        o("demuxer-max-bytes", "32MiB")
        o("network-timeout", "15")
        o("tls-verify", "yes")
        o("deinterlace", "no")
        // заголовки из запроса (User-Agent отдельно)
        intent.getStringArrayExtra("headers")?.let { h ->
            val fields = ArrayList<String>()
            var i = 0
            while (i + 1 < h.size) {
                if (h[i].equals("User-Agent", true)) o("user-agent", h[i + 1]) else fields.add("${h[i]}: ${h[i + 1]}")
                i += 2
            }
            if (fields.isNotEmpty()) o("http-header-fields", fields.joinToString(","))
        }
        MPVLib.init()
        MPVLib.addObserver(this)
    }

    // ---------------------------------------------------------------- поверхность и запуск
    override fun surfaceCreated(holder: SurfaceHolder) {
        surfaceReady = true
        MPVLib.attachSurface(holder.surface)
        if (!started) { started = true; loadChannel(cur) }
        ui.removeCallbacks(tick)
        ui.postDelayed(tick, 1000)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        MPVLib.setPropertyString("android-surface-size", "${width}x${height}")
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        exec?.execute { try { MPVLib.detachSurface() } catch (_: Throwable) {} }
    }

    private fun loadChannel(i: Int) {
        if (uris.isEmpty()) return
        cur = ((i % uris.size) + uris.size) % uris.size
        lastPos = -1.0
        afrDone = 0f
        loadedAt = System.currentTimeMillis()
        lastMoveAt = loadedAt
        statusView.text = "Загрузка…"
        showInfo(true)
        MPVLib.command(arrayOf("loadfile", uris[cur], "replace"))
        ui.removeCallbacks(saveLast)
        ui.postDelayed(saveLast, 5000)   // канал считается просмотренным, если на нём задержались
        if (panelOpen) (chList.adapter as? BaseAdapter)?.notifyDataSetChanged()
    }

    private fun stepChannel(dir: Int) {
        if (uris.size < 2) return
        loadChannel(cur + dir)
        if (panelOpen) goTo(cur)
    }

    // ---------------------------------------------------------------- плашка «сейчас/далее»
    private val hideInfo = Runnable { infoBox.visibility = View.GONE }

    private fun fmtClock(ts: Long): String = clock.format(Date(ts * 1000))

    private fun showInfo(autoHide: Boolean) {
        fillInfo()
        infoBox.visibility = View.VISIBLE
        ui.removeCallbacks(hideInfo)
        if (autoHide) ui.postDelayed(hideInfo, 4500)
    }

    // только обновляет текст, не трогая таймер скрытия (иначе обновление программы оставляло плашку навсегда)
    private fun fillInfo() {
        nameView.text = "${cur + 1}. ${names.getOrNull(cur) ?: ""}"
        val sid = Uri.parse(uris.getOrNull(cur) ?: "").lastPathSegment ?: ""
        val e = epgMap?.optJSONObject(sid)
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
            while (!destroyed) {
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
                    ui.post { if (infoBox.visibility == View.VISIBLE) fillInfo(); (chList.adapter as? BaseAdapter)?.notifyDataSetChanged() }
                } catch (_: Exception) {
                }
                try { Thread.sleep(60_000) } catch (_: InterruptedException) { return@Thread }
            }
        }.start()
    }

    // ---------------------------------------------------------------- логотипы и программа выбранного канала
    private fun placeholderLogo(name: String): Bitmap {
        val clean = name.replace(Regex("^\\d+\\.\\s*"), "").trim()
        val words = clean.split(Regex("[\\s!._-]+")).filter { it.isNotEmpty() }
        var letters = words.take(3).joinToString("") { it.take(1) }.uppercase()
        if (letters.length < 2) letters = clean.take(2).uppercase()
        val key = "ph:$clean"
        logoCache.get(key)?.let { return it }
        val w = 120; val h = 80
        val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
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

    private fun loadLogo(iv: ImageView, url: String?, name: String = "") {
        iv.tag = url ?: ("ph:" + name)
        if (url.isNullOrEmpty()) {
            if (name.isNotEmpty()) iv.setImageBitmap(placeholderLogo(name)) else iv.setImageDrawable(null)
            return
        }
        val cached = logoCache.get(url)
        if (cached != null) { iv.setImageBitmap(cached); return }
        if (logoFailed.contains(url)) { if (name.isNotEmpty()) iv.setImageBitmap(placeholderLogo(name)); return }
        iv.setImageDrawable(null)
        Thread {
            var bmp: Bitmap? = null
            try {
                // переходы вручную: адреса логотипов часто http, а сервер переводит на https (301)
                var curUrl = url
                for (hop in 0 until 5) {
                    val c = URL(curUrl).openConnection() as HttpURLConnection
                    c.connectTimeout = 6000; c.readTimeout = 8000
                    c.instanceFollowRedirects = false
                    val code = c.responseCode
                    if (code in 300..399) {
                        val loc = c.getHeaderField("Location") ?: break
                        curUrl = URL(URL(curUrl), loc).toString()
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
        val uri = uris.getOrNull(pos) ?: return
        val sid = Uri.parse(uri).lastPathSegment ?: return
        detName.text = names.getOrNull(pos) ?: ""
        loadLogo(detLogo, logoMap[sid], names.getOrNull(pos) ?: "")
        val cached = dayCache[sid]
        if (cached != null && System.currentTimeMillis() - cached.first < 5 * 60_000) { renderDay(cached.second); return }
        detNow.text = "Загрузка…"; detDesc.text = ""; detNext.text = ""
        val base = (tvNowUrl ?: return).substringBefore("/tv/now")
        Thread {
            try {
                val c = homeConnection("$base/tv/day?id=$sid")
                c.connectTimeout = 6000; c.readTimeout = 8000
                val arr = JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("programs") ?: JSONArray()
                dayCache[sid] = Pair(System.currentTimeMillis(), arr)
                ui.post { if (detailPos == pos) renderDay(arr) }
            } catch (_: Exception) {
                ui.post { if (detailPos == pos) { detNow.text = "Программа недоступна"; detDesc.text = ""; detNext.text = "" } }
            }
        }.start()
    }

    private fun renderDay(arr: JSONArray) {
        val now = System.currentTimeMillis() / 1000
        var curIdx = -1
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            if (o.optLong("s") <= now && o.optLong("e") > now) { curIdx = i; break }
        }
        if (curIdx < 0) {
            detNow.text = "Нет данных о программе"; detDesc.text = ""
            detNext.text = (0 until minOf(arr.length(), 8)).joinToString("\n") { arr.getJSONObject(it).let { o -> fmtClock(o.optLong("s")) + "  " + o.optString("t") } }
            return
        }
        val o = arr.getJSONObject(curIdx)
        detNow.text = "Сейчас · ${fmtClock(o.optLong("s"))}–${fmtClock(o.optLong("e"))}\n${o.optString("t")}"
        val desc = o.optString("d")
        detDesc.text = if (desc.isNotBlank() && desc != "null") desc else "Описание отсутствует"
        val sb = StringBuilder()
        var k = curIdx + 1
        while (k < arr.length() && k <= curIdx + 8) {
            val p = arr.getJSONObject(k)
            sb.append(fmtClock(p.optLong("s"))).append("  ").append(p.optString("t")).append('\n')
            k++
        }
        detNext.text = sb.toString().trimEnd()
    }

    // ---------------------------------------------------------------- сторож и статистика (раз в секунду)
    private val tick = object : Runnable {
        override fun run() {
            if (destroyed) return
            try { watch() } catch (_: Exception) {}
            ui.postDelayed(this, 1000)
        }
    }

    private fun prop(name: String): String = try { MPVLib.getPropertyString(name) ?: "—" } catch (_: Exception) { "—" }

    private fun watch() {
        if (!surfaceReady || !started) return
        val now = System.currentTimeMillis()
        val pos = try { MPVLib.getPropertyDouble("time-pos") } catch (_: Exception) { null }
        // Some Android outputs advertise AC3/DTS but cannot open mpv's IEC61937
        // AudioTrack. Do not let the live reconnect watchdog repeat that failure.
        if (spdifActive && now - loadedAt > 2500 && prop("audio-codec-name") != "—" && prop("current-ao") == "—") {
            spdifActive = false
            MPVLib.setPropertyString("audio-spdif", "")
            android.util.Log.w("HomeAudio", "mpv passthrough output unavailable; retry PCM")
            Toast.makeText(this, "Проброс недоступен в mpv: звук через PCM", Toast.LENGTH_LONG).show()
            loadChannel(cur)
            return
        }
        if (pos != null && pos != lastPos) {
            if (lastPos < 0) android.util.Log.i("MpvTv", "ПУСК: ход времени через ${now - loadedAt} мс после loadfile, канал=${cur + 1}")
            lastPos = pos
            lastMoveAt = now
            val t = statusView.text.toString()
            if (t == "Загрузка…" || t == "Переподключение…") statusView.text = ""
        }
        // нет хода времени: при запуске даём 15 с (+буфер), дальше 6 с (+буфер)
        val limit = (if (lastPos < 0) 15_000L else 6_000L) + bufferWait * 1000L
        if (now - lastMoveAt > limit && now - loadedAt > 2_000) {
            reconnects++
            statusView.text = "Переподключение…"
            loadChannel(cur)
            return
        }
        // частота кадров видео известна, пока идёт воспроизведение: подбираем режим экрана (АФР)
        if (lastPos >= 0) {
            val fps = prop("container-fps").toFloatOrNull() ?: 0f
            if (fps > 0f) applyFrameRate(fps)
        }
        // диагностика цвета: снимок декодированного кадра (extra shot=1; работает при программном декодировании mpv_sw)
        // и цветовые параметры, как их видит mpv; файл лежит в папке приложения: /sdcard/Android/data/app.lampac.home/files/
        if (intent.getIntExtra("shot", 0) == 1 && !shotDone && lastPos >= 8.0) {
            shotDone = true
            try {
                val dir = getExternalFilesDir(null)
                val sid = Uri.parse(uris[cur]).lastPathSegment ?: "ch"
                val path = "${dir?.absolutePath}/shot_$sid.png"
                // снимок того, что mpv вывел на поверхность (кодировщика картинок в сборке libmpv нет): копируем пиксели средствами Android
                val bmp = Bitmap.createBitmap(maxOf(surfaceView.width / 2, 2), maxOf(surfaceView.height / 2, 2), Bitmap.Config.ARGB_8888)
                android.view.PixelCopy.request(surfaceView, bmp, { res ->
                    try {
                        if (res == android.view.PixelCopy.SUCCESS) {
                            java.io.FileOutputStream(path).use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }
                            android.util.Log.i("MpvTv", "СНИМОК: $path ${bmp.width}x${bmp.height}")
                        } else android.util.Log.i("MpvTv", "СНИМОК не удался, код $res")
                    } catch (e: Throwable) { android.util.Log.i("MpvTv", "СНИМОК ошибка: ${e.message}") }
                }, ui)
                android.util.Log.i("MpvTv", "ЦВЕТ: pixfmt=${prop("video-params/pixelformat")} matrix=${prop("video-params/colormatrix")} " +
                    "levels=${prop("video-params/colorlevels")} primaries=${prop("video-params/primaries")} gamma=${prop("video-params/gamma")} " +
                    "sig-peak=${prop("video-params/sig-peak")} hdr-метаданные=${prop("video-params/max-luma")}")
            } catch (e: Throwable) {
                android.util.Log.i("MpvTv", "снимок не удался: ${e.message}")
            }
        }
        val dm = windowManager.defaultDisplay.mode
        val disp = "${dm.physicalWidth}x${dm.physicalHeight} @ ${"%.2f".format(dm.refreshRate)} Гц"
        val w = prop("video-params/w"); val h = prop("video-params/h")
        // строка в журнал приставки раз в 5 секунд: по ней можно оценивать работу движка без картинки (adb logcat -s MpvTv)
        if (now - lastLog > 5000) {
            lastLog = now
            android.util.Log.i("MpvTv", "движок=$engine hwdec=${prop("hwdec-current")} видео=${prop("video-format")} ${w}x${h} fps=${prop("container-fps")} " +
                "drop=${prop("decoder-frame-drop-count")}/${prop("frame-drop-count")} звук=${prop("audio-codec-name")} кэш=${prop("demuxer-cache-duration")} " +
                "t=${if (pos != null) "%.1f".format(pos) else "—"} реконнектов=$reconnects экран=$disp канал=${cur + 1}")
        }
        if (statsOn) {
            statsView.text = "mpv · hwdec: ${prop("hwdec-current")} (запрошен $hwdec) · деинтерлейс: ${if (deint) "да" else "нет"} · экран $disp\n" +
                "видео: ${prop("video-format")} ${w}x${h} · ${prop("container-fps")} к/с · пропущено ${prop("decoder-frame-drop-count")}/${prop("frame-drop-count")}\n" +
                "звук: ${prop("audio-codec-name")} ${prop("audio-params/samplerate")} Гц ${prop("audio-params/channels")} · буфер $bufferWait с · кэш ${prop("demuxer-cache-duration")} с\n" +
                "время: ${if (pos != null) "%.1f".format(pos) else "—"} · переподключений: $reconnects"
            statsView.visibility = View.VISIBLE
        } else statsView.visibility = View.GONE
    }

    // ---------------------------------------------------------------- события mpv
    override fun eventProperty(property: String) {}
    override fun eventProperty(property: String, value: Long) {}
    override fun eventProperty(property: String, value: Double) {}
    override fun eventProperty(property: String, value: Boolean) {}
    override fun eventProperty(property: String, value: String) {}

    override fun event(eventId: Int) {
        when (eventId) {
            MPVLib.MPV_EVENT_PLAYBACK_RESTART, MPVLib.MPV_EVENT_FILE_LOADED -> ui.post {
                if (statusView.text == "Загрузка…") statusView.text = ""
                sessionDrc?.let { android.util.Log.i("HomeAudio", "mpv DRC session=${it.sessionId} enabled=${it.enabled} ao=${prop("current-ao")}") }
                android.util.Log.i("HomeAudio", "mpv_output ao=${prop("current-ao")} format=${prop("audio-out-params/format")} rate=${prop("audio-out-params/samplerate")} channels=${prop("audio-out-params/channels")}")
            }
        }
    }

    // ---------------------------------------------------------------- последний канал на сервере (как у встроенного плеера)
    private val saveLast = Runnable {
        val sid = Uri.parse(uris.getOrNull(cur) ?: return@Runnable).lastPathSegment ?: return@Runnable
        val base = (tvNowUrl ?: return@Runnable).substringBefore("/tv/now")
        val dev = java.net.URLEncoder.encode(tvDev, "UTF-8")
        Thread {
            try {
                val c = homeConnection("$base/tv/last?set=$sid&dev=$dev")
                c.connectTimeout = 4000; c.readTimeout = 4000
                c.inputStream.close()
            } catch (_: Exception) {}
        }.start()
    }

    // ---------------------------------------------------------------- пульт (как у штатного плеера эфира)
    private fun showPanel() {
        panelOpen = true
        android.util.Log.d("TvPanel", "engine=mpv visible=true")
        infoBox.visibility = View.GONE
        panel.visibility = View.VISIBLE
        goTo(cur)
        scheduleDetail(cur)
        chList.requestFocus()
        touchPanelTimer()
    }

    // список каналов закрывается через 10 с; чтение программы не прерываем.
    private val autoHidePanel = Runnable { if (panelOpen) hidePanel() }

    private fun touchPanelTimer() {
        ui.removeCallbacks(autoHidePanel)
        if (panelOpen && !progOpen && hasWindowFocus() && !finishing) ui.postDelayed(autoHidePanel, 10_000L)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) touchPanelTimer() else { ui.removeCallbacks(autoHidePanel); panelOk.cancel() }
    }

    // ОК в списке: короткое — включить канал, удержание (0,6 с) — окно программ; в окне программ удержание — напоминание
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val started = android.os.SystemClock.uptimeMillis()
        val handled = dispatchTvKey(event)
        TvInputLog.record(event, started, handled, "mpv", currentFocus)
        return handled
    }

    private fun dispatchTvKey(event: KeyEvent): Boolean {
        if (panelOpen && event.action == KeyEvent.ACTION_DOWN) touchPanelTimer()
        val ok = event.keyCode == KeyEvent.KEYCODE_DPAD_CENTER || event.keyCode == KeyEvent.KEYCODE_ENTER
        if (panelOpen && ok) {
            if (event.action == KeyEvent.ACTION_DOWN) {
                panelOk.press()
                return true
            }
            if (event.action == KeyEvent.ACTION_UP) {
                panelOk.release(event.isCanceled)
                return true
            }
        }
        return super.dispatchKeyEvent(event)
    }

    private fun onOkShort() {
        if (progOpen) { toggleRemind(progList.selectedItemPosition); return }
        if (chList.selectedItemPosition >= 0) { loadChannel(selAbs()); hidePanel() }
    }

    private fun onOkLong() {
        if (progOpen) toggleRemind(progList.selectedItemPosition)
        else openProgMenu(selAbs())
    }

    private fun apiBase(): String? = tvNowUrl?.substringBefore("/tv/now")
    private fun devQ(): String = if (tvDev.isBlank()) "" else "dev=" + java.net.URLEncoder.encode(tvDev, "UTF-8")

    private fun applyReminders(list: JSONArray?) {
        val set = HashSet<String>()
        if (list != null) for (i in 0 until list.length()) list.getJSONObject(i).let { set.add(it.optString("id") + "|" + it.optLong("s")) }
        reminded.clear(); reminded.addAll(set)
    }

    private fun openProgMenu(pos: Int) {
        val sid = Uri.parse(uris.getOrNull(pos) ?: return).lastPathSegment ?: return
        progSid = sid
        progTitle.text = names.getOrNull(pos) ?: ""
        progItems = emptyList()
        (progList.adapter as BaseAdapter).notifyDataSetChanged()
        progOpen = true
        ui.removeCallbacks(autoHidePanel)
        progMenu.visibility = View.VISIBLE
        chList.isFocusable = false
        progList.requestFocus()
        val cached = dayCache[sid]
        if (cached != null && System.currentTimeMillis() - cached.first < 5 * 60_000) { fillProg(cached.second); return }
        val base = apiBase() ?: return
        Thread {
            try {
                val c = homeConnection("$base/tv/day?id=$sid")
                c.connectTimeout = 6000; c.readTimeout = 8000
                val arr = JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("programs") ?: JSONArray()
                dayCache[sid] = Pair(System.currentTimeMillis(), arr)
                ui.post { if (progOpen && progSid == sid) fillProg(arr) }
            } catch (_: Exception) {
                ui.post { if (progOpen) progTitle.text = (names.getOrNull(pos) ?: "") + " — программа недоступна" }
            }
        }.start()
    }

    private fun fillProg(arr: JSONArray) {
        val now = System.currentTimeMillis() / 1000
        val l = ArrayList<JSONObject>()
        for (i in 0 until arr.length()) arr.getJSONObject(i).let { if (it.optLong("e") > now) l.add(it) }
        progItems = l
        (progList.adapter as BaseAdapter).notifyDataSetChanged()
        progList.setSelection(if (l.size > 1 && l[0].optLong("s") <= now) 1 else 0)
    }

    private fun closeProgMenu() {
        progOpen = false
        progMenu.visibility = View.GONE
        chList.isFocusable = true
        chList.requestFocus()
        touchPanelTimer()
    }

    private fun toggleRemind(pos: Int) {
        val o = progItems.getOrNull(pos) ?: return
        val s = o.optLong("s")
        if (s <= System.currentTimeMillis() / 1000) { Toast.makeText(this, "Передача уже идёт", Toast.LENGTH_SHORT).show(); return }
        val base = apiBase() ?: return
        val sid = progSid
        val title = java.net.URLEncoder.encode(o.optString("t"), "UTF-8")
        val chName = java.net.URLEncoder.encode(progTitle.text.toString(), "UTF-8")
        Thread {
            try {
                val c = homeConnection("$base/tv/remind/toggle?${devQ()}&id=$sid&s=$s&t=$title&n=$chName")
                c.connectTimeout = 5000; c.readTimeout = 5000
                val r = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                applyReminders(r.optJSONArray("list"))
                val on = r.optBoolean("on")
                ui.post {
                    (progList.adapter as BaseAdapter).notifyDataSetChanged()
                    Toast.makeText(this, if (on) "Напоминание поставлено: включится в ${fmtClock(s - 120)}" else "Напоминание снято", Toast.LENGTH_SHORT).show()
                }
            } catch (_: Exception) {
                ui.post { Toast.makeText(this, "Нет связи с сервером", Toast.LENGTH_SHORT).show() }
            }
        }.start()
    }

    // раз в 15 секунд читаем напоминания устройства; за 2 минуты до начала переключаем канал на нужный
    private fun startRemindPolling() {
        val base = apiBase() ?: return
        Thread {
            while (!destroyed) {
                try {
                    val c = homeConnection("$base/tv/remind?${devQ()}")
                    c.connectTimeout = 4000; c.readTimeout = 5000
                    val arr = JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("list")
                    applyReminders(arr)
                    val now = System.currentTimeMillis() / 1000
                    if (arr != null) for (i in 0 until arr.length()) {
                        val r = arr.getJSONObject(i)
                        val s = r.optLong("s")
                        if (now < s - 120) continue
                        val id = r.optString("id")
                        val t = r.optString("t")
                        try {
                            homeConnection("$base/tv/remind/done?${devQ()}&id=$id&s=$s").apply { connectTimeout = 4000; readTimeout = 4000 }.let { done -> try { done.inputStream.close() } finally { done.disconnect() } }
                        } catch (_: Exception) { continue }   // не удалось пометить — попробуем в следующий раз, чтобы не включить дважды
                        ui.post { fireReminder(id, t, s) }
                    }
                } catch (_: Exception) {
                }
                try { Thread.sleep(15_000) } catch (_: InterruptedException) { return@Thread }
            }
        }.start()
    }

    private fun fireReminder(id: String, title: String, start: Long) {
        if (destroyed) return
        val idx = uris.indexOfFirst { Uri.parse(it).lastPathSegment == id }
        if (idx < 0) return
        val msg = "⏰ ${fmtClock(start)} $title"
        if (idx == cur) { Toast.makeText(this, "Напоминание: $msg — уже на этом канале", Toast.LENGTH_LONG).show(); return }
        if (progOpen) closeProgMenu()
        if (panelOpen) hidePanel()
        loadChannel(idx)
        Toast.makeText(this, "Напоминание: $msg", Toast.LENGTH_LONG).show()
    }

    private fun pageCount() = maxOf(1, (names.size + pageSize - 1) / pageSize)

    private fun updateHint() {
        hintView.text = "стр. ${pageStart / pageSize + 1}/${pageCount()} · OK — включить · удержание — программа и напоминания · ←→ страницы"
    }

    // показать страницу, где лежит канал abs (номер с нуля), и выделить его; без прокрутки
    private fun goTo(abs: Int) {
        val n = names.size
        if (n == 0) return
        val a = ((abs % n) + n) % n
        pageStart = (a / pageSize) * pageSize
        (chList.adapter as? BaseAdapter)?.notifyDataSetChanged()
        chList.setSelection(a - pageStart)
        updateHint()
    }

    private fun selAbs(): Int = pageStart + chList.selectedItemPosition.coerceAtLeast(0)

    // влево/вправо — страница назад/вперёд по кругу, строка на странице та же
    private fun pagePanel(dir: Int) {
        val n = names.size
        if (n == 0) return
        val rel = chList.selectedItemPosition.coerceAtLeast(0)
        val pg = Math.floorMod(pageStart / pageSize + dir, pageCount())
        goTo(minOf(pg * pageSize + rel, n - 1))
    }

    private fun hidePanel() {
        panelOk.cancel()
        ui.removeCallbacks(autoHidePanel)
        panelOpen = false
        android.util.Log.d("TvPanel", "engine=mpv visible=false")
        if (progOpen) closeProgMenu()
        panel.visibility = View.GONE
        chList.clearFocus()
    }

    // ---------------------------------------------------------------- панель плеера (удержание ОК)
    private fun showControlPanel() {
        val sid = Uri.parse(uris.getOrNull(cur) ?: return).lastPathSegment ?: return
        val items = arrayOf("Источник…", "Звук…", "Деинтерлейс: ${if (deint) "включён" else "выключен"}")
        android.app.AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle("${cur + 1}. ${names.getOrNull(cur) ?: ""}")
            .setItems(items) { _, which ->
                when (which) {
                    0 -> pickSource(sid)
                    1 -> pickAudio()
                    2 -> toggleDeint()
                }
            }
            .show()
    }

    private fun pickSource(sid: String) {
        val u = Uri.parse(uris[cur])
        val base = "${u.scheme}://${u.authority}"
        Thread {
            try {
                val c = homeConnection("$base/tsb/sources/$sid")
                c.connectTimeout = 5000; c.readTimeout = 6000
                val arr = JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONArray("sources")
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
                    val ico = if (o.optString("type") == "ts") "\uD83D\uDCE1 " else "\uD83C\uDF10 "
                    labels.add("${if (i == activeIdx) "▶ " else ""}$ico${i + 1}. ${o.optString("name")}${if (i == activeIdx) "  (играет сейчас)" else ""}")
                }
                ui.post {
                    if (labels.size <= 2) { Toast.makeText(this, "У этого канала один источник", Toast.LENGTH_SHORT).show(); return@post }
                    android.app.AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                        .setTitle("Источник")
                        .setSingleChoiceItems(labels.toTypedArray(), forcedSrcIdx + 1) { d, pick ->
                            d.dismiss()
                            forcedSrcIdx = pick - 1
                            val url = if (pick == 0) uris[cur] else "$base/stream/$sid?src=${pick - 1}"
                            statusView.text = "Загрузка…"
                            MPVLib.command(arrayOf("loadfile", url, "replace"))
                        }.show()
                }
            } catch (_: Exception) {
                ui.post { Toast.makeText(this, "Не удалось получить источники", Toast.LENGTH_SHORT).show() }
            }
        }.start()
    }

    private fun pickAudio() {
        try {
            val tl = org.json.JSONArray(MPVLib.getPropertyString("track-list") ?: "[]")
            val ids = ArrayList<Int>(); val labels = ArrayList<String>(); var selected = -1
            for (i in 0 until tl.length()) {
                val t = tl.getJSONObject(i)
                if (t.optString("type") != "audio") continue
                val lang = t.optString("lang"); val title = t.optString("title"); val codec = t.optString("codec")
                labels.add(listOf(lang, title, codec, if (t.optInt("demux-channel-count") > 0) "${t.optInt("demux-channel-count")} кан." else "").filter { it.isNotBlank() }.joinToString(" · ").ifBlank { "Дорожка ${t.optInt("id")}" })
                ids.add(t.optInt("id"))
                if (t.optBoolean("selected")) selected = ids.size - 1
            }
            if (ids.size < 2) { Toast.makeText(this, "Звуковая дорожка одна", Toast.LENGTH_SHORT).show(); return }
            android.app.AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle("Звук")
                .setSingleChoiceItems(labels.toTypedArray(), selected) { d, pick ->
                    d.dismiss()
                    MPVLib.setPropertyString("aid", ids[pick].toString())
                }.show()
        } catch (_: Throwable) {
            Toast.makeText(this, "Не удалось получить дорожки", Toast.LENGTH_SHORT).show()
        }
    }

    private fun toggleDeint() {
        deint = !deint
        MPVLib.setPropertyString("deinterlace", if (deint) "yes" else "no")
        statusView.text = "Деинтерлейс: ${if (deint) "включён" else "выключен"}"
        ui.postDelayed({ if (statusView.text.startsWith("Деинтерлейс")) statusView.text = "" }, 1500)
    }

    private val commitDigits = Runnable {
        val n = digits.toIntOrNull()
        digits = ""
        statusView.text = ""
        if (n != null && n in 1..uris.size) loadChannel(n - 1)
    }

    // Назад: 1 раз — список каналов; из списка — к эфиру; два раза подряд (≤1,2 с) — выход
    private fun handleBack(event: KeyEvent) {
        if (event.repeatCount > 0) return
        val now = event.eventTime
        val dbl = now - lastBackAt < 1200
        lastBackAt = now
        when {
            dbl -> finishPlayer()
            progOpen -> closeProgMenu()
            panelOpen -> if (fromLaunch) finishPlayer() else hidePanel()   // запуск через активити: «Назад» из списка — выход
            else -> showPanel()
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_ESCAPE) { handleBack(event); return true }
        when (keyCode) {
            KeyEvent.KEYCODE_MEDIA_STOP -> { finishPlayer(); return true }
            KeyEvent.KEYCODE_MEDIA_NEXT, KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> { stepChannel(1); return true }
            KeyEvent.KEYCODE_MEDIA_PREVIOUS, KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_MEDIA_REWIND -> { stepChannel(-1); return true }
            KeyEvent.KEYCODE_MENU -> { PlayerSettingsDialog.show(this); return true }
            KeyEvent.KEYCODE_INFO -> { statsOn = !statsOn; return true }
        }
        if (panelOpen) {
            if (progOpen) {
                if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT) { closeProgMenu(); return true }
                return super.onKeyDown(keyCode, event)
            }
            if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT) { pagePanel(-1); return true }
            if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) { pagePanel(1); return true }
            return super.onKeyDown(keyCode, event)
        }
        // цифры — номер канала
        if (keyCode in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9) {
            digits += (keyCode - KeyEvent.KEYCODE_0).toString()
            statusView.text = "Канал $digits"
            ui.removeCallbacks(commitDigits)
            ui.postDelayed(commitDigits, 1500)
            return true
        }
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT -> { showPanel(); return true }
            KeyEvent.KEYCODE_DPAD_RIGHT -> { showInfo(true); return true }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER -> {
                // короткое нажатие — список каналов (в onKeyUp), удержание — деинтерлейс
                event.startTracking()
                return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onKeyLongPress(keyCode: Int, event: KeyEvent): Boolean {
        if (!panelOpen && (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER)) {
            okLong = true
            showControlPanel()
            return true
        }
        return super.onKeyLongPress(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER) {
            if (okLong) { okLong = false; return true }
            if (!panelOpen && event.isTracking && !event.isCanceled) { showPanel(); return true }
        }
        return super.onKeyUp(keyCode, event)
    }

    // ---------------------------------------------------------------- выход
    private fun finishPlayer() {
        if (finishing) return
        finishing = true
        // звук глушим сразу (иначе mpv доигрывает из своего буфера, пока идёт закрытие)
        exec?.execute {
            try { MPVLib.setPropertyBoolean("pause", true) } catch (_: Throwable) {}
            try { MPVLib.command(arrayOf("stop")) } catch (_: Throwable) {}
        }
        setResult(RESULT_CANCELED)
        finish()
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        finishPlayer()   // Home: эфир в фоне не оставляем
    }

    override fun onStop() {
        panelOk.cancel()
        ui.removeCallbacks(autoHidePanel)
        super.onStop()
        if (!isFinishing) finishPlayer()
    }

    override fun onDestroy() {
        destroyed = true
        top.rootu.lampa.RemindWatcher.mpvAlive = false
        ui.removeCallbacksAndMessages(null)
        try { MPVLib.removeObserver(this) } catch (_: Exception) {}
        // уничтожение mpv — в фоне: на главном потоке оно могло висеть больше 10 секунд
        exec?.execute {
            try { MPVLib.command(arrayOf("stop")) } catch (_: Throwable) {}
            try { MPVLib.detachSurface() } catch (_: Throwable) {}
            try { MPVLib.destroy() } catch (_: Throwable) {}
            sessionDrc?.release(); sessionDrc = null
        }
        super.onDestroy()
    }
}
