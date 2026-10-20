# Sentinel Core — Build & Deployment Guide

This is a from-scratch, no-assistant-required guide to building, configuring, and running
this system on a fresh Linux, Windows, or macOS box, and to updating an existing install. For
how to actually *use* the dashboard once it's running, see [USER_GUIDE.md](USER_GUIDE.md).

**Two ways to install**, covered by different parts of this guide:

- **Prebuilt release package** — download a .zip for your OS, unzip, run one install script.
  No .NET SDK, no git, no build step. This is the fastest path if you just want it running; see
  [section 17](#17-prebuilt-release-packages-no-sdk-required).
- **Build from source** — clone the repo and build it yourself with the .NET SDK, either to
  develop, to customize something, or because you're the one producing the release packages
  above. Sections 1–16 below cover this path.

## Contents

1. [Architecture, in one page](#1-architecture-in-one-page)
2. [Prerequisites](#2-prerequisites)
3. [Getting the code](#3-getting-the-code)
4. [Building](#4-building)
5. [Running the tests](#5-running-the-tests)
6. [First-time configuration](#6-first-time-configuration)
7. [Running interactively (no systemd)](#7-running-interactively-no-systemd)
8. [Installing as systemd services](#8-installing-as-systemd-services)
9. [The deploy script](#9-the-deploy-script)
10. [Passwordless restarts (optional)](#10-passwordless-restarts-optional)
11. [Updating to a new version](#11-updating-to-a-new-version)
12. [Verifying health](#12-verifying-health)
13. [Blue Iris-side configuration](#13-blue-iris-side-configuration)
14. [Firewall](#14-firewall)
15. [Troubleshooting](#15-troubleshooting)
16. [Stopping / uninstalling](#16-stopping--uninstalling)
17. [Prebuilt release packages (no SDK required)](#17-prebuilt-release-packages-no-sdk-required)

---

## 1. Architecture, in one page

Two independent ASP.NET Core services, both **built in place and run directly out of their
own `bin/Release/net10.0` folder** inside the repo — there is no `dotnet publish` step and
nothing gets copied to `/opt` or anywhere else. This matters: if you only rebuild and forget
that detail, you might go looking for the running binary in the wrong place.

```
Blue Iris (Windows) ──POST /api/blueiris/alert──▶ SentinelCore (port 5080)
                                                          │
                                                          ├──▶ Ollama (port 11434, local)
                                                          └──▶ Email / Telegram / Webhook

SentinelCore.Web (port 3320) ──reads/writes the main app's appsettings.json
                                       ──reads the main app's events.db directly
                                       ──owns its own cameras.db / triggers.db
                                       ──can itself POST into /api/blueiris/alert
                                         (trigger-zone "send to alert pipeline")
```

The Web project has a `ProjectReference` back to the main app so it reuses its event-store
and prompt-profile types directly, rather than duplicating them — the two are built together
by one `dotnet build` at the repo root.

| | Main app (`SentinelCore`) | Web dashboard (`SentinelCore.Web`) |
|---|---|---|
| Port | 5080 | 3320 |
| systemd unit | `sentinelcore.service` | `sentinelcore-web.service` |
| Working directory | `SentinelCore/` | `SentinelCore.Web/` |
| Binary | `bin/Release/net10.0/SentinelCore.dll` | `bin/Release/net10.0/SentinelCore.Web.dll` |

> **Note on the `systemd/` folder in this repo:** those files are templates with
> `__PLACEHOLDER__` values that `scripts/install-linux.sh` fills in, and they describe this
> same in-place layout. The worked examples in
> [section 8](#8-installing-as-systemd-services) are what the installer generates from them,
> shown with the placeholders substituted.

## 2. Prerequisites

**The installer handles all of these for you** — `./scripts/install-linux.sh` on Linux,
`./scripts/install-macos.sh` on macOS, or `.\scripts\install-windows.ps1` on Windows detects
what's missing and offers to install it. This list is what it's installing, for reference or if
you'd rather do it by hand.

- **Linux, macOS, or Windows.** Most of this guide is written for Linux (Ubuntu/Debian-family).
  Windows is supported too and is worth considering, since Blue Iris is Windows-only — running
  both on one machine avoids needing a second box. See [section 8b](#8b-windows) for what
  differs there, and [section 8c](#8c-macos) for macOS.
- **.NET 10 SDK**
- **Ollama**, running locally, with a vision-capable model (default `gemma4:e2b-it-qat`, downloaded automatically) -
  required for the default configuration. An offsite Cloud Vision backend (OpenAI, Anthropic, or
  Google) can be used
  instead, or as an automatic fallback, with no local install of its own - just an API key in
  Settings. See the User Guide's
  [Vision analysis backend](USER_GUIDE.md#8-vision-analysis-backend--local-cloud-or-both)
  section.
- **ffmpeg** (with `ffprobe` beside it), installed and on `PATH` — required by the Web project
  for the Cameras/Live/Events tabs (it shells out to ffmpeg for HLS streaming, snapshot capture,
  and clip extraction), and by Movies & TV, Music and Podcasts (see
  [section 6a](#6a-what-the-media-files-and-home-features-need-on-the-server)). The distro
  package is enough: Ubuntu 26.04's ffmpeg 8.0 is what the reference box runs.
- Optional: **whisper.cpp** (`whisper-cli`) and a `ggml-*.bin` model, only if you want the
  server to write podcast transcripts (off by default)
- Optional, Linux only: the **movie downloads** stack (a VPN, Prowlarr, qBittorrent), installed
  by its own script - see [section 8d](#8d-movie-downloads-linux-optional)
- **git**
- Optional but useful: `sqlite3` (to query the databases directly), `openssl` (to generate a
  random API key), `curl` (for health checks)
- Blue Iris (Windows), on the same LAN, able to reach this host

Install .NET 10 (Snap is the simplest path on Ubuntu):

```bash
sudo snap install dotnet --classic
sudo snap alias dotnet.dotnet dotnet 2>/dev/null || true
dotnet --version
```

If `dotnet` doesn't end up at `/usr/bin/dotnet`, run `command -v dotnet` and use that path in
the systemd unit files in section 8 instead.

Install ffmpeg:

```bash
sudo apt-get update && sudo apt-get install -y ffmpeg
ffmpeg -version
```

Install and start Ollama, then pull the model:

```bash
curl -fsSL https://ollama.com/install.sh | sh
ollama pull gemma4:e2b-it-qat
```

You can skip the pull: if the model set in `Ollama:Model` isn't installed, the main service
downloads it by itself on startup. The same goes for picking a different model in
**Settings → Ollama → Model** - the list includes recommended models that aren't downloaded yet;
pick one, save, and it downloads in the background while alerts keep using the current model,
then switches over when the download finishes. Reopen the list to see its progress.

#### Turn off Ollama's prompt cache (the installers do this for you)

Ollama runs llama.cpp's `llama-server`, whose RAM prompt cache defaults to **8 GB**. Every
camera alert carries a different image, so the cache never gets a hit - it just grows by about
400 MB per alert until the model (~4 GB) plus a full cache (~8 GB) runs a 16 GB box out of
memory every few hours. Turning it off costs nothing for this app.

`install-linux.sh`, `install-windows.ps1` and the release-bundle installers set this up (and pull
`gemma4:e2b-it-qat` if it's missing) whenever Ollama is present.
To do it by hand:

- **Linux** - once you have the code (section 3):

  ```bash
  sudo mkdir -p /etc/systemd/system/ollama.service.d
  sudo cp core/systemd/ollama-cache-ram.conf /etc/systemd/system/ollama.service.d/cache-ram.conf
  sudo systemctl daemon-reload && sudo systemctl restart ollama
  ```

- **Windows** - set a machine-wide `LLAMA_ARG_CACHE_RAM=0` environment variable (elevated
  PowerShell: `[Environment]::SetEnvironmentVariable('LLAMA_ARG_CACHE_RAM','0','Machine')`), then
  quit and reopen Ollama from the tray.
- **macOS** - `install-macos.sh` does **not** do this yet. For Ollama started by Homebrew, add
  `LLAMA_ARG_CACHE_RAM` = `0` to the `EnvironmentVariables` of its launchd plist
  (`brew services info ollama` shows the path) and `brew services restart ollama`; for the
  Ollama app, `launchctl setenv LLAMA_ARG_CACHE_RAM 0` and reopen it (repeat after each reboot).

To confirm, after the next alert: `journalctl -u ollama | grep "prompt cache"` should say
`prompt cache is disabled` (not `enabled, size limit: 8192 MiB`). The dashboard's memory
warning (Settings → SELF-MONITORING) is the backstop if it ever comes back - for example an
Ollama reinstall that removed the drop-in.

## 3. Getting the code

```bash
git clone <your-repo-url> SentinelCore
cd SentinelCore/core
```

(If you're reading this on a box where it's already deployed, the working copy already
lives wherever it was cloned (this guide uses `/opt/sentinelcore`) — the repo root holds `.git` plus a
`core/` folder with all the .NET projects (and a sibling iOS app folder, if present).
`cd` into `core/` and `git pull` for updates, covered in
[section 11](#11-updating-to-a-new-version).)

**If you just want it running**, skip ahead to [section 8](#8-installing-as-systemd-services)
and run `./scripts/install-linux.sh` — it does the build, tests, service install and startup in
one go. Sections 4 through 7 are for understanding the pieces or running them by hand.

## 4. Building

From `core/` (not inside either project folder, and not the repo root - that's shared with
the iOS app now) — this builds all three .NET projects (main app, Web, and the test
project) in one pass:

```bash
dotnet build -c Release
```

`-c Release` matters: both systemd services are configured to run the `Release` build output.
A plain `dotnet build` (Debug) will not be picked up by the services.

If you hit an error like `Fatal error. Internal CLR error. (0x80131506)`, that's a known,
occasional MSBuild build-server flakiness, not a real problem with the code. Fix it and
retry:

```bash
dotnet build-server shutdown
dotnet build -c Release
```

If it comes straight back, build without the build servers and parallel nodes, which has always
got past it:

```bash
dotnet build -c Release --disable-build-servers -m:1
```

`scripts/deploy.sh` does both of these retries for you (see [section 9](#9-the-deploy-script)).

## 5. Running the tests

```bash
dotnet test
```

About 1,950 tests, a few minutes. Worth running before every deploy: it's the one thing
standing between a bad change and the live alert pipeline.

Occasionally the test host aborts partway ("Test process crashed with exit code 133/134") with no
real failures. That's the host, not the code; run it again. Only treat it as real if it happens
three times running on the same tree.

The client apps have their own build and test procedures; see
[`docs/BUILDING.md`](../../docs/BUILDING.md) at the repository root.

## 6. First-time configuration

The first visit to the dashboard asks for the **admin account** (username + password). The login
is required from first boot: until that account exists every API call except creating it gets
`401`, the apps' sign-in shows "This server has no accounts yet...", and the account can only be
created by a device connecting directly from the local network - never through the Internet
Exposed port, and never through a tunnel or reverse proxy. (Those connect from `127.0.0.1`, which
used to count as local; since 2026-10-20 any request carrying a proxy's forwarding header -
`CF-Connecting-IP`, `X-Forwarded-For`, `Forwarded`, `X-Real-IP` - is refused, so a Cloudflare
Tunnel published before the account exists can't be used to claim the server.) Do this straight after
installing - until it's done, anyone on the LAN who opens the dashboard first gets to choose the
admin password.

After that, the dashboard's own setup wizard (opens automatically once you're signed in - see
[USER_GUIDE.md § Opening the dashboard](USER_GUIDE.md#2-opening-the-dashboard))
covers the essentials below - API key, vision backend, a camera, notifications - without
hand-editing `appsettings.json`. The walkthrough here is the manual/advanced path, for anything
the wizard doesn't cover or if you'd rather edit the file directly.

### Main app — `SentinelCore/appsettings.json`

This file is **not in git**. The repo carries `SentinelCore/appsettings.defaults.json`, a
secret-free starting point (login on, every alert channel off, no API key), and the install
scripts and the app itself create `appsettings.json` from it the first time - after that it is
this machine's own live configuration, the file the dashboard saves every credential into, and
it is gitignored so a `git commit -a` can never publish it. Change shipped defaults in
`appsettings.defaults.json`; new keys added there reach existing installs on their next boot
(`ConfigDefaultsSeeder`, add-only - it never changes a value you've set).

1. **Generate and set a real API key** (Blue Iris authenticates with this) - the setup wizard
   does this step for you automatically; only do it by hand if you're skipping the wizard:
   ```bash
   openssl rand -hex 32
   ```
   Put the result in `Bridge:ApiKey`. Anything Blue Iris sends without a matching
   `X-Api-Key` header is rejected.
2. Confirm `Ollama:Model` matches whatever you pulled in Ollama. Optionally set
   `CloudVision:ApiKey` and flip `CloudVision:Enabled`/`VisionRouting:Primary` to use an
   offsite model instead of, or as a fallback for, local Ollama - see
   [USER_GUIDE.md § Vision analysis backend](USER_GUIDE.md#8-vision-analysis-backend--local-cloud-or-both).
3. Review `PromptProfiles` — 103 profiles ship built-in (see
   [USER_GUIDE.md § Prompt profiles](USER_GUIDE.md#9-prompt-profiles--what-the-ai-is-told-to-look-for)
   for what's in there and how to add your own by hand-editing this section directly, if
   you're not going through the Settings UI).
4. Configure whichever alert channels you want under `Email`, `Telegram`, `Webhook` —
   all optional, all off by default except what's already been turned on.
5. Set `Bridge:DataDirectory` (default `data`, relative to the project's working directory)
   if you want images/database stored somewhere else.
6. If this install was purchased, drop the `license.lic` file you were emailed next to
   `appsettings.json` (path configurable via `License:FilePath`). It's a one-time-purchase,
   perpetual license - no expiry, nothing to renew. Missing or invalid, the dashboard shows a
   small notice but every feature keeps working regardless - this is visibility, not a
   functional lock. See `LicenseGenerator/README.md` if you're the one issuing licenses rather
   than installing a purchased copy.
7. The dashboard login lives under `Auth:Enabled` and is **on** unless that is explicitly
   `false` - a missing section or value means required. Accounts themselves are not in this
   file (they're in the dashboard's `data/accounts.db`); create the first one on the dashboard's first screen and
   the rest under the accounts tab. `Auth:Enabled: false` - what Settings → LOGIN → turn off
   writes - makes every request an admin; only set it on a network where that's acceptable.
   Upgrading an older install whose file already says `"Enabled": false` keeps it open;
   change it to `true` (or delete the line) to get the new behaviour.

Do not expose port 5080 to the internet. The API key is LAN authentication, not a substitute
for TLS on an untrusted network. The same applies to port 3320/the dashboard login - it's a
username/password over plain HTTP unless you put a TLS-terminating reverse proxy in front of
it yourself.

#### The two `Ollama` keys the Settings UI can't set

Most of the `Ollama` block is editable from **Settings → OLLAMA** and applies within a
couple of seconds, no restart. Two keys are deliberately not in the UI, because both are baked
into the `HttpClient` once at startup and can only be changed by hand here:

```json
"Ollama": {
  "BaseUrl": "http://127.0.0.1:11434",
  "RequestTimeoutSeconds": 300
}
```

- `Ollama:BaseUrl` — where Ollama is listening. Change this only if Ollama runs on another
  machine or a non-default port. **Restart the service after editing.**
- `Ollama:RequestTimeoutSeconds` — how long one request to Ollama may take before it's
  abandoned and retried (default `300`, allowed range 5–1800). This is a ceiling, not a
  target, but on CPU-only hardware it is not a generous one: on the reference box a
  `gemma3:4b` alert took **150–190 s** end to end (queue wait included) with a single alert
  in flight, and the default `gemma4:e2b-it-qat` about 70 s, so a slower machine or a larger model can reach 300 s. If analyses fail on
  timeout rather than on a model error, raise this before changing anything else.
  **Restart the service after editing.**

The rest of the block — `Model`, `KeepAlive`, `Temperature`, `MaximumOutputTokens`, `TopK`,
`TopP`, `MaximumRetries`, `RetryDelaySeconds` — is described in
[USER_GUIDE.md § The SETTINGS tab](USER_GUIDE.md#7-the-settings-tab) and is better
changed there. One of them is worth knowing about at deploy time: `KeepAlive` (default `1h`)
is how long Ollama holds the model in RAM between alerts. On a busy install the interval
between alerts never reaches it, so the model process is effectively never unloaded — which is
fine if you have the RAM to spare and a problem if you don't.

### Web dashboard — `SentinelCore.Web/appsettings.json`

```json
{
  "Urls": "http://0.0.0.0:3320",
  "Bridge": {
    "DataDirectory": "/absolute/path/to/SentinelCore/SentinelCore/data"
  },
  "MainAppSettingsPath": "/absolute/path/to/SentinelCore/SentinelCore/appsettings.json",
  "MainAppBaseUrl": "http://127.0.0.1:5080",
  "CamerasDataDirectory": "data",
  "FfmpegPath": "ffmpeg"
}
```

- `Bridge:DataDirectory` must point at the **same** data folder the main app uses — the Web
  project reads `events.db` and images directly, it doesn't go through an API for that.
- `MainAppSettingsPath` must be an absolute path to the main app's `appsettings.json` — the
  Settings tab reads and writes this file directly.
- `MainAppBaseUrl` is where the Web project sends trigger-zone alerts that opt into the AI
  pipeline (see [USER_GUIDE.md § Motion zones](USER_GUIDE.md#6-motion-zones-on-your-own-cameras)).
- `CamerasDataDirectory` is where `cameras.db`, `triggers.db`, and recorded clips live — can
  be relative (to the Web project's working directory) or absolute.
- `FfmpegPath` can be a bare command name (resolved via `PATH`) or an absolute path.
- `CameraDiscovery:FfprobePath` (not shown above — omit it entirely unless you need it) overrides
  where the camera discovery feature looks for `ffprobe`, the tool it uses to verify a candidate
  stream URL actually carries video. Left unset, it's derived from `FfmpegPath` (swapping `ffmpeg`
  for `ffprobe` in the same location), which is correct for the overwhelming majority of installs
  since the two ship side by side. Only set this if `ffprobe` lives somewhere `ffmpeg` doesn't.
- `FfprobePath` (top level, also not shown above) is the same override for Movies & TV, Music and
  Podcasts, which use `ffprobe` to read video, audio and tag details while scanning. Left unset
  it is derived from `FfmpegPath` the same way. It is a separate key from
  `CameraDiscovery:FfprobePath`; set both if `ffprobe` really does live apart from `ffmpeg`.

#### PTZ sections (both omitted above — code defaults are the intended setup)

Neither section appears in the shipped `appsettings.json`. They are listed here because one of them
moves your camera on its own by default, which is not something to discover from the log.

```json
  "PtzHoming": { "Enabled": true, "IdleMinutes": 3 },
  "PtzTracking": {
    "Enabled": false,
    "Track": ["person"],
    "MinimumScore": 0.5,
    "Deadzone": 0.12,
    "Speed": 0.35,
    "NudgeMilliseconds": 350,
    "PollMilliseconds": 500,
    "LostSeconds": 8,
    "RestSeconds": 30,
    "MaxTrackSeconds": 120
  }
```

- **`PtzHoming` is on by default.** A PTZ camera that has been driven somewhere returns to its home
  position after `IdleMinutes` with no PTZ command. Only cameras with trigger zones configured are
  moved, because the reason for homing is to restore detection — detection on a PTZ camera runs
  only while it is settled at home (see
  [USER_GUIDE.md § PTZ](USER_GUIDE.md#5a-pantiltzoom-ptz--moving-cameras-that-support-it)).
  `Enabled: false` leaves the camera where it was left, and its zones off until somebody presses
  Home.

- **`PtzTracking` is off by default** and stays off unless you set `Enabled: true` — it drives the
  camera to follow whatever a zone caught, then sends it home. `Track` takes COCO class names, or
  bare numeric ids for classes with no name in the table (`PtzTrackClasses`), so it isn't limited
  to what the camera's own AI supports. `Deadzone` is the number worth tuning first: the control
  loop is a frame, then inference, then a network round trip, so too tight a deadzone makes the
  camera hunt around the subject instead of settling. `MaxTrackSeconds` must stay comfortably below
  `PtzHoming:IdleMinutes` so the two never race for the same camera.

  Turning tracking on loads the local detection model into the dashboard process — the same
  ~100 MB RF-DETR the `fastlocal` backend uses, downloaded once on first use. A default install
  never pays for it.

Also do not expose port 3320 to the internet. It trusts the local network (it is where the first
admin account is created, and where `Auth:Enabled: false` turns the login off entirely); reach
the dashboard from outside through Internet Exposed Mode
([section 14b](#14b-internet-exposed-mode-optional)) instead.

## 6a. What the media, files and home features need on the server

Movies & TV, Music, Podcasts, movie downloads, the home alarm, Family Files, Weather and weather
alerts are all switched on and configured from the dashboard's Settings and their own tabs - no
hand-editing and no restart. Their settings are stored in the main app's `appsettings.json` like
everything else, under `Media`, `Music`, `Podcasts`, `MediaDownloads`, `Alarm`, `FamilyFiles`,
`WeatherAlerts` and `Location`. This section is only what the *server* has to provide for them.
How to use them is in [USER_GUIDE.md](USER_GUIDE.md); the API contracts are
[MOVIES.md](MOVIES.md), [MOVIE_DOWNLOADS.md](MOVIE_DOWNLOADS.md) and
[MUSIC_AND_PODCASTS.md](MUSIC_AND_PODCASTS.md).

**Which are on out of the box.** Movies & TV, Music, Podcasts, the Alarm and Family Files are on
by default; each tab just shows an empty "add a library / add a sensor" state until there is
something in it. Movie downloads and Weather Alerts are off until you turn them on.

**Decide where the big folders go before anyone uses them.** Apart from the media libraries
themselves, everything below lives in the dashboard's data folder (`<data>` -
`CamerasDataDirectory`, `SentinelCore.Web/data/` by default) unless you point it elsewhere, and
on a small system disk the first two rows fill it quickly. Changing
either folder later does not move what is already there.

| What | Default location | Moved by |
|---|---|---|
| Downloaded podcast episodes (capped at 50 GB by default) | `<data>/podcasts/` | Settings → PODCASTS → Download Folder, Storage Limit |
| The Family Files library | `<data>/family-files/library/` | Settings → FAMILY FILES → Library Folder (a local path, or `smb://computer/Share/folder` on another computer) |
| Movie, TV and music libraries | Wherever you point each library | The Movies tab / Music → Libraries |
| Movie HLS sessions, extracted subtitles, TMDB artwork | `<data>/media-hls/`, `media-subs/`, `media-art/` | - (caches; HLS sessions are wiped at startup) |
| Music covers, tracks converted for a device | `<data>/music-art/`, `<data>/audio-cache/` | - (caches) |
| Catalogs and per-person history | `media.db`, `media-downloads.db`, `music.db`, `podcasts.db`, `audio.db`, `alarm.db` in `<data>/` | - |

The services run as `APP_USER` (`media` by default, see section 8), so every library folder must
be readable by that account - and writable where you want a movie's **Delete** button to work or
downloads to be filed into it.

### Movies & TV

- **ffmpeg** plays anything a device can't take as it is. An MP4 the device decodes is served
  directly; anything else is remuxed to HLS on the fly, with MP3 audio converted to AAC (Apple
  players refuse MP3 inside HLS) and video re-encoded with `libx264` when the device can't decode
  it. A remux finishes in a minute or two; a re-encode runs at roughly real time and is the
  heaviest thing this feature asks of the CPU.
- **Files stamped in decode order** (the "picture wiggles back and forth" fault some downloads
  have) are found by the scan and copied through ffmpeg's `dts2pts` bitstream filter, or
  re-encoded to H.264 when this ffmpeg's `dts2pts` doesn't handle the codec. Ubuntu 26.04's
  ffmpeg 8.0 handles H.264 only; HEVC needs 8.1. `ffmpeg -h bsf=dts2pts` shows what yours
  handles - the server asks the same thing once at startup.
- Without ffmpeg the library still scans and browses, `GET /api/media/status` reports
  `ffmpegAvailable: false`, and playback of a file that needs converting answers 503.
- **TMDB API key** (optional, free from themoviedb.org): Settings → MOVIES & TV → TMDB API Key.
  Either the v3 key or the v4 read-access token works. Without it titles come from file names
  and only artwork sitting beside the files is shown.
- **Folders on other computers** (Windows file sharing, a Mac with File Sharing on, a NAS) are
  read by the server itself over SMB 2/3 - nothing is mounted, no root, the same on Linux,
  Windows and macOS. Library paths look like `smb://192.168.1.20/Movies`. The server needs
  outbound TCP 445 to that computer; finding computers on the network uses mDNS (`_smb._tcp`),
  which Windows PCs don't advertise, so type those by name or address. Sign-ins are kept in
  `<data>/media-network.json` and go into backups. A share is read-only to SentinelCore: it can't
  receive downloads and a movie on one can't be deleted from the apps.

### Music

- ffmpeg converts a track to AAC (Settings → MUSIC → Converted Quality, 256 kbps by default) only
  for a device that can't play the original. Tags and cover art are read in-process; `ffprobe` is
  only the fallback for files the tag reader can't open.
- **Find Missing Album Covers** (MusicBrainz + Cover Art Archive) and **Find Missing Lyrics**
  (LRCLIB) are on by default and send only artist/album/title details out. Turn them off on a
  server with no internet access.
- Music libraries on other computers use the same SMB sign-ins as Movies & TV.

### Podcasts

- The server fetches each followed feed itself (every 60 minutes by default) and searches Apple's
  podcast directory (`itunes.apple.com`), so it needs outbound HTTPS. A show nobody follows is
  never checked.
- **Voice boost** and **trim silence** run through ffmpeg (`highpass`, `acompressor`,
  `dynaudnorm`, `silencedetect`, `atrim` and `concat` - all in a stock build). Trim silence uses
  `atrim` + `concat` rather than `aselect` because ffmpeg 8.0's `aselect` lets every audio frame
  through, which produced "trimmed" episodes as long as the original.
- **Transcripts** (off by default) need whisper.cpp on this machine. On a Mac:
  `brew install whisper-cpp`; on Linux build it from github.com/ggerganov/whisper.cpp. Then set
  Settings → PODCASTS → Whisper Command (`whisper-cli` if it's on `PATH`, otherwise a full path),
  Whisper Model (the full path to a model from huggingface.co/ggerganov/whisper.cpp;
  `ggml-base.en.bin` is a good start) and Whisper Threads (default 4). Expect an hour-long
  episode to take 10-30 minutes on a CPU, and the rest of the server to slow down while it runs.
  Shows that publish their own transcripts need none of this.

### Weather and weather alerts

- Set the server's position under Settings → LOCATION first. The forecast comes from the US
  National Weather Service, so only locations inside the US get one; elsewhere the tab says so.
- The server talks to `api.weather.gov` (with its own identifying `User-Agent`, which the NWS
  requires - nothing to configure) and proxies the radar map from the NWS map server
  (`opengeo.ncep.noaa.gov`), the Storm Prediction Center, the Iowa Environmental Mesonet and
  Esri's basemap, so browsers and apps only ever talk to this server. Nothing is fetched unless
  someone has the tab open or a morning briefing reads the weather out.
- **Weather Alerts** (Settings → WEATHER ALERTS, off by default) has the server ask the NWS every
  5 minutes and send new watches and warnings through the same channels as the outage alerts
  (email, Telegram, push). **send test alert** checks the channels without waiting for a storm.

### The home alarm

- It hears its sensors over MQTT, so `Mqtt:Enabled` and `DeviceHub:Enabled` must both be on, and
  the sensors must be Device Hub devices - Zigbee door, window, motion, leak and smoke sensors
  published by Zigbee2MQTT to the same broker are the intended setup. Sirens are Device Hub
  switches.
- Set `Mqtt:Username`/`Mqtt:Password` once the alarm is in use (see
  [section 14](#14-firewall)): with an anonymous broker, anything on the LAN can publish to the
  topics the alarm listens on.
- The PIN, delays and zones are set on the ALARM tab; the PIN hash is in `alarm.db` and the backup.

### Family Files

- Each backup drive is either a **folder** - a USB disk or network share mounted on this machine,
  which must be mounted and writable by `APP_USER` when the nightly pass runs - or another
  computer over **SFTP**. Add as many as you like on the FILES tab.
- The nightly backup and duplicate check run at 04:00 by default (Settings → FAMILY FILES →
  Hour/Minute, in the Schedules time zone); deleted files and retired duplicates wait 30 days in
  `<data>/family-files/trash/`. The largest single upload is 4096 MB by default (Largest File).
- The library is **not** in the backup file (it can be far bigger than everything else); the
  drives are its backup, and **bring files back** on the FILES tab rebuilds it from any one of
  them.

### Kiosk mode

A per-device browser setting, nothing on the server: add `?kiosk=1` to a wall display's start URL
(or tick the kiosk switch in its Settings) and that browser hides hint tooltips, shows hover-only
buttons and enlarges tap targets. `?kiosk=0` turns it off.

## 7. Running interactively (no systemd)

Useful for testing a change before installing it as a service:

```bash
dotnet run --project SentinelCore/SentinelCore.csproj
```

```bash
dotnet run --project SentinelCore.Web/SentinelCore.Web.csproj
```

Run each in its own terminal. Stop with Ctrl+C.

## 8. Installing as systemd services

**The short version:** run the installer and skip the rest of this section.

```bash
./scripts/install-linux.sh
```

It installs what's missing (.NET, ffmpeg, Node.js, Ollama), sets Ollama up for this app (pulls
`gemma4:e2b-it-qat` and turns off its memory-hungry prompt cache - see
[section 2](#turn-off-ollamas-prompt-cache-the-installers-do-this-for-you)), builds, runs the
tests, installs and starts all three services (alert pipeline, dashboard, and — if Node.js is
present — the HomeKit sidecar), then prints a health check.
Everything runs
in place from this checkout, so updating later is just `git pull && ./scripts/deploy.sh`.

Override the defaults if your setup differs:

```bash
APP_USER=someone INSTALL_DIR=/srv/sentinel WEB_PORT=8080 ./scripts/install-linux.sh
```

The installer deliberately leaves two things to you, because both depend on your network:
the firewall rules ([section 14](#14-firewall)) and passwordless restarts
([section 10](#10-passwordless-restarts-optional)). It prints reminders for both when it finishes.

---

The rest of this section is what the installer generates, for reference or if you'd rather do it
by hand. The templates live in `systemd/` with `__PLACEHOLDER__` values the installer fills in;
what follows is one worked example with those substituted for this machine (`media` /
`/opt/sentinelcore/core`).

**`/etc/systemd/system/sentinelcore.service`:**

```ini
[Unit]
Description=SentinelCore AI Vision Engine
Wants=network-online.target
After=network-online.target ollama.service

[Service]
Type=simple
User=media
Group=media
WorkingDirectory=/opt/sentinelcore/core/SentinelCore
ExecStart=/usr/bin/dotnet /opt/sentinelcore/core/SentinelCore/bin/Release/net10.0/SentinelCore.dll

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5080

Restart=always
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=sentinelcore

[Install]
WantedBy=multi-user.target
```

**`/etc/systemd/system/sentinelcore-web.service`:**

```ini
[Unit]
Description=SentinelCore Web Dashboard
Wants=network-online.target
After=network-online.target sentinelcore.service

[Service]
Type=simple
User=media
Group=media
WorkingDirectory=/opt/sentinelcore/core/SentinelCore.Web
ExecStart=/usr/bin/dotnet /opt/sentinelcore/core/SentinelCore.Web/bin/Release/net10.0/SentinelCore.Web.dll

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:3320

Restart=always
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=sentinelcore-web

[Install]
WantedBy=multi-user.target
```

Install and start them (the third line only if you're adding the HomeKit sidecar):

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now sentinelcore
sudo systemctl enable --now sentinelcore-web
sudo systemctl enable --now sentinelcore-homekit
```

`Restart=always` means systemd brings a crashed service back up on its own after 5 seconds —
useful, but it also means a `pkill` by process name/command-line pattern can hit the real
service instead of a test instance and get silently restarted, masking the mistake. When
testing manually alongside a live install, always track the exact PID you started and kill
only that PID.

### 8a. The HomeKit sidecar

Installed automatically by `install-linux.sh` whenever Node.js (v22 or newer) is present — you
don't need to do anything here unless Node was missing at install time.

It's a small Node.js service rather than .NET. HomeKit speaks HAP (its own pairing crypto and
mDNS advertisement), and `@homebridge/hap-nodejs` — the library Homebridge itself runs on — is
the only maintained implementation. It's ~200 lines of glue around that library.

**Running it costs nothing if you don't use HomeKit.** The sidecar watches `HomeKit:Enabled` in
the main app's config and stays completely off the network until it's switched on in the
dashboard — no accessory is advertised, no port is opened. Flipping the toggle takes effect
within about ten seconds, no restart needed, and flipping it back off unpublishes the bridge
while keeping existing pairings intact.

If Node wasn't installed when you ran the installer, add it and re-run:

```bash
curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash - && sudo apt-get install -y nodejs
```

```bash
./scripts/install-linux.sh
```

Two ports are involved, and they're deliberately different in kind:

- **3321** — the sidecar's control API, which the main app pings when an alert fires. Bound to
  `127.0.0.1` only. Never open this in the firewall; nothing outside this host should reach it.
- **51826** — the HomeKit accessory port. This one *must* be reachable from your LAN, because
  iPhones connect to it directly (mDNS only advertises where to find it). See
  [section 14](#14-firewall).

With **live video in home** turned on, video doesn't use either port: the sidecar's ffmpeg sends
encrypted RTP (SRTP) over UDP straight to the viewing iPhone or home hub, which sends receiver
reports back to a random high UDP port. A firewall that allows outbound UDP and established
replies is enough. The sidecar reaches the dashboard's camera endpoints at
`http://127.0.0.1:3320` (override with `HOMEKIT_WEB_APP_URL`) and needs `ffmpeg` on the PATH
(override with `HOMEKIT_FFMPEG_PATH`).

mDNS advertisement goes through the system `avahi-daemon` over D-Bus rather than the sidecar
binding UDP 5353 itself, which avoids fighting avahi for the port. That's why the unit file has
`After=avahi-daemon.service`. If avahi isn't running on your system, change `MDNSAdvertiser.AVAHI`
to `MDNSAdvertiser.CIAO` in `index.js` to use the sidecar's own built-in responder instead.

Pairing state and this bridge's stable identity (its virtual MAC and pairing code) live in
`core/homekit-bridge/persist/`, which is gitignored. **Don't delete it** — doing so unpairs
every iPhone and generates a new pairing code.

### 8b. Windows

Worth considering over Linux for one reason: **Blue Iris is Windows-only**, so running Sentinel
Core on the same machine means you don't need a second box at all.

From an **elevated** PowerShell (right-click → Run as Administrator):

```powershell
.\scripts\install-windows.ps1
```

Same job as the Linux installer — detects and offers to install missing dependencies (via
`winget`), builds, runs the tests, registers the services, starts them, health-checks. Overrides
work the same way:

```powershell
.\scripts\install-windows.ps1 -WebPort 8080 -AssumeYes
```

What differs from Linux:

| | Linux | Windows |
|---|---|---|
| Service manager | systemd units | Windows Services |
| Service names | `sentinelcore`, `-web`, `-homekit` | `SentinelCoreBridge`, `SentinelCoreWeb`, `SentinelCoreHomeKit` |
| Dependencies | apt / NodeSource / Ollama script | winget |
| HomeKit mDNS | avahi over D-Bus | hap-nodejs's own responder (`ciao`) |
| Logs | `journalctl -u <name>` | Event Viewer, or the console when run by hand |
| Restart a service | `sudo systemctl restart <name>` | `Restart-Service <name>` |

The same binaries run on both — the .NET hosts call `UseWindowsService()` and `UseSystemd()`,
each a no-op when the process wasn't started by that service manager, so there's no separate
build and no `#if WINDOWS` anywhere.

Firewall rules are the one manual step, same as on Linux; the installer prints the exact
`New-NetFirewallRule` commands when it finishes.

> **Status:** the Windows installer is newer and less battle-tested than the Linux one. The
> application code itself is the same on both platforms and is well covered, but if you hit a
> problem with service registration specifically, that's the least-exercised part.

### 8c. macOS

Uses launchd `LaunchDaemon`s instead of systemd units - the macOS equivalent, giving the same
"starts at boot, restarts if it crashes, no login session required" behavior.

```bash
sudo ./scripts/install-macos.sh
```

Must be run with `sudo` - `LaunchDaemons` live under `/Library/LaunchDaemons`, which only root
can write to. Same job as the Linux installer — detects and offers to install missing
dependencies (via [Homebrew](https://brew.sh)), builds, runs the tests, installs and starts the
services, health-checks. Overrides work the same way:

```bash
sudo APP_USER=morgan WEB_PORT=8080 ASSUME_YES=1 ./scripts/install-macos.sh
```

What differs from Linux:

| | Linux | macOS |
|---|---|---|
| Service manager | systemd units | launchd `LaunchDaemon`s |
| Service names | `sentinelcore`, `-web`, `-homekit` | `com.sentinelcore.bridge`, `.web`, `.homekit` |
| Dependencies | apt / NodeSource / Ollama script | Homebrew |
| HomeKit mDNS | avahi over D-Bus | hap-nodejs's own responder (`ciao`) |
| Logs | `journalctl -u <name>` | `logs/com.sentinelcore.<name>.log` under the install dir |
| Restart a service | `sudo systemctl restart <name>` | `sudo launchctl kickstart -k system/com.sentinelcore.<name>` |
| Status | `systemctl status <name>` | `sudo launchctl print system/com.sentinelcore.<name>` |

`scripts/deploy.sh` already knows about both - it detects macOS via `uname` and uses `launchctl`
instead of `systemctl` automatically, so `git pull && ./scripts/deploy.sh` works unchanged on
either platform.

### 8d. Movie downloads (Linux, optional)

Only needed for "+ Add" on the Movies and Music tabs (Settings → MOVIE DOWNLOADS, off by default).
Prowlarr (searches the torrent sites) and qBittorrent (downloads) run as systemd units inside a
network namespace, `scvpn`, whose only route out is a VPN tunnel - if the VPN drops they have no
route at all rather than falling back to the home connection. No Docker. The design is in
[MOVIE_DOWNLOADS.md](MOVIE_DOWNLOADS.md); this is the install.

It needs a systemd Linux with `apt` (Debian/Ubuntu), `sudo` once, and usually your VPN provider's
WireGuard `.conf` or OpenVPN `.ovpn` file (the "manual" or "router" setup file from its website).
From the checkout:

```bash
sudo ./downloads/install-downloads.sh --vpn-config ~/Downloads/mullvad-se-sto.conf
sudo ./downloads/install-downloads.sh --vpn-config us1234.nordvpn.com.udp.ovpn --vpn-user SERVICE_USER --vpn-pass SERVICE_PASS
sudo ./downloads/install-downloads.sh        # no file: choose the VPN in Settings afterwards
```

`--vpn-user`/`--vpn-pass` are an OpenVPN provider's *service* credentials, not your website
login. `--user NAME` names the account SentinelCore runs as (default: whoever ran `sudo`, which
must be the same `APP_USER` the services run as). The script is not in the prebuilt release
.zip - it lives in `core/downloads/` of the repository.

What it installs:

| | Where |
|---|---|
| Packages | `wireguard-tools openvpn qbittorrent-nox curl sqlite3 iproute2 xvfb` and the current `libicu` |
| Prowlarr (updates itself, so owned by `APP_USER`) | `/opt/sentinelcore-prowlarr` |
| FlareSolverr (gets past Cloudflare checks; x86_64 only - elsewhere it's skipped and sites behind Cloudflare fail to add) | `/opt/sentinelcore-flaresolverr` |
| qBittorrent and Prowlarr settings, Prowlarr's site list, the VPN choice from Settings | `/var/lib/sentinelcore-downloads/` |
| The VPN file given with `--vpn-config` (root only) | `/etc/sentinelcore/vpn/` |
| The tunnel helper | `/usr/local/lib/sentinelcore-downloads/sentinelcore-vpn` |
| Units | `sentinelcore-downloads.target` plus `sentinelcore-vpn`, `-vpn-watch`, `-qbittorrent`, `-prowlarr`, `-flaresolverr` |
| Permission for `APP_USER` to start/stop/restart exactly those six units, nothing else | `/etc/polkit-1/rules.d/50-sentinelcore-downloads.rules` |

With a VPN file it then **tests itself**: starts everything as `APP_USER` (which also proves the
polkit rule works), waits for the tunnel, checks qBittorrent, Prowlarr and FlareSolverr answer,
prints your home address next to the address seen through the VPN, fails if they're the same,
and stops everything again. `--skip-test` skips that; it's skipped anyway when no VPN file was
given.

After that nothing runs until it's needed. The target is never started at boot: SentinelCore
starts it when someone searches, downloads or manages sites, and stops it once nothing has needed
it for Settings → MOVIE DOWNLOADS → Switch Off After (5 minutes by default). Downloads left
unfinished by a restart resume by themselves.

**Choosing the VPN.** Settings → MOVIE DOWNLOADS → VPN Provider: the file installed above, a
**Mullvad account number** (the server makes its own WireGuard key, registers it as one of the
account's devices via `api.mullvad.net`, and picks a server in the chosen location), or **another
WireGuard provider's** five values. OpenVPN providers only work as the installed file. With
Mullvad, the owner is warned through the usual alert channels a week before the account runs out
and again when it has. To swap the installed file later:
`sudo /usr/local/lib/sentinelcore-downloads/sentinelcore-vpn set-config NEWFILE`
(add `--user U --pass P` for OpenVPN).

**Use VPN For Movie Downloads** is on by default. Off, the same services use the home connection
directly and every app shows an amber warning while it stays off.

Things that bite on a real network:

- **Ubuntu's AppArmor** only lets `wg` and `wg-quick` read `/etc/wireguard`, even as root. The
  helper strips the WireGuard file itself and hands `wg setconf` a short-lived copy under
  `/etc/wireguard/` for that reason - leave that directory alone.
- **MTU.** The tunnel measures the path to the VPN server when it starts and sizes itself to fit,
  unless the WireGuard file sets `MTU`. Connections that carry less than the usual 1500 bytes
  (T-Mobile Home Internet carries about 1440) otherwise pass the handshake and DNS and then hang
  every TLS connection. If you set `MTU` in the file yourself, you've turned that off.
- **Firewall.** Nothing to open. Prowlarr (`10.250.0.2:9696`), qBittorrent (`:8080`) and
  FlareSolverr (`:8191`) are only reachable from this host, over the namespace's private link.

Downloads are filed only into libraries on this server's own disks (not `smb://` shares), and the
files are written as `APP_USER`.

Checking on it:

```bash
cat /run/sentinelcore-vpn/status.json      # what the 45-second tunnel check last saw
journalctl -u sentinelcore-vpn -u sentinelcore-vpn-watch -n 50 --no-pager
journalctl -u sentinelcore-qbittorrent -u sentinelcore-prowlarr -u sentinelcore-flaresolverr -n 50 --no-pager
```

SentinelCore refuses to search or download unless that status says connected **and** its address
differs from the server's own. `sudo ./downloads/install-downloads.sh --uninstall` removes the
units, the polkit rule and the namespace, and keeps `/var/lib/sentinelcore-downloads`, the two
`/opt` folders and `/etc/sentinelcore/vpn`.

On Windows, macOS, or with a Prowlarr and qBittorrent you already run behind a VPN: turn **Start
And Stop The Services** off and point the two addresses at yours. They must run on this same
machine - finished files are moved, not copied over the network.

## 9. The deploy script

`scripts/deploy.sh` automates build → test → restart for whichever service(s) changed:

```bash
./scripts/deploy.sh          # build + restart everything installed
./scripts/deploy.sh main     # build + restart only sentinelcore
./scripts/deploy.sh web      # build + restart only sentinelcore-web
./scripts/deploy.sh homekit  # restart only the HomeKit sidecar (Node, nothing to build)
./scripts/deploy.sh relay    # build + restart only sentinelcore-push-relay (the vendor's push host)
```

With no argument it restarts `sentinelcore` and `sentinelcore-web`, and the HomeKit sidecar and
push relay too wherever their units are installed. Services that aren't installed are skipped,
so `./deploy.sh` works unchanged whether or not you use HomeKit or run the relay.

Its closing health check runs a couple of seconds after the restart and can report the dashboard
as unreachable while it's still starting; check again a few seconds later before worrying.

It runs (in order): a check that it will be able to restart the services at all, `dotnet
build-server shutdown` (safety, see section 4), `dotnet build -c Release`, `dotnet test` (set
`SKIP_TESTS=1` to skip — not recommended), then the systemd restart(s), then a status +
health-check summary. If the build or tests fail, it stops before touching the running services.

A failed build is tried up to three times. The .NET SDK on the reference box sometimes dies with
`Internal CLR error (0x80131506)` before compiling anything, so a retry is safe; the second and
third attempts shut the build servers down again and build with `--disable-build-servers -m:1`,
which is what always got past it by hand. Three failures in a row is a real build error: the
script prints `ERROR: the build failed three times. Nothing was restarted.` and exits.

It needs `sudo` for the `systemctl restart` calls — either enter your password each time it
prompts, or see the next section to make that passwordless.

The restart check comes **first**, before the build, and the script exits without building if the
restart would need a password it can't ask for (a cron job, an agent, any non-interactive shell).
That ordering is deliberate. A deploy that builds and then fails at the restart isn't a no-op: it
leaves new binaries in `bin/Release` while the old processes keep serving, which looks just like a
successful deploy from the outside. On this project it goes one step further — `wwwroot` is served
from the source tree, not from `bin/Release`, so the dashboard picks up new JavaScript the moment
it's written, and a half-finished deploy leaves the browser calling API routes the running server
doesn't have yet. Both of those are quiet; the failure message isn't.

Set `SKIP_RESTART=1` to build and test without restarting — useful for checking a change compiles
against the live tree. It skips the check, prints the restart command to finish with, and warns
that the frontend is already live against the old backend.

## 10. Passwordless restarts (optional)

Only needed if you want `scripts/deploy.sh` (or an automated agent) to restart the services
without a password prompt each time. This grants `sudo` for **exactly** two commands, nothing
broader:

```bash
sudo visudo -f /etc/sudoers.d/blueiris-deploy
```

Add (replace `media` with your actual deploy user if different):

```
media ALL=(root) NOPASSWD: /usr/bin/systemctl restart sentinelcore
media ALL=(root) NOPASSWD: /usr/bin/systemctl restart sentinelcore-web
media ALL=(root) NOPASSWD: /usr/bin/systemctl restart sentinelcore-homekit
```

(The third line is only needed if you installed the optional HomeKit sidecar — see section 8a.)

`visudo` validates the syntax before saving, so a typo can't lock out `sudo` entirely. Verify
it took effect:

```bash
sudo -l -U media | grep -A2 systemctl
```

Note that `deploy.sh` restarts each service with its own separate `systemctl restart <name>`
call rather than one call naming both — that's intentional, so it matches these two exact
allowlisted command lines regardless of whether you run `./deploy.sh`, `./deploy.sh main`, or
`./deploy.sh web`.

## 10a. Rebooting the machine from the apps (optional)

Off by default. Section 10 above grants restarts of the *services*; this grants a restart of the
*machine*, which is the one recovery step that previously still needed SSH — a wedged network
stack, a kernel or driver update, an Ollama that a service restart alone won't clear.

Two things have to be true before the button works, and they are deliberately separate:

1. **Host Reboot** is switched on under Settings → REBOOT SERVER (dashboard, iOS/macOS, or
   Android). This is a stored setting and takes effect immediately, no restart needed.
2. The operating system has been told to allow it. That cannot be done from any app — it is one
   command run once on the server itself, and it differs per platform.

You do not have to work out which command yourself. Switch the setting on, open the REBOOT SERVER
section, and the panel underneath reports what this specific machine needs — the right commands
for its operating system, with its actual service account and paths already filled in — plus a
**Check Again** button that verifies the grant landed **without rebooting to find out**. What
follows is the same thing, for reference.

There is deliberately no shut-down option, and there should not be: a machine that is off cannot
be turned back on by software, so a mis-tap would end in a trip to wherever the box lives. A
reboot's worst case is the one any crash already has.

### Linux

The services run as `APP_USER` (`media` by default), not root, so `shutdown` needs an explicit
grant:

```bash
sudo mkdir -p /etc/sudoers.d
printf '%s\n' 'media ALL=(root) NOPASSWD: /usr/sbin/shutdown -r now' | sudo tee /etc/sudoers.d/sentinelcore-reboot >/dev/null
sudo chmod 440 /etc/sudoers.d/sentinelcore-reboot
sudo visudo -c -f /etc/sudoers.d/sentinelcore-reboot
```

Replace `media` with your actual `APP_USER`, and check `/usr/sbin/shutdown` is where your distro
keeps it (`command -v shutdown`) — the grant matches the exact path and arguments, which is what
keeps it narrow. Verify without rebooting:

```bash
sudo -n -l /usr/sbin/shutdown -r now
```

A polkit rule granting `org.freedesktop.login1.reboot` is the more native answer on systemd and
grants less. It is not the default advice here for one reason: polkit checks
`org.freedesktop.login1.reboot-multiple-sessions` instead whenever anyone else is logged in, so a
rule that only grants the first action works on the day you write it and mysteriously stops
working months later. Grant both if you go that route.

To undo either: delete `/etc/sudoers.d/sentinelcore-reboot`.

### macOS

The LaunchDaemons in section 8c set `<key>UserName</key>` to the install user rather than running
as root, so macOS needs the same kind of grant. `/etc` is a symlink to `/private/etc` and the
stock `sudoers` already carries `#includedir /private/etc/sudoers.d`, so a file dropped here is
picked up with no further edit:

```bash
sudo mkdir -p /etc/sudoers.d
printf '%s\n' 'media ALL=(root) NOPASSWD: /sbin/shutdown -r now' | sudo tee /etc/sudoers.d/sentinelcore-reboot >/dev/null
sudo chmod 440 /etc/sudoers.d/sentinelcore-reboot
sudo visudo -c -f /etc/sudoers.d/sentinelcore-reboot
```

On newer macOS releases Terminal may need Full Disk Access before it can write to
`/etc/sudoers.d`.

### Windows

Usually nothing to do. `scripts/install-windows.ps1` registers the services with `New-Service` and
no `-Credential`, so they run as LocalSystem, which already holds `SeShutdownPrivilege`. The panel
will report **Ready** straight away.

If you changed the services to run under a named account, either put them back:

```powershell
sc.exe config SentinelCoreBridge obj= LocalSystem
sc.exe stop SentinelCoreBridge
sc.exe start SentinelCoreBridge
```

(the space after `obj=` is required — `sc.exe` treats it as part of the syntax), or grant that
account the **Shut down the system** right under Local Security Policy (`secpol.msc`) → Local
Policies → User Rights Assignment, then restart the service.

### Containers

Not supported, and reported as such rather than offered and then failing. A container cannot
reboot the host it runs on; do that from the host, or from whatever manages the container.

### What a reboot actually costs

If your recorder is a separate machine — Blue Iris on Windows, in the usual setup — it keeps
recording throughout. What stops for a minute or two is AI analysis, the dashboard, HomeKit, and
notifications. The services come back on their own: systemd units are `enabled`, LaunchDaemons are
loaded at boot, and Windows services are set to Automatic.

Every reboot is scheduled with an undo window (60 seconds by default, configurable, 0 to reboot
immediately). The countdown is held in the app's own process rather than handed to
`shutdown -r +1`, so cancelling is reliable and needs no privilege of its own — and if the process
dies inside the window, the reboot simply doesn't happen, which is the right way for this to fail.

## 11. Updating to a new version

This section is for a **git checkout** install. If you installed from a prebuilt release .zip
instead, there's no checkout to pull - see
[§ 17, Upgrading an existing install](#upgrading-an-existing-install).

```bash
cd /opt/sentinelcore/core
git pull
./scripts/deploy.sh
```

Or manually, if you're not using the script:

```bash
git pull
dotnet build-server shutdown
dotnet build -c Release
dotnet test
sudo systemctl restart sentinelcore
sudo systemctl restart sentinelcore-web
```

If you installed movie downloads ([section 8d](#8d-movie-downloads-linux-optional)) and the
update touched `core/downloads/` (`git diff ORIG_HEAD --stat -- downloads` right after the
pull shows whether it did), re-run `sudo ./downloads/install-downloads.sh` too. `deploy.sh` never touches
that side: the tunnel helper, units and polkit rule are copies under `/usr/local/lib`,
`/etc/systemd/system` and `/etc/polkit-1`, so a pull alone leaves the old ones running. A re-run
keeps your VPN choice, sites and qBittorrent settings.

Configuration changes to `appsettings.json` (most of them — see the note in
[USER_GUIDE.md § Settings](USER_GUIDE.md#7-the-settings-tab) about the handful of fields that
need a restart) take effect live without any of the above; a restart is only needed after
pulling new *code*.

## 12. Verifying health

```bash
systemctl status sentinelcore sentinelcore-web --no-pager
curl http://127.0.0.1:5080/health/live
curl http://127.0.0.1:5080/health/ready   # also checks Ollama is reachable
curl http://127.0.0.1:3320/api/health
journalctl -u sentinelcore -f       # live tail
journalctl -u sentinelcore-web -f
```

## 13. Blue Iris-side configuration

On the Blue Iris (Windows) machine, copy `scripts/Send-BlueIrisAlert.ps1` somewhere like
`C:\BlueIrisScripts\Send-BlueIrisAlert.ps1`, then create a Blue Iris alert action that runs
`powershell.exe` with parameters along these lines (use the actual macros from your Blue Iris
version's macro picker):

```text
-NoProfile -ExecutionPolicy Bypass -File "C:\BlueIrisScripts\Send-BlueIrisAlert.ps1" -ImagePath "&ALERT_PATH" -Camera "&CAM" -AlertId "&ALERT_DB" -Profile "default" -ApiKey "YOUR-LONG-KEY"
```

The only hard requirement is that `-ImagePath` resolves to an existing JPEG/PNG/WebP file when
the action runs. Use `-Profile "gate"`, `-Profile "front-door"`, or any other profile name
from `PromptProfiles` per camera — an unrecognized or blank profile name silently falls back
to `default`.

## 14. Firewall

Restrict both ports to your LAN subnet — neither is designed for internet exposure:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 5080 proto tcp
sudo ufw allow from 192.168.1.0/24 to any port 3320 proto tcp
```

(Adjust the subnet to match your actual network.)

If you turn on the built-in MQTT broker (`Mqtt:RunEmbeddedBroker`), also open its port (1883
by default) to your LAN so Home Assistant or other MQTT clients elsewhere on the network can
reach it:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 1883 proto tcp
```

Not needed if you're pointing this app at a broker that already exists elsewhere (e.g. Home
Assistant's own Mosquitto add-on) - only the outbound connection from this app to that broker
matters then, and outbound connections aren't affected by `ufw`'s default incoming-only rules.

**Set broker credentials if you use the Device Hub.** The built-in broker binds to every
interface, and with `Mqtt:Username` blank it accepts anonymous connections from anywhere the
firewall rule above allows - which is fine when the only thing on it is camera event topics,
but the Device Hub (USER_GUIDE section 13a) puts light and lock control on that same broker.
Set `Mqtt:Username` and `Mqtt:Password` and the built-in broker will require them; leave them
blank and it stays open exactly as before, so existing Home Assistant setups keep working
untouched. For an external broker reached over an untrusted network, set `Mqtt:UseTls` as well -
this app's outbound connections then use TLS.

If you installed go2rtc for the real-time live view (USER_GUIDE section 4a), open its media
port as well - **both TCP and UDP**, because WebRTC will use either:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 18555
```

This is the one go2rtc port that has to be reachable from a viewer. Its other two - the API on
11984 and RTSP on 18554 - deliberately bind to loopback only, so no rule can help or hurt them;
the dashboard reaches them from the same machine and browsers never touch them at all.

Leaving 18555 closed does not break the live view, which is exactly what makes it easy to miss:
tiles that can't establish WebRTC quietly settle on the MSE rung, which rides the dashboard's
own HTTPS connection and needs no rule of its own. You lose the top rung of the ladder without
being told, so the symptom is a live view that is merely good rather than one that is broken.
The tile shows which transport it settled on if you want to confirm the rule worked.

If you installed the HomeKit sidecar (section 8a), open its accessory port so iPhones on your
LAN can reach it — they connect to it directly, mDNS only tells them where to look:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 51826 proto tcp
```

Do **not** open 3321. That's the sidecar's control API, bound to `127.0.0.1` and meant only for
the main app on this same host.

Camera discovery (Test Connection / Discover Cameras on the CAMERAS tab) needs no firewall change
at all — it's outbound only, from this host to a camera, so `ufw`'s default incoming-only rules
don't affect it. Nothing listens for a reply on an open port; it only runs when you click one of
those two buttons, never in the background.

The media, files and home features ([section 6a](#6a-what-the-media-files-and-home-features-need-on-the-server))
need **no inbound rules either** - every app reaches them over the dashboard's own ports. What
they need is outbound access, worth knowing on a network that filters it:

| Feature | Connects out to |
|---|---|
| Movies & TV | `api.themoviedb.org`, `image.tmdb.org` (only with a TMDB key); TCP 445 to computers whose shares are libraries |
| Music | `musicbrainz.org`, `coverartarchive.org`, `lrclib.net` (each switchable off); `api.listenbrainz.org` for anyone who connects ListenBrainz |
| Podcasts | Each followed show's feed and media host; `itunes.apple.com` for search and charts |
| Weather | `api.weather.gov`, `radar.weather.gov`, `opengeo.ncep.noaa.gov`, `www.spc.noaa.gov`, `mesonet.agron.iastate.edu`, `server.arcgisonline.com` |
| Movie downloads | `api.ipify.org` (the home-address check) and, with Mullvad, `api.mullvad.net`; everything else goes through the VPN tunnel |
| Family Files, Nightly Auto Export | The SFTP servers you configure (TCP 22 unless you set another port) |

The movie-download services' own ports are covered in
[section 8d](#8d-movie-downloads-linux-optional): don't open them.

## 14a. HTTPS (on by default, no setup required)

**Nothing to do here.** The dashboard generates its own certificate authority and server
certificate the first time it starts, covering this machine's hostname, its `.local` name, and
every LAN IP it currently has, and serves HTTPS on port 3443 alongside the existing plain-HTTP
listener — see `AutoCertificateProvisioner` in `SentinelCore.Web` if you want to see how. There's
no script to run and no `appsettings.json` field to hand-edit; this replaced an earlier version
of this product that required both. `Https:Enabled: false` in `SentinelCore.Web/appsettings.json`
turns it off if you'd rather not run HTTPS at all.

**The awkward part, stated plainly:** no public certificate authority will issue a certificate
for a private address like `192.168.1.50`. Let's Encrypt cannot help, which is why this uses a
*local* authority instead — one generated on and unique to your own machine.

> **Why not skip the authority and just self-sign the server certificate directly?** Because
> that's actively worse, not better: browsers show a full-page "your connection is not private"
> warning on every visit — far more alarming than the small "Not Secure" label plain HTTP gets.
> A certificate signed by an authority your devices are told to trust shows an ordinary padlock
> instead - which is what having a local CA sign it, rather than self-signing it, buys you.

### Trusting the authority on your devices

The certificate works immediately for any HTTP client that doesn't check who signed it (Blue
Iris's calls into this app, curl with `-k`, etc.), but a browser will still show a warning until
you install this install's authority certificate on each device you browse from:

```
core/certs/ca.crt   (source install)
certs/ca.crt         (a prebuilt release - see section 17; top-level, alongside SentinelCore/
                      and SentinelCore.Web/, not nested inside either one)
```

The app prints the exact path in its startup log the first time it generates one. You don't
have to copy the file off the server: the dashboard serves it at
`http://<server>:3320/api/certificate-authority` (or the same path on the `https://` address),
which is where the webcam publish page sends a device that needs it. Per platform:

- **Linux:** `sudo cp ca.crt /usr/local/share/ca-certificates/sentinel-core.crt && sudo update-ca-certificates`
  for system tools; Chrome and Firefox keep their own store, so also import it in the browser
  under Settings → Privacy and security → Security → Manage certificates → Authorities.
- **macOS:** double-click it, then in Keychain Access set it to "Always Trust"
- **Windows:** double-click → Install Certificate → Local Machine → place in "Trusted Root
  Certification Authorities"
- **iOS:** email or AirDrop it to yourself, open it, install the profile, THEN go to
  **Settings → General → About → Certificate Trust Settings** and switch it on. That last step
  is easy to miss and nothing works without it.
- **Android:** Settings → Security → Encryption & credentials → Install a certificate → CA

**Plain HTTP keeps listening on 3320.** That's deliberate — both mobile apps default to `http://`
when you type a bare address, so forcing everything to HTTPS would break them on any device that
hadn't yet trusted the authority. Bookmark the `https://` address once you've trusted it;
nothing else changes.

**The Apple apps fall back to plain HTTP on their own.** When an iPhone/iPad/Mac/Apple TV app is
pointed at the `https://` address (which is what local-network discovery advertises) but the
device hasn't trusted the authority yet, the app automatically retries on the plain-HTTP port
above and says so in its Settings screen — so it works out of the box, and switches to HTTPS by
itself once you trust `ca.crt` on that device.

The fallback only ever goes to *this* server. The first time it works, the app downloads and
remembers the server's certificate authority (that's normally at home, during setup); after that
it only drops to plain HTTP for a server that completes a TLS handshake with a certificate that
authority signed. Anything else answering at the same address — on someone else's Wi-Fi, where
`192.168.1.50` can be any device — is refused before a single request is sent, so it never gets the
session cookie or the saved password, and the app says *"Something at this address isn't your
Sentinel Core server"*. The saved password is also never sent automatically over the fallback
until the authority is remembered; the app shows its sign-in screen instead. The server
reissuing its own certificate after an IP change is fine (the authority stays the same); a
**reinstalled** server has a new authority, so re-save its address in the app's Settings once to
make the app forget the old one.

Open the firewall for the HTTPS port the same way as the others:

```bash
sudo ufw allow from 192.168.1.0/24 to any port 3443 proto tcp
```

**Renewal.** The certificate is capped at 825 days because Apple rejects anything longer, even
from an authority generated on your own machine. It's checked and reissued automatically (from
the existing authority, so already-trusted devices stay trusted) whenever the app starts and the
current one is within 30 days of expiring **or no longer covers this machine's current names and
addresses** — a reboot or DHCP renewal that hands the server a different LAN IP used to leave the
old certificate mismatched until it neared expiry; now it's reissued on the next start. In
practice this means it fixes itself across whatever restart happens next, the same as picking up
any other code update. The authority itself lasts ten years, so this never requires touching a
phone again.

### Route B — a real domain (no per-device setup)

If per-device trust is too much friction — likely if other people use the dashboard — buy a cheap
domain and get a genuinely trusted certificate via a **DNS-01** challenge, which works for a host
that isn't reachable from the internet. Point `sentinel.yourdomain.com` at the LAN IP using your
router's DNS, and every device just works with no installation at all. Caddy or certbot with a
DNS plugin will automate issuance and renewal, then point the dashboard at the resulting files
explicitly:

```json
"Https": {
  "Enabled": true,
  "CertificatePath": "/full/path/to/fullchain.pem",
  "KeyPath": "/full/path/to/privkey.pem",
  "Port": 3443
}
```

Setting `CertificatePath`/`KeyPath` explicitly like this is the one case where auto-provisioning
steps aside - it never overwrites a certificate you've configured yourself.

### A note if you're distributing this

The authority is generated per install, in-process, and deliberately **not** bundled with the
product. Shipping one would mean every install shared a single private key, so anyone with a
copy could issue certificates that every other customer's devices would trust. That's why HTTP
remains available at all rather than this being the only option — the same choice Home
Assistant, Unifi and Proxmox all make.

## 14b. Internet Exposed Mode (optional)

For reaching the dashboard from outside your home network - e.g. through a
[Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/).
This adds a **second, dedicated port** that always requires login and only speaks HTTPS,
regardless of how Login and HTTPS above are configured. The normal LAN port is completely
unaffected - both mobile apps' existing "local address" / "remote address" split already
prefers the LAN address when it's reachable and falls back to whatever remote address you give
it, so point the remote address at your tunnel's hostname and nothing else changes there.

**Prerequisites**, both enforced by the settings validation:

- Local HTTPS must already be configured (Route A or B in §14a above) - this mode reuses that
  certificate rather than needing its own.
- At least one account must already exist (Settings → LOGIN → require login) - otherwise the new
  port would require a login nobody could complete.

Turn it on under Settings → INTERNET EXPOSED MODE (advanced mode), or edit
`SentinelCore.Web/appsettings.json` directly:

```json
"InternetExposed": {
  "Enabled": true,
  "Port": 3444
}
```

Port must differ from the HTTPS port (3443 by default). Restart the web dashboard for it to
take effect: `sudo systemctl restart sentinelcore-web`.

Then point your Cloudflare Tunnel's ingress rule at this port over HTTPS, skipping certificate
verification (the certificate is signed by your own local authority from §14a, not a public one
cloudflared would recognize):

```yaml
ingress:
  - hostname: sentinel.yourdomain.com
    service: https://localhost:3444
    originRequest:
      noTLSVerify: true
  - service: http_status:404
```

> **Point the tunnel at this port, not 3320.** The forced-login behaviour is tied to the port
> the request arrives on. A tunnel pointed at the ordinary dashboard port (3320) reaches a
> listener that still trusts the local network, so if the login has ever been turned off
> (`Auth:Enabled: false`) the entire API would be reachable from the internet with no login at
> all - and on an install whose admin account hasn't been created yet, a stranger could create
> it. The same
> applies to the "Remote address" field in the iOS, Android, and Apple TV apps: it should be the
> hostname of a tunnel that terminates on the Internet Exposed port.

Open the firewall for this port the same way as the others - but **only to `127.0.0.1`**, since
only `cloudflared` running on this same machine should ever connect to it directly; nothing on
your LAN needs to reach this port, and the tunnel is what actually exposes it to the internet.

```bash
sudo ufw allow from 127.0.0.1 to any port 3444 proto tcp
```

**Client IP logging.** Every login attempt, logout, and change to the login requirement itself is
logged with the resolved client IP. On the normal LAN port, that's always the raw TCP peer
address — a forwarded-for header is never trusted there, since honoring one would let any LAN
client spoof its own logged IP. On the Internet Exposed port specifically, `CF-Connecting-IP` is
trusted first (the header `cloudflared` passes through from Cloudflare's edge unmodified), then
`X-Forwarded-For` if that's absent, falling back to the raw TCP peer otherwise. This is only safe
because this port is firewalled to `127.0.0.1` as shown above, so nothing except `cloudflared`
itself running on this same host can ever be in a position to set that header before the app sees
it — don't skip that firewall rule. None of this affects authorization, only what IP shows up in
a log line; a misconfigured firewall would at worst mislabel who did something, not open a
security hole. There's no separate audit-log file for any of this — it's ordinary `ILogger`
output, so it lands wherever the rest of the web dashboard's logs do:
`journalctl -u sentinelcore-web -f`. Repeated failed logins from the same resolved IP (10 within
5 minutes) get a 429 rather than being logged forever; that counter is in-memory only and resets
on every restart.

### Push notifications don't need any of this

Native push to the phones works without a tunnel or Internet Exposed Mode: Core reaches *out* to
the push relay at `push.nr5p.com` (or straight to Apple/Google in direct mode), and Apple/Google
reach the phone. A phone away from home still gets the alert; it just can't open the event until
it's back on the LAN or the tunnel above is set up. See
[PUSH_NOTIFICATIONS.md](PUSH_NOTIFICATIONS.md) - and, for whoever runs the relay itself,
[PUSH_RELAY.md](PUSH_RELAY.md).

## 14c. Local network discovery (optional)

The web dashboard advertises itself over mDNS/DNS-SD (service type `_sentinelcore._tcp`) so the
iOS and Android apps can find it on the LAN during first-time setup instead of the user typing an
IP address. This is on by default and self-contained - unlike the HomeKit sidecar (§8a), it does
**not** depend on `avahi-daemon`; it's a managed responder built into the dashboard process
itself, so it works out of the box even on installs that never set up Avahi.

The TXT record only ever carries the connection scheme, port, and a random, non-secret instance
id - never an API key or certificate fingerprint. A client still goes through `/api/health` and
the normal login flow after resolving an address this way.

To turn it off (e.g. if you'd rather not have this host answer multicast queries at all), edit
`SentinelCore.Web/appsettings.json`:

```json
"Discovery": {
  "Enabled": false
}
```

Restart the web dashboard for it to take effect: `sudo systemctl restart sentinelcore-web`. This
only removes the auto-discovery convenience - manual server address entry in both apps is
unaffected either way.

## 15. Troubleshooting

**Build fails with `Fatal error. Internal CLR error. (0x80131506)`.** Known MSBuild
build-server flakiness. Run `dotnet build-server shutdown` and retry, then
`dotnet build -c Release --disable-build-servers -m:1` (`deploy.sh` already does both). If that
doesn't clear it, find and kill any stale `MSBuild.dll` / `VBCSCompiler` processes by PID
(`ps aux | grep -E "MSBuild|VBCSCompiler"`) and retry again.

**Movies play but some won't convert, or `ffmpegAvailable` is false.** Check
`journalctl -u sentinelcore-web` for the ffmpeg error, and that `FfmpegPath`/`FfprobePath` point
at a working pair (section 6). A movie whose picture jumps back and forth is a file stamped in
decode order: see [section 6a](#movies--tv) for what this ffmpeg can repair on the fly.

**Movie downloads say the VPN isn't connected.** `cat /run/sentinelcore-vpn/status.json` and
`journalctl -u sentinelcore-vpn -n 50`; [section 8d](#8d-movie-downloads-linux-optional) lists
the usual causes (an expired Mullvad account, AppArmor, MTU). Re-running
`sudo ./downloads/install-downloads.sh` repeats the self-test without changing anything you've
set up.

**Service won't bind its port ("Address already in use").** Something else — often a leftover
test/scratch instance, or the service already running — is holding that port. Find it with
`ss -ltnp | grep :5080` (or `:3320`) and confirm what it actually is before touching it;
don't `pkill` by a name pattern that could also match the real systemd-managed process.

**Service starts then immediately exits.** Check `journalctl -u <service> -n 50 --no-pager`
for the actual exception. The most common causes are a missing/invalid required config value
(Options validation fails at startup — e.g. an empty `Bridge:ApiKey`, or a malformed
`Ollama:BaseUrl`) or `MainAppSettingsPath`/`Bridge:DataDirectory` in the Web project's config
pointing at a path that doesn't exist.

**Web dashboard loads but Settings/Prompt Profiles is empty or errors.** Confirm
`MainAppSettingsPath` in `SentinelCore.Web/appsettings.json` is an absolute path that
actually points at the main app's `appsettings.json` — a relative or wrong path will read
nothing.

**Cameras/Live/Events tabs don't work, or streams never start.** Confirm `ffmpeg` is
installed and either on `PATH` or that `FfmpegPath` in the Web project's config is an
absolute path to the binary. Check `journalctl -u sentinelcore-web -f` while
opening the Live tab — ffmpeg errors show up there.

**Health check passes but Blue Iris alerts never arrive.** Confirm the API key in Blue Iris's
action parameters matches `Bridge:ApiKey` exactly, and that Blue Iris can actually reach this
host's port 5080 over the LAN (firewall, wrong IP, or Blue Iris and this host on different
VLANs are the usual causes).

**Ollama unreachable / `/health/ready` fails.** Confirm Ollama is running
(`systemctl status ollama` or `curl http://127.0.0.1:11434`) and that the model in
`Ollama:Model` has actually been pulled (`ollama list`).

**Memory warnings naming `llama-server`, or the box running out of memory every few hours.**
llama-server's prompt cache is still on: `journalctl -u ollama | grep "prompt cache"` says
`enabled, size limit: 8192 MiB`. Install the drop-in from section 2 (and check it survived any
Ollama reinstall).

## 16. Stopping / uninstalling

```bash
sudo systemctl disable --now sentinelcore
sudo systemctl disable --now sentinelcore-web
sudo rm /etc/systemd/system/sentinelcore.service
sudo rm /etc/systemd/system/sentinelcore-web.service
sudo systemctl daemon-reload
```

If you installed movie downloads, remove them first with
`sudo ./downloads/install-downloads.sh --uninstall` (see
[section 8d](#8d-movie-downloads-linux-optional) for what that keeps).

This does not delete the repo, its data (`data/`, `cameras.db`, `triggers.db`, images,
clips - and, under `SentinelCore.Web/data/` unless you moved them, the Family Files library and
downloaded podcast episodes), your movie and music folders, or the sudoers rule from
[section 10](#10-passwordless-restarts-optional) — remove those separately if you want them gone
too.

## 17. Prebuilt release packages (no SDK required)

Everything above assumes a git checkout and the .NET SDK. This section is the other path: a
self-contained .zip someone can download and install without either - the thing to hand to a
less-technical user, or to install on a machine you don't want to put a full dev toolchain on.

### Building the packages (maintainer side)

From a machine with the .NET SDK (any OS - .NET cross-compiles, so a single machine builds
binaries for every target):

```bash
./scripts/publish-release.sh
```

This produces, under `core/dist/`:

```
SentinelCore-win-x64.zip      Windows (x64)
SentinelCore-linux-x64.zip    Linux (x64)
SentinelCore-osx-x64.zip      macOS (Intel)
SentinelCore-osx-arm64.zip    macOS (Apple Silicon)
```

Each is a **self-contained, single-file publish** (`dotnet publish -r <rid> --self-contained
-p:PublishSingleFile=true`) — the .NET runtime is built into the `SentinelCore` and
`SentinelCore.Web` binaries themselves, so the target machine needs none of it installed. ffmpeg
(and optionally Node.js for HomeKit, Ollama for local AI vision) are still separate, native
tools that can't be bundled this way - the installer inside each .zip offers to install them,
same as `install-linux.sh`/`install-macos.sh`/`install-windows.ps1` do for a source build.

Before zipping, the script also builds each bundle's configuration from the checked-in
`appsettings.defaults.json` (never the untracked live `appsettings.json` on the build machine)
and re-applies the same blanking as a second line of defence: `Bridge:ApiKey` blanked (the app generates and persists a
fresh one on first boot - see `AutoApiKeyProvisioner` - so this isn't a manual step either),
`Setup:Completed` set to false so the setup wizard runs, `Auth:Enabled` set to true so the
install starts locked until its owner creates the admin account, and every alert channel (Email,
Telegram, Discord, Webhook, MQTT, Blue Iris, Cloud Vision) reset to `Enabled: false` with its
credentials cleared. Otherwise a release built from a machine with a live, configured install
would ship that maintainer's own API keys and personal settings to everyone who downloaded it.
Build with `PLATFORMS="linux-x64 win-x64" ./scripts/publish-release.sh` to build a subset.
`linux-arm64` still builds if you name it, but it isn't built by default: a Raspberry Pi can't
realistically run the local vision model.

Before zipping each bundle, the script **starts the binaries it just published** and fails the
build if either doesn't answer its health endpoint. Only the bundle matching the build machine's
own OS and architecture can be started this way — .NET cross-compiles the others but can't run
them — so the others print `Skipping startup check` and are not verified here. It runs against a
copy, so the archive never picks up the `appsettings.json`, `data/` or `certs/` a booting server
creates.

This is worth the minute it costs: "it compiles and the tests pass" says nothing about whether a
*published* bundle can start, because publishing rearranges where everything lives. A release
where the ONNX face-recognition models were no longer findable shipped exactly that way — every
bundle died on startup while every git-checkout build was fine.

That cleaned-up file is shipped as **`appsettings.defaults.json`**, and the archive contains no
`appsettings.json` at all. This is what makes the .zip safe to unpack over an existing install:
`appsettings.json` is the file the dashboard writes every camera, credential and channel setting
into, and an archive containing one would factory-reset a configured install the moment it was
unpacked. Nothing else in the archive collides with user state either - `data/` is excluded from
both projects' publish output by their `.csproj` files, and `certs/` and `homekit-bridge/persist/`
are never staged.

### Installing a package (end-user side)

1. Download and unzip the file for your OS, then **move the unzipped folder somewhere
   permanent**. That folder *is* the install: the services run from it and keep every setting,
   database and recording inside it, so it must not live in Downloads or a temp folder. On Linux:
   ```bash
   unzip SentinelCore-linux-x64.zip
   sudo mv linux-x64 /opt/sentinelcore
   cd /opt/sentinelcore
   ```
   The Linux installer stops and asks if it's run from Downloads or `/tmp`.
2. Run the install script inside it:
   - Linux: `sudo ./install.sh`
   - macOS: `sudo ./install.sh` (also clears Gatekeeper's quarantine flag on the bundle - macOS
     otherwise refuses to run anything downloaded by a browser without it)
   - Windows: right-click `install.ps1` → Run with PowerShell, from an elevated (Administrator)
     prompt
3. Open the dashboard at the address the installer prints when it finishes, create the admin
   account it asks for, and go through the setup wizard (API key, vision backend, cameras,
   notifications).

This installs the same three services (bridge, dashboard, optional HomeKit sidecar) under the
same service manager as sections 8/8b/8c — systemd, Windows Services, or launchd — just pointed
at the prebuilt binaries directly instead of `dotnet SomeProject.dll`. See
`scripts/release/dist-install-*.{sh,ps1}` for exactly what each one does; they're deliberately
self-contained (no shared template files) since they ship standalone inside the .zip.

### Upgrading an existing install

Updating isn't `git pull` here, since there's no git checkout. It's the same two steps as a
first install, run over the top of the old one:

```bash
# unpack the new release somewhere else, then copy it over the install folder
cd /tmp && unzip -o ~/Downloads/SentinelCore-linux-x64.zip
sudo cp -a --remove-destination linux-x64/. /opt/sentinelcore/
cd /opt/sentinelcore && sudo ./install.sh
```

Two traps this avoids: unzipping *inside* the install folder lands the files in a new
`linux-x64/` subfolder instead of over the old ones, and a plain `cp` fails with "Text file busy"
on the two executables while the services are running. `--remove-destination` replaces each file
rather than writing into it; the running services keep the old binaries until `install.sh`
restarts them.

On Windows, unzip over the folder and re-run `install.ps1` from an elevated prompt. The install
script detects the existing install, prints `Existing install found - upgrading`, copies the
current configuration into `backups/<timestamp>/`, and restarts the services onto the new
binaries. It does not re-run the setup wizard.

**What carries over, and why:**

| | Where it lives | Why it survives |
|---|---|---|
| Settings, credentials, alert channels | `SentinelCore/appsettings.json` | Not in the archive - shipped as `appsettings.defaults.json` instead |
| Cameras, trigger zones, accounts | `SentinelCore.Web/data/*.db` | `data/` is excluded from the publish output |
| Events, captured images | `SentinelCore/data/` | Same |
| HTTPS certificate authority | `certs/` | Never staged into the archive |
| HomeKit pairings | `homekit-bridge/persist/` | Excluded when the sidecar is staged |

The certificate authority surviving matters more than it looks: it's the one every phone, Mac and
Apple TV was told to trust (§ 14a). Regenerating it on upgrade would silently break HTTPS on every
device at once.

**Settings that are new in the version you're upgrading to** are added to your existing
`appsettings.json` on the next start, at their shipped defaults, and logged as
`Added N new setting(s) introduced by this version`. The merge is strictly add-only - a key you
have already set is never touched, whatever its value, including one you deliberately blanked.
See `ConfigDefaultsSeeder` and `ConfigDefaultsSeederTests`.

**Database schema changes** are applied in place on first start by the same `AddColumnIfMissing`
pattern used in `EventStore`, `CameraStore`, `TriggerStore` and `PushDeviceStore`, so an event
store written by an older version keeps its rows and gains the new columns.

**Rolling back.** Keep the .zip you were running before - once the new archive has replaced the
binaries, it's the only copy of the old ones. To go back: unpack the previous .zip over the
directory, re-run its install script, and restore the configuration the upgrade saved:

```bash
cp backups/<timestamp>/SentinelCore.appsettings.json SentinelCore/appsettings.json
```

Your cameras, events and images are never modified by any of this, so a rollback doesn't lose
them. For a portable copy of everything you'd otherwise have to set up again, use
**Settings → Backup → download full backup** in the dashboard before upgrading - or better, turn
on **Settings → Nightly Auto Export** and never think about it again (below).

The full backup is one `.zip` (or, with a passphrase, an encrypted `.scbackup`) holding:

- every setting, including Nightly Auto Export's own, so a restored install keeps exporting;
- cameras, camera groups, trigger zones, recording schedules and gate checks;
- user accounts and each person's live grid arrangement;
- the main service's Device Hub devices, automation rules and schedules;
- every Organizer note, event, reminder, board, card and comment, **with the attached files**,
  plus each person's calendar-feed link and Organizer preferences, and every board automation;
- the home alarm's sensors and settings (including the PIN hash) and the Family Files backup
  drives (with their SFTP passwords);
- the Movies & TV libraries, saved sign-ins to other computers' shares, and everyone's watched
  progress, My List and play history; the Music libraries, plays, loves, ratings and playlists;
  podcast subscriptions, per-show settings, progress, Up Next queues and bookmarks; wake-up
  alarms and ListenBrainz connections;
- known faces **with their reference photos**, and the licence file;
- this server's HTTPS certificate authority (`certs/ca.crt` + `ca.key`) and HomeKit pairing,
  which are only put back when you tick *This is a replacement server* while restoring.

The plain **export settings only (.json)** button - and the phone, Mac and Windows apps' backup
screens - still produce the older single JSON file: the same configuration, without the attached
files, photos, licence or certificates.

Because it carries accounts, a backup includes every stored password hash, and every API key and
bot token the install has. **Anyone holding it can stand up a copy of this install and sign in as
any of your users.** Treat it as a credential, not a config file: set a passphrase if it leaves
this machine, and keep it out of shared drives, chat messages and git repositories.

What is still **not** in any backup, deliberately:

| Not in the backup | Why, and what it means after a machine loss |
|---|---|
| Events, captured images, recorded video, review marks | History, not configuration, and far too large. Back up `SentinelCore/data/` separately if you want to keep it. |
| The Family Files library itself | Far bigger than everything else. Its backup drives are its backup: after restoring, use **bring files back** on the FILES tab. |
| Movie, TV and music files; downloaded podcast episodes; transcripts | Your media folders are yours to back up. Catalogs, artwork and covers are rebuilt by a rescan; episodes are downloaded again when wanted. |
| Movie download history, the alarm's event log | History, not configuration. |
| Login sessions | Everyone signs in again. A restored session would be a live login handed to another machine. |
| Push registrations | Each phone re-registers the next time the app connects. |
| A schedule's next-due time, a gate's last seen state | Worked out afresh on the restored install, so nothing fires for things that "should" have happened while it was down. |
| An HTTPS certificate you configured by hand (a real domain certificate) | Only the certificate authority this server generated for itself is backed up. Keep your own certificate files wherever you got them. |

**Restoring onto a replacement machine:** install as normal, create the admin account, then
**Settings → Backup → Restore from backup**, pick the file, enter its passphrase if it has one,
and tick **This is a replacement server**. Keep the main SentinelCore service running during the
restore - the devices, rules and schedules are written through it, and a restore that includes
them is refused, with nothing changed, if it can't be reached. Afterwards restart the dashboard
(`sudo systemctl restart sentinelcore-web`) so it serves a certificate from the restored
authority, and the HomeKit service (`sudo systemctl restart sentinelcore-homekit`) so the Home
app finds the bridge it already knows. Devices that trusted the old server then trust this one
with no re-setup.

### Nightly Auto Export

**Settings → Nightly Auto Export** writes the full backup every night at the time you pick (in
the Schedules time zone), keeps the last *N*, and deletes older ones it made - nothing else in
the folder is ever touched. Each file is written privately first, read back end to end to prove it
decrypts and unzips, and only then copied to the destination under a temporary name and renamed
into place, so the destination only ever holds complete files. A night that fails is retried
hourly, up to six times. If the machine was off at the scheduled time, it runs as soon as it's
back. A failed or incomplete night is sent to you on every alert channel you have set up (email,
Telegram, push), at most once a day. The dashboard's Backup section shows how the last run went.

Destinations:

- **Folder** - a full path on this machine, a mounted network share (e.g. an NFS or SMB mount
  at `/mnt/nas/backups`), or on Windows a `\\server\share\folder` path. A folder on this
  machine's own disk is better than nothing, but it doesn't survive losing the disk.
- **SFTP** - another computer over SSH, with nothing to mount: a Mac with *Remote Login* on
  (System Settings → General → Sharing), a Linux box, or most NAS units. Username and password
  authentication. Leave **Host Key Fingerprint** blank for the first run; the fingerprint seen is
  shown in the result of **export now**, and pasting it into the field makes later uploads refuse
  any other machine answering at that address. To check it against the target itself, run
  `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` there.

Press **export now** after saving to test the destination straight away; it works even with
the feature switched off.

A passphrase encrypts each file with AES-256-GCM (key from PBKDF2-SHA256, 600,000 iterations).
**There is no way to open a file whose passphrase is lost** - keep it somewhere other than this
machine.

**Restoring accounts onto a machine you are signed in to** leaves your own account exactly as it
is - matched by account id *or* username, so it holds whether you are re-importing onto the same
install or restoring onto a fresh one where setup minted you a new id. Every other account in the
file is written over the current one of the same name, and those people are signed out. This is
the rule that stops a backup whose admin password nobody remembers from replacing the working
login halfway through a restore, which is not recoverable from the dashboard.

### Testing an upgrade before handing builds to testers

A fresh install passing proves nothing about an upgrade - the two paths barely overlap. Before
shipping a new release .zip, run this end to end at least once:

1. Install the **previous** release from its .zip.
2. Create the admin account, then go through the setup wizard for real: set an API key, add a
   camera, enable one alert channel
   with a credential in it, and trust the server's certificate on a phone.
3. Let some events land so `SentinelCore/data/events.db` and `data/images/` are non-empty.
4. Unpack the **new** .zip over that directory and re-run its install script.
5. Confirm all five: the setup wizard does *not* reappear, the camera and alert channel are still
   configured, old events are still listed, the phone still connects without a certificate
   warning, and any HomeKit accessories are still paired.
