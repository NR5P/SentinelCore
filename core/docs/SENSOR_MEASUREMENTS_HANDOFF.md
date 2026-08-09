# Numeric sensor measurements — client notes

The server and the web dashboard now read and show **the numbers a sensor reports** — temperature,
humidity, battery, power draw — instead of only whether a device is on or off.

**Status: done.** iOS, tvOS and Android now read the field too, built and tested on a Mac with the
Swift and Android toolchains. What follows is kept as the record of the wire format and the rules
the three clients hold to, not as outstanding work.

## Why the feature exists

Every device in the Device Hub was binary. `DeviceStateReading` carried a power state and a
brightness and nothing else, so a Zigbee2MQTT climate sensor — already discovered, already
subscribed, already parsed — rendered as a permanently blank tile. The temperature was arriving on
the broker every few minutes and being dropped on the floor.

This was never a missing feature so much as an unfinished one: `MqttDeviceDiscovery` has mapped
both `binary_sensor` and `sensor` to kind `sensor` since the Device Hub shipped.

## Wire format

Two endpoints carry it, both **additive** — no existing field changed shape or meaning:

- `GET /api/devices` — the full list, each device gains `measurements`
- `GET /api/devices/state` — the poll the clients already run while the tab is open

```jsonc
{
  "id": "…",
  "power": "unknown",      // a numeric sensor has no on/off, and says so
  "brightness": null,
  "available": true,
  "lastSeen": "2026-08-08T10:31:02Z",

  // NEW. Always present, empty for the switches and lamps that report nothing.
  "measurements": [
    { "key": "battery",     "value": 94   },
    { "key": "humidity",    "value": 48   },
    { "key": "linkquality", "value": 120  },
    { "key": "temperature", "value": 21.5 }
  ]
}
```

**Sorted by key, server-side**, so the rows don't reshuffle between polls. Render in the order
given; do not re-sort.

Values are the raw numbers the device published. There are **no units and no friendly labels** —
that is deliberate, not an oversight. `unit_of_measurement` seeding from Home Assistant discovery
is a later item; until it lands, the device's own key is the honest label. Do not invent units by
guessing from the key name.

Keys are whatever the payload used: `temperature`, `battery`, `linkquality`, and one level of
nesting flattened with a dot for Tasmota's power meter — `ENERGY.Power`, `ENERGY.Total`.

## Rules that must not be broken

**1. `measurements` must be optional in Swift.**
Swift's synthesized `Codable` throws on a *missing* key it declares non-optional, so an app that
declares it required stops decoding entirely against any server older than this change — the same
trap documented in `MULTI_ZONE_CLIENT_HANDOFF.md`. Declare `var measurements: [HubMeasurement]?`
and treat nil as empty. Android's `org.json` parsing and the web client both ignore unknown or
absent keys already.

**2. A sensor is not "off".**
`power` is `"unknown"` for a numeric sensor and always will be — it has no on/off to report. The
existing clients render that as "state unknown" and dim the tile to the same 0.55 opacity they use
for an unavailable device, which makes a perfectly healthy thermometer look broken. The web
dashboard now treats a non-commandable device with `power == "unknown"` and at least one
measurement as **"reporting"**, at full opacity. Match that; the wording is worth keeping identical
across clients.

**3. Don't filter the noisy keys.**
`linkquality`, `voltage` and friends come through because harvesting everything is what makes a
Z2M sensor work with zero setup. Hiding specific keys is a display preference nobody has designed
yet — showing them all is the current, intended behaviour.

## What each client needs

| Client | Model | State apply | View |
|---|---|---|---|
| iOS | `ios/SentinelCore/Models/Models.swift:391` (`HubDevice`), `:453` (`HubDeviceState`) | `ios/SentinelCore/Views/DeviceHubView.swift:185` | `DeviceHubView.swift:301-340` (tile), `:393` (`statusText`) |
| tvOS | `appletv/SentinelCoreTV/Models/Models.swift:359`, `:385` | `appletv/SentinelCoreTV/App/AppModel.swift:293` | `appletv/SentinelCoreTV/Views/DeviceHubView.swift` |
| Android | `android/.../data/Models.kt:315` (`HubDevice`), `:359` (`HubDeviceState`) | `SentinelViewModel.kt` / `SentinelRepository.kt` | Device Hub composable |

Each was the same three-step change: add the field to both models, copy it across in the poll
handler beside `power`/`brightness`/`available`, and render the rows on the tile. Note tvOS applies
state in *two* places — the startup prime in `AppModel.swift` as well as the screen's own poll — and
missing either one leaves the tile stale in a way the other client's single site doesn't.

The shared rules are covered by `android/app/src/test/java/com/sentinelcore/android/data/HubMeasurementTest.kt`.
Both Swift clients were checked against the same cases, including that an older server sending no
`measurements` key still decodes.

A shared decimal rule, so the three clients agree with the web:

```
round to 2 decimals, then drop trailing zeros
21.500000000000004 -> "21.5"      48.0 -> "48"      1.23 -> "1.23"
```

The float noise is real — it comes from brightness-style scaling upstream, not from the sensor.

## Reference implementation

`core/SentinelCore.Web/wwwroot/devices.js` is the one to copy:

- `refreshState()` — one added line, copies `state.measurements` onto the device
- `buildTile()` — an empty `.device-measurements` container between status and controls
- `applyTileState()` — computes `reporting`, then calls `renderMeasurements`
- `renderMeasurements()` — rebuilds rows **only when the set of keys changes**, then updates values
  in place. Blowing the DOM away on every five-second poll fights anyone trying to read it. The
  mobile clients get this for free from SwiftUI/Compose diffing; the note is here so nobody
  "simplifies" the web version into an `innerHTML` rewrite.
- `formatMeasurement()` — the decimal rule above

Styling lives in `styles.css` under `.device-measurements`, including `:empty { display: none }` so
a lamp with nothing to report doesn't grow an empty gap, and `font-variant-numeric: tabular-nums`
so a value ticking 21.4 → 21.5 doesn't shuffle the row beside it.

## Verifying without a real sensor

No Zigbee device needed — publish to the broker by hand. SentinelCore *is* the broker (port 1883),
so this works against a running install:

```bash
mosquitto_pub -h 127.0.0.1 -t 'sentinel-test/barn' \
  -m '{"temperature":21.5,"humidity":48,"battery":94}'
```

Add a device in the Device Hub with kind `sensor`, state topic `sentinel-test/barn`, and leave
schema on `raw` — that is exactly what discovery produces for a real sensor, and the parser reads
the JSON anyway. Republish with a *partial* payload (`{"battery":91}`) to confirm the client keeps
showing the temperature: the server merges readings rather than replacing them, and a client that
re-renders from a stale local copy can undo that.

## Server-side notes, for context

Four commits' worth of behaviour the clients depend on:

- `DeviceStateReading.Measurements` — harvested from the payload, not declared per device. One Z2M
  sensor reports five or six at once and naming each before any appear would make the commonest
  device the most work to set up.
- `MqttDeviceBus` merges readings copy-on-write. Z2M and Tasmota both publish partial payloads.
- A `sensor` is exempt from needing a State Value Template — a thermometer has no key holding
  ON/OFF, and demanding one meant the only way to save one was to invent a key.
- A **raw-schema device publishing a JSON document is read as one**. Home Assistant discovery has
  no `schema` field for a `sensor` component, so every discovered sensor is filed as `raw` and then
  publishes JSON regardless. Without this the feature worked only for devices nobody owns.

Tests: `core/SentinelCore.Tests/DeviceHubTests.cs`, sections "Numeric measurements" and the
`MqttDeviceBus_AccumulatesMeasurementsAcrossPartialSensorPayloads` end-to-end test.
