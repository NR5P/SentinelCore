package com.sentinelcore.android.ui.organizer

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.speech.RecognizerIntent
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Notes
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sentinelcore.android.MainActivity
import com.sentinelcore.android.organizer.OrganizerDevice
import com.sentinelcore.android.organizer.OrganizerRuntime
import com.sentinelcore.android.organizer.QuickCapture
import com.sentinelcore.android.organizer.VoiceCapture
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelTheme
import com.sentinelcore.android.widgets.OrganizerBackgroundSync
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

// Quick Add over the home screen, opened by the Quick Add widget (widgets/QuickAddWidget): one
// line, said or typed, that becomes a reminder, a note or an event - "add a reminder to take out
// the trash at 7am tomorrow". QuickCapture decides which kind and when; the person sees the result
// before it's added, and can switch the kind if the words didn't say.
//
// It writes straight into the process's one Organizer (OrganizerRuntime), so the full app never
// opens. The item is saved on this phone at once, alert and all, and a background sync sends it.
class QuickCaptureActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Only on a fresh start: a rotation must not start listening again.
        show(intent, fresh = savedInstanceState == null)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        // Another tap on the widget while this is open: start over with that button.
        setIntent(intent)
        show(intent, fresh = true)
    }

    private fun show(intent: Intent?, fresh: Boolean) {
        val address = intent?.data
        val listen = address?.getQueryParameter("mode") == "voice"
        val kind = QuickCapture.Kind.entries.firstOrNull { it.organizerKind == address?.getQueryParameter("kind") }
        // A new composition each time, so a second tap starts from an empty line.
        setContent {
            SentinelTheme {
                QuickCaptureDialog(
                    listenFirst = listen && fresh,
                    preset = kind,
                    blocked = blockedReason(this),
                    onClose = { finish() },
                )
            }
        }
    }

    companion object {
        // Why nothing can be added right now, if so.
        fun blockedReason(context: Context): String? {
            val preferences = context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE)
            if (preferences.getString(SentinelViewModel.SERVER_URL_KEY, "").isNullOrBlank()) {
                return "Sentinel Core isn't connected to a server yet. Open the app to set it up."
            }
            if (OrganizerDevice.knownEnabled(context) == false) {
                return "The Organizer is turned off. It's in the dashboard's Settings, under Organizer."
            }
            return null
        }
    }
}

@Composable
private fun QuickCaptureDialog(listenFirst: Boolean, preset: QuickCapture.Kind?, blocked: String?, onClose: () -> Unit) {
    val context = LocalContext.current
    val runtime = remember { OrganizerRuntime.get(context) }
    val spaces by runtime.engine.spaces.collectAsStateWithLifecycle()
    var text by rememberSaveable { mutableStateOf("") }
    // The kind picked by hand; null follows the words.
    var chosen by rememberSaveable { mutableStateOf(preset) }
    var trouble by remember { mutableStateOf<String?>(null) }
    val focus = remember { FocusRequester() }

    val parsed = QuickCapture.parse(text)
    val kind = chosen ?: parsed.kind
    val draft = parsed.draft(kind)
    // Until the first sync this phone doesn't know the person's own space, and new things would
    // default to Family - where everyone sees them.
    val synced = spaces.isNotEmpty()
    val canAdd = blocked == null && synced && !draft.isEmpty

    val listen = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val words = result.data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()
        if (result.resultCode == Activity.RESULT_OK && !words.isNullOrBlank()) {
            text = words
            trouble = null
        } else if (text.isEmpty()) {
            trouble = "Didn't catch that. Tap the microphone to try again, or type it."
        }
        runCatching { focus.requestFocus() }
    }
    val startListening: () -> Unit = {
        try {
            listen.launch(
                Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                    .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                    .putExtra(RecognizerIntent.EXTRA_PROMPT, prompt(chosen)),
            )
        } catch (_: ActivityNotFoundException) {
            trouble = "This phone has no speech recognition app. Type it instead."
            runCatching { focus.requestFocus() }
        }
    }
    LaunchedEffect(Unit) {
        if (listenFirst) startListening() else runCatching { focus.requestFocus() }
    }

    val add: () -> Unit = add@{
        if (!canAdd) return@add
        runtime.engine.create(draft.kind.organizerKind, draft.fields())
        // Plans a reminder's alert now, even with no signal.
        runtime.engine.refreshAlerts()
        OrganizerBackgroundSync.syncSoon(context)
        Toast.makeText(context, "Added “${draft.title}”" + (whenSaid(draft)?.let { " – $it" } ?: ""), Toast.LENGTH_LONG).show()
        onClose()
    }

    // The scrim: a tap outside the card closes it, as a dialog's would.
    Box(
        Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.45f))
            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null, onClick = onClose)
            .imePadding().navigationBarsPadding(),
        contentAlignment = Alignment.BottomCenter,
    ) {
        Card(
            Modifier.padding(12.dp).widthIn(max = 560.dp).fillMaxWidth()
                // Taps on the card stay on the card.
                .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) {},
            shape = RoundedCornerShape(24.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        ) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text("Quick Add", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    QuickCapture.Kind.entries.forEach { option ->
                        FilterChip(
                            selected = kind == option,
                            onClick = { chosen = option },
                            label = { Text(option.label) },
                            leadingIcon = { Icon(icon(option), contentDescription = null, Modifier.size(18.dp)) },
                        )
                    }
                }
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it; trouble = null },
                    modifier = Modifier.fillMaxWidth().focusRequester(focus),
                    placeholder = { Text("Say or type it") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { add() }),
                    trailingIcon = {
                        IconButton(onClick = startListening) {
                            Icon(Icons.Default.Mic, contentDescription = "Speak", tint = SentinelGreen)
                        }
                    },
                )
                trouble?.let { Text(it, color = SentinelAmber, style = MaterialTheme.typography.bodyMedium) }
                Preview(draft)
                when {
                    blocked != null -> Text(blocked, color = SentinelAmber, style = MaterialTheme.typography.bodyMedium)
                    !synced -> Text(
                        "Sentinel Core hasn't synced your Organizer yet. Open the app once while it can reach your server.",
                        color = SentinelAmber,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    if (blocked != null || !synced) {
                        TextButton(onClick = {
                            context.startActivity(Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                            onClose()
                        }) { Text("Open Sentinel Core") }
                    }
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = onClose) { Text("Cancel") }
                    Spacer(Modifier.width(8.dp))
                    Button(
                        onClick = add,
                        enabled = canAdd,
                        colors = ButtonDefaults.buttonColors(containerColor = SentinelGreen, contentColor = MaterialTheme.colorScheme.surface),
                    ) { Text("Add ${kind.label}") }
                }
            }
        }
    }
}

// What will be added, as it will be saved.
@Composable
private fun Preview(draft: QuickCapture.Draft) {
    if (draft.isEmpty) {
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text("Try:", style = MaterialTheme.typography.labelMedium)
            listOf(
                "“Add a reminder to take out the trash at 7am tomorrow”",
                "“Note the garage code is 4412”",
                "“Dentist Friday from 2 to 3pm on my calendar”",
            ).forEach { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        return
    }
    Surface(shape = RoundedCornerShape(12.dp), color = MaterialTheme.colorScheme.surfaceVariant, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Icon(icon(draft.kind), contentDescription = null, tint = SentinelGreen)
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(draft.title, style = MaterialTheme.typography.titleMedium)
                whenSaid(draft)?.let { Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                if (draft.body.isNotEmpty()) {
                    Text(draft.body, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 3)
                }
            }
        }
    }
}

private fun icon(kind: QuickCapture.Kind): ImageVector = when (kind) {
    QuickCapture.Kind.REMINDER -> Icons.Default.Checklist
    QuickCapture.Kind.NOTE -> Icons.AutoMirrored.Filled.Notes
    QuickCapture.Kind.EVENT -> Icons.Default.CalendarMonth
}

private fun prompt(kind: QuickCapture.Kind?): String = when (kind) {
    QuickCapture.Kind.NOTE -> "What should the note say?"
    QuickCapture.Kind.EVENT -> "What's the event, and when?"
    QuickCapture.Kind.REMINDER -> "What's the reminder?"
    null -> "Say a reminder, note or event"
}

// "Tomorrow at 7:00 AM", "Friday at 2:00 PM until 3:00 PM", "October 10, all day".
private fun whenSaid(draft: QuickCapture.Draft): String? {
    if (draft.kind == QuickCapture.Kind.NOTE) return null
    val start = draft.start ?: return "No date"
    val said = VoiceCapture.spoken(start, draft.hasTime, ZonedDateTime.now(start.zone)).replaceFirstChar { it.uppercaseChar() }
    if (draft.kind != QuickCapture.Kind.EVENT) return said
    if (!draft.hasTime) return "$said, all day"
    val end = draft.end ?: return said
    val format = if (end.toLocalDate() == start.toLocalDate()) DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)
    else DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
    return "$said until ${end.format(format)}"
}
