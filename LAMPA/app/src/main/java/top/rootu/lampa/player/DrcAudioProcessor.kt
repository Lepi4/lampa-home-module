package top.rootu.lampa.player

import androidx.media3.common.C
import androidx.media3.common.audio.AudioProcessor
import androidx.media3.common.audio.BaseAudioProcessor
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.abs
import kotlin.math.roundToInt

/** Processes decoded PCM; encoded passthrough is rejected by the sink while DRC is enabled. */
class DrcAudioProcessor : BaseAudioProcessor() {
    @Volatile var mode: Int = 0
        set(value) { field = value.coerceIn(0, 2) }
    private val compressor = LinkedDrc()

    override fun onConfigure(format: AudioProcessor.AudioFormat): AudioProcessor.AudioFormat {
        if (format.encoding != C.ENCODING_PCM_16BIT) return AudioProcessor.AudioFormat.NOT_SET
        compressor.configure(format.sampleRate)
        android.util.Log.i("HomeAudio", "PCM drc=$mode rate=${format.sampleRate} channels=${format.channelCount}")
        DLog.log("i", "drc_pcm", mode.toString(), "rate=${format.sampleRate} channels=${format.channelCount}")
        return format
    }

    override fun onFlush() { compressor.reset() }

    override fun queueInput(input: ByteBuffer) {
        // The pipeline also polls processors with its shared empty buffer while draining output.
        if (!input.hasRemaining()) return
        val output = replaceOutputBuffer(input.remaining()).order(ByteOrder.nativeOrder())
        val currentMode = mode
        if (currentMode == 0) {
            output.put(input); output.flip(); return
        }
        input.order(ByteOrder.nativeOrder())
        val channels = inputAudioFormat.channelCount
        val frameBytes = channels * 2
        while (input.remaining() >= frameBytes) {
            var peak = 0.0
            for (c in 0 until channels) peak = maxOf(peak, abs(input.getShort(input.position() + c * 2).toInt()) / 32768.0)
            val gain = compressor.gain(peak, currentMode)
            for (c in 0 until channels) output.putShort((input.short * gain).roundToInt().coerceIn(-32768, 32767).toShort())
        }
        output.flip()
    }
}
