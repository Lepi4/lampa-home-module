package top.rootu.lampa.player

/** Device-wide choice, shared by profiles and both built-in players. */
data class AudioOutputPolicy(val passthrough: Boolean, val savedDrc: Int) {
    val effectiveDrc: Int get() = if (passthrough) 0 else savedDrc.coerceIn(0, 2)

    // The bundled mpv AudioTrack backend uses a stereo IEC61937 carrier. Keep HD
    // formats on its PCM path; ExoPlayer supports the full Android capability set.
    fun mpvCodecs(supported: Set<String>): String =
        if (passthrough) listOf("ac3", "dts").filter { it in supported }.joinToString(",") else ""
}
