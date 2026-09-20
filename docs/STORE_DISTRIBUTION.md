# Getting the apps to testers through the stores

TestFlight (Apple) and Google Play's internal testing track are the "only these people, and
they auto-update" mechanism. Nothing custom is needed, and it's the same pipeline production
uses later. This is the checklist from the current repo state to a build on a tester's phone.

Legend: **[you]** needs your accounts or a password · **[script]** one command in this repo.

## 0. What's already done in the repo (2026-09-20)

- Privacy manifests (`PrivacyInfo.xcprivacy`) in the iOS/Mac and tvOS targets.
- `ITSAppUsesNonExemptEncryption = false` (no export-compliance questionnaire per upload) and
  `LSApplicationCategoryType` for the Mac App Store.
- Android: `targetSdk`/`compileSdk` 36 (Play's requirement for new apps from Aug 2026), AGP
  8.9.3 / Gradle 8.11.1, release signing wired to `android/keystore.properties`, backup rules
  excluding the login session, the setup-screen placeholder no longer shows a real server.
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

Each upload appears in App Store Connect → the app → TestFlight within ~10 minutes.

**Your own devices after this:** a TestFlight build registers *production* APNs tokens. Your
Core is on direct Apple with `Apple.UseSandbox: true`, so flip that to `false` (Settings → PUSH
NOTIFICATIONS → *Apple: Use Sandbox* off) once your phone runs the TestFlight build, or direct
pushes to it silently fail. Customers are unaffected: their `Relay.ApnsSandbox` default is
already `false`.

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
4. **Internal testing [you]:** Play Console → Testing → Internal testing → Create new release →
   upload the `.aab` → Testers tab → create a list of Gmail addresses → save → **Start rollout**.
   Copy the opt-in link and send it to testers; they tap it once, install from Play, and update
   automatically after that. Live within minutes, no review. Up to 100 testers.

   Note: a Play developer account created after Nov 2023 as a *personal* account must run a
   closed test with 12 testers for 14 days before it may publish to production. That does not
   affect internal testing.

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

then upload the `.aab` in Play Console. Testers on both stores update automatically.
