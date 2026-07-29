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
4. Run the app and enter the dashboard address, for example: `http://192.168.1.50:3320`

The Apple TV and Sentinel Core server must be on the same local network (or reachable via the
optional remote address in Settings).

## Current foundation

- Live camera grid with muted auto-preview tiles, full-screen playback on select
- AI-verified alert feed (`/api/events`) with a notified-only filter and a live-updating poll
- Motion trigger history with snapshot/clip playback
- Full-screen "AI Verified Alert" takeover, auto-starting the live feed for the alerted camera
- Optional dashboard username/password login
- Server connection settings, push registration, and a connection log

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

## Before shipping to TestFlight/App Store

This project currently has no App Icon & Top Shelf Image asset set (`Assets.xcassets` is empty
apart from `Contents.json`) - fine for Simulator/development builds, but required before an
archive/App Store submission. Add one via Xcode's tvOS app icon editor.
