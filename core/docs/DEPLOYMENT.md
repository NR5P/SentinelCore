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
- **Ollama**, running locally, with a vision-capable model pulled (default `gemma3:4b`) -
  required for the default configuration. An offsite Cloud Vision backend (OpenAI, Anthropic, or
  Google) can be used
  instead, or as an automatic fallback, with no local install of its own - just an API key in
  Settings. See the User Guide's
  [Vision analysis backend](USER_GUIDE.md#8-vision-analysis-backend--local-cloud-or-both)
  section.
- **ffmpeg**, installed and on `PATH` — required by the Web project for the Cameras/Live/
  Triggers tabs (it shells out to ffmpeg for HLS streaming, snapshot capture, and clip
  extraction)
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
ollama pull gemma3:4b
```

## 3. Getting the code

```bash
git clone <your-repo-url> SentinelCore
cd SentinelCore/core
```

(If you're reading this on the box where it's already deployed, the working copy already
lives at `/home/sentinel/Desktop/SentinelCore` — the repo root holds `.git` plus a
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

## 5. Running the tests

```bash
dotnet test
```

This is fast (well under a second of actual test time) and worth running before every deploy
— it's the one thing standing between a bad change and the live alert pipeline.

## 6. First-time configuration

The dashboard's own setup wizard (opens automatically on first visit once both services are
running - see [USER_GUIDE.md § Opening the dashboard](USER_GUIDE.md#2-opening-the-dashboard))
covers the essentials below - API key, vision backend, a camera, notifications - without
hand-editing `appsettings.json`. The walkthrough here is the manual/advanced path, for anything
the wizard doesn't cover or if you'd rather edit the file directly.

### Main app — `SentinelCore/appsettings.json`

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
7. Optionally require a login for the dashboard under `Auth` (`Enabled`/`Username`/
   `PasswordHash`) - off by default, matching every install before this feature existed. The
   setup wizard and Settings → LOGIN are the supported way to turn this on (they handle
   hashing); there's no supported way to set `PasswordHash` by hand.

Do not expose port 5080 to the internet. The API key is LAN authentication, not a substitute
for TLS on an untrusted network. The same applies to port 3320/the dashboard login - it's a
username/password over plain HTTP unless you put a TLS-terminating reverse proxy in front of
it yourself.

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
  pipeline (see [USER_GUIDE.md § Triggers](USER_GUIDE.md#6-the-triggers-tab--motion-zones-on-your-own-cameras)).
- `CamerasDataDirectory` is where `cameras.db`, `triggers.db`, and recorded clips live — can
  be relative (to the Web project's working directory) or absolute.
- `FfmpegPath` can be a bare command name (resolved via `PATH`) or an absolute path.
- `CameraDiscovery:FfprobePath` (not shown above — omit it entirely unless you need it) overrides
  where the camera discovery feature looks for `ffprobe`, the tool it uses to verify a candidate
  stream URL actually carries video. Left unset, it's derived from `FfmpegPath` (swapping `ffmpeg`
  for `ffprobe` in the same location), which is correct for the overwhelming majority of installs
  since the two ship side by side. Only set this if `ffprobe` lives somewhere `ffmpeg` doesn't.

Also do not expose port 3320 to the internet — there's no login on the dashboard.

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

It builds, runs the tests, installs and starts all three services (alert pipeline, dashboard,
and — if Node.js is present — the HomeKit sidecar), then prints a health check. Everything runs
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
`/home/sentinel/Desktop/SentinelCore/core`).

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
WorkingDirectory=/home/sentinel/Desktop/SentinelCore/core/SentinelCore
ExecStart=/usr/bin/dotnet /home/sentinel/Desktop/SentinelCore/core/SentinelCore/bin/Release/net10.0/SentinelCore.dll

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
WorkingDirectory=/home/sentinel/Desktop/SentinelCore/core/SentinelCore.Web
ExecStart=/usr/bin/dotnet /home/sentinel/Desktop/SentinelCore/core/SentinelCore.Web/bin/Release/net10.0/SentinelCore.Web.dll

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
sudo APP_USER=nathan WEB_PORT=8080 ASSUME_YES=1 ./scripts/install-macos.sh
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

## 9. The deploy script

`scripts/deploy.sh` automates build → test → restart for whichever service(s) changed:

```bash
./scripts/deploy.sh          # build + restart all services
./scripts/deploy.sh main     # build + restart only sentinelcore
./scripts/deploy.sh web      # build + restart only sentinelcore-web
./scripts/deploy.sh homekit  # restart only the HomeKit sidecar (Node, nothing to build)
```

The HomeKit sidecar is skipped automatically if its unit isn't installed, so `./deploy.sh` works
unchanged whether or not you're using HomeKit.

It runs (in order): `dotnet build-server shutdown` (safety, see section 4), `dotnet build -c
Release`, `dotnet test` (set `SKIP_TESTS=1` to skip — not recommended), then the systemd
restart(s), then a status + health-check summary. If the build or tests fail, it stops before
touching the running services.

It needs `sudo` for the `systemctl restart` calls — either enter your password each time it
prompts, or see the next section to make that passwordless.

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

## 11. Updating to a new version

```bash
cd /home/sentinel/Desktop/SentinelCore/core
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
sudo ufw allow from 192.168.50.0/24 to any port 5080 proto tcp
sudo ufw allow from 192.168.50.0/24 to any port 3320 proto tcp
```

(Adjust the subnet to match your actual network.)

If you turn on the built-in MQTT broker (`Mqtt:RunEmbeddedBroker`), also open its port (1883
by default) to your LAN so Home Assistant or other MQTT clients elsewhere on the network can
reach it:

```bash
sudo ufw allow from 192.168.50.0/24 to any port 1883 proto tcp
```

Not needed if you're pointing this app at a broker that already exists elsewhere (e.g. Home
Assistant's own Mosquitto add-on) - only the outbound connection from this app to that broker
matters then, and outbound connections aren't affected by `ufw`'s default incoming-only rules.

If you installed the HomeKit sidecar (section 8a), open its accessory port so iPhones on your
LAN can reach it — they connect to it directly, mDNS only tells them where to look:

```bash
sudo ufw allow from 192.168.50.0/24 to any port 51826 proto tcp
```

Do **not** open 3321. That's the sidecar's control API, bound to `127.0.0.1` and meant only for
the main app on this same host.

Camera discovery (Test Connection / Discover Cameras on the CAMERAS tab) needs no firewall change
at all — it's outbound only, from this host to a camera, so `ufw`'s default incoming-only rules
don't affect it. Nothing listens for a reply on an open port; it only runs when you click one of
those two buttons, never in the background.

## 14a. HTTPS (on by default, no setup required)

**Nothing to do here.** The dashboard generates its own certificate authority and server
certificate the first time it starts, covering this machine's hostname, its `.local` name, and
every LAN IP it currently has, and serves HTTPS on port 3443 alongside the existing plain-HTTP
listener — see `AutoCertificateProvisioner` in `SentinelCore.Web` if you want to see how. There's
no script to run and no `appsettings.json` field to hand-edit; this replaced an earlier version
of this product that required both. `Https:Enabled: false` in `SentinelCore.Web/appsettings.json`
turns it off if you'd rather not run HTTPS at all.

**The awkward part, stated plainly:** no public certificate authority will issue a certificate
for a private address like `192.168.50.8`. Let's Encrypt cannot help, which is why this uses a
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

The app prints the exact path in its startup log the first time it generates one. Per platform:

- **Linux:** `sudo cp ca.crt /usr/local/share/ca-certificates/sentinel-core.crt && sudo update-ca-certificates`
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

Open the firewall for the HTTPS port the same way as the others:

```bash
sudo ufw allow from 192.168.50.0/24 to any port 3443 proto tcp
```

**Renewal.** The certificate is capped at 825 days because Apple rejects anything longer, even
from an authority generated on your own machine. It's checked and reissued automatically (from
the existing authority, so already-trusted devices stay trusted) whenever the app starts and the
current one is within 30 days of expiring — which in practice means it renews itself across
whatever restart happens next, the same as picking up any other code update. The authority
itself lasts ten years, so this never requires touching a phone again.

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

Open the firewall for this port the same way as the others - but **only to `127.0.0.1`**, since
only `cloudflared` running on this same machine should ever connect to it directly; nothing on
your LAN needs to reach this port, and the tunnel is what actually exposes it to the internet.

```bash
sudo ufw allow from 127.0.0.1 to any port 3444 proto tcp
```

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
build-server flakiness. Run `dotnet build-server shutdown` and retry. If that alone doesn't
clear it, find and kill any stale `MSBuild.dll` / `VBCSCompiler` processes by PID
(`ps aux | grep -E "MSBuild|VBCSCompiler"`) and retry again.

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

**Cameras/Live/Triggers tabs don't work, or streams never start.** Confirm `ffmpeg` is
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

## 16. Stopping / uninstalling

```bash
sudo systemctl disable --now sentinelcore
sudo systemctl disable --now sentinelcore-web
sudo rm /etc/systemd/system/sentinelcore.service
sudo rm /etc/systemd/system/sentinelcore-web.service
sudo systemctl daemon-reload
```

This does not delete the repo, its data (`data/`, `cameras.db`, `triggers.db`, images,
clips), or the sudoers rule from [section 10](#10-passwordless-restarts-optional) — remove
those separately if you want them gone too.

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
SentinelCore-linux-arm64.zip  Linux (ARM64 - Raspberry Pi 4/5 and similar)
SentinelCore-osx-x64.zip      macOS (Intel)
SentinelCore-osx-arm64.zip    macOS (Apple Silicon)
```

Each is a **self-contained, single-file publish** (`dotnet publish -r <rid> --self-contained
-p:PublishSingleFile=true`) — the .NET runtime is built into the `SentinelCore` and
`SentinelCore.Web` binaries themselves, so the target machine needs none of it installed. ffmpeg
(and optionally Node.js for HomeKit, Ollama for local AI vision) are still separate, native
tools that can't be bundled this way - the installer inside each .zip offers to install them,
same as `install-linux.sh`/`install-macos.sh`/`install-windows.ps1` do for a source build.

Before zipping, the script also replaces the repo's own `appsettings.json` files with a clean,
secret-free copy for each bundle: `Bridge:ApiKey` blanked (the app generates and persists a
fresh one on first boot - see `AutoApiKeyProvisioner` - so this isn't a manual step either),
`Setup:Completed` set to false so the setup wizard runs, and every alert channel (Email,
Telegram, Discord, Webhook, MQTT, Blue Iris, Cloud Vision) reset to `Enabled: false` with its
credentials cleared. Otherwise a release built from a machine with a live, configured install
would ship that maintainer's own API keys and personal settings to everyone who downloaded it.
Build with `PLATFORMS="linux-x64 win-x64" ./scripts/publish-release.sh` to build a subset.

### Installing a package (end-user side)

1. Download and unzip the file for your OS.
2. Run the install script inside it:
   - Linux: `sudo ./install.sh`
   - macOS: `sudo ./install.sh` (also clears Gatekeeper's quarantine flag on the bundle - macOS
     otherwise refuses to run anything downloaded by a browser without it)
   - Windows: right-click `install.ps1` → Run with PowerShell, from an elevated (Administrator)
     prompt
3. Open the dashboard at the address the installer prints when it finishes, and go through the
   setup wizard (API key, vision backend, cameras, notifications).

This installs the same three services (bridge, dashboard, optional HomeKit sidecar) under the
same service manager as sections 8/8b/8c — systemd, Windows Services, or launchd — just pointed
at the prebuilt binaries directly instead of `dotnet SomeProject.dll`. See
`scripts/release/dist-install-*.{sh,ps1}` for exactly what each one does; they're deliberately
self-contained (no shared template files) since they ship standalone inside the .zip.

**Updating** isn't `git pull` here, since there's no git checkout - download the next release
.zip and re-run its install script with the same install location. Your data
(`SentinelCore/data`, `SentinelCore.Web/data`, and the HTTPS certificate authority in the
top-level `certs/` folder) isn't part of the release archive, so it's untouched.
