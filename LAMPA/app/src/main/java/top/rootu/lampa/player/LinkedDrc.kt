package top.rootu.lampa.player

import kotlin.math.exp
import kotlin.math.log10
import kotlin.math.pow

/** Linked-channel compressor and peak limiter. State is confined to the audio thread. */
class LinkedDrc {
    private var envelope = 0.0
    private var gain = 1.0
    private var desired = 1.0
    private var limiter = 1.0
    private var attack = 0.0
    private var release = 0.0
    private var gainAttack = 0.0
    private var gainRelease = 0.0
    private var limiterRelease = 0.0
    private var ticks = 0

    fun configure(sampleRate: Int) {
        fun alpha(ms: Double) = exp(-1.0 / (sampleRate.coerceAtLeast(1) * ms / 1000.0))
        attack = alpha(10.0); release = alpha(180.0)
        gainAttack = alpha(5.0); gainRelease = alpha(120.0); limiterRelease = alpha(70.0)
        reset()
    }

    fun reset() { envelope = 0.0; gain = 1.0; desired = 1.0; limiter = 1.0; ticks = 0 }

    fun gain(peak: Double, mode: Int): Float {
        if (mode == 0) return 1f
        val coeff = if (peak > envelope) attack else release
        envelope = coeff * envelope + (1 - coeff) * peak
        if (ticks++ % 16 == 0) {
            val threshold = if (mode == 1) -18.0 else -24.0
            val ratio = if (mode == 1) 2.0 else 3.0
            val makeup = if (mode == 1) 2.0 else 4.0
            val over = 20.0 * log10(envelope.coerceAtLeast(1e-9)) - threshold
            val compression = when {
                over <= -3.0 -> 0.0
                over < 3.0 -> (1.0 / ratio - 1.0) * (over + 3.0).pow(2) / 12.0
                else -> (1.0 / ratio - 1.0) * over
            }
            desired = 10.0.pow((compression + makeup) / 20.0)
        }
        val gCoeff = if (desired < gain) gainAttack else gainRelease
        gain = gCoeff * gain + (1.0 - gCoeff) * desired
        val cap = if (peak > 0.0) minOf(1.0, 0.89125094 / (peak * gain)) else 1.0
        limiter = if (cap < limiter) cap else minOf(cap, limiterRelease * limiter + 1.0 - limiterRelease)
        return (gain * limiter).toFloat()
    }
}
