# Sentinel Core for Android

Native Android client for a Sentinel Core server on the local network. The app is written in Kotlin with Jetpack Compose and supports phones and tablets.

## Run

1. Open the `android` folder in Android Studio.
2. Let Gradle sync.
3. Choose an Android 8.0 (API 26) or newer phone, tablet, or emulator.
4. Run the `app` configuration.
5. Enter the server address, for example `http://192.168.50.8:3320`.

For a physical device, the device and Sentinel Core server must be on the same local network. Android emulators can usually reach other LAN hosts directly, but `localhost` refers to the emulator itself.

## Current features

- Cookie-based dashboard authentication
- Event feed with search and notified-only filtering
- Event images and metadata
- Day-indexed Blue Iris trigger clips with AI event markers, pre/post-event frame scrubbing,
  automatic unsaved-gap skipping, object/severity filters, and shared reviewed/unreviewed state
- Motion-trigger snapshots and clips
- Live camera HLS playback with audio
- Use this phone as one of the dashboard's cameras: its camera is pushed to the server over WebRTC (`stream-webrtc-android`, the same path as the web dashboard's publish page) from a foreground service, so the screen can be off; streaming resumes after a relaunch
- FCM event alerts with foreground presentation, test delivery, and event deep links
- Phone bottom navigation and tablet navigation rail
- Pull-to-refresh equivalents and 30-second foreground refresh
- Connection status, errors, and local diagnostics
- Persistent local server configuration

Cleartext HTTP is intentionally enabled because initial deployments use private-LAN addresses. Use HTTPS before exposing Sentinel Core beyond a trusted local network.

Real push delivery requires a Firebase Android app, `google-services.json`, and Core
provider configuration. See
[`core/docs/PUSH_NOTIFICATIONS.md`](../core/docs/PUSH_NOTIFICATIONS.md).
