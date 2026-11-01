package top.rootu.lampa.player

import android.app.Activity
import android.hardware.display.DisplayManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.view.Display
import kotlin.math.abs

/** The sole owner of refresh-rate requests for this playback window. Media3's surface voting is off. */
class PlaybackFrameRateController(private val activity: Activity) : DisplayManager.DisplayListener {
    private val handler = Handler(Looper.getMainLooper())
    private val displays = activity.getSystemService(Activity.DISPLAY_SERVICE) as DisplayManager
    private val originalMode = if (Build.VERSION.SDK_INT >= 23) activity.window.attributes.preferredDisplayModeId else 0
    private var item = ""
    private var live = false
    private var enabled = false
    private var visible = true
    private var closed = false
    private var decided = false
    private var selected: FrameRatePolicy.Mode? = null
    private var candidate = 0f
    private var origin = ""
    private var actualId = -1
    @Volatile var generation: Int = 0
        private set
    @Volatile var needsFrames: Boolean = false
        private set

    init { displays.registerDisplayListener(this, handler) }

    private fun trace(code: String, key: String, detail: String) {
        android.util.Log.i("HomeAFR", "$code $key $detail")
        DLog.log("i", code, key, detail)
    }

    fun configure(on: Boolean, isLive: Boolean) {
        enabled = on && Build.VERSION.SDK_INT >= 23; live = isLive
        needsFrames = enabled && !decided
        if (!on) restore()
    }

    fun beginItem(key: String): Boolean {
        if (key == item) return false
        item = key; generation++; decided = false; selected = null; candidate = 0f; origin = ""
        needsFrames = enabled
        handler.removeCallbacks(commit)
        // Keep the previous output until the next format is known, avoiding a 50/60 Hz detour.
        return true
    }

    fun offer(fps: Float, source: String, epoch: Int = generation) {
        if (closed || !enabled || decided || epoch != generation) return
        val value = FrameRatePolicy.normalize(fps) ?: return
        // An actual input format takes precedence over a later PTS estimate.
        if (origin == "format" && source != "format" && candidate > 0f) return
        if (candidate > 0f && abs(candidate - value) < 0.005f) return
        candidate = value; origin = source
        handler.removeCallbacks(commit)
        if (visible) handler.postDelayed(commit, 500)
    }

    private val commit = Runnable {
        if (closed || !visible || !enabled || decided || Build.VERSION.SDK_INT < 23 || candidate <= 0f) return@Runnable
        @Suppress("DEPRECATION") val display = activity.windowManager.defaultDisplay
        try {
            fun convert(m: Display.Mode) = FrameRatePolicy.Mode(m.modeId, m.physicalWidth, m.physicalHeight, m.refreshRate)
            val current = convert(display.mode)
            selected = FrameRatePolicy.choose(candidate, live, current, display.supportedModes.map(::convert))
            decided = true; needsFrames = false
            val chosen = selected
            trace("afr_choice", if (chosen == null) "unsupported" else chosen.id.toString(),
                "fps=$candidate source=$origin from=${current.id}/${current.hz} to=${chosen?.id}/${chosen?.hz}")
            if (chosen == null) restore() else applySelected("item")
        } catch (e: Exception) {
            decided = true; needsFrames = false
            DLog.log("w", "afr_error", e.javaClass.simpleName)
        }
    }

    private fun applySelected(reason: String) {
        val mode = selected ?: return
        if (!enabled || !visible || closed || Build.VERSION.SDK_INT < 23) return
        @Suppress("DEPRECATION") val current = activity.windowManager.defaultDisplay.mode
        if (activity.window.attributes.preferredDisplayModeId == mode.id) return
        activity.window.attributes = activity.window.attributes.apply { preferredDisplayModeId = mode.id }
        trace("afr_request", mode.id.toString(), "reason=$reason from=${current.modeId} to=${mode.id} hz=${mode.hz}")
    }

    fun suspend() { visible = false; handler.removeCallbacks(commit); restore() }
    fun resume() {
        visible = true
        if (decided) applySelected("foreground")
        else if (enabled && candidate > 0f) handler.postDelayed(commit, 500)
    }

    private fun restore() {
        if (Build.VERSION.SDK_INT < 23) return
        if (activity.window.attributes.preferredDisplayModeId != originalMode)
            activity.window.attributes = activity.window.attributes.apply { preferredDisplayModeId = originalMode }
    }

    fun release() {
        if (closed) return
        handler.removeCallbacksAndMessages(null)
        restore(); displays.unregisterDisplayListener(this); closed = true; needsFrames = false
    }

    override fun onDisplayChanged(displayId: Int) {
        if (closed || Build.VERSION.SDK_INT < 23) return
        @Suppress("DEPRECATION") val d = activity.windowManager.defaultDisplay
        if (d.displayId != displayId || d.mode.modeId == actualId) return
        actualId = d.mode.modeId
        trace("afr_actual", actualId.toString(), "hz=${d.mode.refreshRate} selected=${selected?.id}")
        // Observe actual hardware changes; do not turn them into another refresh-rate request.
    }
    override fun onDisplayAdded(displayId: Int) = Unit
    override fun onDisplayRemoved(displayId: Int) = Unit
}
