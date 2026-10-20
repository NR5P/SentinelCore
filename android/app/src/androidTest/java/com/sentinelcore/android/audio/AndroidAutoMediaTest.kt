package com.sentinelcore.android.audio

import android.content.ComponentName
import android.content.Context
import android.media.MediaMetadata
import android.media.browse.MediaBrowser
import android.media.session.MediaController
import android.media.session.PlaybackState
import android.os.Handler
import android.os.HandlerThread
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.sentinelcore.android.ui.SentinelViewModel
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import okio.Buffer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

// Android Auto's half of music and podcasts, without a car: Android Auto is a client of the app's
// media service over the platform's MediaBrowser protocol (the legacy MediaBrowserService one,
// which media3 serves for it), so this test is that client. It connects the way the car does,
// browses the tabs, opens an album, presses play and skips, against a stub server in this
// process that serves real audio. Run on an emulator or a phone:
//   ./gradlew :app:connectedDebugAndroidTest -Pandroid.testInstrumentationRunnerArguments.class=com.sentinelcore.android.audio.AndroidAutoMediaTest
@RunWith(AndroidJUnit4::class)
class AndroidAutoMediaTest {
    private val context: Context = InstrumentationRegistry.getInstrumentation().targetContext
    private val testAssets = InstrumentationRegistry.getInstrumentation().context.assets
    private val server = MockWebServer()
    private val routes = ConcurrentHashMap<String, () -> MockResponse>()
    private val requests = CopyOnWriteArrayList<String>()
    private val thread = HandlerThread("auto-client").apply { start() }
    private val handler = Handler(thread.looper)
    private var browser: MediaBrowser? = null

    private fun json(body: String) = { MockResponse().setHeader("Content-Type", "application/json").setBody(body) }
    private fun audio(asset: String) = {
        MockResponse().setHeader("Content-Type", "audio/mp4").setBody(Buffer().write(testAssets.open(asset).readBytes()))
    }

    @Before
    fun setUp() {
        // Nothing playing or remembered from a previous run, as on a cold start from the car.
        AudioRuntime::class.java.getDeclaredField("instance").apply { isAccessible = true }.get(null)?.let { runtime ->
            InstrumentationRegistry.getInstrumentation().runOnMainSync { (runtime as AudioRuntime).engine.player.clearMediaItems() }
        }
        context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE).edit().clear().commit()

        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                val key = "${request.method} ${request.requestUrl?.encodedPath}"
                requests += key
                return routes[key]?.invoke() ?: MockResponse().setResponseCode(404).setBody("""{"error":"not found"}""")
            }
        }
        server.start()
        routes["GET /api/auth/status"] = json("""{"enabled":false,"authenticated":true}""")
        routes["GET /api/audio/enabled"] = json("""{"music":true,"podcasts":true}""")
        routes["GET /api/music/home"] = json(
            """{"trackCount":2,"albumCount":1,"pinned":[],"mixes":[],"recentlyPlayed":[],"heavyRotation":[],
                "recentlyAdded":[{"id":"al-1","title":"Test Tones","artist":"Sine Band","trackCount":2}]}""",
        )
        routes["GET /api/music/albums/al-1"] = json(
            """{"id":"al-1","title":"Test Tones","artist":"Sine Band","trackCount":2,"tracks":[
                {"id":"t1","title":"Tone One","artist":"Sine Band","album":"Test Tones","albumId":"al-1","trackNumber":1,"durationSeconds":6},
                {"id":"t2","title":"Tone Two","artist":"Sine Band","album":"Test Tones","albumId":"al-1","trackNumber":2,"durationSeconds":6}]}""",
        )
        routes["GET /api/music/playlists"] = json("[]")
        val episode = """{"id":"ep-1","podcastId":"pod-1","podcastTitle":"Test Cast","title":"Episode One","durationSeconds":6}"""
        routes["GET /api/podcasts/home"] = json("""{"upNext":[$episode],"subscriptions":[]}""")
        routes["GET /api/podcasts/queue"] = json("[$episode]")
        routes["GET /api/music/tracks/t1/stream"] = audio("tone-a.m4a")
        routes["GET /api/music/tracks/t2/stream"] = audio("tone-b.m4a")
        routes["GET /api/podcasts/episodes/ep-1/audio"] = audio("tone-a.m4a")
    }

    @After
    fun tearDown() {
        browser?.let { b ->
            runCatching { MediaController(context, b.sessionToken).transportControls.stop() }
            handler.post { b.disconnect() }
        }
        thread.quitSafely()
        // A test that left a reply hanging on purpose makes shutdown complain; that's fine.
        runCatching { server.shutdown() }
    }

    private fun pointAt(local: String) {
        context.getSharedPreferences("sentinel_core", Context.MODE_PRIVATE).edit()
            .putString(SentinelViewModel.SERVER_URL_KEY, local)
            .commit()
        // A fresh runtime picks the address up, as a cold start would.
        AudioRuntime::class.java.getDeclaredField("instance").apply { isAccessible = true }.set(null, null)
    }

    private fun connect(): MediaBrowser {
        val connected = CountDownLatch(1)
        var failed = false
        handler.post {
            browser = MediaBrowser(
                context,
                ComponentName(context, AudioPlaybackService::class.java),
                object : MediaBrowser.ConnectionCallback() {
                    override fun onConnected() = connected.countDown()
                    override fun onConnectionFailed() { failed = true; connected.countDown() }
                },
                null,
            ).also { it.connect() }
        }
        assertTrue("never connected", connected.await(20, TimeUnit.SECONDS))
        assertTrue("connection refused", !failed)
        return browser!!
    }

    // Subscribes and returns the first delivery that satisfies `until` (the service may send a
    // first answer and then, after notifyChildrenChanged, a better one - as the car would see it).
    private fun children(parent: String, timeoutSeconds: Long = 30, until: (List<MediaBrowser.MediaItem>) -> Boolean = { true }): List<MediaBrowser.MediaItem> {
        val latest = java.util.concurrent.LinkedBlockingQueue<List<MediaBrowser.MediaItem>>()
        val callback = object : MediaBrowser.SubscriptionCallback() {
            override fun onChildrenLoaded(parentId: String, children: MutableList<MediaBrowser.MediaItem>) {
                latest.add(children.toList())
            }
            override fun onError(parentId: String) { latest.add(emptyList()) }
        }
        handler.post { browser!!.subscribe(parent, callback) }
        val deadline = System.currentTimeMillis() + timeoutSeconds * 1000
        try {
            while (true) {
                val left = deadline - System.currentTimeMillis()
                if (left <= 0) fail("No good answer for \"$parent\"")
                val got = latest.poll(left, TimeUnit.MILLISECONDS) ?: fail("No answer for \"$parent\"").let { emptyList() }
                if (until(got)) return got
            }
        } finally {
            handler.post { browser!!.unsubscribe(parent, callback) }
        }
    }

    private val MediaBrowser.MediaItem.title: String get() = description.title.toString()

    // From Android 15 an app may only take audio focus while it's on screen (or bound by an app
    // that is - in a car, Android Auto, which sends the play command). Here the "car" is a client
    // inside the app's own background process, which Android refuses ("AS.HardeningEnforcer:
    // Focus request DENIED ... procState:4" - a foreground service alone isn't enough). So the
    // app is put on screen from the shell first (an activity start from a background app would
    // be blocked), and what's under test is everything else: browse ids resolving to a queue,
    // the stream request, decoding, Next.
    private fun controller(b: MediaBrowser): MediaController {
        // A set-up phone has already answered the notification prompt; left open, the prompt
        // sits on top of the app and it's no longer the app on screen.
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            InstrumentationRegistry.getInstrumentation().uiAutomation
                .grantRuntimePermission(context.packageName, android.Manifest.permission.POST_NOTIFICATIONS)
        }
        InstrumentationRegistry.getInstrumentation().uiAutomation
            .executeShellCommand("am start -W -n ${context.packageName}/com.sentinelcore.android.MainActivity")
            .close()
        Thread.sleep(2_000)
        return MediaController(context, b.sessionToken)
    }

    private fun waitForPlaying(controller: MediaController, title: String) {
        val deadline = System.currentTimeMillis() + 30_000
        while (System.currentTimeMillis() < deadline) {
            val playing = controller.playbackState?.state == PlaybackState.STATE_PLAYING
            val now = controller.metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)

            if (playing && now == title) return
            Thread.sleep(200)
        }
        fail("Never played \"$title\": state ${controller.playbackState?.state}, now ${controller.metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)}, last error ${controller.playbackState?.errorMessage}")
    }

    @Test
    fun theCarSeesTheTabsBrowsesAnAlbumAndPlaysIt() {
        pointAt(server.url("/").toString().trimEnd('/'))
        val b = connect()

        // The four tabs, right on the first answer even though this is a first run with nothing
        // remembered about which are on (the car never hears about a later correction).
        val tabs = children(b.root)
        assertEquals(listOf("Music", "Playlists", "Podcasts", "Downloads"), tabs.map { it.title })
        assertTrue(tabs.all { it.isBrowsable && !it.isPlayable })

        val music = children("home")
        assertEquals("Shuffle all", music.first().title)
        val album = music.single { it.title == "Test Tones" }
        assertTrue(album.isBrowsable && album.isPlayable)

        val tracks = children(album.mediaId!!)
        assertEquals(listOf("Tone One", "Tone Two"), tracks.map { it.title })
        assertEquals("tr:t1@album:al-1", tracks.first().mediaId)

        val controller = controller(b)
        controller.transportControls.playFromMediaId(tracks.first().mediaId, null)
        waitForPlaying(controller, "Tone One")
        assertTrue(requests.contains("GET /api/music/tracks/t1/stream"))

        // The album became the queue, so Next goes to track two.
        controller.transportControls.skipToNext()
        waitForPlaying(controller, "Tone Two")
    }

    @Test
    fun podcastsUpNextPlaysFromTheCar() {
        pointAt(server.url("/").toString().trimEnd('/'))
        val b = connect()

        val podcasts = children("podcasts")
        val episode = podcasts.single { it.title == "Episode One" }
        assertEquals("ep:ep-1@upnext", episode.mediaId)

        val controller = controller(b)
        controller.transportControls.playFromMediaId(episode.mediaId, null)
        waitForPlaying(controller, "Episode One")
        assertTrue(requests.contains("GET /api/podcasts/episodes/ep-1/audio"))
    }

    @Test
    fun downloadsTabExplainsWhenEmpty() {
        pointAt(server.url("/").toString().trimEnd('/'))
        connect()

        val downloads = children("downloads")
        assertEquals(listOf("No downloads"), downloads.map { it.title })
        assertTrue(!downloads.single().isPlayable)
    }

    @Test
    fun anUnreachableServerSaysSoInsteadOfSpinning() {
        pointAt("http://127.0.0.1:1")
        connect()

        val started = System.currentTimeMillis()
        val music = children("home", timeoutSeconds = 40)
        assertEquals(listOf("Can't reach Sentinel Core"), music.map { it.title })
        assertTrue("took ${System.currentTimeMillis() - started} ms", System.currentTimeMillis() - started < AudioPlaybackService.BROWSE_LIMIT_MS + 5_000)
    }

    @Test
    fun aServerThatNeverAnswersStillGetsAnAnswerToTheCar() {
        routes["GET /api/music/home"] = { MockResponse().setHeadersDelay(30, TimeUnit.SECONDS).setBody("{}") }
        pointAt(server.url("/").toString().trimEnd('/'))
        connect()

        val started = System.currentTimeMillis()
        val music = children("home", timeoutSeconds = 40)
        val took = System.currentTimeMillis() - started
        assertEquals(listOf("Can't reach Sentinel Core"), music.map { it.title })
        assertTrue("took $took ms", took < AudioPlaybackService.BROWSE_LIMIT_MS + 5_000)
    }
}
