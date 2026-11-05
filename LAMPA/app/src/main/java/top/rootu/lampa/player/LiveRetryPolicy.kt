package top.rootu.lampa.player

/**
 * Backoff for reconnecting a live channel. A retry is only allowed while the
 * caller's channel epoch still matches; this class only owns delays/attempts so
 * it can be tested without Android or ExoPlayer.
 */
class LiveRetryPolicy(
    private val delaysMs: LongArray = longArrayOf(1_500L, 3_000L, 5_000L, 8_000L, 12_000L),
    private val maxAttempts: Int = 12
) {
    var attempt = 0
        private set

    val exhausted: Boolean get() = attempt >= maxAttempts

    /** Returns the delay before the next retry, or null when the budget is exhausted. */
    fun nextDelay(): Long? {
        if (exhausted) return null
        val delay = delaysMs[minOf(attempt, delaysMs.lastIndex)]
        attempt++
        return delay
    }

    fun reset() {
        attempt = 0
    }
}
