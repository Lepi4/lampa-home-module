package top.rootu.lampa.player

/** One gesture per press, including remotes that repeat DOWN with repeatCount=0. */
class TvOkPress(
    private val post: (Runnable, Long) -> Unit,
    private val remove: (Runnable) -> Unit,
    private val short: () -> Unit,
    private val long: () -> Unit
) {
    private var down = false
    val isPressed: Boolean get() = down
    private var fired = false
    private val timeout = Runnable {
        if (down && !fired) { fired = true; long() }
    }
    fun press() {
        if (down) return
        down = true; fired = false
        post(timeout, 600L)
    }
    fun release(canceled: Boolean) {
        val click = down && !fired && !canceled
        cancel()
        if (click) short()
    }
    fun cancel() { down = false; fired = false; remove(timeout) }
}
