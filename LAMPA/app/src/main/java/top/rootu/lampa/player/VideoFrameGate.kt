package top.rootu.lampa.player

/**
 * Binds the "first frame" state to the current channel/item and output
 * generation. A late callback from an older item or surface must not hide the
 * cover of the new one, and an AFR change must not invalidate video readiness.
 */
class VideoFrameGate {
    private var epoch = 0
    private var itemKey: String? = null
    private var outputKey: String? = null
    private var firstFrameEpoch = -1
    private var renderedBase = 0L

    val generation: Int get() = epoch

    fun onItem(item: String?, output: String? = null, rendered: Long = 0L): Int {
        epoch++
        itemKey = item
        outputKey = output
        firstFrameEpoch = -1
        renderedBase = rendered
        return epoch
    }

    fun onOutput(output: String?, rendered: Long = 0L): Int {
        epoch++
        outputKey = output
        firstFrameEpoch = -1
        renderedBase = rendered
        return epoch
    }

    /**
     * Accepts a frame only for the current item and, when both sides know the
     * output, the current output. A null output is a wildcard used while the
     * player has not reported a period/surface yet.
     */
    fun onFrame(item: String?, output: String? = null): Boolean {
        if (item != itemKey) return false
        if (output != null && outputKey != null && output != outputKey) return false
        if (output != null && outputKey == null) outputKey = output
        firstFrameEpoch = epoch
        return true
    }

    fun hasFrame(): Boolean = firstFrameEpoch == epoch

    fun renderedBase(): Long = renderedBase

    /** Recovery path: only after the caller proved frames of this generation. */
    fun markFrame() {
        firstFrameEpoch = epoch
    }
}
