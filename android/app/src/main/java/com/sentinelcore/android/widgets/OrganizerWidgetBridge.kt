package com.sentinelcore.android.widgets

import android.content.Context
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.ui.organizer.completeReminder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// The app's half of the Organizer widgets: rewrites the snapshot they draw shortly after anything
// in the Organizer changes (an edit here or one synced in), and makes the real edit for reminders
// checked off on a widget - at once while the app is running, otherwise on its next launch.
class OrganizerWidgetBridge(
    private val context: Context,
    private val engine: OrganizerEngine,
    private val scope: CoroutineScope,
) {
    private val files = OrganizerWidgets.files(context)
    // Bumped when the app comes forward: the two-week window moves on with the date even when
    // nothing was edited.
    private val refresh = MutableStateFlow(0)
    private var lastWritten: OrganizerWidgetSnapshot? = null

    fun start(enabled: Flow<Boolean>) {
        scope.launch {
            combine(engine.revision, engine.spaces, engine.holidays, enabled, refresh) { _, _, _, isEnabled, _ -> isEnabled }
                .collectLatest { isEnabled ->
                    // A sync applies a reply in several steps; one write after they settle is enough.
                    delay(500)
                    write(isEnabled)
                }
        }
        // Starts with the flow's current value, so check-offs made while the app was closed are
        // applied on launch.
        scope.launch { OrganizerWidgets.actionsPosted.collect { applyActions() } }
    }

    fun appBecameActive() {
        applyActions()
        refresh.update { it + 1 }
    }

    fun applyActions() {
        for (action in files.takeActions()) {
            val reminder = engine.item(action.reminderId) ?: continue
            if (reminder.kind != OrganizerKinds.REMINDER || reminder.bool("completed")) continue
            // Moved on since the widget showed it (done elsewhere, or a repeat already advanced).
            if (reminder.string("due")?.takeIf { it.isNotEmpty() } != action.dueField) continue
            completeReminder(engine, reminder, true)
        }
    }

    private suspend fun write(enabled: Boolean) {
        val changed = withContext(Dispatchers.IO) {
            // A check-off not yet applied stays off the widgets rather than reappearing.
            val waiting = files.pendingActions().map { it.reminderId }.toSet()
            val snapshot = engine.widgetSnapshot(enabled, skippingReminders = waiting)
            if (lastWritten?.copy(generatedAt = 0) == snapshot.copy(generatedAt = 0)) return@withContext false
            files.writeSnapshot(snapshot)
            lastWritten = snapshot
            true
        }
        if (changed) OrganizerWidgets.updateAll(context)
    }
}
