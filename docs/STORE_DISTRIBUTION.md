# Getting the apps to testers through the stores

TestFlight (Apple) and Google Play's internal testing track are the "only these people, and
they auto-update" mechanism. Nothing custom is needed, and it's the same pipeline production
uses later. This is the checklist from the current repo state to a build on a tester's phone.

Legend: **[you]** needs your accounts or a password · **[script]** one command in this repo.

The procedure for each release (bump, archive, upload, deploy) and the pitfalls hit so far are
in [`BUILDING.md`](BUILDING.md) §6. This file is the one-time setup behind it.

## Where things stand (2026-09-23)

| | Status |
| --- | --- |
| iOS + macOS | App Store Connect record "Sentinel Core" (id 6814252066). Internal TestFlight is live; latest upload is build 7. The daily-use iPhone and Mac run TestFlight builds. |
| tvOS | No App Store Connect record yet; the Apple TV runs a locally installed build. |
| Android | Play Console app "Sentinel Core" (package `com.sentinelcore.android`), internal testing track active with tester list "testersSentinelCore". Uploads are manual (no service account yet). Store listing not filled in, so Play shows it as unreviewed. |
| Server push | Direct APNs on production (`Apple.UseSandbox: false`) plus Firebase; the relay for customer installs. |
| Privacy policy | Live at https://nr5p.com/privacy. |

## 0. What's already done in the repo (2026-09-20)

- Privacy manifests (`PrivacyInfo.xcprivacy`) in the iOS/Mac and tvOS targets.
- `ITSAppUsesNonExemptEncryption = false` (no export-compliance questionnaire per upload) and
  `LSApplicationCategoryType` for the Mac App Store.
- Android: `targetSdk`/`compileSdk` 36 (Play's requirement for new apps from Aug 2026), AGP
  8.9.3 / Gradle 8.11.1, release signing wired to `android/keystore.properties`, backup rules
  excluding the login session and the phone-as-a-camera state, the setup-screen placeholder no longer shows a real server.
- Release scripts: `ios/scripts/release.sh`, `appletv/scripts/release.sh`, `android/release.sh`,
  and `scripts/bump-build.sh`.

## 1. Accounts **[you]**

- **Apple Developer Program** - already enrolled (team `G4GK5372G8`).
- **Google Play Console** - https://play.google.com/console, one-time US$25. Use the same
  Google account that owns the Firebase project `firebase-project-id` so the app record and the
  push project sit together.

## 2. Apple: app records and signing **[you]**, ~15 minutes

1. Xcode → Settings → Accounts → make sure the Apple ID for the team is signed in. This is what
   lets `xcodebuild -allowProvisioningUpdates` create the *Distribution* certificate and App
   Store profiles on the first archive (only Development identities exist on this Mac today).
2. https://appstoreconnect.apple.com → My Apps → **+** → New App, twice:
   - **Sentinel Core** - platforms iOS *and* macOS, bundle ID `com.sentinelcore.client`, SKU
     `sentinelcore-client`.
   - **Sentinel Core TV** - platform tvOS, bundle ID `com.sentinelcore.client.tv`, SKU
     `sentinelcore-tv`.
   If a bundle ID isn't offered in the dropdown, register it first at
   https://developer.apple.com/account/resources/identifiers (Push Notifications capability on
   - it should already be, since push works).
3. Nothing else is needed for **internal** TestFlight. Screenshots, description, privacy label
   and review notes (§6) come with the external ring.

## 3. Apple: upload **[script]**

```bash
scripts/bump-build.sh          # every upload needs a higher build number; commit the result
ios/scripts/release.sh         # archives + uploads iOS and macOS (pass "ios" or "macos" for one)
appletv/scripts/release.sh     # archives + uploads tvOS
```

Each upload appears in App Store Connect → the app → TestFlight within ~10 minutes, and a
"Sentinel Core 0.1.0 (N) for iOS is now available to test" email follows.

**An upload that "succeeded" can still be rejected in processing.** The build then never
appears in TestFlight, and the reason arrives only as an email to the developer account
(you@example.com, which Outlook files under **Other**): "Action needed: The uploaded build for
Sentinel Core has one or more issues", with an ITMS code. The one that has happened:

- **ITMS-90626 Invalid Siri Support**: an App Intent's text contained the word "Siri". This hit
  iOS builds 5 and 6; the Mac builds went through, which made it look like an iOS-only glitch.

Also keep every extension's build number equal to the app's, which Apple requires.
`scripts/bump-build.sh` does this; a newly added extension target needs its number brought in
line once. (It was caught before an upload here, so what Apple's rejection says is unconfirmed.)

New extension targets (the widgets, the share extension) need no manual setup in the developer
portal. The first `release.sh` after adding one registers its App ID and profile through
`-allowProvisioningUpdates`.

**Your own devices after this:** a TestFlight build registers *production* APNs tokens, so a
server pushing directly to Apple needs `Apple.UseSandbox: false` (Settings → PUSH
NOTIFICATIONS → *Apple: Use Sandbox* off). Yours has been set that way since 2026-09-22.
Customers are unaffected: their `Relay.ApnsSandbox` default is already `false`. The flip side:
a locally built app always registers a *sandbox* token, so installing one on a device that
normally runs TestFlight breaks its push until TestFlight's build goes back on.

## 4. Apple: testers **[you]**

- **Internal (no review, immediate):** App Store Connect → Users and Access → **+** → add each
  tester by Apple ID email with any role (Customer Support is the least privileged). Then
  TestFlight → Internal Testing → **+** group → add them → pick the build. They install the
  TestFlight app, accept the email, and get every future build automatically. Up to 100 people.
- **External (one Beta App Review, then automatic):** TestFlight → External Testing → new group
  → add emails or enable the public link → submit the build for review with the notes in §6.
  Up to 10,000 people; builds expire after 90 days.

Mac and Apple TV builds work the same way inside the same TestFlight app records.

## 5. Android

1. **Upload keystore [you], once.** Pick a password; you'll be asked for it twice:

   ```bash
   keytool -genkeypair -v -keystore ~/sentinelcore-upload.jks -alias upload -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Sentinel Core, O=NR5P"
   ```

   Then create `android/keystore.properties` (gitignored):

   ```
   storeFile=/Users/you/sentinelcore-upload.jks
   storePassword=<the password>
   keyAlias=upload
   keyPassword=<the password>
   ```

   Back the `.jks` up somewhere safe. With Play App Signing (default for new apps) Google holds
   the real signing key and this is only the *upload* key - losing it is recoverable through
   support, but slowly.

2. **App record [you].** Play Console → Create app → "Sentinel Core", app/free, then complete the
   *Set up your app* checklist in the dashboard: privacy policy URL (§6), app access (a demo
   login - §6), ads (none), content rating questionnaire, target audience (18+), data safety
   (see §6), category *Tools*.
3. **Build [script]:** `scripts/bump-build.sh` (if not already done for this round), then
   `android/release.sh` → `android/app/build/outputs/bundle/release/app-release.aab`.
   With the service account from step 5 configured, the same command also **uploads to the
   internal track** and finishes the rollout - no browser needed. Without it, the script prints
   where the `.aab` is for a manual upload.
4. **Internal testing [you]:** Play Console → Testing → Internal testing → Create new release →
   upload the `.aab` → Testers tab → create a list of Gmail addresses → save → **Start rollout**.
   For this app the page is
   https://play.google.com/console/u/0/developers/6252521596077819619/app/4974287699313226200/tracks/internal-testing.
   Each later manual release is: Create new release → drop the `.aab` → Next → Save and publish.
   Copy the opt-in link and send it to testers; they tap it once, install from Play, and update
   automatically after that. Live within minutes, no review. Up to 100 testers.

   Note: a Play developer account created after Nov 2023 as a *personal* account must run a
   closed test with 12 testers for 14 days before it may publish to production. That does not
   affect internal testing.

5. **Automatic uploads [you], once.** `android/scripts/play-upload.py` talks to the Play
   Developer API with a *service account* - a robot identity you invite to the Play Console.
   - https://console.cloud.google.com → project picker → choose the Firebase project
     (`firebase-project-id`; Firebase projects are Google Cloud projects) → **IAM & Admin →
     Service Accounts → Create service account**: name `play-publisher`, no roles → Done.
   - Open it → **Keys → Add key → Create new key → JSON**. Save the download as
     `~/sentinelcore-play-publisher.json` (outside the repo; gitignored anyway).
   - Play Console → **Users and permissions → Invite new users** → email = the service
     account's address (`play-publisher@firebase-project-id.iam.gserviceaccount.com`) →
     **App permissions → Add app → Sentinel Core** → tick **Release apps to testing tracks**
     (and *Manage testing tracks and edit tester lists*) → **Invite user**. Takes a few minutes
     to become active.
   - Add one line to `android/keystore.properties`:
     `playServiceAccountJson=/Users/you/sentinelcore-play-publisher.json`
   - Verify: `python3 android/scripts/play-upload.py --key ~/sentinelcore-play-publisher.json --check`
     should print the current internal release. From then on `android/release.sh` uploads.

## 6. Review prep (needed for external TestFlight and for anything beyond Play internal)

- **Privacy policy URL: `https://nr5p.com/privacy`.** The push relay serves it (and a landing
  page at `/`) straight from `docs/PRIVACY_POLICY.md`, so editing that file and running
  `./scripts/deploy.sh relay` on the box updates the live page. One-time **[you]**: in
  Cloudflare Zero Trust → the tunnel → Published application routes, change the existing
  `nr5p.com` route's service from `http://127.0.0.1` to **HTTP `127.0.0.1:3330`** (same target
  as `push.nr5p.com`). Nothing served the bare domain before, so nothing is displaced.
- **A server reviewers can log in to.** Deferred (2026-09-20): testers install Core themselves
  before installing the app, so internal TestFlight / Play internal need nothing here. Be aware
  that *App Review* staff won't install Core - for external TestFlight or a public listing,
  expect "we were unable to sign in" unless a demo server and login are in the review notes.
  When that day comes: a second, *licensed* Core with the demo data (`video/demo/`) behind a
  `demo.nr5p.com` tunnel route, plus a Viewer account.
- **Review notes text (App Store Connect → TestFlight → Test Information, and again on the
  App Store version):**

  > Sentinel Core is a client for a camera-alert server the user runs on their own computer
  > (the server is a separate download; testers install it first). [When a demo server exists:
  > A demo server is available at https://demo.nr5p.com - sign in with the credentials
  > provided.] The app uses NSAllowsArbitraryLoads because the user configures their own
  > server address, which on a home network is commonly plain HTTP. Accounts are created and
  > deleted by the server's administrator inside the app (Settings → Manage Accounts).
- **App privacy label (Apple) / Data safety (Google):** no data collected by the developer; the
  app sends the device's push token and login to the user's own server; no tracking; no
  third-party SDKs that collect data (Firebase Messaging is delivery only).
- **Screenshots:** iPhone 6.9", iPad 13", Mac, Apple TV; Android phone + 7" and 10" tablets;
  Play also wants a 512×512 icon and a 1024×500 feature graphic. `branding/` has the icon
  master.

## 7. Each later build

```bash
scripts/bump-build.sh && git commit -am "Build N" && ios/scripts/release.sh && appletv/scripts/release.sh && android/release.sh
```

Testers on both stores update automatically: TestFlight installs new builds in the background,
Play auto-updates like any store app (usually within a day; a tester can force it from the Play
Store's *Manage apps & device*).
