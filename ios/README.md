# Sentinel Core for Apple Platforms

Native SwiftUI client for iPhone, iPad, and macOS.

## Open and run

1. Open `SentinelCore.xcodeproj` in Xcode.
2. Select the `SentinelCore` scheme.
3. Choose an iPhone, iPad, or Mac destination.
4. Run the app and enter the dashboard address, for example:
   `http://192.168.1.50:3320`

The device and Sentinel Core server must currently be on the same local network.

> **Open issue, 2026-08-05:** Live view stutters on iPhone over Wi-Fi — a frozen frame that jumps
> every few seconds — while the same cameras play correctly in the web dashboard. The server side has
> been verified healthy at frame level, so start by checking whether the installed build predates the
> `LiveStreamPlayer` stall fixes of 30 July. Full findings and a suggested order of attack:
> [`core/docs/IOS_LIVE_VIEW_HANDOFF.md`](../core/docs/IOS_LIVE_VIEW_HANDOFF.md).

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

The app talks to the existing `SentinelCore.Web` JSON API. It does not connect
directly to cameras or the main bridge on port 5080.

Real push delivery requires an Apple Developer signing identity, an APNs key, and
Core provider configuration. See
[`core/docs/PUSH_NOTIFICATIONS.md`](../core/docs/PUSH_NOTIFICATIONS.md).
