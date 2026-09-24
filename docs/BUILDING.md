# Building, testing and releasing Sentinel Core

Everything in this repository, from a fresh clone to a build on a tester's phone and a new
server in production. Each part also has its own README with more detail on what it does. This
guide is how to build it and ship it, and the things that have gone wrong doing so.

| Part | Folder | Language | Ships as |
| --- | --- | --- | --- |
| Server: AI vision engine | `core/SentinelCore` | C# / .NET 10 | systemd service `sentinelcore` |
| Server: web dashboard + API | `core/SentinelCore.Web` | C# / .NET 10, plain JS | systemd service `sentinelcore-web` |
| Push delivery library | `core/SentinelCore.Push` | C# / .NET 10 | part of the two above |
| Push relay (vendor host) | `core/SentinelCore.PushRelay` | C# / .NET 10 | `sentinelcore-push-relay` at push.nr5p.com |
| Licensing + license tools | `core/SentinelCore.Licensing`, `core/LicenseGenerator*` | C# / .NET 10 | part of the server; CLI + GUI for issuing |
| HomeKit sidecar | `core/homekit-bridge` | Node 22 | systemd service `sentinelcore-homekit` |
| iPhone, iPad and Mac app | `ios/` | Swift 6 / SwiftUI | TestFlight → App Store (`com.sentinelcore.client`) |
| &nbsp;&nbsp;Home Screen widgets | `ios/SentinelCoreWidgets` | WidgetKit | inside the iPhone app |
| &nbsp;&nbsp;Share extension | `ios/SentinelCoreShare` | UIKit + SwiftUI | inside the iPhone app |
| &nbsp;&nbsp;Organizer engine | `ios/OrganizerKit` | Swift package | linked by the app, widgets, share extension and Apple TV app |
| Apple TV app | `appletv/` | Swift / SwiftUI | TestFlight (`com.sentinelcore.client.tv`) |
| Android app | `android/` | Kotlin / Compose | Google Play (`com.sentinelcore.android`) |
| Windows app | `windows/` | C# / WPF, .NET 8 | self-contained folder from `publish.ps1` |

Shared contracts that more than one part implements:

- [`core/docs/ORGANIZER_SYNC.md`](../core/docs/ORGANIZER_SYNC.md) is the Organizer's sync protocol,
  implemented by the server, the dashboard and all four apps.
- [`core/docs/voice-phrase-cases.json`](../core/docs/voice-phrase-cases.json) holds the spoken-reminder examples
  ("call mom tomorrow at 5") that the Apple and Android phrase parsers are both tested against.
- [`core/docs/PUSH_NOTIFICATIONS.md`](../core/docs/PUSH_NOTIFICATIONS.md) covers the push providers
  (APNs, FCM, relay).

---

## 1. Tools

| Tool | Version in use | Needed for |
| --- | --- | --- |
| .NET SDK | 10.0 (server), 8.0 (Windows app) | `core/`, `windows/` |
| Xcode | 27 (Swift 6, strict concurrency on) | `ios/`, `appletv/`, `ios/OrganizerKit` |
| JDK | 17 or newer (22 works) | `android/` |
| Android SDK | platform 36, build tools via Android Studio | `android/` |
| Node.js | 22 or newer | `core/homekit-bridge`, `node --check` on dashboard JS |
| ffmpeg | any recent | server (clips, snapshots, HLS) |
| sqlite3 | any | inspecting databases, test scripts |

The Gradle wrapper (8.11.1) and the Android Gradle Plugin (8.9.3) download themselves. On macOS,
Homebrew's `node` lives in `/opt/homebrew/bin`, which may not be on a non-login shell's `PATH`.

**Files that are not in git and must exist before some builds:**

| File | Where it comes from | Needed by |
| --- | --- | --- |
| `android/local.properties` | Android Studio writes it (`sdk.dir=…`) | every Android build |
| `android/app/google-services.json` | Firebase console, project `firebase-project-id` | Android push. Without it the app still builds, but has no push; never ship a release built that way |
| `android/keystore.properties` + the `.jks` it names | you, once (see §6.3) | signed Android release bundles |
| `core/*/appsettings.json` real values | each server's own config | a running server (the committed copies are defaults) |
| APNs `.p8`, Firebase service-account JSON | Apple / Google consoles | a server that sends push |

**Working in a git worktree** (for example `.claude/worktrees/<name>`): copy
`android/local.properties`, `android/app/google-services.json` and `android/keystore.properties`
over from the main checkout first. They're gitignored, so a new worktree doesn't have them.
Without `local.properties` Gradle can't find the SDK, without `google-services.json` the app
quietly builds with no push, and without `keystore.properties` `android/release.sh` refuses to run.

---

## 2. The server (`core/`)

Full install and operations guide: [`core/docs/DEPLOYMENT.md`](../core/docs/DEPLOYMENT.md).
The short version for developers:

```bash
cd core
dotnet build -c Release        # all projects; the services run the Release output
dotnet test                    # ~1,360 tests, about two minutes
```

- `dotnet test` occasionally aborts the test host partway ("Test process crashed with exit
  code 133/134") with zero real failures. Run it again. Only if it happens three times in a row on
  the same tree is something actually wrong.
- `Fatal error. Internal CLR error (0x80131506)` during a build is MSBuild's build server, not
  the code: run `dotnet build-server shutdown` and build again.
- **The web dashboard's HTML, JS and CSS are served straight from `core/SentinelCore.Web/wwwroot/`**,
  not from `bin/`. A `git pull` on a server makes front-end changes live at once, with no
  restart. Only compiled (C#) changes need the service restarted. Parse-check a JS change with
  `node --check core/SentinelCore.Web/wwwroot/<file>.js`.
- **Don't casually run `SentinelCore.Web` on a development machine.** By default it binds ports
  3320/3443, creates real certificates and advertises itself over mDNS. For a safe local run,
  use what `scripts/with-organizer-servers.sh` does: `--Urls=http://127.0.0.1:<port>
  --Https:Enabled=false --Discovery:Enabled=false --InternetExposed:Enabled=false` with the data
  directories pointed at a scratch folder.

HomeKit sidecar:

```bash
cd core/homekit-bridge && npm test      # node_modules is committed; no install step
```

---

## 3. iPhone, iPad and Mac (`ios/`)

One Xcode project, `ios/SentinelCore.xcodeproj`, with three targets. The `SentinelCore` scheme
builds all three:

| Target | Platforms | Bundle id | What it is |
| --- | --- | --- | --- |
| `SentinelCore` | iOS 17+, macOS 14+ | `com.sentinelcore.client` | the app |
| `SentinelCoreWidgets` | iOS only | `com.sentinelcore.client.widgets` | Calendar, Reminders and Notes Home Screen widgets |
| `SentinelCoreShare` | iOS only | `com.sentinelcore.client.share` | "Sentinel Core" in the share sheet |

The folders are Xcode *synchronized groups*: a new `.swift` file dropped into `ios/SentinelCore/`
(or either extension's folder) is part of that target automatically, so there's no project file
to edit.

The app, the widgets and the share extension share an App Group, `group.com.sentinelcore.client`.
The widgets read an Organizer snapshot the app writes there, and the share extension leaves
shared text there for the app to turn into a note. The Mac app has no App Group, which is why
the Mac gets a Services menu item instead of a share extension.

### Build

```bash
# Mac
xcodebuild -project ios/SentinelCore.xcodeproj -scheme SentinelCore -configuration Debug \
  -destination 'platform=macOS' build

# iPhone/iPad, device build without signing (fast compile check)
xcodebuild -project ios/SentinelCore.xcodeproj -scheme SentinelCore -configuration Debug \
  -destination 'generic/platform=iOS' CODE_SIGNING_ALLOWED=NO build

# a simulator (use `xcrun simctl list devices available` for ids)
xcodebuild -project ios/SentinelCore.xcodeproj -scheme SentinelCore -configuration Debug \
  -destination 'id=<simulator-udid>' build
```

Swift 6 strict concurrency is on, so concurrency mistakes are compile errors. Two runtime traps
the compiler doesn't catch:

- **A closure written inside `@MainActor` code and handed to a framework that calls it on its own
  queue traps at runtime** (EXC_BREAKPOINT in `dispatch_assert_queue`). This happened with
  EventKit's `fetchReminders` completion. Mark such closures `@Sendable` and keep their work
  nonisolated.
- `NSItemProvider` and similar non-Sendable objects can't be passed into a `nonisolated` async
  helper. Keep them on the main actor and only send the loaded value back.

### Test

The app targets have no test target; the logic lives in `ios/OrganizerKit`, which does:

```bash
swift test --package-path ios/OrganizerKit                         # unit tests; live-server tests skip
scripts/with-organizer-servers.sh swift test --package-path ios/OrganizerKit   # everything (~77 tests)
```

`with-organizer-servers.sh` starts two throwaway servers on loopback (ports 8911 and 8912, or
`ORGANIZER_PORT_A` / `ORGANIZER_PORT_B`) with scratch data, and exports their addresses so the
live two-device and server-replacement tests run. If a port is already taken, most likely by
another session's servers, pick different ones rather than killing it.

For a Swift model or wire-format change, a quick decode check needs no simulator:
`swiftc main.swift <path>/Models.swift` with a scratch `main.swift` that decodes a sample.

### Run on a simulator or device

- **Simulator:** build as above, then `xcrun simctl install <udid> <app>` and
  `xcrun simctl launch <udid> com.sentinelcore.client -serverURL http://127.0.0.1:<port>`. Launch
  arguments like `-serverURL` override the saved setting for that launch only. Use
  `xcrun simctl spawn <udid> defaults write com.sentinelcore.client serverURL <url>` to make it
  stick. Grant Reminders access without the prompt with
  `xcrun simctl privacy <udid> grant reminders com.sentinelcore.client`.
- **The iPhone and Mac in daily use run TestFlight builds.** A locally built app always signs as
  *development* and registers a sandbox push token, which the production server rejects. So
  installing a local build on those devices breaks their push until a TestFlight build goes back
  on. Test on the simulator, or accept that and reinstall from TestFlight afterwards.
- **Mac, first launch after installing a new build:** macOS rejects the app's first local-network
  request once (a stale Network Extension UUID cache). Launch it twice.

### Siri shortcuts (App Intents): rules App Store Connect enforces

The app's Siri phrases are in `ios/SentinelCore/Organizer/OrganizerIntents.swift`.

- **No intent title, description or dialog may contain the word "Siri".** App Store Connect
  rejects the whole iPhone build during processing with **ITMS-90626 Invalid Siri Support**. The
  upload itself still says "succeeded"; the only sign is an email (see §6.2). The Mac build
  isn't checked for this, which makes it confusing. Check a build before uploading:

  ```bash
  grep -ic siri <built app>/Metadata.appintents/extract.actionsdata   # must print 0
  ```

- Every App Shortcut phrase must contain `\(.applicationName)`. Siri matches the display name,
  "Sentinel Core".
- Siri can't be driven from a script. To exercise an intent's `perform()` in the simulator, use a
  temporary launch-argument hook in `SentinelCoreApp.init` that constructs the intent and calls
  `perform()`, and remove it before committing.

---

## 4. Apple TV (`appletv/`)

A separate project, `appletv/SentinelCoreTV.xcodeproj`, scheme `SentinelCoreTV`, tvOS 17+,
bundle id `com.sentinelcore.client.tv`. It links `ios/OrganizerKit` by relative path, so an
OrganizerKit change must also build here.

```bash
xcodebuild -project appletv/SentinelCoreTV.xcodeproj -scheme SentinelCoreTV -configuration Debug \
  -destination 'generic/platform=tvOS' \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build
```

The three signing overrides are required for a compile check. Without them the build fails with
"Provisioning profile … doesn't include signing certificate", which is a certificate mismatch on
the build machine and says nothing about the code.

Release: `appletv/scripts/release.sh` (TestFlight, once the tvOS App Store Connect record
exists). Until then the Apple TV runs a locally installed build: build Release for
`generic/platform=tvOS`, then `xcrun devicectl device install app --device <tv-id>
<Release-appletvos/SentinelCoreTV.app>`.

---

## 5. Android (`android/`)

```bash
cd android
./gradlew assembleDebug                 # debug APK → app/build/outputs/apk/debug/app-debug.apk
./gradlew testDebugUnitTest             # unit tests; the Organizer live-server tests skip
../scripts/with-organizer-servers.sh ./gradlew testDebugUnitTest --rerun --no-configuration-cache
```

- Gradle prints only "BUILD SUCCESSFUL". For the counts, add up
  `app/build/test-results/testDebugUnitTest/*.xml` (about 127 tests, 9 of them live-server).
- **The live Organizer tests silently skip when Gradle reuses a cached result.** When the servers
  script is involved, pass `--rerun --no-configuration-cache` and check the XML for
  `skipped="0"`.
- The voice-phrase test reads `core/docs/voice-phrase-cases.json` by walking up from the module
  directory, so run it from inside the repository.

### Emulator

AVDs `Pixel_8_API_35` and `Pixel_Fold_API_35`. The emulator reaches the host's loopback as
`10.0.2.2`, so a test server on `127.0.0.1:8931` is `http://10.0.2.2:8931` to the app. Set the
server without going through the UI by writing the app's preferences:

```bash
# prefs.xml: <map><string name="server_url">http://10.0.2.2:8931</string></map>
adb push prefs.xml /data/local/tmp/prefs.xml
adb shell "run-as com.sentinelcore.android mkdir -p shared_prefs"
adb shell "run-as com.sentinelcore.android cp /data/local/tmp/prefs.xml shared_prefs/sentinel_core.xml"
```

With more than one emulator running, every `adb` command needs `-s emulator-<port>`.
`adb emu kill` without it refuses to act, which is a good thing.

Useful checks:

- Launcher shortcuts registered: `adb shell dumpsys shortcut | grep voice_`
- Share target registered: `adb shell cmd package query-activities --brief -a android.intent.action.SEND -t text/plain`
- Simulate the Google Assistant App Action:
  `adb shell am start -a com.sentinelcore.android.VOICE_REMINDER -n com.sentinelcore.android/.MainActivity --es text "'call mom tomorrow at 5pm'"`
- The app's Organizer database: `adb exec-out "run-as com.sentinelcore.android cat no_backup/organizer/organizer.sqlite" > organizer.sqlite`

---

## 6. Releasing

Store accounts, app records, tester lists and review preparation are in
[`STORE_DISTRIBUTION.md`](STORE_DISTRIBUTION.md). This section is the procedure for each release.

### 6.1 One build number for everything

```bash
scripts/bump-build.sh                  # +1 on every Apple target and Android's versionCode
scripts/bump-build.sh --version 0.2.0  # also change the user-visible version
git commit -am "Bump build number to N" && git push
```

Every store upload needs a higher build number than the last. The script rewrites *every*
`CURRENT_PROJECT_VERSION` in both Xcode projects, which matters: **Apple requires an extension's
build number to equal its app's**. A new extension target
copied from another starts with that target's number and must be brought in line once. After
that, the script keeps it there. Commit before releasing so every build traces to a commit.

### 6.2 iPhone, iPad and Mac → TestFlight

```bash
ios/scripts/release.sh            # iOS then macOS
ios/scripts/release.sh ios        # or one of them
ios/scripts/release.sh macos
```

It archives in Release and uploads with `-allowProvisioningUpdates`, which creates missing
distribution certificates, App IDs and profiles, including one for a new extension's bundle id.

**"Upload succeeded" only means the upload reached Apple.** App Store Connect then processes
the build for 5–15 minutes and may still reject it. When that happens:

- the build never appears in TestFlight, while the other platform's build does;
- the only notice is an email to the developer account, you@example.com, in Outlook's
  **Other** tab: "Action needed: The uploaded build for Sentinel Core has one or more issues",
  with an ITMS code naming the problem.

The success notice is a "Sentinel Core 0.1.0 (N) for iOS is now available to test" email. If
it hasn't arrived after about 20 minutes, look for the other one.

Other things worth knowing:

- `release.sh` pipes through `grep`, so per-platform detail is in
  `$TMPDIR/SentinelCore_*.xcdistributionlogs/` (look for "Upload succeeded").
- An "Upload Symbols Failed … dSYM for the A" warning is harmless: a vendored framework ships
  without debug symbols.
- `ios/build/` holds the archives and is gitignored. `release.sh` checks that an archive exists
  after archiving, so delete a stale `ios/build/` before a re-run you want to trust.
- After the TestFlight build installs: on the Mac, launch it twice (see §3).

### 6.3 Android → Google Play

```bash
android/release.sh              # signed .aab; uploads too once a Play service account is set up
android/release.sh --no-upload  # build only
```

- The bundle is `android/app/build/outputs/bundle/release/app-release.aab` (about 36 MB since the
  WebRTC library arrived). Check the signature with `jarsigner -verify` and `keytool -printcert
  -jarfile` (Owner `CN=Sentinel Core, O=NR5P`).
- Without `playServiceAccountJson=` in `android/keystore.properties` the upload is manual: Play
  Console → Sentinel Core → Testing → **Internal testing** → Create new release → drop the
  `.aab` → Save and publish. Setting up the service account is in STORE_DISTRIBUTION.md §5.
- Keep a dated copy (`SentinelCore-0.1.0-buildN.aab`) outside the build folder, because the
  next build overwrites it.

### 6.4 Server → production

On the server itself, from the repository root:

```bash
git pull --ff-only && core/scripts/deploy.sh
```

`deploy.sh` checks that it will be able to restart before it builds, then builds, runs all tests,
restarts whatever's installed (`sentinelcore`, `sentinelcore-web`, and the HomeKit sidecar
and push relay where present) and prints health checks. Details are in DEPLOYMENT.md §9.

- **Never run a wide `git checkout -- core/` on a server.** The `appsettings.json` files there are
  modified on purpose; they *are* that server's configuration. `git pull --ff-only` leaves them
  alone. If they're ever reverted by accident, the copies in `core/<project>/bin/Release/net10.0/`
  still hold the real values.
- Restarting `sentinelcore-web` needs `sudo`. From another machine, run the whole thing in a
  terminal where you can type the password, with `sudo -v` first so it's cached for the restart
  at the end:

  ```bash
  ssh -t <user>@<server> 'cd <repo> && git pull --ff-only && sudo -v && core/scripts/deploy.sh'
  ```

- The script's dashboard health check runs a couple of seconds after the restart and can print
  "unreachable" while it's still starting. Check again after a few seconds. go2rtc
  "connection refused" lines in the first seconds of the log are the same thing.
- **Order matters when the sync protocol gains something older apps can't read.** A new
  Organizer item kind made older Apple builds fail their whole sync. For such a change, get the
  new app builds onto every device *before* deploying the server. Additive server changes can go
  in any order.
- **Silent pushes reach every registered device.** Android builds older than 5 show any push,
  silent or not, as a "Sentinel Core alert" notification. Before a server that sends
  Organizer-change pushes serves an Android phone that has push enabled, that phone needs build
  5 or later.

---

## 7. What to run before calling a change good

| You changed… | Run |
| --- | --- |
| Server C# | `cd core && dotnet test` |
| Dashboard JS | `node --check` on the file; exercise it against a loopback server |
| `ios/OrganizerKit` | `scripts/with-organizer-servers.sh swift test --package-path ios/OrganizerKit`, then build the Mac, iOS **and tvOS** apps (all three link it) |
| iPhone/Mac app | the Mac and iOS builds from §3; the iOS build also compiles both extensions |
| An App Intent | the iOS build, then the `grep -ic siri` check from §3 |
| Android | `./gradlew assembleDebug testDebugUnitTest` |
| Windows | `dotnet build windows/SentinelCore.Windows.sln -p:EnableWindowsTargeting=true` and `dotnet test windows/SentinelCore.Client.Tests -p:EnableWindowsTargeting=true` (see below) |
| The sync protocol | all of the above, plus the live suites several times. One passing run of a live suite proves little. |
| Anything shared between platforms (phrase parsing, shared notes) | both implementations' tests; they read the same fixture |

**Windows from a Mac:** `EnableWindowsTargeting=true` pulls the Windows Desktop reference packs,
so the whole solution, XAML included, compiles on a Mac. `dotnet test` there runs only
`SentinelCore.Client.Tests`. Expect 8 failures, the DPAPI credential tests, because DPAPI
doesn't exist off Windows. Note that it doesn't compile the WPF project, so after a `.xaml` edit
build the solution. `SentinelCore.App.Tests` can only run on Windows.

---

## 8. Sync bugs to remember

Things that went wrong once, so nobody has to rediscover them:

- **A background one-off sync must not count as the app being connected.** Both Organizer engines
  have `syncOnce`, used by background refresh and silent push, which borrows a transport for one
  pass. When `isConnected` counted a borrowed transport, an app coming forward during that pass
  skipped connecting, then was left with nothing when the pass finished, and quietly didn't sync
  while open. `isConnected` now excludes borrowed transports, and `SyncOnceTests` (Swift) and
  `OrganizerEngineTest` (Kotlin) pin it down.
- **A device that doesn't know its spaces yet defaults to Family.** Anything added without the
  person choosing (Siri, a voice shortcut, the Reminders inbox, a share) waits until the device
  has synced once (`knowsSpaces`), so private items never land where everyone sees them.
- **A reused simulator or emulator pointed at a new test server shows the "this server doesn't have
  your Organizer data" prompt**, and nothing syncs until someone answers it. Reinstall the app,
  or on Android run `pm clear`, for a clean test.
