# Overnight trigger pause — client notes

A camera's trigger zone can now be told to **hold back every motion trigger between two hours of
the day**. Server and web dashboard are done, built and tested on the Linux box. iOS, tvOS and
Android have no control for it yet — that is the outstanding work, and it needs a machine with the
Swift and Android toolchains.

**The clients are not broken without it.** The wire contract was built so an app that knows nothing
about this feature cannot damage it (see [Wire format](#wire-format)). Adding the control is an
enhancement, not a fix.

## Why the feature exists

Measured on the gate camera over the three days to 2026-08-14:

| Window | Triggers | AI-confirmed | Rate |
|---|---|---|---|
| Day 07:00–20:00 | 1,100 | 274 | **24.9%** |
| Night 21:00–06:00 | 290 | 0 | **0.0%** |

Night was 21% of the motion and produced **not one** confirmed subject. The cause is the IR
illuminator lighting up something close to the lens — a spider web, dust, condensation; sampled
night frames show heavy IR haze and large soft halos.

> **Update 2026-08-17 — the zero above is real but it is a four-night stretch, not a property of
> nights. Do not build the client control believing the window is free.**
>
> The table measures 2026-08-11 → 08-14, and those four nights genuinely confirmed nothing. Over
> the whole post-calibration record the number is not zero:
>
> | Window (since 2026-08-06) | Triggers | AI-confirmed | Rate |
> |---|---|---|---|
> | Day 07:00–20:59 | 2,500 | 321 | 12.8% |
> | Night 21:00–06:59 | 1,510 | **12** | **0.79%** |
>
> Twelve confirmed subjects fall inside the *default* 21→7 window, and every one of them is a
> `gemma3:4b` escalation at 0.7–0.8 confidence describing a person or a vehicle at the gate — not
> fastlocal noise that slipped through:
>
> ```
> 08-05 22:20  a person standing on the right side of the gate
> 08-05 22:20  a vehicle parked near the gate, a person outside it
> 08-07 22:35  a person standing in the driveway near the gate
> 08-08 06:39  a motorcycle parked in the narrow entrance outside the gate
> 08-10 05:44  a person walking on the inside driveway towards the gate   (x3)
> 08-15 22:35  an unidentified person on the property side of the gate
> 08-16 06:22  a person walking on the driveway side of the gate
> 08-16 06:25  a person standing near the gate on the driveway side
> 08-16 21:21  a person standing on the property side of the gate
> 08-16 21:23  a person standing on the outside of the gate
> ```
>
> Five of those twelve landed in the two most recent nights, so this is not only an artefact of
> older data — the rate is low, not nil, and the events it discards are exactly the ones the camera
> is there for. An enabled window gives them no snapshot, no clip and no alert.
>
> None of this argues the feature is wrong. A 0.79% night rate against 12.8% by day is still a
> ~16x difference, and 1,498 of those 1,510 night triggers were noise. It argues that the honest
> framing is a **trade** — roughly one confirmed subject lost every four nights in exchange for
> ~99% less overnight noise — rather than the free win "not one confirmed subject" implies. Whoever
> adds the client control should surface it as such, and it is worth reading alongside the
> `MaximumConfirmedPercent` guard the spider-web advisor uses for the same reason.
>
> The feature remains **off** on the only configured camera (`NightWindowEnabled=0`); nothing about
> the shipped behaviour changed with this note.

The reason this needed a *new* control rather than a tuning change is the part worth carrying
forward: **minimum object size cannot separate the night noise from real subjects.** Over that
window confirmed blobs averaged 67 detector pixels while rejected ones averaged 84 — the noise is
*larger* than the real subjects, so raising the floor is worse than useless. Modelled against a
slightly earlier sample where the two were level (88px vs 87px):

| Floor | Noise removed | Real detections kept |
|---|---|---|
| 60px | 37% | 139/244 |
| 100px | 76% | 34/244 |
| 120px | 88% | 22/244 |

Raising the floor discards people at the same rate as webs, because at that distance they are the
same size on screen. Time of day is the one axis on which the two genuinely differ. See also
[TRIGGER_SIZE_CONTROL_HANDOFF.md](TRIGGER_SIZE_CONTROL_HANDOFF.md) for the measured size table.

## Wire format

Three fields added to the trigger-config payload, on both
`GET /api/cameras/{id}/trigger-config` and `PUT` of the same:

```jsonc
{
  // …everything that was already here, unchanged…
  "nightWindowEnabled": false,   // off unless someone turns it on
  "nightStartHour": 21,          // 0-23, local time on the SERVER's clock
  "nightEndHour": 7              // 0-23, exclusive - triggers resume at this hour
}
```

Semantics, all three enforced server-side and covered by `TriggerNightWindowTests`:

- The window **wraps past midnight**. `21 -> 7` means 21:00 through 06:59. A naive
  `hour >= start && hour < end` matches nothing for that range — handle the wrap.
- **Start == End is zero-width** (suppresses nothing), never "all day". A camera that goes
  permanently blind because someone enabled the feature before setting hours is the worst
  available failure here. The server rejects saving equal hours while enabled.
- Hours outside 0–23 are rejected with a message rather than clamped.
- The hours are the **server's** local clock, not the phone's and not the camera's. (The gate
  camera's own OSD clock is currently ~3 hours off; nothing reads it.)

### The one rule a client must not break

`nightWindowEnabled`, `nightStartHour` and `nightEndHour` are **nullable on the wire**, and absent
means *"this client doesn't know about the night window"* — **not** "turn it off".

This is the same trap `zones` already fell into. A `PUT` from a client that omits the keys keeps
whatever the stored row said; the server merges rather than overwrites
(`TriggerEndpoints.ResolveNightWindow`). If a mobile build starts sending a plain
`"nightWindowEnabled": false` on every zone edit, it will switch the window off every time someone
nudges a polygon from their phone, and nothing anywhere will say so — the flood would simply come
back the next night.

So: either send all three keys with the user's real choice, or send none of them.

## What to build

A checkbox and two hour fields in the trigger zone editor, alongside the existing
"Save a video clip around each trigger" / "Send triggers to the AI and alert pipeline" switches:

- `ios/SentinelCore/Views/TriggerZoneEditorView.swift`
- `android/app/src/main/java/com/sentinelcore/android/ui/screens/CameraZoneEditorScreen.kt`

The web dashboard's version is in `core/SentinelCore.Web/wwwroot/index.html` (search
`zone-night-window`) with its load/save wiring in `wwwroot/triggers.js` — worth reading for the
copy, and for the `hourOrDefault` helper, which exists because the usual `parseInt(...) || 0` idiom
silently turns a legitimate hour 0 into a fallback.

An hour stepper or a wheel picker is a better fit than a free-text number field on a phone. The
range is 24 discrete values; there is no reason to let someone type `47`.

## Server-side reference

| Thing | Where |
|---|---|
| The window predicate and `EndsAt` | `SentinelCore.Web/TriggerNightWindow.cs` |
| Config fields + SQLite migration | `SentinelCore.Web/TriggerStore.cs` |
| The suppression point | `TriggerZoneMonitor.MaybeTriggerAsync` |
| Merge rule + validation | `SentinelCore.Web/TriggerEndpoints.cs` |
| Tests | `SentinelCore.Tests/TriggerNightWindowTests.cs` |

Suppression happens **before** the cooldown/rate-limit reservation, so a held-back trigger doesn't
consume a slot a real one might want when the window closes. Nothing is captured, recorded, or sent
for analysis while the window is active — that is the point, since the wasted GPU time was the cost
being removed. A sensor binding (`SensorDeviceId`) is a separate path and still fires normally.

The journal notes the pause at most every 30 minutes, with the count held back and the hour it
resumes, so a quiet camera can be told from a dead one:

```
Camera <id> (gate camera front door): motion triggers are paused by the night window
(21:00-07:00) and resume at 07:00. 34 trigger(s) held back since the last note.
```
