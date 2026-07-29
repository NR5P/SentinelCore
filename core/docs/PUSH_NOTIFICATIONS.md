# Real push notifications

Sentinel Core can send event alerts through Apple Push Notification service (APNs)
and Firebase Cloud Messaging (FCM). Delivery originates from the Core service, so
the mobile app does not need to be open or connected to the local network when the
alert is sent.

Notification taps open the matching event when the device can reach Sentinel Core.
Event details and images remain local; a device away from the LAN will still receive
the alert text, but it cannot load the event until it reconnects to the LAN or a VPN.

## Security

- Never commit an APNs `.p8` key, Firebase service-account JSON, or
  `google-services.json`. The repository ignores these files.
- Store server credentials outside the repository and restrict them to the account
  running Sentinel Core.
- The Core host needs outbound HTTPS access on port 443 to Apple and/or Google.
- Dashboard authentication also protects the device-registration and test endpoints.

## Apple setup

1. In the Apple Developer portal, enable Push Notifications for the App ID whose
   bundle identifier is `com.sentinelcore.client`.
2. Create an APNs signing key and record its key ID, your Apple developer team ID,
   and the downloaded `.p8` file.
3. In Xcode, select your development team for the SentinelCore target and confirm the
   Push Notifications capability is present. Use a signed physical iPhone, iPad, or
   Mac for delivery testing.
4. Put the `.p8` file in a protected location on the Core computer. Use an absolute
   path in configuration so both the bridge and web processes resolve the same file.

Example `PushNotifications` section:

```json
{
  "PushNotifications": {
    "Enabled": true,
    "OnlyWhenNotificationRecommended": true,
    "MinimumConfidence": 0.6,
    "DuplicateCooldownMinutes": 5,
    "Apple": {
      "Enabled": true,
      "TeamId": "YOUR_TEAM_ID",
      "KeyId": "YOUR_KEY_ID",
      "BundleId": "com.sentinelcore.client",
      "PrivateKeyPath": "/absolute/path/to/AuthKey_YOUR_KEY_ID.p8",
      "UseSandbox": true
    },
    "Firebase": {
      "Enabled": false,
      "ProjectId": "",
      "ServiceAccountPath": ""
    }
  }
}
```

Use `UseSandbox: true` for development builds and development provisioning. Change
it to `false` for production/TestFlight/App Store builds. Development and production
device tokens are separate.

Apple reference:
[Communicating with APNs](https://developer.apple.com/documentation/usernotifications/setting_up_a_remote_notification_server/sending_notification_requests_to_apns)

### Apple TV app

The `appletv/` client is a separate App ID (`com.sentinelcore.client.tv` by default) from the
iPhone/iPad/Mac app, so APNs pushes to it need their own `apns-topic`. Add `TvBundleId` next to
`BundleId`:

```json
"Apple": {
  "Enabled": true,
  "TeamId": "YOUR_TEAM_ID",
  "KeyId": "YOUR_KEY_ID",
  "BundleId": "com.sentinelcore.client",
  "TvBundleId": "com.sentinelcore.client.tv",
  "PrivateKeyPath": "/absolute/path/to/AuthKey_YOUR_KEY_ID.p8",
  "UseSandbox": true
}
```

Same team, key, and `.p8` file work for both App IDs - just enable Push Notifications for the tv
App ID too in the Apple Developer portal. Until `TvBundleId` is set, Apple TV device tokens are
sent with the iPhone app's topic and APNs rejects them (the tvOS app still works over polling in
that case - see `appletv/README.md` for what that degrades).

## Android setup

1. Create a Firebase project and add an Android app with package name
   `com.sentinelcore.android`.
2. Download `google-services.json` and place it at
   `android/app/google-services.json`. The Android build enables the Google Services
   plugin only when this file exists.
3. Enable the Firebase Cloud Messaging API in the Google Cloud project.
4. Create a dedicated service account that can send FCM messages, download its JSON
   key, and store it in a protected location on the Core computer.

The Android app uses the current Firebase Installation ID registration flow and Core
targets that FID through the HTTP v1 `fid` field. It does not depend on the deprecated
FCM registration-token APIs.

Example Android provider configuration:

```json
{
  "PushNotifications": {
    "Enabled": true,
    "OnlyWhenNotificationRecommended": true,
    "MinimumConfidence": 0.6,
    "DuplicateCooldownMinutes": 5,
    "Apple": {
      "Enabled": false,
      "TeamId": "",
      "KeyId": "",
      "BundleId": "com.sentinelcore.client",
      "PrivateKeyPath": "",
      "UseSandbox": true
    },
    "Firebase": {
      "Enabled": true,
      "ProjectId": "YOUR_FIREBASE_PROJECT_ID",
      "ServiceAccountPath": "/absolute/path/to/firebase-service-account.json"
    }
  }
}
```

Firebase references:
[Set up an Android client](https://firebase.google.com/docs/cloud-messaging/android/client) and
[authorize HTTP v1 sends](https://firebase.google.com/docs/cloud-messaging/auth-server).

## Enable and test

1. Restart the Sentinel Core bridge and web dashboard after configuration changes.
2. Connect the Apple or Android app to the dashboard.
3. Open **Settings → Push Notifications**, choose **Enable**, and accept the operating
   system permission prompt.
4. Wait for the device to register, then choose **Send test**.

Core stores registrations in `push-devices.db` in its configured data directory.
Both Core processes share this database. **Disable on this device** unregisters that
installation from Core; the operating-system permission can still be changed
separately in the device's Settings app.

## Diagnostics

While signed in, `GET /api/push/status` reports whether providers are configured and
how many devices are registered. Provider responses and registration failures are
also written to the normal Core logs.

Common failures:

- `BadDeviceToken`: APNs sandbox/production mismatch, wrong bundle ID, or an
  incorrectly provisioned build.
- Apple provider unavailable: verify team ID, key ID, absolute `.p8` path, and file
  permissions.
- Firebase unavailable: verify the project ID, service-account path, API enablement,
  and IAM role.
- Android never registers: confirm `android/app/google-services.json` exists, rebuild
  the app, and use a device or emulator with Google Play services.
- Notification arrives but the event does not open: reconnect to the Sentinel Core
  LAN or its VPN, then tap the notification again.
