package com.sentinelcore.android.audio

import android.content.Context
import android.media.AudioAttributes
import android.os.Bundle
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import java.util.Locale
import java.util.UUID
import kotlin.coroutines.resume

// The phone's own speech voice, for speaker announcements and the Morning Briefing. Speaking is
// a suspend call that returns once the words have been said, so a caller can duck the music
// around it and an announce finishes before the next speaker command runs (§8).
class SpeechVoice(context: Context) {
    private val appContext = context.applicationContext
    private var tts: TextToSpeech? = null
    private var ready: CompletableDeferred<Boolean>? = null
    private val pending = mutableMapOf<String, (Boolean) -> Unit>()

    private suspend fun engine(): TextToSpeech? {
        val existing = tts
        if (existing != null && ready?.isCompleted == true && ready?.getCompleted() == true) return existing
        val waiting = ready ?: CompletableDeferred<Boolean>().also { deferred ->
            ready = deferred
            tts = TextToSpeech(appContext) { status -> deferred.complete(status == TextToSpeech.SUCCESS) }.apply {
                setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_ASSISTANCE_NAVIGATION_GUIDANCE)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                        .build(),
                )
                setOnUtteranceProgressListener(object : UtteranceProgressListener() {
                    override fun onStart(utteranceId: String?) = Unit
                    override fun onDone(utteranceId: String?) = finish(utteranceId, true)
                    @Deprecated("Deprecated in Java")
                    override fun onError(utteranceId: String?) = finish(utteranceId, false)
                    override fun onError(utteranceId: String?, errorCode: Int) = finish(utteranceId, false)
                    override fun onStop(utteranceId: String?, interrupted: Boolean) = finish(utteranceId, false)
                })
            }
        }
        // A cold Google TTS engine loads its voice packs before onInit fires; on a busy phone that
        // took ~5 s and a 5 s cap dropped the first announcement (shutdown, then nothing said).
        val ok = withTimeoutOrNull(15_000) { waiting.await() } ?: false
        if (!ok) {
            ready = null
            tts?.shutdown()
            tts = null
            return null
        }
        tts?.language = Locale.getDefault()
        return tts
    }

    private fun finish(id: String?, done: Boolean) {
        val callback = synchronized(pending) { id?.let { pending.remove(it) } } ?: return
        callback(done)
    }

    // Says it and returns when it has been said (or after a generous timeout). False when the
    // phone has no working voice.
    suspend fun speak(text: String): Boolean {
        if (text.isBlank()) return true
        val voice = engine() ?: return false
        val id = UUID.randomUUID().toString()
        val timeout = 5_000L + text.length * 120L
        return withTimeoutOrNull(timeout) {
            suspendCancellableCoroutine { continuation ->
                synchronized(pending) { pending[id] = { done -> if (continuation.isActive) continuation.resume(done) } }
                continuation.invokeOnCancellation {
                    synchronized(pending) { pending.remove(id) }
                    voice.stop()
                }
                val params = Bundle()
                if (voice.speak(text, TextToSpeech.QUEUE_ADD, params, id) != TextToSpeech.SUCCESS) {
                    synchronized(pending) { pending.remove(id) }
                    if (continuation.isActive) continuation.resume(false)
                }
            }
        } ?: false
    }

    fun stop() {
        tts?.stop()
    }
}
