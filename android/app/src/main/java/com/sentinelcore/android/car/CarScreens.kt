package com.sentinelcore.android.car

import androidx.car.app.CarContext
import androidx.car.app.CarToast
import androidx.car.app.Screen
import androidx.car.app.constraints.ConstraintManager
import androidx.car.app.model.Action
import androidx.car.app.model.ActionStrip
import androidx.car.app.model.ItemList
import androidx.car.app.model.ListTemplate
import androidx.car.app.model.Row
import androidx.car.app.model.Template
import androidx.car.app.model.Toggle
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.lifecycleScope
import com.sentinelcore.android.data.ActivityItem
import com.sentinelcore.android.data.AlarmModes
import com.sentinelcore.android.data.AlarmStatus
import com.sentinelcore.android.data.HubDevice
import com.sentinelcore.android.data.Schedule
import com.sentinelcore.android.data.SentinelApiException
import com.sentinelcore.android.data.SentinelRepository
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import com.sentinelcore.android.audio.withHardTimeout
import java.time.Duration
import java.time.Instant

// A screen that loads something from the server while it's visible, and optionally keeps
// polling it so a garage door shown as "open" doesn't stay that way after it closes. The Car App
// Library counts template changes against a budget, but a refresh of the same template with new
// data is free, so polling is fine.
internal abstract class LoadingScreen<T>(carContext: CarContext, private val pollMillis: Long = 0) : Screen(carContext) {
    protected var data: T? = null
    protected var error: String? = null
    private var job: Job? = null

    init {
        lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) {
                job = lifecycleScope.launch {
                    do {
                        refresh()
                        if (pollMillis > 0) delay(pollMillis)
                    } while (pollMillis > 0)
                }
            }

            override fun onStop(owner: LifecycleOwner) {
                job?.cancel()
                job = null
            }
        })
    }

    protected abstract suspend fun load(repository: SentinelRepository): T

    protected suspend fun refresh() {
        runCatching { carContext.withServer { load(it) } }
            .onSuccess { data = it; error = null }
            .onFailure { if (data == null) error = it.carMessage() }
        invalidate()
    }

    protected fun reload() {
        lifecycleScope.launch { refresh() }
    }

    protected fun toast(text: String) = CarToast.makeText(carContext, text, CarToast.LENGTH_LONG).show()

    // Runs a command, says what happened, then re-reads the screen.
    protected fun command(done: String? = null, block: suspend (SentinelRepository) -> String?) {
        lifecycleScope.launch {
            runCatching { carContext.withServer { block(it) } }
                .onSuccess { said -> (said ?: done)?.let(::toast) }
                .onFailure { toast(it.carMessage()) }
            refresh()
        }
    }

    // How many rows this car will show in a list; hosts before API 2 can't say, and six is the
    // floor every host supports.
    protected val listLimit: Int
        get() = if (carContext.carAppApiLevel >= 2) {
            runCatching {
                carContext.getCarService(ConstraintManager::class.java)
                    .getContentLimit(ConstraintManager.CONTENT_LIMIT_TYPE_LIST)
            }.getOrDefault(6)
        } else 6

    protected fun list(title: String, back: Boolean = true, actions: ActionStrip? = null, rows: () -> List<Row>?): Template {
        val builder = ListTemplate.Builder()
            .setTitle(title)
            .setHeaderAction(if (back) Action.BACK else Action.APP_ICON)
        actions?.let(builder::setActionStrip)
        val message = error
        if (data == null && message == null) return builder.setLoading(true).build()
        val items = ItemList.Builder()
        if (message != null) {
            items.setNoItemsMessage(message)
        } else {
            rows().orEmpty().take(listLimit).forEach(items::addItem)
            items.setNoItemsMessage("Nothing here.")
        }
        return builder.setSingleList(items.build()).build()
    }

    protected fun refreshStrip(): ActionStrip = ActionStrip.Builder()
        .addAction(Action.Builder().setTitle("Refresh").setOnClickListener { reload() }.build())
        .build()
}

internal const val HOME_READ_LIMIT_MS = 8_000L

internal fun row(title: String, vararg text: String?, onClick: (() -> Unit)? = null, browsable: Boolean = false): Row {
    val builder = Row.Builder().setTitle(title)
    text.filterNotNull().filter { it.isNotBlank() }.take(2).forEach(builder::addText)
    if (onClick != null) {
        builder.setOnClickListener(onClick)
        if (browsable) builder.setBrowsable(true)
    }
    return builder.build()
}

internal fun ago(at: Instant): String {
    val minutes = Duration.between(at, Instant.now()).toMinutes().coerceAtLeast(0)
    return when {
        minutes < 1 -> "Just now"
        minutes < 60 -> "$minutes min ago"
        minutes < 24 * 60 -> "${minutes / 60} h ago"
        else -> "${minutes / (24 * 60)} d ago"
    }
}

internal fun alarmLine(status: AlarmStatus): String = when (status.status) {
    "triggered" -> "ALARM GOING OFF" + (status.triggeredBy?.let { " - $it" } ?: "")
    "pending" -> "Entry delay - about to go off"
    "arming" -> "Arming ${AlarmModes.label(status.mode).lowercase()}"
    "armed" -> "Armed ${AlarmModes.label(status.mode).lowercase()}"
    else -> "Disarmed"
}

internal fun alertTitle(item: ActivityItem): String =
    item.eventType?.takeIf { it.isNotBlank() }?.let { "${item.camera}: ${it.replace('_', ' ')}" } ?: item.camera

// Schedules that act on the house - the car's "scenes". Recording and notification schedules
// aren't something to fire from the road.
private val sceneActions = setOf("device", "mqtt", "webhook")
internal fun Schedule.isScene(): Boolean = enabled && action.type in sceneActions

// ---------------------------------------------------------------------------------------------

internal class CarHomeScreen(carContext: CarContext) : LoadingScreen<CarHomeScreen.Home>(carContext, pollMillis = 15_000) {
    class Home(
        val alarm: AlarmStatus?,
        val devices: List<HubDevice>?,
        val scenes: Int,
        val latest: ActivityItem?,
        val alertsFailed: Boolean = false,
    )

    // Side by side, each cut off after 8 seconds, so one slow answer (a busy Device Hub, a long
    // alert list) can't hold up or blank the rest of the screen.
    override suspend fun load(repository: SentinelRepository): Home = coroutineScope {
        fun <T> read(block: suspend () -> T) = async {
            runCatching { withHardTimeout(HOME_READ_LIMIT_MS) { block() } }
        }
        val alarm = read { repository.alarmStatus() }
        val hub = read { repository.deviceHubEnabled() }
        val devices = read { repository.devices() }
        val schedules = read { repository.schedules() }
        val activity = read { repository.activity() }
        val hubOn = hub.await().getOrNull() == true
        val alerts = activity.await()
        // Every account can read alerts, so when even that fails along with the hub check the
        // server really is out of reach - say so rather than show a half-empty screen.
        if (alerts.isFailure && hub.await().isFailure) throw alerts.exceptionOrNull()!!
        Home(
            alarm = alarm.await().getOrNull()?.takeIf { it.enabled },
            devices = if (hubOn) devices.await().getOrNull() else null,
            scenes = if (hubOn) schedules.await().getOrNull()?.count { it.isScene() } ?: 0 else 0,
            latest = alerts.getOrNull()?.filter { it.notified }?.maxByOrNull { it.occurredAt },
            alertsFailed = alerts.isFailure,
        )
    }

    override fun onGetTemplate(): Template = list("Sentinel Core", back = false) {
        val home = data ?: return@list null
        buildList {
            home.alarm?.let { alarm ->
                add(row("Alarm", alarmLine(alarm), onClick = { screenManager.push(CarAlarmScreen(carContext)) }, browsable = true))
            }
            home.devices?.let { devices ->
                val shown = devices.filter { it.enabled && it.commandable }
                val on = shown.count { it.isOn && !it.isMomentary }
                add(row("Devices", "${shown.size} devices, $on on", onClick = { screenManager.push(CarDevicesScreen(carContext)) }, browsable = true))
            }
            if (home.scenes > 0) {
                add(row("Scenes", "${home.scenes} to run", onClick = { screenManager.push(CarScenesScreen(carContext)) }, browsable = true))
            }
            add(
                row(
                    "Recent alerts",
                    home.latest?.let { "${alertTitle(it)} - ${ago(it.occurredAt)}" }
                        ?: if (home.alertsFailed) "Couldn't load alerts" else "No recent alerts",
                    onClick = { screenManager.push(CarAlertsScreen(carContext)) },
                    browsable = true,
                ),
            )
        }
    }
}

internal class CarAlarmScreen(carContext: CarContext) : LoadingScreen<AlarmStatus>(carContext, pollMillis = 3_000) {
    override suspend fun load(repository: SentinelRepository): AlarmStatus = repository.alarmStatus()

    override fun onGetTemplate(): Template = list("Alarm") {
        val status = data ?: return@list null
        if (!status.enabled) return@list listOf(row("The alarm is turned off", PHONE_ONLY))
        buildList {
            val open = status.sensors.filter { it.enabled && it.isOpen }.joinToString { it.name }
            add(row(alarmLine(status), if (open.isNotEmpty()) "Open: $open" else status.issues.firstOrNull()))
            if (status.status == "disarmed") {
                if (status.config?.requirePinToArm == true) {
                    add(row("Arming needs the PIN", PHONE_ONLY))
                } else {
                    listOf("home" to "Doors and windows", "night" to "Doors and windows, night zones", "away" to "Everything, after the exit delay")
                        .forEach { (mode, detail) ->
                            add(row("Arm ${AlarmModes.label(mode).lowercase()}", detail, onClick = { arm(mode) }))
                        }
                }
            } else {
                // Disarming takes the PIN - not something to type on a car screen.
                add(row("To disarm", PHONE_ONLY))
            }
        }
    }

    private fun arm(mode: String) = command {
        try {
            val reply = it.armAlarm(mode, force = false, pin = null)
            val said = if (mode == "away") "Arming away" else "Armed ${AlarmModes.label(mode).lowercase()}"
            reply.note?.let { note -> "$said. $note" } ?: said
        } catch (e: SentinelApiException) {
            if (e.statusCode == 403) throw IllegalStateException("Arming needs the PIN. $PHONE_ONLY")
            throw e
        }
    }
}

internal class CarDevicesScreen(carContext: CarContext) : LoadingScreen<List<HubDevice>>(carContext, pollMillis = 4_000) {
    override suspend fun load(repository: SentinelRepository): List<HubDevice> =
        repository.devices()
            .filter { it.enabled && it.commandable }
            .sortedWith(compareBy({ it.sortOrder }, { it.room }, { it.name }))

    override fun onGetTemplate(): Template = list("Devices", actions = refreshStrip()) {
        data?.map { device -> deviceRow(device) }
    }

    private fun deviceRow(device: HubDevice): Row {
        val where = device.room.takeIf { it.isNotBlank() }
        val state = when {
            !device.available -> "Unavailable"
            device.isMomentary -> "Tap to press"
            device.isLock -> when (device.power) { "on" -> "Locked"; "off" -> "Unlocked"; else -> "Unknown" }
            device.isCoverLike -> when (device.power) { "on" -> "Open"; "off" -> "Closed"; else -> "Unknown" }
            else -> when (device.power) { "on" -> "On"; "off" -> "Off"; else -> "Unknown" }
        }
        val text = listOfNotNull(where, state).joinToString(" - ")
        return when {
            device.isMomentary -> row(device.name, text, onClick = { send(device, "press", "${device.name}: pressed") })
            device.isCoverLike || device.isLock ->
                row(device.name, text, onClick = { screenManager.push(CarDeviceScreen(carContext, device)) }, browsable = true)
            else -> Row.Builder()
                .setTitle(device.name)
                .addText(text)
                .setToggle(Toggle.Builder { _ -> send(device, "toggle", null) }.setChecked(device.isOn).build())
                .build()
        }
    }

    private fun send(device: HubDevice, action: String, done: String?) = command(done) {
        val result = it.sendDeviceCommand(device.id, action = action)
        if (!result.sent) "${device.name} didn't get the command." else null
    }
}

// Covers, gates, garage doors and locks: their two (or three) actions, one tap each.
internal class CarDeviceScreen(carContext: CarContext, private val device: HubDevice) :
    LoadingScreen<HubDevice>(carContext, pollMillis = 3_000) {
    override suspend fun load(repository: SentinelRepository): HubDevice =
        repository.devices().firstOrNull { it.id == device.id } ?: device

    override fun onGetTemplate(): Template = list(device.name) {
        val current = data ?: return@list null
        val actions = if (current.isLock) {
            listOf("lock" to "Lock", "unlock" to "Unlock")
        } else {
            listOfNotNull("open" to "Open", "close" to "Close", if (current.hasStop) "stop" to "Stop" else null)
        }
        val state = when {
            !current.available -> "Unavailable"
            current.isLock -> if (current.power == "on") "Locked" else if (current.power == "off") "Unlocked" else "State unknown"
            else -> if (current.power == "on") "Open" else if (current.power == "off") "Closed" else "State unknown"
        }
        listOf(row(state)) + actions.map { (action, label) ->
            row(label, onClick = {
                command("${current.name}: ${label.lowercase()}") { repo ->
                    val result = repo.sendDeviceCommand(current.id, action = action)
                    if (!result.sent) "${current.name} didn't get the command." else null
                }
            })
        }
    }
}

internal class CarScenesScreen(carContext: CarContext) : LoadingScreen<List<Schedule>>(carContext) {
    override suspend fun load(repository: SentinelRepository): List<Schedule> =
        repository.schedules().filter { it.isScene() }.sortedBy { it.name }

    override fun onGetTemplate(): Template = list("Scenes") {
        data?.map { schedule ->
            row(schedule.name, "Tap to run", onClick = {
                command {
                    try {
                        val result = it.runSchedule(schedule.id)
                        if (result.fired) "${schedule.name}: done" else result.detail.ifBlank { "${schedule.name} didn't run." }
                    } catch (e: SentinelApiException) {
                        if (e.statusCode == 403) throw IllegalStateException("Only an admin can run scenes.")
                        throw e
                    }
                }
            })
        }
    }
}

internal class CarAlertsScreen(carContext: CarContext) : LoadingScreen<List<ActivityItem>>(carContext, pollMillis = 30_000) {
    override suspend fun load(repository: SentinelRepository): List<ActivityItem> =
        repository.activity().filter { it.notified }.sortedByDescending { it.occurredAt }

    // Text only: Car App Quality IU-1 keeps camera snapshots off the car screen.
    override fun onGetTemplate(): Template = list("Recent alerts", actions = refreshStrip()) {
        data?.map { item -> row(alertTitle(item), item.summary?.take(120), ago(item.occurredAt)) }
    }
}
