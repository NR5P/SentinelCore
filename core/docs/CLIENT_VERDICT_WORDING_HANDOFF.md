# Handoff: what the clients say about a hidden event

Written 2026-09-05 on the Linux box, which has no Swift or Kotlin toolchain — none of this is
compiled or run here. The web dashboard's half is already done and deployed; this is the same set
of corrections for iOS, Android, Windows and tvOS, for whoever is at the Mac.

Nothing here is a crash or a data bug. Every item is a string the UI shows that is now false, and
they became false because of four server changes on 2026-09-04. **Read "Why" before changing any
wording** — the wrong fix is easy and reads as more helpful than the right one.

---

## Background: why the text went stale

Two new per-camera switches shipped (see `AI_ROUTING_HANDOFF.md` for the switches themselves,
already wired on all three editors by `e3db4d3` and verified correct):

- **Always use the full AI** (`bypassFastLocal`) — skip the fast local detector.
- **Only keep what the AI would alert on** (`keepOnlyAlertable`) — this is the one that matters
  here. It changes the *verdict rule*. Normally a trigger is confirmed when the frame contained a
  person, vehicle, package or animal. With this on, it is confirmed only when the model set
  `notification_recommended`.

The camera it was built for is a chicken coop. Chickens **are** animals, they are always in shot,
and they are the reason the camera exists. Under the old rule every trigger confirmed and every
clip was kept. Under the new rule they are rejected.

Measured the night it went live: **0 confirmed, 13 rejected** on that camera.

Two more server changes matter for the text:

- `7f29718` deletes a rejected trigger's **video** as soon as the verdict lands. The snapshot stays.
- `be3b824` releases the **video** of motion nothing ever judged, once it ages past the Unverified
  Motion Days window. Row and snapshot stay.

---

## 1. "AI found nothing" is now false — highest priority

**Where**

| Client | File:line | Current string |
|---|---|---|
| iOS | `ios/SentinelCore/Views/EventFeedView.swift:252-253` | `"AI found nothing"` |
| iOS | `ios/SentinelCore/Views/ActivityDetailView.swift:115-119` | `"Nothing found in frame"` |
| Android | `android/.../ui/screens/EventScreens.kt:367-373` | `"AI found nothing"` |
| Windows | `windows/SentinelCore.App/Views/ActivityRow.cs:85-89` | `"rejected" => "AI found nothing"` |
| tvOS | `appletv/SentinelCoreTV/Views/AlertFeedView.swift:164-167` | `"AI found nothing"` |
| tvOS | `appletv/SentinelCoreTV/Views/ActivityDetailView.swift:115-119` | `"Nothing found in frame"` |

**Why it is wrong.** On a `keepOnlyAlertable` camera the AI found chickens and said so in the
summary. It simply would not have alerted. The badge asserts the opposite of the analysis printed
directly beneath it, and it is the *only* explanation the user gets for why almost every row on
that camera is hidden.

**Why the obvious fix doesn't work.** You cannot branch on which reason applied: the activity item
does not carry the camera's `keepOnlyAlertable` flag. `ActivityItem` (`ActivityEndpoints.cs:~410`)
has no such field, and adding one is a server change plus a decode change on four clients.

**Do this instead — neutral wording that is true in both cases:**

- Badge: **"Filtered out by the AI"** (or "Filtered out")
- Detail: **"The AI decided this wasn't worth showing"**

Do **not** write "nothing was there", "empty frame", or anything asserting what was in the picture.

The web dashboard's version, already shipped, is the reference. `wwwroot/app.js` now reads
`⚠ filtered out` with a tooltip that gives both reasons and says the snapshot is kept and the video
deleted. Match the spirit; the phones have less room for the long form.

## 2. `unverified` is labelled as if it were still in progress

**Where:** the `else` / `_` fallback branch immediately after the `rejected` case in each of the
files above.

**Why it is wrong.** `unverified` means *nothing ever judged this frame* — the analysis failed or
the forward was refused. It is terminal. All four clients currently say **"Awaiting AI verdict"**,
which tells the user to wait for something that will never arrive. The states in play are
`confirmed`, `pending`, `rejected`, `unverified`; only `pending` is a wait.

**Fix:** add a third branch before the fallback.

- `unverified` → **"Never checked"**, detail **"Nothing ever judged this frame"**
- keep `pending` → "Awaiting AI verdict"

The web has had this right all along (`app.js`: `⚠ never checked`) — copy that wording.

**Android additionally:** `EventScreens.kt:368` uses `Icons.Default.Warning` for *both* branches, so
unlike iOS/Windows/tvOS the two cannot be told apart by glyph either. Give `pending` a clock or
`Icons.Default.Schedule`.

## 3. "No clip" false fault — **already fixed server-side, just verify**

No client change needed. `ClipStateFor` in `ActivityEndpoints.cs` now returns `none` instead of
`missing` for a rejected trigger, because we delete that clip deliberately. Before the fix every
coop row carried an amber "No clip" fault for six hours, and the web tooltip told the user to go
read the logs about a file the system had removed on purpose.

**Verify after pulling:** a rejected event should show no clip warning at all. The affected renderers
are `ios/.../EventFeedView.swift:193`, `android/.../EventScreens.kt:282`,
`windows/.../ActivityRow.cs:67,75-79`. If a warning still appears, the client is treating `none` as
a fault, which would be a client bug worth fixing.

---

## Not gaps — checked, don't spend time here

- **Both new switches are correctly wired** on iOS, Android and Windows. Nullable field, both
  rendered, both keys sent on every save, absent key resolving to the server default. Verified
  independently, not just from the commit message.
- **tvOS is correctly skipped.** It has no trigger editor and no camera admin
  (`appletv/SentinelCoreTV/Views/SettingsView.swift:125` states the policy). `fetchTriggerConfig` /
  `saveTriggerConfig` exist in its client but have zero callers. Items 1 and 2 above still apply to
  its *feed*.
- **"Show hidden" already exists on all four clients** and passes `includeUnconfirmed=true`.

## One real gap worth closing while you are there

**iOS has no test target at all.** `find ios -iname "*test*"` returns nothing and the pbxproj has no
`XCTest` reference. Both `AI_ROUTING_HANDOFF.md` and `NIGHT_WINDOW_HANDOFF.md` cite "the standalone
`Models.swift` decode check described in the repo's verification notes" — **that document does not
exist in the tree.** iOS is the only client whose nullable-encoding contract is unpinned, and that
contract is load-bearing: encoding a plain `false` instead of omitting the key silently switches a
setting off on the server. Android pins it at
`android/.../data/TriggerAiRoutingTest.kt:57-63`, Windows at
`windows/.../SentinelClientTests.cs:270-271,287-288`.

## Also stale, unrelated to the above

`core/docs/NIGHT_WINDOW_HANDOFF.md:136,142` — the prose says "Neither app uses a free-text number
field" and "Both read the three keys through a `resolvedNightWindow` helper". Commit `e3db4d3` added
a Windows row to the table above it, and Windows does neither: free-text `TextBox`es at
`TriggerZoneEditorWindow.xaml:108,112` parsed at `.xaml.cs:451-452`, and three separate properties
with no combined helper at `Models.cs:308-312`. The table was updated; the sentences under it were
not.
