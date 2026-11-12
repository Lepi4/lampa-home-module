package top.rootu.lampa.player

import android.os.SystemClock

/** Logical Unix milliseconds; retries reuse the already serialized u. */
class ProgressClock(private val unixAnchor: Long,
                    private val mono: () -> Long = { SystemClock.elapsedRealtime() }) {
    private val monoAnchor=mono()
    private var last=unixAnchor
    @Synchronized fun next(): Long {
        last=maxOf(last+1,unixAnchor+(mono()-monoAnchor).coerceAtLeast(0))
        return last
    }
}
