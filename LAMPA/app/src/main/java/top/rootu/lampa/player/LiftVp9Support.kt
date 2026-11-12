package top.rootu.lampa.player

import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.mediacodec.MediaCodecUtil
import androidx.media3.exoplayer.mediacodec.MediaCodecSelector
import androidx.media3.exoplayer.trackselection.DefaultTrackSelector
import org.json.JSONArray

@UnstableApi
object LiftVp9Support {
    fun allowed(format: Format, descriptor: String, maxHeight: Int = 0): Boolean {
        if(maxHeight>0 && format.width>maxHeight*16L/9+8)return false
        val ids=try { val a=JSONArray(descriptor);(0 until a.length()).mapNotNull { a.optJSONObject(it)?.optString("id") }.toSet() } catch(_:Exception){emptySet()}
        return format.id in ids && hardwareDecoderSupports(format.codecs.orEmpty(),format.width,format.height,format.frameRate)
    }
    fun videoOverride(tracks: Tracks, descriptor: String, maxHeight: Int = 0): TrackSelectionOverride? {
        return tracks.groups.filter { it.type==C.TRACK_TYPE_VIDEO }.mapNotNull { group ->
            val indices=(0 until group.length).filter { group.isTrackSupported(it) && allowed(group.getTrackFormat(it),descriptor,maxHeight) }
            if(indices.isEmpty())null else TrackSelectionOverride(group.mediaTrackGroup,indices)
        }.maxByOrNull { o -> o.trackIndices.maxOf { o.mediaTrackGroup.getFormat(it).width } }
    }
    fun hardwareVp9Selector(): MediaCodecSelector = MediaCodecSelector { mime, secure, tunnel ->
        if(mime.startsWith("video/") && mime!=MimeTypes.VIDEO_VP9) emptyList()
        else MediaCodecSelector.DEFAULT.getDecoderInfos(mime,secure,tunnel)
            .filter { mime!=MimeTypes.VIDEO_VP9 || it.hardwareAccelerated }
    }

    fun hardwareDecoderSupports(
        codecs: String, width: Int, height: Int, frameRate: Float? = null
    ): Boolean {
        if(!codecs.startsWith("vp09",true) && !codecs.startsWith("vp9",true))return false
        if(width<=0 || height<=0)return false
        val format=Format.Builder().setSampleMimeType(MimeTypes.VIDEO_VP9)
            .setCodecs(codecs).setWidth(width).setHeight(height)
            .apply { if(frameRate!=null && frameRate>0)setFrameRate(frameRate) }.build()
        return try {
            MediaCodecUtil.getDecoderInfos(MimeTypes.VIDEO_VP9,false,false)
                .any { it.hardwareAccelerated && it.isFormatSupported(format) }
        } catch (_: Exception) { false }
    }

    fun mediaItem(url: String): MediaItem = MediaItem.Builder()
        .setUri(url).setMimeType(MimeTypes.APPLICATION_MPD).build()

    fun voiceOverride(tracks: Tracks, representationId: String): TrackSelectionOverride? {
        val hits=tracks.groups.filter{it.type==C.TRACK_TYPE_AUDIO}.flatMap { group ->
            (0 until group.length).filter { index ->
                group.isTrackSupported(index) && group.getTrackFormat(index).id==representationId
            }.map { index -> TrackSelectionOverride(group.mediaTrackGroup,listOf(index)) }
        }
        return hits.singleOrNull() // Never guess another voice on a missing/ambiguous ID.
    }

    fun applyVoice(selector: DefaultTrackSelector, tracks: Tracks, representationId: String): Boolean {
        val override=voiceOverride(tracks,representationId)?:return false
        selector.parameters=selector.buildUponParameters()
            .clearOverridesOfType(C.TRACK_TYPE_AUDIO).setOverrideForType(override).build()
        return true
    }
}
