package com.sentinelcore.android.audio

import android.media.audiofx.BassBoost
import android.media.audiofx.Equalizer
import android.util.Log

// The Equalizer screen's engine: android.media.audiofx on the player's audio session. Presets come
// from the phone itself (they differ between makers); "Custom" is the band levels the person set,
// saved in AudioPrefs and put back each time the session is attached.
class AudioEffects(private val runtime: AudioRuntime) {
    private var equalizer: Equalizer? = null
    private var bass: BassBoost? = null
    private var sessionId: Int = 0

    data class Band(val index: Int, val centerHz: Int, val level: Int)

    data class Info(
        val available: Boolean,
        val presets: List<String>,
        val bands: List<Band>,
        val minLevel: Int,
        val maxLevel: Int,
        val bassSupported: Boolean,
    )

    fun attach(audioSessionId: Int) {
        if (audioSessionId <= 0 || audioSessionId == sessionId) return
        release()
        sessionId = audioSessionId
        apply()
    }

    // Creates the effects when the Equalizer is on, applies the saved settings, and releases them
    // when it's off (an idle Equalizer still costs a little on some phones).
    fun apply() {
        val prefs = runtime.prefs
        if (!prefs.equalizerEnabled) {
            equalizer?.enabled = false
            bass?.enabled = false
            return
        }
        val eq = equalizer ?: runCatching { Equalizer(0, sessionId) }.onFailure {
            Log.w(AudioApi.TAG, "No equalizer on this session: ${it.message}")
        }.getOrNull()?.also { equalizer = it } ?: return
        runCatching {
            val preset = prefs.equalizerPreset
            if (preset in 0 until eq.numberOfPresets) {
                eq.usePreset(preset.toShort())
            } else {
                val levels = prefs.equalizerBands
                for (band in 0 until eq.numberOfBands) {
                    levels.getOrNull(band)?.let { eq.setBandLevel(band.toShort(), it.toShort()) }
                }
            }
            eq.enabled = true
        }
        val boost = bass ?: runCatching { BassBoost(0, sessionId) }.getOrNull()?.also { bass = it }
        runCatching {
            if (boost != null && boost.strengthSupported) {
                boost.setStrength(prefs.bassBoost.coerceIn(0, 1000).toShort())
                boost.enabled = prefs.bassBoost > 0
            }
        }
    }

    fun info(): Info {
        val eq = equalizer ?: runCatching { Equalizer(0, sessionId.coerceAtLeast(0)) }.getOrNull()?.also {
            if (runtime.prefs.equalizerEnabled) equalizer = it
        }
        if (eq == null) return Info(false, emptyList(), emptyList(), -1500, 1500, false)
        return runCatching {
            val range = eq.bandLevelRange
            val saved = runtime.prefs.equalizerBands
            val preset = runtime.prefs.equalizerPreset
            if (preset >= 0 && preset < eq.numberOfPresets) eq.usePreset(preset.toShort())
            val bands = (0 until eq.numberOfBands).map { band ->
                val level = if (preset >= 0) eq.getBandLevel(band.toShort()).toInt() else saved.getOrNull(band) ?: 0
                Band(band, eq.getCenterFreq(band.toShort()) / 1000, level)
            }
            val presets = (0 until eq.numberOfPresets).map { eq.getPresetName(it.toShort()) }
            val bassOk = (bass ?: runCatching { BassBoost(0, sessionId) }.getOrNull())?.strengthSupported == true
            Info(true, presets, bands, range[0].toInt(), range[1].toInt(), bassOk)
        }.getOrElse { Info(false, emptyList(), emptyList(), -1500, 1500, false) }
            .also { if (equalizer !== eq) eq.release() }
    }

    fun setEnabled(enabled: Boolean) {
        runtime.prefs.equalizerEnabled = enabled
        apply()
    }

    fun usePreset(preset: Int) {
        runtime.prefs.equalizerPreset = preset
        apply()
    }

    fun setBand(band: Int, level: Int, count: Int) {
        val levels = runtime.prefs.equalizerBands.toMutableList()
        while (levels.size < count) levels.add(0)
        levels[band] = level
        runtime.prefs.equalizerBands = levels
        runtime.prefs.equalizerPreset = -1
        apply()
    }

    fun setBassBoost(strength: Int) {
        runtime.prefs.bassBoost = strength
        apply()
    }

    fun reset() {
        runtime.prefs.equalizerBands = emptyList()
        runtime.prefs.equalizerPreset = -1
        runtime.prefs.bassBoost = 0
        equalizer?.let { eq -> runCatching { for (band in 0 until eq.numberOfBands) eq.setBandLevel(band.toShort(), 0) } }
        apply()
    }

    private fun release() {
        runCatching { equalizer?.release() }
        runCatching { bass?.release() }
        equalizer = null
        bass = null
    }
}
