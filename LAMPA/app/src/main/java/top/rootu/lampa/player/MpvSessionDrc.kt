package top.rootu.lampa.player

import android.content.Context
import android.media.AudioManager
import android.media.audiofx.DynamicsProcessing
import android.os.Build
import android.util.Log
import android.widget.Toast

/** A private AudioTrack session only: never attach an effect to the global mix (session 0). */
class MpvSessionDrc private constructor(val sessionId: Int, private val effect: DynamicsProcessing) {
    val enabled: Boolean get() = effect.enabled
    fun release() { try { effect.release() } catch (_: Exception) {} }

    companion object {
        fun create(context: Context, mode: Int = AudioOutputSettings.read(context).effectiveDrc): MpvSessionDrc? {
            if (mode == 0) { Log.i("HomeAudio", "mpv DRC off"); return null }
            var effect: DynamicsProcessing? = null
            try {
                check(Build.VERSION.SDK_INT >= 28) { "Android < 9" }
                val session = (context.getSystemService(Context.AUDIO_SERVICE) as AudioManager).generateAudioSessionId()
                check(session > 0) { "Invalid audio session" }
                val night = mode == 2
                val mbc = DynamicsProcessing.Mbc(true, true, 1)
                mbc.setBand(0, DynamicsProcessing.MbcBand(true, 22000f, 10f, 180f,
                    if (night) 3f else 2f, if (night) -24f else -18f, 6f, -90f, 1f, 0f, if (night) 4f else 2f))
                val limiter = DynamicsProcessing.Limiter(true, true, 0, 1f, 70f, 100f, -1f, 0f)
                val config = DynamicsProcessing.Config.Builder(DynamicsProcessing.VARIANT_FAVOR_FREQUENCY_RESOLUTION,
                    2, false, 0, true, 1, false, 0, true)
                    .setMbcAllChannelsTo(mbc).setLimiterAllChannelsTo(limiter).build()
                effect = DynamicsProcessing(0, session, config)
                effect.enabled = true
                check(effect.enabled) { "Effect not enabled" }
                Log.i("HomeAudio", "mpv DRC mode=$mode session=$session threshold=${effect.getMbcBandByChannelIndex(0, 0).threshold}")
                return MpvSessionDrc(session, effect)
            } catch (e: Exception) {
                try { effect?.release() } catch (_: Exception) {}
                Log.e("HomeAudio", "mpv DRC unavailable", e)
                Toast.makeText(context, "DRC недоступна в mpv на этом устройстве", Toast.LENGTH_LONG).show()
                return null
            }
        }
    }
}
