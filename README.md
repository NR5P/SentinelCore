# Sentinel Core

A self-hosted camera security system with AI review of motion events, plus a family home server
around it. A server runs on your own computer; apps on iPhone, iPad, Mac, Apple TV, Android and
Windows, and a web dashboard, all talk to it.

What it does:

- **Cameras**: live view, motion and trigger zones checked by a local (or cloud) vision model,
  clips, a timeline, PTZ, known faces, push / email / Telegram / Discord alerts, and any phone,
  tablet or computer with a camera can stream itself in as one more camera.
- **Organizer**: shared notes (with checklists), calendar (with optional holiday calendars),
  reminders and Trello-style boards with no-code automations, synced and usable offline on every
  app. Home Screen widgets, a Quick Add widget, Siri and Android voice shortcuts, and a share
  extension add to it.
- **Home**: a Device Hub for MQTT lights, plugs and locks (with a wall display), a home alarm built
  from its sensors, and a Weather tab with National Weather Service forecasts, radar and alerts.
- **Media**: Movies & TV (local folders and shares on other computers), Music & Podcasts, and
  optional downloads of movies, shows and albums through a VPN.
- **Family Files**: a shared file library backed up nightly to any number of drives.
- **Running it**: an admin login from the first launch, a full backup and a nightly automatic
  export to a folder or SFTP, self-monitoring, and a kiosk mode for wall-mounted touch screens.

## Where things are

| Folder | What it is | README |
| --- | --- | --- |
| `core/` | The server: AI vision engine, web dashboard and API, push delivery, push relay, licensing, HomeKit sidecar, and the movie-downloads installer (`core/downloads/`) | [core/README.md](core/README.md) |
| `ios/` | iPhone, iPad and Mac app, with Home Screen widgets (Calendar, Reminders, Notes, Alarm), the Quick Add widget (iPhone and Mac), a share extension, and the shared `OrganizerKit` package | [ios/README.md](ios/README.md) |
| `appletv/` | Apple TV app | [appletv/README.md](appletv/README.md) |
| `android/` | Android app for phones and tablets | [android/README.md](android/README.md) |
| `windows/` | Windows app (WPF) | [windows/README.md](windows/README.md) |
| `scripts/` | `bump-build.sh` (one build number for every app), `with-organizer-servers.sh` (throwaway test servers) | |
| `docs/` | Building and releasing, store distribution, privacy policy | |
| `branding/` | App icon masters | |

## Documentation

- **[docs/BUILDING.md](docs/BUILDING.md)**: tools, building and testing every part, releasing to
  TestFlight, Google Play and a production server, and the pitfalls hit so far. Start here.
- [core/docs/DEPLOYMENT.md](core/docs/DEPLOYMENT.md): installing and operating the server.
- [core/docs/USER_GUIDE.md](core/docs/USER_GUIDE.md): using the dashboard and its settings.
- [docs/STORE_DISTRIBUTION.md](docs/STORE_DISTRIBUTION.md): App Store Connect and Play Console
  setup, testers, and review preparation.
- [core/docs/PUSH_NOTIFICATIONS.md](core/docs/PUSH_NOTIFICATIONS.md) and
  [core/docs/PUSH_RELAY.md](core/docs/PUSH_RELAY.md): push providers and the relay.
- [core/docs/ORGANIZER_SYNC.md](core/docs/ORGANIZER_SYNC.md): the Organizer's sync protocol,
  shared by the server and every app.
- [core/docs/MOVIES.md](core/docs/MOVIES.md), [core/docs/MOVIE_DOWNLOADS.md](core/docs/MOVIE_DOWNLOADS.md)
  and [core/docs/MUSIC_AND_PODCASTS.md](core/docs/MUSIC_AND_PODCASTS.md): the Movies & TV, "+ Add"
  downloads, and Music & Podcasts APIs every app builds against.
- [docs/PRIVACY_POLICY.md](docs/PRIVACY_POLICY.md): what the apps, the server and the push relay
  send where.
- `core/docs/*_HANDOFF.md`: design notes and investigations for specific features.

## Quick start (developer)

```bash
# each line runs from the repository root
(cd core && dotnet build -c Release && dotnet test)                       # server
swift test --package-path ios/OrganizerKit                                # Organizer engine (Apple)
xcodebuild -project ios/SentinelCore.xcodeproj -scheme SentinelCore -destination 'platform=macOS' build
(cd android && ./gradlew assembleDebug testDebugUnitTest)                 # Android
dotnet build windows/SentinelCore.Windows.sln -p:EnableWindowsTargeting=true   # Windows (builds on a Mac too)
```

Details, prerequisites and the files that aren't in git are in
[docs/BUILDING.md](docs/BUILDING.md).
