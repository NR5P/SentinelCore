package com.sentinelcore.android.organizer

import android.content.Context
import com.sentinelcore.android.widgets.OrganizerBackgroundSync
import com.sentinelcore.android.widgets.OrganizerWidgetBridge
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow

// The one Organizer in this process. The app's screens use it, and so does syncing in the
// background (widgets/OrganizerBackgroundSync), which runs with no Activity at all - two engines on
// the same database would each hold their own copy in memory and overwrite the other's edits.
class OrganizerRuntime private constructor(context: Context) {
    // Its own scope on IO, since a sync applies server replies to the local SQLite copy. Lives as
    // long as the process: nothing here belongs to any one screen.
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val engine = OrganizerEngine(
        SqliteOrganizerStore(context, OrganizerDevice.directory(context)),
        OrganizerDevice.id(context),
        OrganizerDevice.policy(context),
        scope,
    )
    // Whether the server has the Organizer switched on, as last heard. The app updates it.
    val enabled = MutableStateFlow(OrganizerDevice.lastKnownEnabled(context))
    // Keeps the home-screen widgets' copy current.
    val widgets = OrganizerWidgetBridge(context, engine, scope)

    init {
        OrganizerAlarms.ensureChannel(context)
        engine.onAlertsChanged = { alerts -> OrganizerAlarms.schedule(context, alerts) }
        engine.refreshAlerts()
        widgets.start(enabled)
        OrganizerBackgroundSync.schedulePeriodic(context)
    }

    fun setEnabled(context: Context, value: Boolean) {
        OrganizerDevice.saveLastKnownEnabled(context, value)
        enabled.value = value
    }

    companion object {
        @Volatile private var instance: OrganizerRuntime? = null

        fun get(context: Context): OrganizerRuntime =
            instance ?: synchronized(this) {
                instance ?: OrganizerRuntime(context.applicationContext).also { instance = it }
            }
    }
}
