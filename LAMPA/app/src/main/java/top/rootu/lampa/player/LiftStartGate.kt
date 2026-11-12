package top.rootu.lampa.player

import android.os.Handler
import android.os.Looper
import androidx.media3.common.C
import androidx.media3.common.Player
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.trackselection.DefaultTrackSelector

@UnstableApi
class LiftStartGate(
    private val player: Player,
    private val selector: DefaultTrackSelector,
    private val representationId: String,
    private val videoFormats: String,
    private val maxHeight: Int,
    private val ready: () -> Unit,
    private val failed: (String) -> Unit
) : Player.Listener {
    private enum class State { WAITING, APPLYING, READY, FAILED, CLOSED }
    private var state = State.WAITING
    private val ui = Handler(Looper.getMainLooper())
    private val timeout = Runnable { fail("Lift: не удалось подтвердить выбранную озвучку") }
    init {
        player.playWhenReady = false
        player.addListener(this)
        ui.postDelayed(timeout, 20_000)
    }
    fun canPlay(): Boolean = state == State.READY
    override fun onTracksChanged(tracks: Tracks) {
        if(state != State.WAITING && state != State.APPLYING) return
        if(tracks.groups.none { it.type == C.TRACK_TYPE_AUDIO } || tracks.groups.none { it.type == C.TRACK_TYPE_VIDEO }) return
        if(state == State.WAITING) {
            val video=LiftVp9Support.videoOverride(tracks,videoFormats,maxHeight)
                ?:return fail("Lift: нет аппаратного VP9 в пределах качества устройства")
            val override = LiftVp9Support.voiceOverride(tracks, representationId)
                ?: return fail("Lift: выбранная озвучка не поддерживается или неоднозначна")
            state = State.APPLYING // Set before changing parameters: callback may repeat.
            selector.parameters = selector.buildUponParameters()
                .clearOverridesOfType(C.TRACK_TYPE_AUDIO).setOverrideForType(override)
                .clearOverridesOfType(C.TRACK_TYPE_VIDEO).setOverrideForType(video).build()
        }
        val selected = player.currentTracks.groups.filter { it.type == C.TRACK_TYPE_AUDIO }
            .sumOf { group -> (0 until group.length).count { i ->
                group.isTrackSelected(i) && group.getTrackFormat(i).id == representationId
            } }
        val videoSelected=player.currentTracks.groups.filter { it.type==C.TRACK_TYPE_VIDEO }.any { group ->
            (0 until group.length).any { i -> group.isTrackSelected(i) && LiftVp9Support.allowed(group.getTrackFormat(i),videoFormats,maxHeight) }
        }
        if(selected == 1 && videoSelected) {
            state = State.READY
            ui.removeCallbacks(timeout)
            ready()
        }
    }
    private fun fail(message: String) {
        if(state == State.READY || state == State.FAILED || state == State.CLOSED) return
        state = State.FAILED
        ui.removeCallbacks(timeout)
        player.pause()
        failed(message)
    }
    fun close() {
        state = State.CLOSED
        ui.removeCallbacks(timeout)
        player.removeListener(this)
    }
}
