package com.sentinelcore.android.car

import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

// A Sentinel Core server small enough to live inside a unit test: canned JSON per route, an
// optional delay per route, and a record of every request so a test can check what the car sent.
// Unknown routes answer 404, which the app reads the way it reads an older server.
class StubSentinelServer {
    data class Request(val method: String, val path: String, val body: String)

    private class Reply(val status: Int, val body: String, val delayMillis: Long)

    private val routes = ConcurrentHashMap<String, Reply>()
    val requests = CopyOnWriteArrayList<Request>()
    private val server = MockWebServer()

    val url: String get() = "http://127.0.0.1:${server.port}"

    fun start(): StubSentinelServer {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                val method = request.method.orEmpty()
                val path = request.requestUrl?.encodedPath.orEmpty()
                requests += Request(method, path, request.body.readUtf8())
                val reply = routes["$method $path"] ?: Reply(404, """{"error":"not found"}""", 0)
                return MockResponse()
                    .setResponseCode(reply.status)
                    .setHeader("Content-Type", "application/json")
                    .setBody(reply.body)
                    .setHeadersDelay(reply.delayMillis, TimeUnit.MILLISECONDS)
            }
        }
        server.start(java.net.InetAddress.getByName("127.0.0.1"), 0)
        // Every connection starts with this probe.
        on("GET", "/api/auth/status", """{"enabled":false,"authenticated":true}""")
        return this
    }

    fun stop() = runCatching { server.shutdown() }.let { }

    fun on(method: String, path: String, body: String, status: Int = 200, delayMillis: Long = 0): StubSentinelServer {
        routes["$method $path"] = Reply(status, body, delayMillis)
        return this
    }

    fun sent(method: String, path: String): List<Request> = requests.filter { it.method == method && it.path == path }

    companion object {
        // A house with an alarm, a Device Hub of MQTT devices (one of each kind the car treats
        // differently, plus two it must leave out), two scenes and a recording schedule (which
        // isn't a scene), and a few alerts.
        fun house(): StubSentinelServer = StubSentinelServer().start()
            .on("GET", "/api/alarm/status", alarm())
            .on("GET", "/api/device-hub/enabled", """{"enabled":true}""")
            .on("GET", "/api/devices", devices())
            .on("GET", "/api/schedules", schedules())
            .on("GET", "/api/activity", activity())

        fun alarm(status: String = "disarmed", mode: String = "", requirePin: Boolean = false, openSensor: Boolean = false) = """
            {"enabled":true,"hubEnabled":true,"status":"$status","mode":"$mode","ready":true,
             "sensors":[{"id":"s1","name":"Back door","zone":"perimeter","enabled":true,"state":"${if (openSensor) "open" else "closed"}"}],
             "issues":[],"config":{"requirePinToArm":$requirePin}}
        """.trimIndent()

        fun device(id: String, name: String, kind: String, power: String = "off", room: String = "Garage",
                   enabled: Boolean = true, commandable: Boolean = true, available: Boolean = true,
                   momentary: Boolean = false, payloadStop: String = "") = """
            {"id":"$id","name":"$name","kind":"$kind","room":"$room","enabled":$enabled,"power":"$power",
             "available":$available,"commandable":$commandable,"momentary":$momentary,"payloadStop":"$payloadStop",
             "commandTopic":"home/$id/set","stateTopic":"home/$id/state","payloadOn":"ON","payloadOff":"OFF"}
        """.trimIndent()

        fun devices(vararg extra: String) = "[" + (listOf(
            device("light-1", "Porch light", "light", power = "on", room = "Front"),
            device("garage-1", "Garage door", "garage", power = "off", payloadStop = "STOP"),
            device("lock-1", "Front door lock", "lock", power = "on", room = "Front"),
            device("bell-1", "Gate buzzer", "button", momentary = true),
            device("sensor-1", "Freezer temp", "sensor", commandable = false),
            device("old-1", "Old switch", "switch", enabled = false),
        ) + extra).joinToString(",") + "]"

        fun schedules() = """
            [{"id":"sc-1","name":"Lights off","enabled":true,"kind":"daily","action":{"type":"device","deviceAction":"off"}},
             {"id":"sc-2","name":"Open the gate","enabled":true,"kind":"daily","action":{"type":"mqtt","mqttTopic":"gate/set"}},
             {"id":"sc-3","name":"Record at night","enabled":true,"kind":"daily","action":{"type":"record"}},
             {"id":"sc-4","name":"Disabled scene","enabled":false,"kind":"daily","action":{"type":"device"}}]
        """.trimIndent()

        fun activity() = """
            [{"id":"a1","kind":"analysis","camera":"Driveway","occurredAt":"2026-10-20T12:00:00Z","status":"done","notified":true,"eventType":"person","summary":"A person walked up the driveway."},
             {"id":"a2","kind":"analysis","camera":"Backyard","occurredAt":"2026-10-20T13:00:00Z","status":"done","notified":true,"eventType":"animal","summary":"A deer."},
             {"id":"a3","kind":"motion","camera":"Garage","occurredAt":"2026-10-20T14:00:00Z","status":"done","notified":false}]
        """.trimIndent()
    }
}
