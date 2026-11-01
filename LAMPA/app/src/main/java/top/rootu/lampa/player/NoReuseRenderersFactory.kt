package top.rootu.lampa.player

import android.content.Context
import android.os.Handler
import androidx.media3.common.Format
import androidx.media3.common.MimeTypes
import androidx.media3.exoplayer.audio.AudioOffloadSupport
import androidx.media3.exoplayer.audio.AudioSink
import androidx.media3.exoplayer.audio.DefaultAudioSink
import androidx.media3.exoplayer.audio.ForwardingAudioSink
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

    override fun buildAudioSink(context: Context, enableFloatOutput: Boolean, enableAudioTrackPlaybackParams: Boolean): AudioSink {
        val processor = drc ?: return super.buildAudioSink(context, enableFloatOutput, enableAudioTrackPlaybackParams)!!
        val sink = DefaultAudioSink.Builder(context)
            .setEnableFloatOutput(false)
            .setEnableAudioTrackPlaybackParams(enableAudioTrackPlaybackParams)
            .setAudioProcessors(arrayOf(processor))
            .build()
        return object : ForwardingAudioSink(sink) {
            override fun getFormatSupport(format: Format): Int =
                if (processor.mode != 0 && format.sampleMimeType != MimeTypes.AUDIO_RAW) AudioSink.SINK_FORMAT_UNSUPPORTED
                else super.getFormatSupport(format)

            override fun supportsFormat(format: Format): Boolean = getFormatSupport(format) != AudioSink.SINK_FORMAT_UNSUPPORTED
            override fun getFormatOffloadSupport(format: Format): AudioOffloadSupport =
                if (processor.mode != 0) AudioOffloadSupport.DEFAULT_UNSUPPORTED else super.getFormatOffloadSupport(format)

            override fun setOffloadMode(offloadMode: Int) =
                super.setOffloadMode(if (processor.mode != 0) AudioSink.OFFLOAD_MODE_DISABLED else offloadMode)
        }
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
                context, codecAdapterFactory, mediaCodecSelector, allowedVideoJoiningTimeMs,
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
