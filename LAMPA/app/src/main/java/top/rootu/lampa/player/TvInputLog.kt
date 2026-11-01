package top.rootu.lampa.player

import android.os.SystemClock
import android.util.Log
import android.view.KeyEvent
import android.view.View

/** Records delivery and handling, without changing routing or held-key repeats. */
internal object TvInputLog {
    fun record(event: KeyEvent, started: Long, handled: Boolean, engine: String, focus: View?) {
        if (event.action != KeyEvent.ACTION_DOWN || event.repeatCount != 0) return
        Log.d("TvInput", "engine=$engine key=${event.keyCode} " +
            "queued=${(started - event.eventTime).coerceAtLeast(0)}ms " +
            "handle=${SystemClock.uptimeMillis() - started}ms " +
            "focus=${focus?.javaClass?.simpleName} handled=$handled")
    }
}
