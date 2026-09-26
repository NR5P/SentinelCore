# Sentinel Core for Windows

Native Windows client, written in C# on WPF (.NET 8). The Windows counterpart to the SwiftUI app in
[`ios/`](../ios) — same server, same JSON API, same feature set.

## Build and run

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Nothing else — no Visual
Studio, no workloads.

```bash
dotnet run --project SentinelCore.App
```

On first launch the app searches the local network for a server and lists what it finds; you can
also type the address by hand, for example `http://192.168.1.50:3320`.

## Test

```bash
dotnet test
```

The Organizer's end-to-end tests need two throwaway servers, which one script starts on loopback
with scratch data:

```bash
../scripts/with-organizer-servers.sh dotnet test SentinelCore.Client.Tests
```

Without it they are skipped, not failed.

286 tests. The client library is exercised end to end against a real loopback HTTP server rather
than a mocked handler — status codes, cookies, error-body shapes and all — because half of what that
layer does is precisely the part a mock replaces.

## Ship it to another PC

The build above produces an app that needs the .NET 8 **Desktop** Runtime installed on the machine
that runs it. To produce a folder that needs nothing at all:

```bash
./publish.ps1
```

That writes a self-contained x64 build to `publish/`. It is larger (~150 MB) and it is the version
worth handing to someone who should not have to install a runtime first.

## What it does

- **Events** — the server's merged list of everything the cameras caught: motion and the AI's read
  of it, one row per moment. Search, notified-only and show-hidden filters, refreshing every 30
  seconds. Opening a row gives the full-resolution snapshot, the clip when one was recorded, and
  everything the server knows about the event.
- **Timeline** — a day of one camera drawn end to end: recorded stretches, AI event marks, severity
  and unreviewed filters, and the reviewed flag shared with every other client.
- **Live** — a tile per camera, arranged per account by dragging one onto another. Double-click for
  the full-size view with PTZ. The record button (on both the tile and the full-size view) files a
  trigger with a short look-back clip and starts recording until pressed again, or until the limit
  in Settings → Scheduled Recording stops it.
- **Movies** — the family film and TV library the server scans (shown only while Movies & TV is on
  in the server's settings): Home shelves, Movies and TV Shows grids with search, sort, genre and
  unwatched filters, a detail page with Resume / Start over and season episode lists, and a
  full-window player in WebView2 (`Live/movie.html`: direct MP4 or HLS through hls.js, subtitles,
  "Up next" for the next episode). Progress is saved to the server, so it resumes on any device.
  The API is [`core/docs/MOVIES.md`](../core/docs/MOVIES.md).
- **This PC as a camera** — the PC's webcam becomes one of the dashboard's cameras (Settings → Cameras, or the
  button at the top of Live). The dashboard's own publish page runs in WebView2 with this session's cookie, and
  the app holds the PC out of sleep while it streams and resumes on the next launch. See
  `Views/DeviceCameraWindow.xaml.cs` for how the http origin and the self-signed certificate are handled.
- **Organizer** — calendar, reminders, notes and Trello-style boards, shared with the dashboard and
  the phone apps: private to you, the whole family, or a group you choose, with pictures, files and
  links on anything. It is kept on this PC as well as the server, so all of it works with no
  connection, and a PC keeps the whole thing by default — the copy that refills a server that had
  to be rebuilt (Settings → Organizer). Reminder and event alerts come from that copy, so they fire
  with the server down. The engine is `SentinelCore.Client/Organizer/`; the protocol every client
  speaks is [`core/docs/ORGANIZER_SYNC.md`](../core/docs/ORGANIZER_SYNC.md).
- **Files** — the household's shared file library, once it is switched on in Settings. Browse
  folder by folder or search the whole library; add files with a dialog or by dropping them on the
  list (a dropped folder keeps its structure); double-click to open a file (it is fetched into
  `%TEMP%\SentinelCore\Files` and handed to Windows), or save a copy, rename, or move it to the
  trash, where it can be put back. Uploads and downloads stream, so a video does not have to fit in
  memory. **Backup drives** shows every drive (a folder the server can see, or another computer
  over SFTP), how its last backup went and how much room is left, and lets an admin back up now,
  bring files back, run the nightly pass, or check for duplicate copies; a Viewer sees the same
  list read-only. The server does the copying and reports progress in a banner that polls while a
  job runs. The API is `SentinelCore.Client/Networking/SentinelClient.FamilyFiles.cs`.
- **Device Hub** — the lights, gates, locks and sensors the server tracks over MQTT, with the
  controls each kind actually speaks, plus discovery and editing.
- **Alarm** — arm and disarm the house (with the PIN, if one is set), every sensor's state and
  battery, and the admin windows for sensors, delays, sirens and the PIN.
- **Settings** — connection (including the remote fallback address), account, server health, service
  restarts and host reboot, the connection log, and every admin screen: cameras, trigger zones,
  accounts, dashboard login, known faces, known vehicles, schedules, prompt profiles, HomeKit,
  backup, licence, and the whole schema-driven server settings tree.

## Two things worth knowing

**Live video runs inside WebView2.** WPF can play none of the transports this server offers —
WebRTC needs a peer connection and MSE needs MediaSource, and both live in a browser engine. So each
tile hosts the Edge WebView2 control running the same WebRTC → MSE ladder the web dashboard uses,
handed this session's own cookie. The runtime ships with Windows 11 and current Windows 10; where it
is missing, the app says so and everything else keeps working.

The dashboard's third tier is HLS and this deliberately does not have it: the server's HLS is
MPEG-TS for H.264 cameras, which no browser engine plays without a JavaScript transmuxer. On a
server with no go2rtc at all, tiles fall back to refreshing the server's cached thumbnail and the
badge says `SNAPSHOTS`, so nobody mistakes a two-second-old still for a live feed.

**Notifications are local, not push.** Sentinel Core sends push through Apple and Firebase; there is
no Windows provider, so this app cannot register for a token the way the phone does. Instead it
raises a tray notification itself whenever its own refresh finds an alert the server decided to
notify about — which means it has to be running to alert you. The first refresh of a session only
seeds the set, so connecting to a server with a day of history behind it doesn't fire a hundred
notifications at once.

## Layout

| Project | What's in it |
| --- | --- |
| `SentinelCore.Client` | Models, the API client, mDNS discovery, credential and preference storage, and `AppModel` — all of it UI-framework-free and testable without a window. `Models/FamilyFiles.cs` and `Networking/SentinelClient.FamilyFiles.cs` are the Files library and its backup drives. |
| `SentinelCore.App` | The WPF application: theme, views, dialogs, tray icon, and the WebView2 live player. The Files section is `Views/FilesView.xaml`, with `FilesDrivesWindow`, `FilesDriveEditWindow` and `FilesTrashWindow` beside it. |
| `SentinelCore.Client.Tests` | 250 tests over the client library, including the DNS-SD parser, the connect/fallback sequence and the Files transfers. |
| `SentinelCore.App.Tests` | 36 tests over the presentation layer — row wording, badges, converters, relative time, and a smoke test that builds every screen. |

## Where things are kept

`%LOCALAPPDATA%\SentinelCore\` holds `settings.json` (the server address and a couple of
preferences), `credentials\` (the dashboard login, encrypted with DPAPI against your Windows
account), `WebView2\` (the live player's browser profile), and `Organizer\` (this PC's copy of the
Organizer: a SQLite database plus the attached files). Deleting `Organizer\` is safe once everything
has synced — the next launch fetches it all again. Deleting the folder returns the app
to a fresh install.

## HTTPS and the server's own certificate

The server generates its own certificate authority, which Windows does not trust out of the box. The
app notices, falls back to the server's plain-HTTP port, and says so in Settings rather than failing
or downgrading silently. Traffic stays on your local network either way. To keep HTTPS instead,
install the server's `certs/ca.crt` into **Local Machine → Trusted Root Certification Authorities**
on this PC — see `core/docs/DEPLOYMENT.md` section 14a.

The fallback is deliberately local-only. A remote address crosses the open internet, where quietly
dropping encryption is not an acceptable trade, so it is never downgraded.
