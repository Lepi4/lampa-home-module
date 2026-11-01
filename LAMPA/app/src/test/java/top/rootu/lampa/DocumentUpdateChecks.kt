package top.rootu.lampa

object DocumentUpdateChecks {
    @JvmStatic fun main(args: Array<String>) {
        fun verify(name: String, ok: Boolean) { check(ok) { name }; println("PASS $name") }
        val d = McDocumentState()
        val old = d.begin(1000)
        verify("document gets its full startup window", !d.needsRecovery(120999))
        verify("unready document eventually requires a health check", d.needsRecovery(121001))
        d.markReady(old); d.pong(old, 121001)
        verify("ready responsive document is preserved", !d.needsRecovery(140000))
        verify("missing pong eventually requires recovery check", d.needsRecovery(166002))
        val next = d.begin(170000)
        d.markReady(old); d.pong(old, 190000)
        verify("old callbacks cannot mark the new document ready", !d.ready && d.pongAt == 0L)
        verify("old health callback cannot restart a new document", !d.acceptsRecovery(old, 400000))
        verify("profile change starts a fresh startup window", !d.needsRecovery(173000))
        d.markReady(next); d.pong(next, 173000)
        verify("current callbacks still work", d.ready && !d.needsRecovery(174000))
        val p = ServerUpdatePolicy()
        verify("same version is never offered", !p.shouldOffer(1115, 1115, 1))
        verify("older version is never offered", !p.shouldOffer(1115, 1113, 1))
        verify("newer version is offered", p.shouldOffer(1115, 1117, 1))
        p.decline(1117,100)
        verify("cancel suppresses before three hours", !p.shouldOffer(1115,1117,10800099))
        verify("cancel expires at three hours", p.shouldOffer(1115,1117,10800100))
        verify("a newer release also respects the three-hour cancellation", !p.shouldOffer(1115,1118,101))
        verify("restart offers the release again", ServerUpdatePolicy().shouldOffer(1115,1117,101))
    }
}
