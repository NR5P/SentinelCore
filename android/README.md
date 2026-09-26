# Sentinel Core for Android

Native Android client for phones and tablets (Android 8.0 / API 26 and newer, targeting API 36),
written in Kotlin with Jetpack Compose. Package `com.sentinelcore.android`, distributed through
Google Play's internal testing track.

**Building, testing and releasing:** [`docs/BUILDING.md`](../docs/BUILDING.md) §5 and §6.3.
This README covers what's here and how it fits together.

## Open and run

1. Open the `android` folder in Android Studio and let Gradle sync. Android Studio writes
   `local.properties` (the SDK path) the first time.
2. Choose a phone, tablet or emulator (API 26+) and run the `app` configuration. Or from a
   terminal: `./gradlew assembleDebug`, then `adb install -r app/build/outputs/apk/debug/app-debug.apk`.
3. Enter the server address, for example `http://192.168.50.8:3320`. On an emulator, the host
   computer's own loopback is `10.0.2.2`, not `localhost`.

Files the build looks for that aren't in git: `local.properties` (the SDK path, required),
`app/google-services.json` (Firebase; without it the app builds with no push) and
`keystore.properties` (release signing; only `release.sh` needs it). In a git worktree, copy all
three from the main checkout.

Cleartext HTTP is enabled on purpose, because home servers are usually plain HTTP on a private
address. Use HTTPS before exposing a server beyond the local network.

## Layout

| Package | What's in it |
| --- | --- |
| `ui/` | The Compose app shell (`SentinelApp`, `SentinelViewModel`), screens, the Organizer UI (`ui/organizer`), the Files library UI (`ui/files`), theme, tab bar customization. |
| `data/`, `net/` | The API client and models; server discovery over mDNS. The Files library's wire format and path helpers are in `data/FamilyFilesModels.kt`, its picked-file naming in `data/FamilyUploadNaming.kt`. |
| `organizer/` | The Organizer's on-device store and sync engine (`OrganizerEngine`, kept once per process by `OrganizerRuntime`), local alarms, holidays, and the voice and share entry points (`VoiceCapture`, `VoicePhrase`). |
| `widgets/` | Home-screen widgets and the background sync that keeps them current (WorkManager, plus the server's silent push). |
| `push/` | Firebase Cloud Messaging: security alerts, and the silent "Organizer changed" push. |
| `publish/` | This phone as one of the dashboard's cameras (foreground service, WebRTC). |
| `res/xml/shortcuts.xml` | The launcher shortcuts and the Google Assistant App Action. |

Tests are in `app/src/test`: plain JVM unit tests, plus Organizer tests that run against real
loopback servers when started through `scripts/with-organizer-servers.sh` (see BUILDING.md §5).

## Features

**Security cameras**
- Events with search and notified-only filters, snapshots and metadata; the recording timeline
  with AI event marks, frame scrubbing and the shared reviewed flag; motion-trigger snapshots and
  clips.
- Live camera playback with audio.
- This phone as one of the dashboard's cameras (WebRTC, H.264), streaming from a foreground
  service with the screen off and resuming after a relaunch.
- Push alerts through Firebase, opt-in per phone (Settings → Push notifications → Enable
  Notifications), with deep links to the event.

**Organizer:** calendar, reminders, notes and boards, shared with the dashboard and the other
apps; protocol in [`core/docs/ORGANIZER_SYNC.md`](../core/docs/ORGANIZER_SYNC.md).
- Personal, Family and group spaces; pictures, files and links on anything; board cards with
  comments that can be dragged between columns and boards; optional holiday calendars.
- A copy kept on the phone (light by default) that works offline. Reminder and event alerts are
  local alarms, restored after a reboot, so they fire with no signal. Exact timing needs the
  "Alarms & reminders" permission; without it alerts arrive within a few minutes.
- Home-screen widgets for Calendar, Reminders and Notes.

**Getting things into the Organizer without opening the app**
- **"Voice reminder" and "Voice note"**: long-press the app icon (or drag them onto the home
  screen), tap and speak. Dates in what you say are understood ("call mom tomorrow at 5"), using
  the same rules and test cases as the Apple apps (`core/docs/voice-phrase-cases.json`).
- **Google Assistant:** "Hey Google, save … to Sentinel Core", through an App Action. Gemini
  doesn't run App Actions yet, so on a phone that has switched to Gemini only the shortcuts work.
- **Share to Sentinel Core:** "Sentinel Core note" in any app's Share menu saves text or a link.
- None of these puts anything in Family by default. They wait until the phone has synced and
  knows the person's own space.

**Movies:** the family media library (contract in [`core/docs/MOVIES.md`](../core/docs/MOVIES.md)),
shown when the server has Movies & TV on: Home shelves, paged Movies and TV Shows grids with
search, sort, genre and unwatched filters, detail pages with seasons and episodes, and a
full-screen Media3 player that resumes where anyone in the family left off (direct MP4 or the
server's HLS remux, with subtitles) and rolls on to the next episode.

**Other:** Alarm (arm/disarm with a PIN, sensors, batteries, a home-screen widget), Device Hub (lights, gates, locks, sensors, and a full-screen wall display of big
tap-to-switch tiles for a tablet on a wall), the admin screens, optional dashboard login,
a remote fallback address, a bottom bar on phones and a navigation rail on tablets, and a
customizable tab bar.

**Files:** the household's shared file library on the server (`/api/family-files`), as a FILES
tab that appears when the server has the feature on. Browse folders with thumbnails, search the
whole library, add photos and videos from the phone's picker or any file through the system
picker (uploads stream straight from the content Uri and carry on if you switch tabs), make
folders, rename, and move things to the trash; open or share a file through whatever app handles
its type. The trash screen puts files back per batch or one at a time. An Admin also manages the
backup drives (a folder on the server's machine, or another computer over SFTP with a pinned host
key), runs a backup, brings files back from a drive, checks for duplicates and runs the nightly
pass by hand; everyone else sees the drives read-only. Downloaded copies for Open and Share go
under the app's cache as `files-open/`, one of the `res/xml/file_paths.xml` entries the
FileProvider serves.

## Things to know before changing code here

- **Push is opt-in on each phone.** A phone isn't registered with the server until someone taps
  Enable Notifications. Builds older than 5 show *any* push, including the server's silent
  "Organizer changed" push, as a visible alert, so update a phone before enabling push on it.
- **One Organizer engine per process.** `OrganizerRuntime` owns it, because the widgets'
  background sync runs with no Activity. Two engines on one database would overwrite each other.
- **The Organizer is only "connected" when the app connected it.** A background `syncOnce`
  borrowing a transport doesn't count (see BUILDING.md §8).
- Push setup (Firebase project, service account, `fid`): [`core/docs/PUSH_NOTIFICATIONS.md`](../core/docs/PUSH_NOTIFICATIONS.md).
