# Sentinel Core — Build & Deployment Guide

This is a from-scratch, no-assistant-required guide to building, configuring, and running
this system on a fresh Linux box, and to updating an existing install. For how to actually
*use* the dashboard once it's running, see [USER_GUIDE.md](USER_GUIDE.md).

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

---

## 1. Architecture, in one page

Two independent ASP.NET Core services, both **built in place and run directly out of their
own `bin/Release/net10.0` folder** inside the repo — there is no `dotnet publish` step and
nothing gets copied to `/opt` or anywhere else. This matters: if you only rebuild and forget
that detail, you might go looking for the running binary in the wrong place.

```
Blue Iris (Windows) ──POST /api/blueiris/alert──▶ BlueIrisOllamaBridge (port 5080)
                                                          │
                                                          ├──▶ Ollama (port 11434, local)
                                                          └──▶ Email / Telegram / Webhook

BlueIrisOllamaBridge.Web (port 3320) ──reads/writes the main app's appsettings.json
                                       ──reads the main app's events.db directly
                                       ──owns its own cameras.db / triggers.db
                                       ──can itself POST into /api/blueiris/alert
                                         (trigger-zone "send to alert pipeline")
```

The Web project has a `ProjectReference` back to the main app so it reuses its event-store
and prompt-profile types directly, rather than duplicating them — the two are built together
by one `dotnet build` at the repo root.

| | Main app (`BlueIrisOllamaBridge`) | Web dashboard (`BlueIrisOllamaBridge.Web`) |
|---|---|---|
| Port | 5080 | 3320 |
| systemd unit | `blueiris-ollama-bridge.service` | `blueiris-ollama-bridge-web.service` |
| Working directory | `BlueIrisOllamaBridge/` | `BlueIrisOllamaBridge.Web/` |
| Binary | `bin/Release/net10.0/BlueIrisOllamaBridge.dll` | `bin/Release/net10.0/BlueIrisOllamaBridge.Web.dll` |

> **Note on the `systemd/` folder in this repo:** those two `.service` files are an older
> install-to-`/opt` template (used by `scripts/install-linux.sh`) and do **not** match how
> this system actually runs today. Use the exact unit files in
> [section 8](#8-installing-as-systemd-services) below, which reflect the real, in-place
> deployment — not the checked-in templates.

## 2. Prerequisites

**The installer handles all of these for you** — `./scripts/install-linux.sh` on Linux or
`.\scripts\install-windows.ps1` on Windows detects what's missing and offers to install it. This
list is what it's installing, for reference or if you'd rather do it by hand.

- **Linux or Windows.** Most of this guide is written for Linux (Ubuntu/Debian-family). Windows
  is supported too and is worth considering, since Blue Iris is Windows-only — running both on
  one machine avoids needing a second box. See [section 8b](#8b-windows) for what differs.
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
git clone <your-repo-url> BlueIrisOllamaBridge
cd BlueIrisOllamaBridge/core
```

(If you're reading this on the box where it's already deployed, the working copy already
lives at `/opt/sentinelcore` — the repo root holds `.git` plus a
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

### Main app — `BlueIrisOllamaBridge/appsettings.json`

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

### Web dashboard — `BlueIrisOllamaBridge.Web/appsettings.json`

```json
{
  "Urls": "http://0.0.0.0:3320",
  "Bridge": {
    "DataDirectory": "/absolute/path/to/BlueIrisOllamaBridge/BlueIrisOllamaBridge/data"
  },
  "MainAppSettingsPath": "/absolute/path/to/BlueIrisOllamaBridge/BlueIrisOllamaBridge/appsettings.json",
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

Also do not expose port 3320 to the internet — there's no login on the dashboard.

## 7. Running interactively (no systemd)

Useful for testing a change before installing it as a service:

```bash
dotnet run --project BlueIrisOllamaBridge/BlueIrisOllamaBridge.csproj
```

```bash
dotnet run --project BlueIrisOllamaBridge.Web/BlueIrisOllamaBridge.Web.csproj
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
`/opt/sentinelcore/core`).

**`/etc/systemd/system/blueiris-ollama-bridge.service`:**

```ini
[Unit]
Description=Blue Iris Ollama Bridge
Wants=network-online.target
After=network-online.target ollama.service

[Service]
Type=simple
User=media
Group=media
WorkingDirectory=/opt/sentinelcore/core/BlueIrisOllamaBridge
ExecStart=/usr/bin/dotnet /opt/sentinelcore/core/BlueIrisOllamaBridge/bin/Release/net10.0/BlueIrisOllamaBridge.dll

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5080

Restart=always
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=blueiris-ollama-bridge

[Install]
WantedBy=multi-user.target
```

**`/etc/systemd/system/blueiris-ollama-bridge-web.service`:**

```ini
[Unit]
Description=Blue Iris Ollama Bridge Web UI
Wants=network-online.target
After=network-online.target blueiris-ollama-bridge.service

[Service]
Type=simple
User=media
Group=media
WorkingDirectory=/opt/sentinelcore/core/BlueIrisOllamaBridge.Web
ExecStart=/usr/bin/dotnet /opt/sentinelcore/core/BlueIrisOllamaBridge.Web/bin/Release/net10.0/BlueIrisOllamaBridge.Web.dll

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:3320

Restart=always
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=blueiris-ollama-bridge-web

[Install]
WantedBy=multi-user.target
```

Install and start them (the third line only if you're adding the HomeKit sidecar):

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now blueiris-ollama-bridge
sudo systemctl enable --now blueiris-ollama-bridge-web
sudo systemctl enable --now blueiris-ollama-bridge-homekit
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
| Service names | `blueiris-ollama-bridge`, `-web`, `-homekit` | `SentinelCoreBridge`, `SentinelCoreWeb`, `SentinelCoreHomeKit` |
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

## 9. The deploy script

`scripts/deploy.sh` automates build → test → restart for whichever service(s) changed:

```bash
./scripts/deploy.sh          # build + restart all services
./scripts/deploy.sh main     # build + restart only blueiris-ollama-bridge
./scripts/deploy.sh web      # build + restart only blueiris-ollama-bridge-web
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
media ALL=(root) NOPASSWD: /usr/bin/systemctl restart blueiris-ollama-bridge
media ALL=(root) NOPASSWD: /usr/bin/systemctl restart blueiris-ollama-bridge-web
media ALL=(root) NOPASSWD: /usr/bin/systemctl restart blueiris-ollama-bridge-homekit
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
sudo systemctl restart blueiris-ollama-bridge
sudo systemctl restart blueiris-ollama-bridge-web
```

Configuration changes to `appsettings.json` (most of them — see the note in
[USER_GUIDE.md § Settings](USER_GUIDE.md#7-the-settings-tab) about the handful of fields that
need a restart) take effect live without any of the above; a restart is only needed after
pulling new *code*.

## 12. Verifying health

```bash
systemctl status blueiris-ollama-bridge blueiris-ollama-bridge-web --no-pager
curl http://127.0.0.1:5080/health/live
curl http://127.0.0.1:5080/health/ready   # also checks Ollama is reachable
curl http://127.0.0.1:3320/api/health
journalctl -u blueiris-ollama-bridge -f       # live tail
journalctl -u blueiris-ollama-bridge-web -f
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

## 14a. HTTPS (optional)

Plain HTTP is the shipped default and is fine on a home network you trust. Turn this on if the
browser's "Not Secure" label bothers you, if you've enabled the dashboard login (the password
crosses the wire in cleartext otherwise), or if you want Android's proper "Install app" prompt
for the web app, which only appears over HTTPS.

**The awkward part, stated plainly:** no public certificate authority will issue a certificate
for a private address like `192.168.50.8`. Let's Encrypt cannot help. That leaves two routes,
and a trap:

> **The trap:** a plain self-signed certificate makes things *worse*, not better. Browsers show a
> full-page "your connection is not private" warning on every visit — far more alarming than the
> small "Not Secure" label you started with. What follows is not that; it's a certificate signed
> by an authority your devices are told to trust, which shows an ordinary padlock.

### Route A — your own certificate authority (no domain needed)

```bash
./scripts/setup-local-https.sh
```

This creates an authority **unique to your install** in `core/certs/`, then issues a server
certificate covering this machine's hostname, its `.local` name, and every LAN IP. It's safe to
re-run: it keeps the authority (so devices stay trusted) and reissues only the server
certificate.

Then point the dashboard at it in `BlueIrisOllamaBridge.Web/appsettings.json`:

```json
"Https": {
  "CertificatePath": "/full/path/to/core/certs/server.crt",
  "KeyPath": "/full/path/to/core/certs/server.key",
  "Port": 3443
}
```

and restart: `sudo systemctl restart blueiris-ollama-bridge-web`

**Plain HTTP keeps listening on 3320.** That's deliberate — both mobile apps default to `http://`
when you type a bare address, so forcing everything to HTTPS would break them on any device that
hadn't yet trusted the authority. Bookmark the `https://` address; nothing else changes.

Finally, install `core/certs/ca.crt` on each device you browse from. The script prints
per-platform instructions when it finishes. The one people get stuck on is iOS: installing the
profile is **not** enough, you must then go to **Settings → General → About → Certificate Trust
Settings** and switch it on.

Open the firewall for the HTTPS port the same way as the others:

```bash
sudo ufw allow from 192.168.50.0/24 to any port 3443 proto tcp
```

**Renewal.** The certificate is capped at 825 days because Apple rejects anything longer, even
from an authority you added yourself. Nobody remembers a two-year deadline, and an expired
certificate means the dashboard won't load at all — worse than the warning it replaced. So
install the timer, which checks monthly and reissues within 30 days of expiry:

```bash
sudo cp systemd/blueiris-ollama-bridge-cert-renew.{service,timer} /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now blueiris-ollama-bridge-cert-renew.timer
```

(Edit the two `__INSTALL_DIR__`/`__APP_USER__` placeholders first, or let
`install-linux.sh` fill them in.) The authority itself lasts ten years, so renewals never require
touching a phone again.

### Route B — a real domain (no per-device setup)

If per-device trust is too much friction — likely if other people use the dashboard — buy a cheap
domain and get a genuinely trusted certificate via a **DNS-01** challenge, which works for a host
that isn't reachable from the internet. Point `sentinel.yourdomain.com` at the LAN IP using your
router's DNS, and every device just works with no installation at all. Caddy or certbot with a
DNS plugin will automate issuance and renewal; feed the resulting files into the same `Https`
block above.

### A note if you're distributing this

The authority is generated per install and deliberately **not** bundled with the product. Shipping
one would mean every install shared a single private key, so anyone with a copy could issue
certificates that every other customer's devices would trust. That's why this is a script rather
than a file, and why HTTP remains the default — the same choice Home Assistant, Unifi and Proxmox
all make.

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
`MainAppSettingsPath` in `BlueIrisOllamaBridge.Web/appsettings.json` is an absolute path that
actually points at the main app's `appsettings.json` — a relative or wrong path will read
nothing.

**Cameras/Live/Triggers tabs don't work, or streams never start.** Confirm `ffmpeg` is
installed and either on `PATH` or that `FfmpegPath` in the Web project's config is an
absolute path to the binary. Check `journalctl -u blueiris-ollama-bridge-web -f` while
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
sudo systemctl disable --now blueiris-ollama-bridge
sudo systemctl disable --now blueiris-ollama-bridge-web
sudo rm /etc/systemd/system/blueiris-ollama-bridge.service
sudo rm /etc/systemd/system/blueiris-ollama-bridge-web.service
sudo systemctl daemon-reload
```

This does not delete the repo, its data (`data/`, `cameras.db`, `triggers.db`, images,
clips), or the sudoers rule from [section 10](#10-passwordless-restarts-optional) — remove
those separately if you want them gone too.
