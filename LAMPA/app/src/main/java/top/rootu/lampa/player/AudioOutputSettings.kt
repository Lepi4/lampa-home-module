package top.rootu.lampa.player

import android.content.Context
import android.os.Build
import androidx.media3.common.C
import androidx.media3.exoplayer.audio.AudioCapabilities

object AudioOutputSettings {
    fun read(context: Context): AudioOutputPolicy {
        val prefs = context.getSharedPreferences("home_player", Context.MODE_PRIVATE)
        return AudioOutputPolicy(prefs.getBoolean("audio_passthrough", false), prefs.getInt("drc", 0))
    }

    fun mpvCodecs(context: Context, policy: AudioOutputPolicy): String {
        if (!policy.passthrough || Build.VERSION.SDK_INT < 24) return ""
        return try {
            val capabilities = AudioCapabilities.getCapabilities(context)
            val supported = mutableSetOf<String>()
            if (capabilities.supportsEncoding(C.ENCODING_AC3)) supported.add("ac3")
            if (capabilities.supportsEncoding(C.ENCODING_DTS)) supported.add("dts")
            policy.mpvCodecs(supported)
        } catch (_: Exception) { "" }
    }
}
