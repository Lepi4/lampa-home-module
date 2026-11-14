package top.rootu.lampa

/** One document owns readiness, heartbeat and recovery callbacks. No user data. */
class McDocumentState {
    companion object {
        // A responsive error/blank page is not a working Lampa document.
        const val HEALTH_PROBE = "(function(){return !!(window.appready && window.Lampa && Lampa.Activity && Lampa.Controller)})()"
    }
    @Volatile var generation = 0L
        private set
    @Volatile var startedAt = 0L
        private set
    @Volatile var ready = false
    @Volatile var pongAt = 0L
    @Volatile private var unhealthySince = 0L

    @Synchronized fun begin(now: Long): Long {
        generation++
        startedAt = now
        ready = false
        pongAt = 0L
        unhealthySince = 0L
        return generation
    }

    @Synchronized fun pong(owner: Long, now: Long) {
        if (owner == generation) pongAt = now
    }

    @Synchronized fun markReady(owner: Long) {
        if (owner == generation) ready = true
    }

    @Synchronized fun observe(owner: Long, healthy: Boolean, now: Long) {
        if (owner != generation) return
        if (healthy) {
            ready = true
            pongAt = now
            unhealthySince = 0L
        } else if (ready || unhealthySince > 0L) {
            if (unhealthySince == 0L) unhealthySince = now
            ready = false
        }
    }

    fun needsRecovery(now: Long): Boolean = if (unhealthySince > 0L) {
        now - unhealthySince > 45_000L
    } else if (ready) {
        pongAt > 0L && now - pongAt > 45_000L
    } else {
        startedAt > 0L && now - startedAt > 120_000L
    }

    fun acceptsRecovery(owner: Long, now: Long): Boolean = owner == generation && needsRecovery(now)
}
