# Multi-zone trigger zones — client notes

The server, the web dashboard, and the iOS/macOS and Android editors all support **several trigger
zones per camera**, each with its own minimum object size. tvOS has no zone editor (it never did) —
it models the full wire format and shows the zone name on trigger cards, nothing more.

## Why the feature exists

Perspective. The same person covers wildly different pixel counts depending on distance from the
camera, so one threshold can never serve a whole scene. Measured on the gate camera on 2026-08-02:

| Subject | Blob size on the 320x180 detector grid |
|---|---|
| Person standing at the gate (~30 ft) | **~161 px** |
| Person / vehicle close to the camera | 230–430 px |
| Moth on the lens at night, lit by the IR illuminator | 230–3400 px |

With a single floor set high enough to ignore the night noise, nobody walking to the gate could ever
trigger the camera. Splitting the view into a far zone and a near zone, each with its own floor, is
what makes both work at once.

## Wire format

`GET`/`PUT /api/cameras/{id}/trigger-config`

```jsonc
{
  "enabled": true,

  // LEGACY MIRROR of zones[0]. Still sent, still accepted, never removed.
  "zonePolygon": [ { "x": 0.41, "y": 0.52 }, ... ],
  "minTriggerArea": 0.0015,

  // The real list. Added for multi-zone.
  "zones": [
    { "name": "Gate",     "polygon": [ { "x": 0.41, "y": 0.52 }, ... ], "minTriggerArea": 0.0015 },
    { "name": "Driveway", "polygon": [ { "x": 0.20, "y": 0.70 }, ... ], "minTriggerArea": 0.0090 }
  ],

  // ...unchanged: cooldownSeconds, maxTriggersPerMinute, shadowSensitivity, recordClips,
  // clipPreSeconds, clipPostSeconds, sendToAlerts, alertProfile, sensor* ...
}
```

`minTriggerArea` is a fraction of the **whole frame**, not of the zone — unchanged from before.
Multiply by `320 * 180 = 57600` to get the detector pixels the threshold is actually compared
against. Every editor shows that number next to the size control, because it is far easier to
reason about than the fraction.

`GET /api/triggers` returns `zoneName` on each event (null for companion captures, sensor and
manual triggers, and anything recorded before this feature). All clients show it on trigger cards
in place of the old hardcoded "motion in zone" label, falling back to the old wording when null.

## Two rules that must not be broken

**1. Never stop sending `zonePolygon` and `minTriggerArea`.**
They mirror `zones[0]`. Swift's synthesized `Codable` throws on a *missing* key it declares
non-optional, so dropping either one breaks every installed copy of the app. Unknown keys are
ignored by both Swift and the Android `org.json` parsing, which is why adding `zones` was safe in
the first place.

**2. `zones` absent and `zones` empty mean different things.**
Server-side (`TriggerEndpoints.ResolveZonesAsync`):

- `zones` **absent/null** → "this client doesn't know about zones". The server updates zone[0] from
  the legacy fields and **preserves zones[1..] untouched**. This is what stops an old app silently
  deleting someone's extra zones when they nudge the polygon from their phone.
- `zones` **present** → authoritative, replaces the whole list.

So the updated editors send the full list every time, and an installed pre-multi-zone build keeps
omitting the key entirely. Do **not** send `"zones": []` as a placeholder — that reads as "delete
all but the primary". The Swift models keep `zones` optional (nil omits the key via
`encodeIfPresent`) and the Android `toJson` only writes the key when the list is non-null, for
exactly this reason.

Regression test for this: `MultiZoneTriggerTests.PutFromAnOlderClient_UpdatesThePrimaryZoneAndKeepsTheRest`.

## How the editors behave (kept consistent across web, iOS/macOS, Android)

- A zone selector row labelled `name · NNNpx`, where the pixel figure is
  `minTriggerArea * 57600` on the detector grid.
- Add / rename (50-char cap, matching server validation) / delete. **A camera always keeps at
  least one zone** — the legacy mirror needs a primary to mirror. Every editor refuses the last
  delete with an explanatory message.
- All zones drawn over the reference frame; the selected one solid with vertices, the others
  dashed and faint, each labelled at its first vertex. Points append to the selected zone.
- The minimum-size control applies to the selected zone only, with a detector-pixel readout.
- A new zone inherits the current zone's size rather than the stock 2% — you're usually adding a
  zone for a different distance, so it's a more useful starting point.

## Sensor binding round-trip

The native `TriggerConfig` models also carry `sensorDeviceId` / `sensorTriggerOn` /
`sensorCooldownSeconds` untouched through every save. They're configured on the web dashboard
only, and the PUT overwrites the whole record rather than merging — a client that omitted them
would silently unbind the camera's Device Hub sensor on every zone edit (which is exactly what the
pre-multi-zone mobile builds did).

Files: `ios/SentinelCore/Views/TriggerZoneEditorView.swift`, `ios/SentinelCore/Models/Models.swift`
(`TriggerConfig`), the tvOS mirror in `appletv/SentinelCoreTV/Models/Models.swift`,
`android/app/src/main/java/com/sentinelcore/android/ui/screens/CameraZoneEditorScreen.kt`,
`data/Models.kt`, and the JSON layer in `data/SentinelRepository.kt`.
