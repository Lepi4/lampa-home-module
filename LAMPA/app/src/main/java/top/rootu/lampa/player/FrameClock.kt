package top.rootu.lampa.player

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import androidx.media3.common.util.UnstableApi
import androidx.media3.effect.BitmapOverlay
import androidx.media3.effect.OverlaySettings

@UnstableApi
class ClockOverlay(private val clock: FrameClock) : BitmapOverlay() {
    override fun getBitmap(presentationTimeUs: Long): Bitmap = clock.frame()
    override fun getOverlaySettings(presentationTimeUs: Long): OverlaySettings =
        OverlaySettings.Builder()
            .setBackgroundFrameAnchor(clock.anchorX(), clock.anchorY())
            .setOverlayFrameAnchor(1f, 1f)
            .setScale(0.16f, 0.28f)
            .build()
}

// Квадрат часов внутри кадра: время, температура и знак погоды. Слой поверх SurfaceView не создаётся.
class FrameClock(
    private val ctx: Context,
    private val base: String,
    private val headers: (HttpURLConnection) -> Unit
) {
    @Volatile var visible = false
        private set
    private val ui = Handler(Looper.getMainLooper())
    private val zone = TimeZone.getTimeZone("Europe/Moscow")
    private val timeFmt = SimpleDateFormat("HH:mm", Locale.US).apply { timeZone = zone }
    private val lock = Any()
    private var bitmap = Bitmap.createBitmap(W, H, Bitmap.Config.ARGB_8888)
    private var temp: Double? = null
    private var condition = "neutral"
    private var started = false
    private var minuteTicks = 0
    private val tick = object : Runnable {
        override fun run() {
            if (!visible) return
            redraw()
            minuteTicks++
            if (minuteTicks >= 3) {
                minuteTicks = 0
                wobble()
            }
            ui.postDelayed(this, 60_000)
        }
    }

    fun start(onDecision: (Boolean) -> Unit) {
        if (started) return
        started = true
        Thread {
            val on = readEnabled()
            visible = on
            if (on) {
                refreshWeather()
                redraw()
                ui.post { ui.postDelayed(tick, 60_000) }
            }
            ui.post { onDecision(on) }
        }.apply { isDaemon = true; name = "frame-clock" }.start()
    }

    fun stop() {
        visible = false
        ui.removeCallbacks(tick)
    }

    fun frame(): Bitmap = synchronized(lock) { bitmap }

    fun shiftPx(): Int = 0

    fun anchorX(): Float = 0.92f
    fun anchorY(): Float = 0.9f

    private fun readEnabled(): Boolean {
        if (!base.startsWith("http://") && !base.startsWith("https://")) return true
        val id = ctx.getSharedPreferences("mc", Context.MODE_PRIVATE).getString("dev", null) ?: return true
        return try {
            val c = URL(base.trimEnd('/') + "/home/dev/hello?dev=" + java.net.URLEncoder.encode(id, "UTF-8")).openConnection() as HttpURLConnection
            c.connectTimeout = 4000; c.readTimeout = 4000; c.requestMethod = "GET"
            headers(c)
            val on = if (c.responseCode == 200) {
                val o = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                val v = o.optJSONObject("cfg")?.optString("mc_clock", "1") ?: "1"
                v != "0"
            } else true
            c.disconnect()
            on
        } catch (_: Exception) { true }
    }

    private fun refreshWeather() {
        if (!base.startsWith("http://") && !base.startsWith("https://")) return
        try {
            val c = URL(base.trimEnd('/') + "/home/weather").openConnection() as HttpURLConnection
            c.connectTimeout = 4000; c.readTimeout = 4000
            headers(c)
            if (c.responseCode == 200) {
                val o = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                if (o.optBoolean("weather") && !o.isNull("temp")) {
                    temp = o.optDouble("temp")
                    condition = o.optString("condition", "neutral")
                } else temp = null
            }
            c.disconnect()
        } catch (_: Exception) {}
        ui.postDelayed({ if (visible) Thread { refreshWeather() }.apply { isDaemon = true }.start() }, 15 * 60_000)
    }

    private fun redraw() {
        val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        paint.color = Color.WHITE
        paint.textAlign = Paint.Align.CENTER
        paint.textSize = 40f
        paint.isFakeBoldText = true
        paint.setShadowLayer(8f, 0f, 2f, Color.argb(200, 0, 0, 0))
        val weather = weatherLine()
        val line = timeFmt.format(Date()) + if (weather.isEmpty()) "" else "  " + weather
        val width = (paint.measureText(line) + 16f).toInt().coerceAtLeast(80)
        val fm = paint.fontMetrics
        val height = (fm.descent - fm.ascent + 6f).toInt().coerceAtLeast(40)
        val next = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        Canvas(next).drawText(line, width / 2f, -fm.ascent + 2f, paint)
        synchronized(lock) { bitmap = next }
        ui.post { showPlate() }
    }

    private fun weatherLine(): String {
        val t = temp ?: return ""
        val n = Math.round(t).toInt()
        val label = if (n > 0) "+$n°" else "$n°"
        return mark() + " " + label
    }

    private fun night(): Boolean {
        val hour = java.util.Calendar.getInstance().get(java.util.Calendar.HOUR_OF_DAY)
        return hour < 6 || hour >= 18
    }

    private fun mark(): String = when (condition) {
        "clear" -> if (night()) "\u263E" else "\u2600"
        "partly" -> if (night()) "\u263E\u2601" else "\u26C5"
        "cloudy" -> if (night()) "\u263E\u2601" else "\u2601"
        "rain" -> "\u2602"
        "snow" -> "\u2744"
        "storm" -> "\u26A1"
        else -> "\u25CF"
    }

    private var plate: android.widget.ImageView? = null

    fun mount(root: android.widget.FrameLayout) {
        val view = android.widget.ImageView(ctx).apply {
            scaleType = android.widget.ImageView.ScaleType.FIT_CENTER
            visibility = android.view.View.GONE
        }
        val side = (8 * ctx.resources.displayMetrics.density).toInt()
        root.addView(view, android.widget.FrameLayout.LayoutParams(W, H, android.view.Gravity.TOP or android.view.Gravity.END).apply {
            topMargin = 6
            marginEnd = side
        })
        plate = view
        start { show ->
            view.visibility = if (show) android.view.View.VISIBLE else android.view.View.GONE
            if (show) showPlate()
        }
    }

    private fun showPlate() {
        val view = plate ?: return
        val bmp = frame()
        view.setImageBitmap(bmp)
        val lp = view.layoutParams
        if (lp.width != bmp.width || lp.height != bmp.height) {
            lp.width = bmp.width
            lp.height = bmp.height
            view.layoutParams = lp
        }
    }

    private fun wobble() {
        val view = plate ?: return
        view.animate().cancel()
        view.rotation = 0f
        view.animate().rotation(4f).setDuration(350).withEndAction {
            if (plate !== view) return@withEndAction
            view.animate().rotation(-4f).setDuration(500).withEndAction {
                if (plate !== view) return@withEndAction
                view.animate().rotation(0f).setDuration(350).start()
            }.start()
        }.start()
    }

    private fun holidayColor(): Int {
        val cal = Calendar.getInstance(zone)
        val m = cal.get(Calendar.MONTH) + 1
        val d = cal.get(Calendar.DAY_OF_MONTH)
        return when {
            m == 1 && d <= 8 -> Color.parseColor("#E53935")
            m == 2 && d == 23 -> Color.parseColor("#1E88E5")
            m == 3 && d == 8 -> Color.parseColor("#EC407A")
            m == 5 && d == 1 -> Color.parseColor("#43A047")
            m == 5 && d == 9 -> Color.parseColor("#F9A825")
            m == 6 && d == 12 -> Color.parseColor("#1565C0")
            m == 11 && d == 4 -> Color.parseColor("#6D4C41")
            else -> Color.argb(180, 255, 255, 255)
        }
    }

    fun writeBgra(file: java.io.File) {
        val bmp = frame()
        val w = bmp.width
        val h = bmp.height
        val pixels = IntArray(w * h)
        bmp.getPixels(pixels, 0, w, 0, 0, w, h)
        val bytes = ByteArray(w * h * 4)
        var i = 0
        for (p in pixels) {
            bytes[i++] = (p and 0xFF).toByte()
            bytes[i++] = ((p shr 8) and 0xFF).toByte()
            bytes[i++] = ((p shr 16) and 0xFF).toByte()
            bytes[i++] = ((p ushr 24) and 0xFF).toByte()
        }
        file.writeBytes(bytes)
    }

    companion object {
        const val SIZE = 220
        const val W = 520
        const val H = 96
        val SHIFT = intArrayOf(0, 8, 16, 8, 0, 10)
        val SHIFT_SPOT = arrayOf(
            floatArrayOf(0f, 0f, 0f),
            floatArrayOf(16f, 10f, 6f),
            floatArrayOf(-8f, 14f, -5f),
            floatArrayOf(12f, -6f, 4f),
            floatArrayOf(-14f, 4f, -6f),
            floatArrayOf(6f, 16f, 5f)
        )
    }
}
