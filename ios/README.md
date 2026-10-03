# Sentinel Core for iPhone, iPad and Mac

Native SwiftUI client for iPhone, iPad (iOS 17+) and Mac (macOS 14+), one codebase. It talks to
`SentinelCore.Web`'s JSON API and never connects to cameras or the engine on port 5080 directly.

**Building, testing and releasing:** [`docs/BUILDING.md`](../docs/BUILDING.md) §3 and §6.2.
This README covers what's here and how it fits together.

## Open and run

1. Open `SentinelCore.xcodeproj` in Xcode and pick the `SentinelCore` scheme.
2. Choose an iPhone, iPad, simulator or "My Mac" destination and run.
3. The setup screen lists servers it finds on the network (Bonjour, `_sentinelcore._tcp`); pick
   one or type the address, for example `http://192.168.1.50:3320`.

Or from a terminal:
`xcodebuild -project ios/SentinelCore.xcodeproj -scheme SentinelCore -destination 'platform=macOS' build`.

The iPhone and Mac in daily use run **TestFlight** builds. A local build registers a sandbox
push token that the production server rejects, so installing one on those devices breaks
their push until the TestFlight build is reinstalled. BUILDING.md §3 has more.

## What's in the project

| Folder / target | What it is |
| --- | --- |
| `SentinelCore/` (target `SentinelCore`) | The app, for iOS and macOS. Subfolders: `App` (model, lifecycle, push, background refresh), `Views`, `Live` (video players), `Organizer`, `Files` (the shared file library and its backup drives), `Movies` (Movies & TV, downloads and library admin), `Music`, `Podcasts` and `Audio` (the player and everything the two share), `Weather`, `Publish` (this device as a camera), `Networking`, `Models`, `Design`. |
| `SentinelCoreWidgets/` (iOS only) | Calendar, Reminders, Notes and Alarm widgets, plus Quick Add. They read a snapshot the app writes to the App Group, and ticking a reminder off on a widget leaves an action for the app to apply. |
| `SentinelCoreMacWidgets/` (macOS only) | The Mac's widget extension, holding just Quick Add. Sandboxed, no App Group. |
| `SentinelCoreQuickAdd/` | The Quick Add widget, compiled by both widget targets. Its buttons are `sentinelcore://capture?mode=voice\|type` links that open the app's Quick Add sheet. |
| `SentinelCoreShare/` (iOS only) | The share extension: "Sentinel Core" in the share sheet saves text or a link as an Organizer note. |
| `OrganizerKit/` (Swift package) | The Organizer's on-device store and sync engine, calendar maths, voice-phrase and Quick Add parsing, note checklists, board automations, the Apple Reminders mapping, and the widget snapshots. Shared with the Apple TV app and tested with `swift test`. |
| `scripts/release.sh` | Archive and upload to TestFlight (iOS, macOS or both). |
| `Info.plist`, `SentinelCoreWidgets-Info.plist`, `SentinelCoreMacWidgets-Info.plist`, `SentinelCoreShare-Info.plist` | One per target. The app's plist is shared by iOS and macOS; keys only one platform reads, such as `NSServices` (Mac) or `UIBackgroundModes` (iOS), are ignored by the other. |

Folders are synchronized groups: new files join their target without editing the project.
Entitlements: `SentinelCore-iOS.entitlements` (push, App Group) and `SentinelCore.entitlements`
(macOS sandbox: network, camera, microphone, user-selected files read and write - the write half
is for saving a copy of a Files download - Calendars/Reminders, push). Usage strings, background
modes and the extension-embedding gotcha are in BUILDING.md §3.

The only Swift package besides OrganizerKit is [stasel/WebRTC](https://github.com/stasel/WebRTC),
for streaming this device as a camera.

## Features

**Security cameras**
- Events: the server's merged motion + AI list, with search, notified-only and show-hidden
  filters, and an event detail with the full-resolution snapshot and clip.
- Recording timeline: a day of one camera, with AI event marks, frame scrubbing, filters and a
  reviewed flag shared across clients.
- Live: tiles and full-screen playback. Transport is MSE over WebSocket first (a quarter to half
  a second behind), with HLS as the fallback. PTZ controls, and a record button that files a
  trigger.
- This iPhone, iPad or Mac as one of the dashboard's cameras (WebRTC, H.264), kept awake while
  streaming and resumed after a relaunch.
- Push alerts through APNs, with foreground presentation and deep links to the event.

**Organizer:** calendar, reminders, notes and boards, shared with the dashboard and the other
apps. Details are in [`core/docs/ORGANIZER_SYNC.md`](../core/docs/ORGANIZER_SYNC.md).
- Personal, Family and custom group spaces; items carry pictures, files and links. Boards have
  columns, cards, card comments, and cards can be dragged between columns and boards. Optional
  holiday calendars, and a private `.ics` subscribe link.
- A copy is kept on the device. A phone keeps a light copy by default and a Mac keeps
  everything; both work offline. Reminder and event alerts are local notifications from that
  copy, so they fire with no signal.
- Kept current in the background by the server's silent push and by background refresh, which
  also keep the widgets up to date.
- Note checklists (tick-off lists kept as plain Markdown), and a no-code automations builder for
  each board (`Organizer/BoardAutomationsView.swift`; the rules run on the server).

**Getting things into the Organizer without opening the app**
- **Siri and Shortcuts** (`Organizer/OrganizerIntents.swift`): "Add a reminder in Sentinel Core",
  "Take a note in Sentinel Core", "Add an event in Sentinel Core", "What's due in Sentinel Core",
  each with variants ("Add a reminder to…", "Create a note in…", "Add to my Sentinel Core
  calendar"). They also appear in Shortcuts, Spotlight and on the Action button. Spoken dates are parsed by
  `OrganizerKit/VoicePhrase.swift` ("call mom tomorrow at 5"). Apple doesn't let an app take over
  plain "remind me to…", so every phrase names the app.
- **Apple Reminders inbox** (Settings → Apple Reminders, per device): reminders added to a chosen
  Reminders list move into an Organizer space (Personal by default) and out of Apple Reminders.
  With that list set as the Reminders default, plain "Hey Siri, remind me…" ends up here too.
  Reminders that alert by location, or repeat in a way the Organizer can't express, stay in
  Apple Reminders.
- **Quick Add** (`Organizer/QuickCaptureSheet.swift`): say or type "take out the trash at 7am
  tomorrow" or "put lunch on my calendar Friday from 2 to 3pm" and it becomes a reminder, note or
  event, parsed by `OrganizerKit/QuickCapture.swift` with no AI. Opened from the Quick Add widget
  (Home Screen, Lock Screen and the Mac's desktop), or File → Quick Add… (⇧⌘N) on the Mac.
- **The microphone button on the Reminders list** (`Organizer/OrganizerVoice.swift`): the
  system recognizer and the same phrase rules as Siri.
- **Share to Sentinel Core:** the share sheet on iPhone/iPad; on the Mac, select text or a link
  and use Services → New Sentinel Core Note.
- None of these puts anything in Family by default. They wait until the device has synced and
  knows the person's own space.

**Other**
- Movies & TV (when switched on server-side): the family media library - Continue Watching and
  recently added shelves, poster grids with sort/genre/unwatched filters, search, detail pages
  with seasons and episodes, and playback with resume and progress synced to every device
  (full-screen AVPlayerViewController on iOS, a sheet on the Mac). See `core/docs/MOVIES.md`.
  When movie downloads are on: "+ Add" to search the household's sites (and a search that finds
  nothing offers to), a Downloads screen with the VPN light, and an "already have it" prompt. An
  admin can delete a movie (not one on a network share), and manage the libraries (`Movies/MediaLibrariesAdminView.swift`),
  including network shares (`smb://PC/Movies`) and a New folder button in the folder picker. See
  `core/docs/MOVIE_DOWNLOADS.md`.
- **Music and Podcasts** (each when switched on server-side; `Music/`, `Podcasts/`, `Audio/`): the
  music library by artist, album, song, genre, playlist and folder; podcasts with Listen Now, a
  Library and Discover. One shared player (`Audio/AudioPlayer.swift`) with a mini player under
  every tab, a Now Playing screen, gapless queues, Sound Check, a sleep timer, the lock screen,
  Control Center and CarPlay Now Playing card, and background audio. Contract:
  [`core/docs/MUSIC_AND_PODCASTS.md`](../core/docs/MUSIC_AND_PODCASTS.md).
- **Weather** (`Weather/`): the dashboard's Weather tab - alerts, conditions, a zoomable radar map
  with storm tracking, hourly and seven-day forecasts - all through the server's `/api/weather`,
  for the location set in the server's Settings.
- Device Hub: lights, gates, locks and sensors over MQTT, with a full-screen wall display of big
  tap-to-switch tiles for an iPad mounted on a wall (remembered, so the app reopens onto it).
- Alarm: arm and disarm the house, see every sensor's state and battery, and (admins) set it up. An Alarm widget (Home and Lock Screen) and "Arm the Sentinel Core alarm" / "Is Sentinel Core armed" shortcuts.
- **Files** (`Files/`): the household's shared file library on the server, a Files tab when the
  server has Family Files switched on. Browse folders, search the whole library, add photos and
  videos from the picker or any file from the Files app/Finder (big videos stream from disk),
  open a file in Quick Look, share it, rename it, or move it to the trash. Backup drives - a
  folder the server can see or another computer over SFTP - each hold a full copy; an admin
  adds and tests them, starts a backup or brings files back from one, runs the nightly pass
  (index, duplicate check, backup, trash cleanup) by hand and sees the last duplicate report.
  Trashed files and removed duplicates wait in the Trash screen, in batches, until put back or
  the trash is emptied. A running server job shows as a banner above every Files screen.
- Admin screens: cameras and trigger zones, accounts, known faces and vehicles, schedules,
  prompt profiles, HomeKit, backup, licence, and the full server settings tree.
- Optional dashboard login; a remote fallback address; automatic plain-HTTP fallback when the
  device doesn't trust the server's own certificate authority (said so in Settings).
- Customizable tab bar on iPhone, with a More tab for the rest.

## Things to know before changing code here

- **Siri shortcut text may not contain the word "Siri".** App Store Connect rejects the iPhone
  build (ITMS-90626) after a successful-looking upload. See BUILDING.md §3.
- **Extensions' build numbers must match the app's.** `scripts/bump-build.sh` keeps them in step.
- **A macOS-only embed needs `platformFilters = (macos, );`** in `project.pbxproj`, plural. The
  singular form is ignored and the iOS Simulator build fails (BUILDING.md §3).
- **Build the Mac and iOS targets one at a time.** Two overlapping `xcodebuild`s fight over the
  Swift package cache.
- **Swift 6 strict concurrency is on.** A `@MainActor` closure handed to a framework that calls it
  on another queue traps at runtime, as EventKit's did (BUILDING.md §3).
- **The Mac, first launch after installing a new build:** the first local-network request fails
  once; launch the app twice.
- Push setup (APNs key, bundle ids, sandbox vs production):
  [`core/docs/PUSH_NOTIFICATIONS.md`](../core/docs/PUSH_NOTIFICATIONS.md).
- Live-view history and the transport ladder: [`core/docs/IOS_LIVE_VIEW_HANDOFF.md`](../core/docs/IOS_LIVE_VIEW_HANDOFF.md).
