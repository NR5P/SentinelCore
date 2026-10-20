package com.sentinelcore.android.car

import android.content.Context
import android.os.Looper
import androidx.car.app.OnDoneCallback
import androidx.car.app.Screen
import androidx.car.app.model.ListTemplate
import androidx.car.app.model.Row
import androidx.car.app.testing.ScreenController
import androidx.car.app.testing.TestAppManager
import androidx.car.app.testing.TestCarContext
import androidx.car.app.testing.TestScreenManager
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ApplicationProvider
import com.sentinelcore.android.audio.AudioRuntime
import com.sentinelcore.android.ui.SentinelViewModel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

// The Android Auto car app (IoT category) driven through the Car App Library's own test host,
// against StubSentinelServer - no phone, no head unit. Every template the screens hand the car
// goes through the library's builders, which enforce the same rules the real host does (row
// text limits, a toggle row can't also be clickable, loading vs. list), so a screen that would
// crash in the car throws here.
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class CarScreensTest {
    private val servers = mutableListOf<StubSentinelServer>()
    private val controllers = mutableListOf<ScreenController>()
    private lateinit var carContext: TestCarContext

    @Before
    fun setUp() {
        // One AudioRuntime per process in the app; a fresh one per test here, so each starts as
        // a cold start from the car would.
        AudioRuntime::class.java.getDeclaredField("instance").apply { isAccessible = true }.set(null, null)
        carContext = TestCarContext.createCarContext(ApplicationProvider.getApplicationContext())
        // A real host says which Car API it speaks before any screen is shown; the test host
        // leaves it to us. 7 is what this library version (1.7) speaks.
        androidx.car.app.CarContext::class.java.getDeclaredField("mCarAppApiLevel")
            .apply { isAccessible = true }.setInt(carContext, 7)
    }

    @After
    fun tearDown() {
        controllers.forEach { it.moveToState(Lifecycle.State.DESTROYED) }
        servers.forEach { it.stop() }
    }

    private fun serve(server: StubSentinelServer) = server.also { servers += it }

    private fun addresses(local: String, remote: String = "") {
        ApplicationProvider.getApplicationContext<Context>()
            .getSharedPreferences("sentinel_core", Context.MODE_PRIVATE).edit()
            .putString(SentinelViewModel.SERVER_URL_KEY, local)
            .putString(SentinelViewModel.REMOTE_SERVER_URL_KEY, remote)
            .commit()
    }

    private fun <S : Screen> show(screen: S): S {
        shownByTest += screen
        controllers += ScreenController(screen).moveToState(Lifecycle.State.STARTED)
        return screen
    }

    // Robolectric's main-looper clock only moves when told to, and the screens' polls and time
    // limits are delays on that looper - so it's walked forward in step with the real clock (the
    // network replies arrive on real threads in real time).
    private fun tick() {
        Thread.sleep(20)
        shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(20))
    }

    // Runs the main looper until the screen has something other than its spinner to show.
    private fun loaded(screen: Screen, timeoutMillis: Long = 15_000, until: (ListTemplate) -> Boolean = { !it.isLoading }): ListTemplate {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (true) {
            tick()
            val template = screen.onGetTemplate() as ListTemplate
            if (until(template)) return template
            if (System.currentTimeMillis() > deadline) fail("Screen never finished loading")
        }
    }

    private val ListTemplate.rows: List<Row> get() = singleList!!.items.map { it as Row }
    private val ListTemplate.noItemsMessage: String? get() = singleList?.noItemsMessage?.toString()
    private fun ListTemplate.row(title: String): Row = rows.firstOrNull { it.title.toString() == title }
        ?: error("No row \"$title\" in ${rows.map { it.title.toString() }}")
    private val Row.lines: List<String> get() = texts.map { it.toString() }

    private val done = object : OnDoneCallback {}
    private fun Row.tap() = onClickDelegate!!.sendClick(done)

    private val toasts: List<String> get() = carContext.getCarService(TestAppManager::class.java).toastsShown.map { it.toString() }
    // What the screens pushed - not the ones the tests put up themselves with show().
    private val shownByTest = mutableSetOf<Screen>()
    private val pushed: List<Screen> get() = carContext.getCarService(TestScreenManager::class.java).screensPushed.filter { it !in shownByTest }

    private fun waitFor(what: String, timeoutMillis: Long = 10_000, check: () -> Boolean) {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (!check()) {
            if (System.currentTimeMillis() > deadline) fail("Timed out waiting for $what")
            tick()
        }
    }

    // ---------------------------------------------------------------- Home

    @Test
    fun homeSummarisesTheHouse() {
        val server = serve(StubSentinelServer.house())
        addresses(server.url)
        val home = loaded(show(CarHomeScreen(carContext)))

        assertEquals(listOf("Alarm", "Devices", "Scenes", "Recent alerts"), home.rows.map { it.title.toString() })
        assertEquals(listOf("Disarmed"), home.row("Alarm").lines)
        // Four enabled, commandable devices (the sensor and the disabled switch are left out), and
        // the two whose power is "on" - the light and the (locked) lock; a button is never "on".
        assertEquals(listOf("4 devices, 2 on"), home.row("Devices").lines)
        assertEquals(listOf("2 to run"), home.row("Scenes").lines)
        assertTrue(home.row("Recent alerts").lines.single().startsWith("Backyard: animal"))
    }

    @Test
    fun homeStillShowsWhenAlertsFail() {
        val server = serve(StubSentinelServer.house().on("GET", "/api/activity", """{"error":"boom"}""", status = 500))
        addresses(server.url)
        val home = loaded(show(CarHomeScreen(carContext)))

        assertEquals(listOf("Couldn't load alerts"), home.row("Recent alerts").lines)
        assertTrue(home.rows.any { it.title.toString() == "Devices" })
    }

    @Test
    fun homeLeavesOutDevicesAndScenesWhenTheHubIsOff() {
        val server = serve(StubSentinelServer.house().on("GET", "/api/device-hub/enabled", """{"enabled":false}"""))
        addresses(server.url)
        val home = loaded(show(CarHomeScreen(carContext)))

        assertEquals(listOf("Alarm", "Recent alerts"), home.rows.map { it.title.toString() })
    }

    @Test
    fun aSlowDeviceHubDoesNotHoldUpHome() {
        val server = serve(StubSentinelServer.house().on("GET", "/api/devices", StubSentinelServer.devices(), delayMillis = 12_000))
        addresses(server.url)
        val started = System.currentTimeMillis()
        val home = loaded(show(CarHomeScreen(carContext)), timeoutMillis = 11_000)

        assertTrue("took ${System.currentTimeMillis() - started} ms", System.currentTimeMillis() - started < HOME_READ_LIMIT_MS + 2_500)
        assertTrue(home.rows.any { it.title.toString() == "Alarm" })
        assertTrue(home.rows.any { it.title.toString() == "Recent alerts" })
    }

    @Test
    fun unreachableServerSaysSoInsteadOfSpinning() {
        addresses("http://127.0.0.1:1")
        val home = loaded(show(CarHomeScreen(carContext)))

        assertEquals("Can't reach your Sentinel Core server.", home.noItemsMessage)
    }

    @Test
    fun notSetUpSendsYouToThePhone() {
        addresses("")
        val home = loaded(show(CarHomeScreen(carContext)))

        assertEquals(NOT_SET_UP, home.noItemsMessage)
    }

    @Test
    fun homeRowsOpenTheirScreens() {
        val server = serve(StubSentinelServer.house())
        addresses(server.url)
        val home = loaded(show(CarHomeScreen(carContext)))

        listOf("Alarm", "Devices", "Scenes", "Recent alerts").forEach { home.row(it).tap() }
        tick()
        assertEquals(
            listOf(CarAlarmScreen::class, CarDevicesScreen::class, CarScenesScreen::class, CarAlertsScreen::class),
            pushed.map { it.javaClass.kotlin },
        )
    }

    // ---------------------------------------------------------------- Away from home

    @Test
    fun coldStartAwayFromHomeUsesTheRemoteAddress() {
        val remote = serve(StubSentinelServer.house())
        addresses(local = "http://127.0.0.1:1", remote = remote.url)
        val home = loaded(show(CarHomeScreen(carContext)))

        assertTrue(home.rows.any { it.title.toString() == "Devices" })
    }

    @Test
    fun drivingAwayFromHomeSwitchesToTheRemoteAddress() {
        // At home: the car connects to the local server.
        val homeServer = serve(StubSentinelServer.house())
        addresses(homeServer.url)
        val devices = show(CarDevicesScreen(carContext))
        assertTrue(loaded(devices).row("Porch light").toggle!!.isChecked)

        // Out of Wi-Fi range: home stops answering, and only the remote address (another copy of
        // the server, with its own device names so we can tell) works.
        homeServer.stop()
        val remote = serve(
            StubSentinelServer.house().on("GET", "/api/devices", "[" + StubSentinelServer.device("light-1", "Porch light (remote)", "light") + "]"),
        )
        addresses(local = homeServer.url, remote = remote.url)

        // The screen's next poll recovers by itself.
        val after = loaded(devices, timeoutMillis = 20_000) { t -> t.singleList?.items?.any { (it as Row).title.toString() == "Porch light (remote)" } == true }
        assertEquals("Porch light (remote)", after.rows.single().title.toString())
    }

    // ---------------------------------------------------------------- Devices (MQTT Device Hub)

    @Test
    fun devicesListEveryControllableDeviceTheRightWay() {
        val server = serve(StubSentinelServer.house())
        addresses(server.url)
        val list = loaded(show(CarDevicesScreen(carContext)))

        // No sortOrder from this server, so by room, then name. The sensor (reports only) and the
        // disabled switch aren't there.
        assertEquals(listOf("Front door lock", "Porch light", "Garage door", "Gate buzzer"), list.rows.map { it.title.toString() })
        // A light is a toggle, showing its state.
        val light = list.row("Porch light")
        assertTrue(light.toggle!!.isChecked)
        assertEquals(listOf("Front - On"), light.lines)
        // Covers and locks open their own screen; a button is pressed in place.
        assertTrue(list.row("Garage door").isBrowsable)
        assertTrue(list.row("Front door lock").isBrowsable)
        assertEquals(listOf("Garage - Tap to press"), list.row("Gate buzzer").lines)
        assertEquals(listOf("Front - Locked"), list.row("Front door lock").lines)
    }

    @Test
    fun togglingALightSendsToggle() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/devices/light-1/command", """{"sent":true}"""))
        addresses(server.url)
        val list = loaded(show(CarDevicesScreen(carContext)))

        list.row("Porch light").toggle!!.onCheckedChangeDelegate.sendCheckedChange(false, done)
        waitFor("the toggle command") { server.sent("POST", "/api/devices/light-1/command").isNotEmpty() }
        assertTrue(server.sent("POST", "/api/devices/light-1/command").single().body.contains("\"toggle\""))
    }

    @Test
    fun pressingAButtonSaysSo() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/devices/bell-1/command", """{"sent":true}"""))
        addresses(server.url)
        val list = loaded(show(CarDevicesScreen(carContext)))

        list.row("Gate buzzer").tap()
        waitFor("the toast") { toasts.isNotEmpty() }
        assertTrue(server.sent("POST", "/api/devices/bell-1/command").single().body.contains("\"press\""))
        assertEquals(listOf("Gate buzzer: pressed"), toasts)
    }

    @Test
    fun aCommandTheDeviceDidNotGetIsReported() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/devices/bell-1/command", """{"sent":false}"""))
        addresses(server.url)
        val list = loaded(show(CarDevicesScreen(carContext)))

        list.row("Gate buzzer").tap()
        waitFor("the toast") { toasts.isNotEmpty() }
        assertEquals(listOf("Gate buzzer didn't get the command."), toasts)
    }

    @Test
    fun garageDoorOffersOpenCloseStop() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/devices/garage-1/command", """{"sent":true}"""))
        addresses(server.url)
        val list = loaded(show(CarDevicesScreen(carContext)))
        list.row("Garage door").tap()
        shadowOf(Looper.getMainLooper()).idle()
        val garage = loaded(show(pushed.single() as CarDeviceScreen))

        assertEquals(listOf("Closed", "Open", "Close", "Stop"), garage.rows.map { it.title.toString() })
        garage.row("Open").tap()
        waitFor("the toast") { toasts.isNotEmpty() }
        assertTrue(server.sent("POST", "/api/devices/garage-1/command").single().body.contains("\"open\""))
        assertEquals(listOf("Garage door: open"), toasts)
    }

    @Test
    fun lockOffersLockAndUnlock() {
        val server = serve(StubSentinelServer.house())
        addresses(server.url)
        val list = loaded(show(CarDevicesScreen(carContext)))
        list.row("Front door lock").tap()
        shadowOf(Looper.getMainLooper()).idle()
        val lock = loaded(show(pushed.single() as CarDeviceScreen))

        assertEquals(listOf("Locked", "Lock", "Unlock"), lock.rows.map { it.title.toString() })
    }

    @Test
    fun aLongDeviceListStaysWithinWhatTheCarAllows() {
        val many = (1..40).map { StubSentinelServer.device("d$it", "Switch $it", "switch", room = "Room") }.toTypedArray()
        val server = serve(StubSentinelServer.house().on("GET", "/api/devices", StubSentinelServer.devices(*many)))
        addresses(server.url)
        val list = loaded(show(CarDevicesScreen(carContext)))

        assertTrue("${list.rows.size} rows", list.rows.size in 1..6)
    }

    // ---------------------------------------------------------------- Alarm

    @Test
    fun disarmedAlarmOffersTheThreeArmModes() {
        val server = serve(StubSentinelServer.house())
        addresses(server.url)
        val alarm = loaded(show(CarAlarmScreen(carContext)))

        assertEquals(listOf("Disarmed", "Arm home", "Arm night", "Arm away"), alarm.rows.map { it.title.toString() })
    }

    @Test
    fun armingHomeSendsTheModeWithoutAPin() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/alarm/arm", """{"ok":true}"""))
        addresses(server.url)
        val alarm = loaded(show(CarAlarmScreen(carContext)))

        alarm.row("Arm home").tap()
        waitFor("the toast") { toasts.isNotEmpty() }
        val sent = server.sent("POST", "/api/alarm/arm").single().body
        assertTrue(sent, sent.contains("\"mode\":\"home\""))
        assertFalse(sent, sent.contains("pin"))
        assertEquals(listOf("Armed home"), toasts)
    }

    @Test
    fun aServerThatWantsThePinSendsYouToThePhone() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/alarm/arm", """{"error":"PIN required"}""", status = 403))
        addresses(server.url)
        val alarm = loaded(show(CarAlarmScreen(carContext)))

        alarm.row("Arm away").tap()
        waitFor("the toast") { toasts.isNotEmpty() }
        assertEquals(listOf("Arming needs the PIN. $PHONE_ONLY"), toasts)
    }

    @Test
    fun pinToArmHidesTheArmButtons() {
        val server = serve(StubSentinelServer.house().on("GET", "/api/alarm/status", StubSentinelServer.alarm(requirePin = true)))
        addresses(server.url)
        val alarm = loaded(show(CarAlarmScreen(carContext)))

        assertEquals(listOf("Disarmed", "Arming needs the PIN"), alarm.rows.map { it.title.toString() })
    }

    @Test
    fun armedAlarmNeverOffersDisarm() {
        val server = serve(StubSentinelServer.house().on("GET", "/api/alarm/status", StubSentinelServer.alarm(status = "armed", mode = "away")))
        addresses(server.url)
        val alarm = loaded(show(CarAlarmScreen(carContext)))

        assertEquals(listOf("Armed away", "To disarm"), alarm.rows.map { it.title.toString() })
        assertEquals(listOf(PHONE_ONLY), alarm.row("To disarm").lines)
        assertTrue(alarm.rows.none { it.onClickDelegate != null })
    }

    // ---------------------------------------------------------------- Scenes and alerts

    @Test
    fun scenesAreTheDeviceMqttAndWebhookSchedules() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/schedules/sc-2/run", """{"fired":true}"""))
        addresses(server.url)
        val scenes = loaded(show(CarScenesScreen(carContext)))

        assertEquals(listOf("Lights off", "Open the gate"), scenes.rows.map { it.title.toString() })
        scenes.row("Open the gate").tap()
        waitFor("the toast") { toasts.isNotEmpty() }
        assertEquals(listOf("Open the gate: done"), toasts)
    }

    @Test
    fun aViewerIsToldOnlyAnAdminCanRunScenes() {
        val server = serve(StubSentinelServer.house().on("POST", "/api/schedules/sc-1/run", """{"error":"forbidden"}""", status = 403))
        addresses(server.url)
        val scenes = loaded(show(CarScenesScreen(carContext)))

        scenes.row("Lights off").tap()
        waitFor("the toast") { toasts.isNotEmpty() }
        assertEquals(listOf("Only an admin can run scenes."), toasts)
    }

    @Test
    fun alertsAreTextOnlyNewestFirst() {
        val server = serve(StubSentinelServer.house())
        addresses(server.url)
        val alerts = loaded(show(CarAlertsScreen(carContext)))

        assertEquals(listOf("Backyard: animal", "Driveway: person"), alerts.rows.map { it.title.toString() })
        assertTrue(alerts.rows.all { it.image == null })
    }

    @Test
    fun pollingKeepsADeviceCurrent() {
        val server = serve(StubSentinelServer.house())
        addresses(server.url)
        val list = show(CarDevicesScreen(carContext))
        assertTrue(loaded(list).row("Porch light").toggle!!.isChecked)

        server.on("GET", "/api/devices", "[" + StubSentinelServer.device("light-1", "Porch light", "light", power = "off", room = "Front") + "]")
        val after = loaded(list, timeoutMillis = 10_000) { t -> t.singleList?.items?.map { it as Row }?.singleOrNull()?.toggle?.isChecked == false }
        assertFalse(after.row("Porch light").toggle!!.isChecked)
    }
}
