package top.rootu.lampa.player

import org.junit.Assert.assertEquals
import org.junit.Test

class AudioOutputPolicyTest {
    @Test fun switchingPassthroughKeepsAndRestoresEveryDrcMode() {
        for (mode in 0..2) {
            val original = AudioOutputPolicy(false, mode)
            val enabled = original.copy(passthrough = true)
            assertEquals(0, enabled.effectiveDrc)
            assertEquals(mode, enabled.savedDrc)
            assertEquals(mode, enabled.copy(passthrough = false).effectiveDrc)
        }
    }

    @Test fun invalidDrcCannotReachTheProcessor() {
        assertEquals(0, AudioOutputPolicy(false, -1).effectiveDrc)
        assertEquals(2, AudioOutputPolicy(false, 9).effectiveDrc)
        assertEquals(0, AudioOutputPolicy(true, 9).effectiveDrc)
    }

    @Test fun disabledPassthroughNeverEnablesMpvBitstream() {
        assertEquals("", AudioOutputPolicy(false, 0).mpvCodecs(setOf("ac3", "dts")))
    }

    @Test fun unsupportedOutputStaysPcmEvenWhenEnabled() {
        assertEquals("", AudioOutputPolicy(true, 2).mpvCodecs(emptySet()))
    }

    @Test fun onlySupportedCarrierFormatsReachBundledMpv() {
        val policy = AudioOutputPolicy(true, 0)
        assertEquals("ac3", policy.mpvCodecs(setOf("ac3")))
        assertEquals("dts", policy.mpvCodecs(setOf("dts")))
        assertEquals("ac3,dts", policy.mpvCodecs(setOf("dts", "ac3", "eac3", "dts-hd", "truehd")))
        assertEquals("", policy.mpvCodecs(setOf("aac", "eac3", "dts-hd", "truehd")))
    }
}
