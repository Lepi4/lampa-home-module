package top.rootu.lampa.player

import android.content.Context
import android.os.Handler
import androidx.media3.common.Format
import androidx.media3.common.MimeTypes
import androidx.media3.exoplayer.audio.AudioOffloadSupport
import androidx.media3.exoplayer.audio.AudioRendererEventListener
import androidx.media3.exoplayer.audio.AudioSink
import androidx.media3.exoplayer.audio.DefaultAudioSink
import androidx.media3.exoplayer.audio.ForwardingAudioSink
import androidx.media3.exoplayer.audio.AudioCapabilities
import androidx.media3.decoder.ffmpeg.FfmpegAudioRenderer
import androidx.media3.exoplayer.DecoderReuseEvaluation
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.Renderer
import androidx.media3.exoplayer.mediacodec.MediaCodecAdapter
import androidx.media3.exoplayer.mediacodec.MediaCodecInfo
import androidx.media3.exoplayer.mediacodec.MediaCodecSelector
import androidx.media3.exoplayer.video.MediaCodecVideoRenderer
import androidx.media3.exoplayer.video.VideoRendererEventListener

/**
 * Видео-рендерер, который не переиспользует аппаратный декодер при смене потока.
 *
 * Декодер Amlogic (OMX.amlogic.avc.decoder.awesome2) после переключения канала внутри одного плеера работает с
 * испорченным состоянием: картинка «сыплется» (зелёный мусор), кадры идут рывками, после сна залипает на последнем кадре.
 * Если декодер при смене канала создаётся заново — всё чисто. Цена: ~0,2–0,4 с на переключение.
 */
class NoReuseRenderersFactory(context: Context, private val drc: DrcAudioProcessor? = null) : DefaultRenderersFactory(context) {

    @Volatile var allowPassthrough = false
    @Volatile var videoSelectorOverride: MediaCodecSelector? = null
    @Volatile var lastAudioOutputMime: String? = null
    private var activeAudioSink: DefaultAudioSink? = null

    // Called through a PlayerMessage on the playback looper. A recoverable
    // AudioTrack write error resets this sink to PCM-only, even if the separate
    // capabilities receiver still remembers a valid HDMI route.
    fun refreshAudioCapabilities(context: Context) {
        if (!allowPassthrough) return
        val capabilities = AudioCapabilities.getCapabilities(context)
        activeAudioSink?.onAudioCapabilitiesChanged(capabilities)
        android.util.Log.i("HomeAudio", "exo refreshed HDMI capabilities $capabilities")
    }

    override fun buildAudioSink(context: Context, enableFloatOutput: Boolean, enableAudioTrackPlaybackParams: Boolean): AudioSink {
        val processor = drc ?: return super.buildAudioSink(context, enableFloatOutput, enableAudioTrackPlaybackParams)!!
        val sink = DefaultAudioSink.Builder(context)
            .setEnableFloatOutput(false)
            .setEnableAudioTrackPlaybackParams(enableAudioTrackPlaybackParams)
            .setAudioProcessors(arrayOf(processor))
            .build()
        activeAudioSink = sink
        return object : ForwardingAudioSink(sink) {
            override fun getFormatSupport(format: Format): Int =
                if ((!allowPassthrough || processor.mode != 0) && format.sampleMimeType != MimeTypes.AUDIO_RAW) AudioSink.SINK_FORMAT_UNSUPPORTED
                else super.getFormatSupport(format)

            override fun supportsFormat(format: Format): Boolean = getFormatSupport(format) != AudioSink.SINK_FORMAT_UNSUPPORTED
            override fun getFormatOffloadSupport(format: Format): AudioOffloadSupport =
                if (!allowPassthrough || processor.mode != 0) AudioOffloadSupport.DEFAULT_UNSUPPORTED else super.getFormatOffloadSupport(format)

            override fun setOffloadMode(offloadMode: Int) =
                super.setOffloadMode(if (!allowPassthrough || processor.mode != 0) AudioSink.OFFLOAD_MODE_DISABLED else offloadMode)

            override fun configure(inputFormat: Format, specifiedBufferSize: Int, outputChannels: IntArray?) {
                lastAudioOutputMime = inputFormat.sampleMimeType
                android.util.Log.i("HomeAudio", "exo_output mime=${inputFormat.sampleMimeType} passthrough=${inputFormat.sampleMimeType != MimeTypes.AUDIO_RAW} drc=${processor.mode} channels=${inputFormat.channelCount}")
                super.configure(inputFormat, specifiedBufferSize, outputChannels)
            }
        }
    }

    override fun buildAudioRenderers(
        context: Context,
        extensionRendererMode: Int,
        mediaCodecSelector: MediaCodecSelector,
        enableDecoderFallback: Boolean,
        audioSink: AudioSink,
        eventHandler: Handler,
        eventListener: AudioRendererEventListener,
        out: ArrayList<Renderer>
    ) {
        super.buildAudioRenderers(
            context, extensionRendererMode, mediaCodecSelector, enableDecoderFallback,
            audioSink, eventHandler, eventListener, out
        )
        if (allowPassthrough) return
        // E-AC3/AC3 без проброса: программный FFmpeg декодирует в PCM в тот же
        // DRC-совместимый sink. Ставим его первым, чтобы MediaCodec приставки
        // не выбирал «поддерживаемый» E-AC3, который затем молчит.
        val softwareSink = buildAudioSink(context, false, false)
        val existing = out.indexOfFirst { it is FfmpegAudioRenderer }
        if (existing >= 0) {
            if (existing != 0) out.add(0, out.removeAt(existing))
        } else {
            out.add(0, FfmpegAudioRenderer(eventHandler, eventListener, softwareSink))
        }
        android.util.Log.i("HomeAudio", "exo_software_eac3 renderer=ffmpeg passthrough=false")
    }

    override fun buildVideoRenderers(
        context: Context,
        extensionRendererMode: Int,
        mediaCodecSelector: MediaCodecSelector,
        enableDecoderFallback: Boolean,
        eventHandler: Handler,
        eventListener: VideoRendererEventListener,
        allowedVideoJoiningTimeMs: Long,
        out: ArrayList<Renderer>
    ) {
        out.add(
            NoReuseVideoRenderer(
                context, codecAdapterFactory, videoSelectorOverride ?: mediaCodecSelector, allowedVideoJoiningTimeMs,
                enableDecoderFallback, eventHandler, eventListener, 50
            )
        )
    }
}

class NoReuseVideoRenderer(
    context: Context,
    adapterFactory: MediaCodecAdapter.Factory,
    selector: MediaCodecSelector,
    joiningMs: Long,
    fallback: Boolean,
    handler: Handler?,
    listener: VideoRendererEventListener?,
    maxDropped: Int
) : MediaCodecVideoRenderer(context, adapterFactory, selector, joiningMs, fallback, handler, listener, maxDropped) {

    override fun canReuseCodec(codecInfo: MediaCodecInfo, oldFormat: Format, newFormat: Format): DecoderReuseEvaluation =
        if (codecInfo.name.contains("amlogic", ignoreCase = true))
            DecoderReuseEvaluation(codecInfo.name, oldFormat, newFormat, DecoderReuseEvaluation.REUSE_RESULT_NO, 0)
        else super.canReuseCodec(codecInfo, oldFormat, newFormat)
}
