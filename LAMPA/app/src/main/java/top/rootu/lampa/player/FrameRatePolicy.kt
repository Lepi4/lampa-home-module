package top.rootu.lampa.player

import kotlin.math.abs
import kotlin.math.roundToInt

/** No Android dependencies: selection and PTS estimation are also exercised on the JVM. */
object FrameRatePolicy {
    data class Mode(val id: Int, val width: Int, val height: Int, val hz: Float)
    internal val rates = floatArrayOf(24000f / 1001f, 24f, 25f, 30000f / 1001f, 30f, 50f, 60000f / 1001f, 60f, 120f)

    fun normalize(fps: Float): Float? {
        if (!fps.isFinite() || fps < 10f || fps > 240f) return null
        val nearest = rates.minByOrNull { abs(it - fps) }!!
        return if (abs(nearest - fps) < 0.008f) nearest else fps
    }

    fun choose(fps: Float, live: Boolean, current: Mode, modes: List<Mode>): Mode? {
        if (normalize(fps) == null) return null
        val minimum = if (live && fps in 24.8f..25.2f) 2 else 1
        fun error(m: Mode): Float {
            if (!m.hz.isFinite() || m.hz <= 0f || m.width != current.width || m.height != current.height) return Float.POSITIVE_INFINITY
            val multiple = (m.hz / fps).roundToInt()
            if (multiple < minimum) return Float.POSITIVE_INFINITY
            return abs(m.hz - fps * multiple)
        }
        val best = modes.filter { error(it) < 0.5f }
            .minWithOrNull(compareBy<Mode> { error(it) }.thenBy { it.hz }) ?: return null
        // Keep an equally accurate multiple (e.g. 25 at 50 Hz), but distinguish 23.976 from 24.
        return if (error(current) <= error(best) + 0.005f) current else best
    }
}

/** Estimate cadence from frame timestamps, never from the number of UI timer callbacks. */
class PtsFrameRateEstimator {
    private var generation = -1
    private var previous = Long.MIN_VALUE
    private val samples = ArrayList<Long>(96)
    private var count = 0

    @Synchronized fun reset() { previous = Long.MIN_VALUE; samples.clear(); count = 0; generation = -1 }

    @Synchronized fun frame(ptsUs: Long, epoch: Int): Float? {
        if (generation != epoch) { reset(); generation = epoch }
        val before = previous; previous = ptsUs
        if (before == Long.MIN_VALUE) return null
        val delta = ptsUs - before
        if (delta !in 4_000L..150_000L) { samples.clear(); count = 0; return null }
        samples.add(delta)
        if (samples.size > 96) samples.removeAt(0)
        if (samples.size < 48 || ++count % 16 != 0) return null
        var best: Float? = null
        var bestScore = Double.POSITIVE_INFINITY
        for (rate in FrameRatePolicy.rates) {
            val period = 1_000_000.0 / rate
            var matched = 0; var cycles = 0L; var time = 0L
            for (sample in samples) {
                val n = (sample / period).roundToInt().coerceAtLeast(1)
                // Millisecond-quantized IPTV timestamps and skipped display frames are normal.
                if (abs(sample - n * period) <= maxOf(1200.0, period * 0.04)) {
                    matched++; cycles += n; time += sample
                }
            }
            if (matched < samples.size * 0.9 || time == 0L) continue
            val measured = 1_000_000.0 * cycles / time
            val relativeError = abs(measured - rate) / rate
            if (relativeError > 0.003) continue
            val score = (samples.size - matched).toDouble() / samples.size + relativeError * 100 + (cycles.toDouble() / matched - 1) * 0.001
            if (score < bestScore) { bestScore = score; best = rate }
        }
        return best
    }
}
