package com.sentinelcore.android.widgets

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.sentinelcore.android.data.Reachability
import com.sentinelcore.android.data.SentinelRepository
import com.sentinelcore.android.data.ServerAddress
import com.sentinelcore.android.data.ServerIdentity
import com.sentinelcore.android.organizer.AppOrganizerTransport
import com.sentinelcore.android.organizer.OrganizerDevice
import com.sentinelcore.android.organizer.OrganizerRuntime
import com.sentinelcore.android.ui.SentinelViewModel
import java.util.concurrent.TimeUnit

// Keeps the Organizer (and so the home-screen widgets) current while the app isn't open, two ways:
//
// - The server's silent push when anything in the Organizer changes (OrganizerPushNudger on the
//   server), which arrives as an FCM data message and asks for a sync straight away.
// - A periodic sync every half hour or so, whenever Android lets it run - the fallback, and what
//   moves the widgets' dates along on a quiet day.
//
// Both are WorkManager jobs, so they only run with a network, survive the app being closed, and
// Android spaces them sensibly under Doze.
object OrganizerBackgroundSync {
    private const val PERIODIC = "organizer-refresh"
    private const val SOON = "organizer-refresh-now"

    private val needsNetwork = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

    fun schedulePeriodic(context: Context) {
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            PERIODIC,
            ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<OrganizerSyncWorker>(30, TimeUnit.MINUTES).setConstraints(needsNetwork).build(),
        )
    }

    // The silent push. A burst of them collapses into one sync.
    fun syncSoon(context: Context) {
        WorkManager.getInstance(context).enqueueUniqueWork(
            SOON,
            ExistingWorkPolicy.REPLACE,
            OneTimeWorkRequestBuilder<OrganizerSyncWorker>().setConstraints(needsNetwork).build(),
        )
    }

    // One sync with no screen open. The app's own connection logic lives in its ViewModel, so the
    // address is picked here the same way: the local address if it answers (or its plain-HTTP
    // port when this device doesn't trust the server's certificate), otherwise the remote one.
    suspend fun syncOnce(context: Context): Boolean {
        val runtime = OrganizerRuntime.get(context)
        val engine = runtime.engine
        if (engine.isConnected) {
            engine.sync()
            return engine.syncState.value.online
        }
        val preferences = context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE)
        val local = preferences.getString(SentinelViewModel.SERVER_URL_KEY, "").orEmpty()
        val remote = preferences.getString(SentinelViewModel.REMOTE_SERVER_URL_KEY, "").orEmpty()
        if (local.isBlank()) return false
        val repository = SentinelRepository(context) { _, _ -> }
        val candidates = buildList {
            when (repository.probe(local)) {
                Reachability.REACHABLE -> add(local)
                // Only to a server that has already proved it's this one - see ServerIdentity.
                // Nobody is watching a background sync, so there's no first-use exception here:
                // until a foreground connect has remembered the server's authority, this waits.
                Reachability.CERTIFICATE_UNTRUSTED ->
                    if (ServerIdentity(context).check(local) == ServerIdentity.Check.Verified) {
                        ServerAddress.httpFallback(local)?.let(::add)
                    }
                Reachability.UNREACHABLE -> Unit
            }
            if (remote.isNotBlank()) add(remote)
            if (isEmpty()) add(local)
        }
        for (address in candidates) {
            repository.baseUrl = address
            if (engine.syncOnce(AppOrganizerTransport(repository))) return true
        }
        return false
    }
}

class OrganizerSyncWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        if (!OrganizerDevice.lastKnownEnabled(applicationContext)) return Result.success()
        val runtime = OrganizerRuntime.get(applicationContext)
        OrganizerBackgroundSync.syncOnce(applicationContext)
        runtime.widgets.writeNow()
        // An unreachable server isn't worth retrying sooner than the next push or period.
        return Result.success()
    }
}
