# Real push notifications

Sentinel Core can send event alerts through Apple Push Notification service (APNs)
and Firebase Cloud Messaging (FCM). Delivery originates from the Core service, so
the mobile app does not need to be open or connected to the local network when the
alert is sent.

Notification taps open the matching event when the device can reach Sentinel Core.
Event details and images remain local; a device away from the LAN will still receive
the alert text, but it cannot load the event until it reconnects to the LAN or a VPN.

There are two ways for Core to reach Apple and Google:

- **Relay (the default).** Core hands each alert to the Sentinel Core push relay at
  `https://push.nr5p.com`, which holds the Apple and Firebase developer credentials and
  forwards it. Nothing to set up beyond turning push on - but it requires a valid license,
  because the license is how the relay knows who is calling. See [Relay](#relay-the-default)
  below.
- **Direct.** Core signs and sends to APNs/FCM itself using developer credentials you
  provide. This is how the relay itself works internally, and what you'd use if you have your
  own Apple Developer / Firebase accounts (or you *are* the relay's operator). Any platform
  whose direct provider is enabled and fully configured bypasses the relay for that platform.
  See [Apple setup](#apple-setup) and [Android setup](#android-setup).

## Relay (the default)

A customer's install cannot hold the APNs `.p8` key or the Firebase service account: those are
team-wide credentials that can't be scoped to one customer or revoked for just one, so they can
only live on a machine the vendor controls. The relay is that machine. Core registers each phone's
device token with it and asks it to send each alert; the relay checks the caller's license, checks
the token belongs to that license, signs the request with the real credentials, and forwards it.

```json
"PushNotifications": {
  "Enabled": true,
  "Relay": {
    "Enabled": true,
    "Url": "https://push.nr5p.com",
    "ApnsSandbox": false
  }
}
```

What to know:

- **A valid license is required.** Core sends the `.lic` file's contents with every relay call
  (base64, in an `X-Sentinel-License` header). No license, or an invalid one, and the relay
  answers 401; Core logs one warning and delivers nothing until a license is installed
  (Settings → LICENSE). Direct providers, if configured, are unaffected.
- **Only alert text crosses the relay** - the notification's title, body, camera name, event
  type and event id. Never images, video, or anything about your cameras beyond their names. The
  relay logs who pushed to which platform and whether it succeeded; it does not log the text.
- **Your devices are yours.** A token registered under your license can only be pushed to by
  your license. Someone with a copy of your license file could push to your own devices and
  nothing else - so treat `license.lic` like any other credential.
- **Direct wins.** If `Apple` (or `Firebase`) below is enabled *and* filled in, that platform
  goes direct and the relay is not contacted for it. That is why `Relay.Enabled: true` is the
  shipped default: an install that already pushes for itself is unaffected.
- **`ApnsSandbox`** tells the relay which Apple environment your tokens belong to. Leave it
  `false` for apps from the App Store or TestFlight. Set `true` only for an app installed from
  Xcode or the command line - those register sandbox tokens (see the note under Apple setup).
- **Registrations are kept in step automatically.** The dashboard forwards each device
  registration to the relay as it happens, Core re-registers a token the relay says it doesn't
  know before retrying, and a background sweep re-confirms every device at startup and every
  six hours. There is nothing to do if the relay's store is ever rebuilt.
- **Limits.** 25 devices per license and 200 pushes per license per 10 minutes - far above any
  household's use. Hitting either shows up as a warning in Core's log.

Operating the relay itself is covered in [PUSH_RELAY.md](PUSH_RELAY.md).

## Security

- Never commit an APNs `.p8` key, Firebase service-account JSON, or
  `google-services.json`. The repository ignores these files.
- Store server credentials outside the repository and restrict them to the account
  running Sentinel Core.
- The Core host needs outbound HTTPS access on port 443 to Apple and/or Google.
- Dashboard authentication also protects the device-registration and test endpoints.

## Apple setup

Only needed for direct mode - skip this entirely if you use the relay.

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

**A Release *configuration* is not a production *signature*.** The Xcode project sets
`APS_ENVIRONMENT = production` for Release, but automatic signing resolves to the
Development team provisioning profile and rewrites the signed entitlement back to
`development`. So a locally built `-configuration Release` app installed with
`devicectl` still produces **sandbox** tokens and still needs `UseSandbox: true`.
Only a distribution-signed archive (TestFlight/App Store) yields production tokens.

Check the signed entitlement rather than the build setting - this is the definitive
answer for which environment a given build will register in:

```
codesign -d --entitlements - --xml /path/to/SentinelCore.app | plutil -p - | grep aps
```

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

Only needed for direct mode - skip this entirely if you use the relay.

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
  incorrectly provisioned build. Before suspecting the token, confirm the build's
  signed `aps-environment` matches `UseSandbox` using the `codesign` check in the
  Apple setup section above - a Release build is development-signed unless it was
  distribution-signed, which is the most common cause of this error.
- Apple provider unavailable: verify team ID, key ID, absolute `.p8` path, and file
  permissions.
- Firebase unavailable: verify the project ID, service-account path, API enablement,
  and IAM role.
- Android never registers: confirm `android/app/google-services.json` exists, rebuild
  the app, and use a device or emulator with Google Play services.
- Notification arrives but the event does not open: reconnect to the Sentinel Core
  LAN or its VPN, then tap the notification again.
- A device silently stops receiving pushes: when a provider reports a token as
  `Unregistered` (APNs HTTP 410) or `UNREGISTERED` (FCM), Core deletes that
  registration from `push-devices.db` and logs a warning naming the device:

  ```
  Removed the push registration for apple device Apple TV (<id>) because the provider
  reported its token is no longer valid - that device receives no further pushes until
  its app re-registers on next launch
  ```

  This is normal after reinstalling an app, restoring a device from backup, or
  leaving one unused long enough for its token to be reissued. The removal is
  permanent: the device reappears only when its app next launches and calls
  `POST /api/push/devices`. Watch for it after any alert whose log line reports a
  partial delivery (`delivered to 1/2 registered devices`) — with more than one device
  registered, that line alone looks like a transient failure.
