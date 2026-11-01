package top.rootu.lampa.player

import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.sin
import kotlin.math.sqrt

/** Standalone JVM checks; no Android device or account state is used. */
object PlaybackPolicyChecks {
    @JvmStatic fun main(args: Array<String>) {
        var count = 0
        fun verify(name: String, ok: Boolean) { check(ok) { name }; count++; println("PASS $name") }
        val modes = listOf(24f, 24000f / 1001f, 25f, 30f, 30000f / 1001f, 50f, 60f, 60000f / 1001f)
            .mapIndexed { i, hz -> FrameRatePolicy.Mode(i + 1, 1920, 1080, hz) }
        val fifty = modes.first { it.hz == 50f }
        fun hz(fps: Float, live: Boolean = true) = FrameRatePolicy.choose(fps, live, fifty, modes)?.hz
        verify("fractional 23.976 selects its exact output mode", hz(24000f / 1001f) == 24000f / 1001f)
        verify("integer 24 selects a different mode", hz(24f) == 24f)
        verify("live 25 keeps 50 Hz", hz(25f) == 50f)
        verify("VOD 25 may keep an accurate existing 50 Hz multiple", hz(25f, false) == 50f)
        verify("59.94 is preserved", hz(60000f / 1001f) == 60000f / 1001f)
        verify("invalid fps makes no selection", FrameRatePolicy.choose(Float.NaN, true, fifty, modes) == null)
        verify("resolution never changes for AFR", FrameRatePolicy.choose(24f, true, fifty, listOf(FrameRatePolicy.Mode(99, 3840, 2160, 24f))) == null)
        verify("unsupported cadence has no forced incompatible mode", hz(43f) == null)
        verify("fractional display preferred over near integer current", FrameRatePolicy.choose(24000f / 1001f, true, modes[0], modes)?.id == 2)
        for (fps in listOf(24000.0 / 1001.0, 24.0, 25.0, 30000.0 / 1001.0, 50.0, 60000.0 / 1001.0)) {
            val estimator = PtsFrameRateEstimator(); var estimate: Float? = null
            for (i in 0..160) estimator.frame((i * 1_000_000.0 / fps).toLong(), 1)?.let { estimate = it }
            verify("PTS estimator distinguishes $fps", estimate != null && abs(estimate!! - fps) < 0.005)
        }
        val variable = PtsFrameRateEstimator(); var found = false; var pts = 0L
        for (i in 0..200) { pts += if (i % 2 == 0) 40000 else 33333; if (variable.frame(pts, 1) != null) found = true }
        verify("variable cadence is not misclassified as stable", !found)
        for (fps in listOf(24000.0 / 1001.0, 24.0)) {
            val quantized=PtsFrameRateEstimator();var estimate:Float?=null
            for(i in 0..160) quantized.frame((i*1000.0/fps).toLong()*1000L,1)?.let{estimate=it}
            verify("millisecond IPTV timestamps still distinguish $fps",estimate!=null&&abs(estimate!!-fps)<0.005)
        }
        val skipped=PtsFrameRateEstimator();var skippedPts=0L;var inferred:Float?=null
        for(i in 0..160){skippedPts+=when(i%3){0->20000;1->40000;else->60000};skipped.frame(skippedPts,1)?.let{inferred=it}}
        verify("50 fps cadence can be recovered while a 24 Hz output skips frames",inferred==50f)
        val epoch = PtsFrameRateEstimator()
        for (i in 0..80) epoch.frame(i * 40000L, 1)
        verify("new item does not inherit preceding samples", epoch.frame(0, 2) == null)
        val drc = LinkedDrc(); drc.configure(48000)
        verify("disabled DRC is exact unity", (0..200).all { drc.gain(it / 200.0, 0) == 1f })
        fun rms(amplitude: Double, mode: Int): Double {
            val processor = LinkedDrc(); processor.configure(48000); var sum = 0.0; var n = 0
            for (i in 0 until 96000) {
                val sample = amplitude * sin(i * 2 * Math.PI * 440 / 48000)
                val output = sample * processor.gain(abs(sample), mode)
                if (i > 24000) { sum += output * output; n++ }
            }
            return sqrt(sum / n)
        }
        val normalRange = 20 * log10(rms(0.8, 0) / rms(0.03, 0))
        val mildRange = 20 * log10(rms(0.8, 1) / rms(0.03, 1))
        val nightRange = 20 * log10(rms(0.8, 2) / rms(0.03, 2))
        verify("mild DRC reduces loud/quiet contrast", mildRange < normalRange - 5)
        verify("night DRC compresses more than mild", nightRange < mildRange - 2)
        for (mode in 1..2) {
            drc.configure(48000); var maximum = 0.0
            for (i in 0..100000) {
                val peak = if (i % 4800 == 0) 1.0 else 0.04
                val gain = drc.gain(peak, mode)
                maximum = maxOf(maximum, peak * gain)
                check(gain.isFinite())
            }
            verify("limiter contains first and later peaks mode=$mode", maximum <= 0.891252)
        }
        drc.configure(48000)
        verify("silence stays finite", (0..10000).all { drc.gain(0.0, 2).isFinite() })
        println("checks=$count normal_range_db=$normalRange mild_range_db=$mildRange night_range_db=$nightRange")
    }
}
