package top.rootu.lampa

/** One document owns readiness, heartbeat and recovery callbacks. No user data. */
class McDocumentState {
    @Volatile var generation = 0L
        private set
    @Volatile var startedAt = 0L
        private set
    @Volatile var ready = false
    @Volatile var pongAt = 0L

    @Synchronized fun begin(now: Long): Long {
        generation++
        startedAt = now
        ready = false
        pongAt = 0L
        return generation
    }

    @Synchronized fun pong(owner: Long, now: Long) {
        if (owner == generation) pongAt = now
    }

    @Synchronized fun markReady(owner: Long) {
        if (owner == generation) ready = true
    }

    fun needsRecovery(now: Long): Boolean = if (ready) {
        pongAt > 0L && now - pongAt > 45_000L
    } else {
        startedAt > 0L && now - startedAt > 120_000L
    }

    fun acceptsRecovery(owner: Long, now: Long): Boolean = owner == generation && needsRecovery(now)
}
