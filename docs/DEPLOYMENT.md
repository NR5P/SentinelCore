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

- **Linux** (this guide assumes Ubuntu/Debian-family; adjust package manager commands for
  others)
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
cd BlueIrisOllamaBridge
```

(If you're reading this on the box where it's already deployed, the working copy already
lives at `/opt/sentinelcore` — just `cd` there and `git pull` for
updates, covered in [section 11](#11-updating-to-a-new-version).)

## 4. Building

From the repository root (not inside either project folder) — this builds all three projects
(main app, Web, and the test project) in one pass:

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

Do not expose port 5080 to the internet. The API key is LAN authentication, not a substitute
for TLS on an untrusted network.

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

These are the **exact, working unit files** for this deployment — copy them verbatim, only
adjusting the user, paths, and `dotnet` location if yours differ from `media` /
`/opt/sentinelcore`.

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
WorkingDirectory=/opt/sentinelcore/BlueIrisOllamaBridge
ExecStart=/usr/bin/dotnet /opt/sentinelcore/BlueIrisOllamaBridge/bin/Release/net10.0/BlueIrisOllamaBridge.dll

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
WorkingDirectory=/opt/sentinelcore/BlueIrisOllamaBridge.Web
ExecStart=/usr/bin/dotnet /opt/sentinelcore/BlueIrisOllamaBridge.Web/bin/Release/net10.0/BlueIrisOllamaBridge.Web.dll

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:3320

Restart=always
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=blueiris-ollama-bridge-web

[Install]
WantedBy=multi-user.target
```

Install and start both:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now blueiris-ollama-bridge
sudo systemctl enable --now blueiris-ollama-bridge-web
```

`Restart=always` means systemd brings a crashed service back up on its own after 5 seconds —
useful, but it also means a `pkill` by process name/command-line pattern can hit the real
service instead of a test instance and get silently restarted, masking the mistake. When
testing manually alongside a live install, always track the exact PID you started and kill
only that PID.

## 9. The deploy script

`scripts/deploy.sh` automates build → test → restart for whichever service(s) changed:

```bash
./scripts/deploy.sh          # build + restart both services
./scripts/deploy.sh main     # build + restart only blueiris-ollama-bridge
./scripts/deploy.sh web      # build + restart only blueiris-ollama-bridge-web
```

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
```

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
cd /opt/sentinelcore
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
