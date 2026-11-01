package top.rootu.lampa.player

object TvOkPressChecks {
    @JvmStatic fun main(args: Array<String>) {
        var pending: Runnable? = null
        var posts = 0; var clicks = 0; var holds = 0
        val press = TvOkPress({ r, delay -> check(delay == 600L); pending = r; posts++ }, { pending = null }, { clicks++ }, { holds++ })
        fun verify(name: String, value: Boolean) { check(value) { name }; println("PASS $name") }
        press.press(); press.release(false)
        verify("short OK selects exactly once", clicks == 1 && holds == 0 && pending == null)
        press.press(); repeat(8) { press.press() }; pending!!.run(); press.release(false)
        verify("repeated DOWN does not restart the hold timer", posts == 2 && holds == 1)
        verify("release after a hold never selects the channel", clicks == 1)
        press.press(); val old = pending!!; press.cancel(); old.run(); press.release(false)
        verify("closing the panel cancels pending actions", clicks == 1 && holds == 1)
        press.press(); press.release(true)
        verify("canceled key does not select", clicks == 1 && holds == 1)
        press.release(false)
        verify("unmatched UP does nothing", clicks == 1 && holds == 1)
    }
}
