# Sentinel Core for Apple Platforms

Native SwiftUI client for iPhone, iPad, and macOS.

## Open and run

1. Open `SentinelCore.xcodeproj` in Xcode.
2. Select the `SentinelCore` scheme.
3. Choose an iPhone, iPad, or Mac destination.
4. Run the app and enter the dashboard address, for example:
   `http://192.168.1.50:3320`

The device and Sentinel Core server must currently be on the same local network.

## Current foundation

- Native event feed with pull-to-refresh, search, notification filter, and automatic refresh
- Event detail with full-resolution alert snapshot
- Day-indexed Blue Iris trigger clips with AI event markers, pre/post-event frame scrubbing,
  automatic unsaved-gap skipping, object/severity filters, and shared reviewed/unreviewed state
- Trigger history with snapshot and recorded-clip playback
- Live camera list and native HLS playback
- APNs event alerts with foreground presentation, test delivery, and event deep links
- Optional dashboard username/password login
- Server connection settings and health status
- Shared SwiftUI codebase for iOS, iPadOS, and macOS

The app talks to the existing `BlueIrisOllamaBridge.Web` JSON API. It does not connect
directly to cameras or the main bridge on port 5080.

Real push delivery requires an Apple Developer signing identity, an APNs key, and
Core provider configuration. See
[`core/docs/PUSH_NOTIFICATIONS.md`](../core/docs/PUSH_NOTIFICATIONS.md).
