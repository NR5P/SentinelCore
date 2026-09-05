package com.sentinelcore.android.ui.components

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.hapticfeedback.HapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// The moment after Save, made unmissable.
//
// Every screen that saves in place used to report success as a small green "Saved." line placed
// somewhere in the list. On a long form that line sits below the fold, and once it has appeared it
// never goes away, so the second save looks identical to the first. Hence the recurring question
// of whether it actually saved.
//
// This gives those screens the same three signals the web dashboard's toast gives: the Save button
// itself turns into a green "Saved" tick for a few seconds, a banner slides up over the bottom of
// the screen - over the list, not inside it, so it shows whatever is scrolled into view - and the
// phone buzzes once. Failures get the same treatment in red, alongside whatever detail the screen
// shows inline.
data class SaveToast(val text: String, val isError: Boolean)

class SaveFeedbackState internal constructor(
    private val scope: CoroutineScope,
    private val haptics: HapticFeedback,
) {
    var toast by mutableStateOf<SaveToast?>(null)
        private set

    // True for a few seconds after a successful save; SaveButton reads it to show the tick.
    var justSaved by mutableStateOf(false)
        private set

    private var clearJob: Job? = null

    fun saved(message: String = "Saved") {
        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
        justSaved = true
        show(SaveToast(message, isError = false))
    }

    // Something that went right but wasn't a save - "Test email sent to you@example.com."
    fun note(message: String) {
        justSaved = false
        show(SaveToast(message, isError = false))
    }

    fun failed(message: String) {
        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
        justSaved = false
        show(SaveToast(message, isError = true))
    }

    private fun show(next: SaveToast) {
        toast = next
        clearJob?.cancel()
        clearJob = scope.launch {
            delay(HOLD_MILLIS)
            toast = null
            justSaved = false
        }
    }

    private companion object {
        const val HOLD_MILLIS = 3_000L
    }
}

@Composable
fun rememberSaveFeedback(): SaveFeedbackState {
    val scope = rememberCoroutineScope()
    val haptics = LocalHapticFeedback.current
    return remember { SaveFeedbackState(scope, haptics) }
}

// A screen root: the usual full-height Column, with the banner floating over its bottom edge.
// Drop-in for `Column(Modifier.fillMaxSize()) { ... }`.
@Composable
fun SaveFeedbackHost(
    feedback: SaveFeedbackState,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Box(modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize(), content = content)

        // The banner keeps showing the last message while it animates out, hence the copy.
        var shown by remember { mutableStateOf(feedback.toast) }
        feedback.toast?.let { shown = it }
        AnimatedVisibility(
            visible = feedback.toast != null,
            modifier = Modifier.align(Alignment.BottomCenter).padding(horizontal = 24.dp, vertical = 20.dp),
            enter = slideInVertically { it / 2 } + fadeIn(),
            exit = slideOutVertically { it / 2 } + fadeOut(),
        ) {
            shown?.let { SaveToastCard(it) }
        }
    }
}

@Composable
private fun SaveToastCard(toast: SaveToast) {
    val tint = if (toast.isError) SentinelRed else SentinelGreen
    val shape = RoundedCornerShape(12.dp)
    Surface(
        shape = shape,
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        tonalElevation = 6.dp,
        shadowElevation = 8.dp,
        modifier = Modifier.border(1.dp, tint.copy(alpha = 0.45f), shape),
    ) {
        Row(
            Modifier.padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                if (toast.isError) Icons.Filled.Warning else Icons.Filled.CheckCircle,
                contentDescription = null,
                tint = tint,
                modifier = Modifier.size(22.dp),
            )
            Spacer(Modifier.width(10.dp))
            Text(
                toast.text,
                color = tint,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.Medium,
                maxLines = 3,
            )
        }
    }
}

// "Save" → "Saving…" → green "✓ Saved" → "Save". The filled button for a form's own row.
@Composable
fun SaveButton(
    feedback: SaveFeedbackState,
    isSaving: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    Button(onClick = onClick, enabled = enabled && !isSaving, modifier = modifier) {
        SaveButtonLabel(isSaving, feedback.justSaved, onDark = true)
    }
}

// The same, as the text button a header row uses.
@Composable
fun SaveTextButton(
    feedback: SaveFeedbackState,
    isSaving: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    TextButton(onClick = onClick, enabled = enabled && !isSaving, modifier = modifier) {
        SaveButtonLabel(isSaving, feedback.justSaved, onDark = false)
    }
}

@Composable
private fun SaveButtonLabel(isSaving: Boolean, justSaved: Boolean, onDark: Boolean) {
    when {
        isSaving -> {
            CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
            Spacer(Modifier.width(8.dp))
            Text("Saving…")
        }
        justSaved -> {
            // A filled button already sits on the accent colour, so the tick inherits its text
            // colour there; a text button gets the green itself.
            val tint = if (onDark) MaterialTheme.colorScheme.onPrimary else SentinelGreen
            Icon(Icons.Filled.CheckCircle, contentDescription = null, tint = tint, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(6.dp))
            Text("Saved", color = tint, fontWeight = FontWeight.SemiBold)
        }
        else -> Text("Save")
    }
}
