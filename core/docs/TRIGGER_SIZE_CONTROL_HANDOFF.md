# Minimum trigger size — floor is screen-dependent, and the control is unusable small

Reported 2026-08-10 from the iPhone app: the minimum trigger size bottoms out at **211 detector
pixels** and will not go lower, and while dragging the circle small your fingertip covers the very
thing you are trying to see.

> **Status: done — shipped 2026-08-10 in `7f8a444`, on all three clients.** Both bugs below are
> fixed. This file is kept as the record of why the control has the shape it does, not as
> outstanding work; if you are here looking for something to pick up, there isn't any.
>
> Two details differ from what was proposed below, and the shipped values are the ones to trust:
> the floor is **4 detector pixels, not 20**, and **the web dashboard was fixed too** rather than
> being left alone. Both are noted inline where they come up.

The original note said nothing was committed and that the work needed a machine with a Swift and
Kotlin toolchain. That was true when it was written — the Linux box has neither — and it stopped
being true when `7f8a444` landed from a Mac.

## Bug 1 — the floor is expressed in the wrong unit

Both mobile editors clamp the drag to a **minimum radius of 10 in view coordinates**:

- `ios/SentinelCore/Views/TriggerZoneEditorView.swift`, in `MinAreaCircleOverlay`'s drag gesture:
  `min(max(sqrt(dx * dx + dy * dy), 10), maxRadius)`
- `android/app/src/main/java/com/sentinelcore/android/ui/screens/CameraZoneEditorScreen.kt`, in
  `MinAreaCircleOverlay`'s `detectDragGestures`: `sqrt(dx * dx + dy * dy).coerceIn(10f, maxRadius)`

The area is then derived from that radius as a fraction of the pane, so **the effective floor in
detector pixels depends entirely on how large the pane is on that device**. Bigger screen, lower
floor. There is no single minimum — it is different on every phone, and different again on iPad and
on the web dashboard.

Working it backwards for the reported 211: a floor of `π · 10² / (w · h)` of the frame, times the
57600-pixel detector grid, equals 211 when the pane is about 390 x 220 — an iPhone-width content
pane. That is the whole explanation for the number.

Why it matters beyond the arbitrariness: a person standing at the gate measures **~161 px** (see
[MULTI_ZONE_CLIENT_HANDOFF.md](MULTI_ZONE_CLIENT_HANDOFF.md) for the measured table). A floor of 211
puts the single most important subject in the scene *below the minimum the UI will let you set*.

**Fixed** by stating the limit in detector pixels, not view points, so it is identical everywhere.
The constant sits alongside the existing `detectorPixels` / `DETECTOR_PIXELS` and is **4**, not the
20 originally suggested here — below a couple of pixels a blob is indistinguishable from sensor
noise, and 4 is the first value comfortably clear of that. The ceiling is the full frame
(`DETECTOR_PIXELS`, 57600) rather than a separate 20,000 constant.

- `ios/SentinelCore/Views/TriggerZoneEditorView.swift` — `private let minDetectorPixels = 4.0`
- `android/.../CameraZoneEditorScreen.kt` — `private const val MIN_DETECTOR_PIXELS = 4.0`
- `core/SentinelCore.Web/wwwroot/triggers.js` — `const MIN_DETECTOR_PIXELS = 4;`

The shape of the fix, in each editor: convert the floor to an area fraction (`pixels /
detectorPixels`), turn that into a radius for the current pane, and clamp the drag against *that*
instead of the literal 10 — so the constraint lives in detector units and the view geometry is
derived from it, rather than the other way round. The resulting area is clamped to the same floor
instead of the old `0.0001`.

One trap worth keeping: the minimum radius is guarded against `maxRadius` (`coerceAtMost` / `min`)
so a very short pane can't produce an inverted range. Kotlin's `coerceIn` throws when min > max,
which would have been a crash on a small window rather than a bad value.

## Bug 2 — a circle you drag is the wrong control on a touch screen

The circle is dragged directly ("drag anywhere on the image"), which is fine when it is large and
useless when it is small — the target is under your finger.

**Fixed** by keeping the circle as the live preview and adding a slider as the precise control, on
all three clients. Both write the same `minTriggerArea`.

The slider is **logarithmic**, as proposed — 65% of its travel is below 2000 px and the old 211 px
floor sits at the 41% mark, so better than a third of the range is territory that could not be
asked for before. The mapping is `log(pixels / MIN) / log(DETECTOR_PIXELS / MIN)` in all three
places (`log` in Swift, `ln` in Kotlin, `Math.log` in JS).

Two things were added beyond what this note proposed, both because a finger can't drag a slider to
an exact value at the small end:

- **−/+ step buttons** beside the slider, moving one detector pixel at a time below 50 and 5% above
  it.
- **A dashed ring** marking the circle's position once it gets too small to see.

The existing "Ignores anything under N detector pixels" readout was already enough feedback, so no
second numeric field was added.

## Scope — as shipped

This section originally scoped the work to iOS/macOS and Android and said the server needed no
change. **Both turned out to be wrong**, and the shipped commit is wider than what is described
above:

- **All three clients were fixed**, not two. The web dashboard carried the same unit bug — a
  `Math.max(8, ...)` screen-pixel floor in the `pointermove` handler — and this note had left it
  alone on purpose. Fixing the two phones and not the browser would have meant a floor that
  differed by client, which is the exact complaint the report opens with, so it went in too.
- **The server did need a change.** `ConfigsEquivalent` in `TriggerZoneMonitor.cs` treated any
  `MinTriggerArea` difference under `0.0001` as no change — 5.8 detector pixels. That was harmless
  while nothing could express a size below ~250, and quietly wrong the moment the new floor landed:
  *every* edit in the new small range fell inside the tolerance, so the reconcile loop saw no
  change, left the running detection session on its old threshold, and the setting only took effect
  at the next restart. It is now `MinTriggerAreaEpsilon = 0.5 / MotionDetector.PixelCount` — half a
  detector pixel, still wide enough that a value round-tripping through JSON and SQLite doesn't read
  as an edit and needlessly rebuild the session.

  Worth remembering as a general trap: a tolerance chosen against one range of values becomes a
  silent bug when the range that can reach it widens.
- The rest of the server needed nothing. `TriggerEndpoints` validates only `0 < minTriggerArea <= 1`,
  and `4 / 57600 ≈ 6.9e-05` passes.

## How it was verified

Checked on the **macOS app against a stub server**, since that is the client this was reported from:
the slider bottoms out at "ignores anything under 4 detector pixels" with the circle still findable,
the −/+ buttons stepped 4 → 5 → 6 → 5, and Save sent `minTriggerArea` `8.68e-05` — exactly
`5 / 57600` — with the camera's other zone untouched. `MultiZoneTriggerTests.cs` covers the server
half.

Never checked against a real subject in the field: nobody has yet set the gate camera's far zone
down near the new floor and confirmed a person walking to the gate still fires. If that gets done,
judge it by `SubjectConfirmed` events rather than raw triggers — raw triggers will go **up** at a
lower floor whether or not the detection got better.
