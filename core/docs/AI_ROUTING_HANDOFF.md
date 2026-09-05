# Per-camera AI routing — client notes

A camera's trigger config now carries **two switches that change how its triggers are judged**:

- **`bypassFastLocal`** — skip the fast local detector and send every trigger straight to the full
  model.
- **`keepOnlyAlertable`** — decide whether a trigger was real by whether the AI *would have
  alerted*, rather than by whether anything was in the frame.

Server, web dashboard, iOS/macOS, Android and Windows all have them. **tvOS is the one surface
without a control** — it has a `saveTriggerConfig` on its client but no trigger editor to put the
switches in, and nothing calls it; it sends neither key, which the merge rule below makes safe.

**The clients are not broken without them.** The wire contract was built so an app that knows
nothing about these two cannot damage them (see [Wire format](#wire-format)) — the same shape the
night window uses, for the same reason. Adding the control is an enhancement, not a fix.

## Why the two switches exist

Both come from the same camera — a chicken coop — and the same night, 2026-09-03.

### `bypassFastLocal`

The fast local detector decides which frames are worth escalating, and at the coop it is the wrong
judge. rf-detr is COCO-trained: its entire animal vocabulary is bird/cat/dog/horse/sheep/cow/
elephant/bear, with **no raccoon, fox, opossum, weasel or coyote in it at all**. A predator in the
frame is therefore not "low confidence" — there is no class for it to score, so the frame reads as
empty and stops at the detector.

| That night at the coop | Count |
|---|---|
| Triggers fired | 697 |
| Reached the detector | 346 |
| Got past it | 40 |
| Cleared by the detector, never checked by anything that could name a predator | **306** |
| Of the 40 escalations, chickens (COCO class 16, `bird`) | **34** — ~95 min of `gemma3:4b` |

Nothing cheap separates a chicken from a hawk when both are `bird` and a raccoon is nothing, so the
switch is an **opt-out of the gate**, not a smarter gate.

> **Surface the cost where someone will read it.** Turning this on means every trigger costs a full
> analysis — around 140s. The zone's cooldown has to be raised to match. The same night showed what
> happens otherwise: the queue ran 85 minutes behind and **89 triggers were dropped unanalysed**,
> and a dropped trigger fails open, so its clip is kept forever. Off everywhere by default, and it
> should stay off nearly everywhere — the detector is the right gate for a driveway, where the
> thing being watched for is a person or a vehicle and it has good classes for both.

### `keepOnlyAlertable`

`TriggerConfirmation` asks "was anything in the frame" — a person, vehicle, package or animal. That
is right for a driveway, where anything in shot is worth a look. At the coop it is wrong twice
over: the chickens **are** animals, they are **always** in shot, and they are the reason the camera
is pointed there. They confirm every trigger, so the Triggers tab fills with them and every clip is
kept, while the raccoon the camera exists for is one row in the same pile.

The flag that already told them apart is the model's own `notification_recommended`. On the coop's
prompt profile it was correct on **all 40** analyses of 2026-09-03 — false for every chicken and
for the owner walking through the run — and it is what the notification itself gates on. Of those
40, 30 had a visible subject and **none** warranted telling anyone. The tab, the clip and the alert
disagreed with each other, and only the alert was right.

With the switch on, ordinary chicken activity is rejected: no event, no clip, no alert. A predator
gets all three. **The snapshot is kept either way**, so "show hidden" still shows everything that
fired — the thing that tells a well-aimed coop zone from one that caught the road.

Off by default, because this narrows what counts as real, and narrowing it on anyone's behalf is
how footage goes missing without them asking.

## Wire format

Two fields added to the trigger-config payload, on both `GET /api/cameras/{id}/trigger-config` and
`PUT` of the same:

```jsonc
{
  // …everything that was already here, unchanged…
  "bypassFastLocal": false,    // off unless someone turns it on
  "keepOnlyAlertable": false   // off unless someone turns it on
}
```

Semantics:

- The two are **independent**. The coop wants both; a driveway wants neither; either alone is a
  legitimate configuration.
- `bypassFastLocal` only does anything while `sendToAlerts` is on (nothing analyses the frame
  otherwise), and only when Fast Local Detection is the primary backend.
- `bypassFastLocal` is deliberately **not** the existing gate-check flag. That one skips the
  detector *and* exempts the result from `MinimumConfidence`; a camera that just wants the full
  model has no business skipping the confidence floor too.
- `keepOnlyAlertable` reads the model's `notification_recommended` rather than
  `AlertNotificationPolicy.ShouldNotify`, which also suppresses duplicates — a repeat sighting of a
  real fox is still footage worth keeping.
- Every fail-open path is untouched: an analysis that was slow, errored, unreachable or never ran
  still confirms. Only a clean "the AI looked, and would not have told you" rejects.

### The one rule a client must not break

`bypassFastLocal` and `keepOnlyAlertable` are **nullable on the wire**, and absent means *"this
client doesn't know about the setting"* — **not** "turn it off".

This is the same trap `zones` and the night window already fell into. A `PUT` from a client that
omits the keys keeps whatever the stored row said; the server merges rather than overwrites
(`TriggerEndpoints`, `dto.BypassFastLocal ?? existing?.BypassFastLocal ?? false`). If a mobile
build starts sending a plain `false` on every zone edit, it will route the coop back through the
detector and re-widen what it keeps every time someone nudges a polygon from their phone, and
nothing anywhere will say so — the chickens would simply come back the next night.

So: either send each key with the user's real choice, or send neither.

## Where the control lives

An "AI Routing" section in each trigger zone editor, immediately after the alert prompt profile —
two switches, each with the cost spelled out under it:

| Surface | File |
|---|---|
| Web | `core/SentinelCore.Web/wwwroot/index.html` (search `zone-bypass-fastlocal`), wired in `wwwroot/triggers.js` |
| iOS / macOS | `ios/SentinelCore/Views/TriggerZoneEditorView.swift`, fields in `ios/SentinelCore/Models/Models.swift` |
| Android | `android/app/src/main/java/com/sentinelcore/android/ui/screens/CameraZoneEditorScreen.kt`, fields in `data/Models.kt`, JSON in `data/SentinelRepository.kt` |
| Windows | `windows/SentinelCore.App/Views/TriggerZoneEditorWindow.xaml` + `.xaml.cs`, fields in `windows/SentinelCore.Client/Models/Models.cs` |
| tvOS | **not built** — no trigger editor exists there |

Every client reads the two keys through a resolved helper that mirrors `resolvedZones` and
`resolvedNightWindow` — `resolvedBypassFastLocal` / `resolvedKeepOnlyAlertable` — so an absent key
becomes the server's own default (off) rather than being confused with an explicit choice. The
editors then send both keys on every save, because a screen that knows about a switch owns its
value.

Client-side checks worth keeping if these are rewritten: `TriggerAiRoutingTest` (Android, the
absent-vs-off distinction and the independence of the two flags), the two
`Saving_a_trigger_config_*` tests and `The_AI_routing_flags_are_read_back_as_the_server_sent_them`
(Windows, which pin null *off the wire* and an explicit `false` *on* it), and for Swift the
standalone `Models.swift` decode check described in the repo's verification notes — it is what
proves nil still omits the keys rather than encoding `false`.

## Server-side reference

| Thing | Where |
|---|---|
| Config fields + SQLite migration | `SentinelCore.Web/TriggerStore.cs` |
| Merge rule + DTO | `SentinelCore.Web/TriggerEndpoints.cs` |
| Where the flag is put on the alert | `SentinelCore.Web/CameraTriggerService.cs`, `TriggerZoneMonitor.cs` |
| The routing decision | `SentinelCore/FailoverVisionClient.cs`, `SentinelCore/Program.cs` |
| The confirmation verdict | `SentinelCore.Web/TriggerConfirmationService.cs` |
| Tests | `SentinelCore.Tests/TriggerBypassFastLocalTests.cs`, `TriggerConfirmationServiceTests.cs`, `FastLocalTests.cs` |

Both flags travel to the analyzer on the same channel `AlertProfile` already uses — a column on
`TriggerConfigs`, a nullable DTO field, a multipart field set only when asked, and one init-only
property on `IncomingAlert` — since that is the proven per-trigger path across the process
boundary.
