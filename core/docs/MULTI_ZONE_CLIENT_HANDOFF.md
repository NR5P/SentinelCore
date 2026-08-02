# Multi-zone trigger zones — native client handoff

The server and the web dashboard support **several trigger zones per camera**, each with its own
minimum object size. The iOS, tvOS and Android apps do **not** yet — they still edit a single zone.

Nothing is broken in the meantime: the API is deliberately additive, and an unmodified app keeps
working against a multi-zone server. This document is what a future client change needs to know.

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
against. The web editor shows that number next to the size control, and it is far easier to reason
about than the fraction; the native editors should show it too.

`GET /api/triggers` now also returns `zoneName` on each event (null for companion captures, sensor
and manual triggers, and anything recorded before this feature). The web Triggers tab shows it in
place of the old hardcoded "motion in zone" label.

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

So a client that adds multi-zone editing must send the full list every time. A client that hasn't
been updated must keep omitting the key entirely — do **not** send `"zones": []` as a placeholder,
that reads as "delete all but the primary".

Regression test for this: `MultiZoneTriggerTests.PutFromAnOlderClient_UpdatesThePrimaryZoneAndKeepsTheRest`.

## What a native editor needs

Roughly what `wwwroot/triggers.js` does now:

- A zone selector (the web build uses a row of tabs labelled `name · NNNpx`).
- Add / rename / delete. **A camera must always keep at least one zone** — the legacy mirror needs a
  primary to mirror. The web build refuses the last delete with an explanatory message.
- Draw all zones over the reference frame, with the selected one emphasised and the others dimmed,
  so their relative placement is visible while editing one.
- The minimum-size control applies to the **selected zone only**.
- Points are appended to the selected zone.

Files: `ios/SentinelCore/Views/TriggerZoneEditorView.swift`, `ios/SentinelCore/Models/Models.swift`
(`TriggerConfig`), the tvOS mirror in `appletv/SentinelCoreTV/Models/Models.swift`, and
`android/app/src/main/java/com/sentinelcore/android/ui/screens/CameraZoneEditorScreen.kt` +
`data/Models.kt`.

Adding an optional `zones` field to the Swift/Kotlin models is safe on its own and can land before
any UI work — decoding it and echoing it back unchanged would already be an improvement over the
current behaviour, which relies on the server-side preservation rule above.
