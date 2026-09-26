# Sentinel Core for Apple TV

Native SwiftUI client for tvOS. A standalone project (like `../ios` and `../android` - it doesn't
share a build target with either, though it ports the same networking/model layer), deliberately
smaller in scope than the iPhone/iPad/Mac app: no camera or account administration, no PTZ
controls, no recording timeline - those stay on iOS/macOS/web, where typing a URL or dragging a
trigger zone is actually practical. What it keeps: live camera viewing, the AI-verified alert
feed, motion triggers, and a full-screen alert takeover that interrupts the screen the moment a
high-confidence event comes in.

## Open and run

1. Open `SentinelCoreTV.xcodeproj` in Xcode.
2. Select the `SentinelCoreTV` scheme.
3. Choose an Apple TV Simulator (or a real Apple TV) destination.
4. Run the app. The setup screen lists any Sentinel Core server it finds on the LAN over
   Bonjour - pick one, or enter the address manually, for example: `http://192.168.1.50:3320`

The Apple TV and Sentinel Core server must be on the same local network (or reachable via the
optional remote address in Settings).

## Build, test and release

Full detail: [`docs/BUILDING.md`](../docs/BUILDING.md) §4.

```bash
# compile check (the three signing overrides are required; without them the build fails on a
# certificate mismatch that has nothing to do with the code)
xcodebuild -project appletv/SentinelCoreTV.xcodeproj -scheme SentinelCoreTV -configuration Debug \
  -destination 'generic/platform=tvOS' CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build

appletv/scripts/release.sh      # archive + upload to TestFlight, once the tvOS App Store Connect record exists
```

The Organizer engine comes from `../ios/OrganizerKit` (linked by relative path). Any change there
must still build here, and its tests run with `swift test --package-path ios/OrganizerKit`.

Until the tvOS app is on TestFlight, the Apple TV runs a locally installed Release build, which
registers a *sandbox* push token. A server on production APNs therefore can't push to it, and the
server deliberately never sends it the silent Organizer pushes.

## Current foundation

- Live camera grid with muted auto-preview tiles, full-screen playback on select
- Bonjour server discovery (`_sentinelcore._tcp`) on the setup screen, alongside manual entry
- AI-verified alert feed (`/api/events`) with a notified-only filter and a live-updating poll
- Motion trigger history with snapshot/clip playback
- Full-screen "AI Verified Alert" takeover, auto-starting the live feed for the alerted camera
- The Organizer, mostly for viewing: calendar, reminders (which can be checked off), notes and
  boards, synced with the other apps through the shared `OrganizerKit` engine
- Files, read-only (`SentinelCoreTV/Files/`): a Files tab whenever the server has Family Files
  switched on - browse the shared library folder by folder, see pictures full screen, and a
  Backup Drives section on the root showing each drive's last result. Adding, renaming and
  trashing stay on the phones, computers and dashboard
- Optional dashboard username/password login, persisted across restarts (see below)
- Server connection settings, push registration, and a connection log

## Why the session cookie is mirrored into UserDefaults

tvOS only guarantees that `UserDefaults` (500KB) survives; everything else in the app container,
including URLSession's cookie jar at `Library/Cookies`, is reclaimable at any time. When tvOS
reclaims it the session cookie disappears and the app falls back to the login screen even though
the server-side session is still valid - which is what made the app ask for a login on almost
every launch. `SessionCookieStore` in `SentinelCoreTV/Networking/SentinelClient.swift` mirrors the
cookie into UserDefaults and restores it before the first request. Note this never reproduces in
the Simulator, which reclaims nothing - to test it, delete `Library/Cookies` from the app's data
container between launches.

## The alert takeover ("interrupt while watching TV")

tvOS can't draw over another app's video - a third-party app has no way to overlay Netflix or
Apple TV+ with a live camera feed. Two things are possible instead, and this app uses both:

1. **The system push banner** shows over whatever's on screen, system-wide, for any
   high-confidence event Core decides is push-worthy (see
   `core/SentinelCore/PushNotifications.cs` `PushAlertAction` - the same gate the iOS app's
   pushes already go through). That's the ceiling for "interrupting" some other app.
2. **A genuine full-screen live takeover** happens whenever Sentinel Core itself is the
   foregrounded app: `application(_:didReceiveRemoteNotification:fetchCompletionHandler:)` (see
   `SentinelCoreTV/App/PushNotificationCoordinator.swift`) reads the alert's event id straight
   from the raw APNs payload and drives `AppModel.alertTakeover`, which `RootView` presents
   full-screen with the camera's live HLS stream already playing.

One tvOS-specific wrinkle worth knowing if you touch this code: `UNNotificationContent.userInfo`
and the interactive `userNotificationCenter(_:didReceive:)` callback are both unavailable on
tvOS (confirmed by the SDK headers, not just docs) - there's no tap-to-open gesture on a tvOS
banner the way there is on iOS's lock screen. All payload reading happens through the legacy
`application(_:didReceiveRemoteNotification:)` delegate method instead, which still gets the raw
dictionary. `willPresent` is only used to control whether the banner/sound show while
foregrounded.

## Push notifications require a second bundle id

APNs requires the `apns-topic` header to exactly match the receiving app's own bundle id. This
app registers as `com.sentinelcore.client.tv` (a separate App ID from the iPhone app's
`com.sentinelcore.client`), so Core needs to know both:

```json
"PushNotifications": {
  "Apple": {
    "BundleId": "com.sentinelcore.client",
    "TvBundleId": "com.sentinelcore.client.tv"
  }
}
```

Same `TeamId`/`KeyId`/APNs `.p8` key works for both - it's one team, two App IDs. Until
`TvBundleId` is set, Apple TV pushes are sent with the iPhone app's topic and APNs will reject
them; the app still works over polling, it just won't get the takeover from a cold/backgrounded
state. See [`core/docs/PUSH_NOTIFICATIONS.md`](../core/docs/PUSH_NOTIFICATIONS.md) for the full
provider setup.

## App icon

`Assets.xcassets/App Icon & Top Shelf Image.brandassets` holds the layered tvOS icon and both top
shelf images, generated from the shared `branding/SentinelCoreAppIcon-1024.png` master. tvOS icons
are parallax image stacks rather than the single square the other platforms use, and the App Store
layer is a 1x-only 1280x768 with no alpha - `actool` rejects the catalog otherwise.
