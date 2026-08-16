# SentinelCore

A .NET 10 ASP.NET Core service that receives camera alert images over HTTP (including from Blue Iris, still fully supported), queues them, asks a local Ollama vision model configurable questions, stores structured JSON results, and optionally posts completed analyses to a webhook. `SentinelCore.Web` layers a dashboard, live camera viewing, PTZ control, HomeKit, trigger zones, and account management on top of the same event store.

**Full documentation:**
- [docs/USER_GUIDE.md](docs/USER_GUIDE.md) — using the dashboard, trigger zones, prompt profiles, Telegram bot, and understanding why an alert did or didn't fire
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — building, configuring, and running this from scratch on a new box, or updating an existing install, without any assistant's help

The rest of this file is a shorter, more code-oriented overview of the main app specifically.

## Architecture

Blue Iris (Windows), or any other alert source posting the same multipart shape, -> HTTP POST -> SentinelCore on port 5080 -> background queue -> Ollama at localhost:11434 -> JSON result/webhook.

The queue lets the sender receive `202 Accepted` immediately instead of waiting for CPU inference to finish.

## Prerequisites

- Ollama running on the Linux host
- `gemma3:4b` installed: `ollama pull gemma3:4b`
- .NET 10 SDK
- Whatever is sending alerts (e.g. Blue Iris) can reach the Linux host over the LAN

## Install .NET 10

On the Ubuntu machine, the terminal's recommended Snap command is acceptable for this new service:

```bash
sudo snap install dotnet --classic
sudo snap alias dotnet.dotnet dotnet 2>/dev/null || true
dotnet --version
```

If `dotnet` installs somewhere other than `/usr/bin/dotnet`, run `command -v dotnet` and update `ExecStart` in the systemd file before installation.

## Configure

Edit `SentinelCore/appsettings.json`:

1. Replace `Bridge:ApiKey` with a long random value, for example:
   `openssl rand -hex 32`
2. Change questions in `PromptProfiles`.
3. Keep `Ollama:Model` as `gemma3:4b`.
4. Optionally enable the webhook action.

To analyze alerts with something other than the local VLM — a hosted model, or the fast ONNX detector in front of it — see [Vision backends](#vision-backends) below.

Do not expose port 5080 directly to the Internet. The API key is intended for LAN authentication, not as a replacement for TLS on an untrusted network.

## Vision backends

Three backends can analyze an alert, and `VisionRouting` decides which one does:

- **`ollama`** (default) — the local VLM named by `Ollama:Model`. Describes the scene in prose and fills in gate state, delivery company, package status. Seconds per alert.
- **`cloud`** — a hosted VLM, configured under `CloudVision`.
- **`fastlocal`** — an ONNX object detector (RF-DETR Nano, ~103 MB, Apache 2.0), downloaded on first use. Milliseconds per alert, but it only reports *what* it saw, not what was happening.

```json
"VisionRouting": {
  "Primary": "ollama",
  "FallbackEnabled": false,
  "EscalationEnabled": false,
  "EscalationTarget": "auto",
  "EscalationVerdictWins": false,
  "MinimumConfidence": 0.0
}
```

| Key | Meaning |
|---|---|
| `Primary` | `ollama`, `cloud`, or `fastlocal`. Defaults to `ollama`, so an existing install behaves exactly as before with no config change. |
| `FallbackEnabled` | If the primary's own retries are exhausted, retry once against another backend rather than failing the alert. |
| `EscalationEnabled` | Only meaningful when `Primary` is `fastlocal`. Once the detector finds a person, vehicle, or animal, re-run the alert through a VLM for the full description. Alerts where the detector finds nothing — most of them — never reach the expensive backend, which is the point of the fast tier. |
| `EscalationTarget` | Where escalation goes: `auto` (cloud if configured, else Ollama), `cloud`, or `ollama`. |
| `EscalationVerdictWins` | Who wins when the detector found a subject and the escalated VLM reports an empty frame. Off keeps the detection; on lets the VLM overrule it. Escalation *failure* — unreachable, threw, not configured — always keeps the detection, since "the VLM said nothing is there" and "the VLM never answered" are different facts. |
| `MinimumConfidence` | Floor for a **VLM** analysis. Below it, the visible flags and package status are cleared and the notification is suppressed, while the summary and raw response are kept so the event still reads correctly. `0` disables the check. Deliberately not applied to fast-detector results — see below. |

`MinimumConfidence` pairs with the prompt convention in `OllamaVisionClient.BuildPrompt`, which asks the model to report confidence at or below 0.2 when the frame is too dark or obstructed to judge. A floor above that turns "I can't see" into "nothing here" rather than a false alert.

### FastLocal

Only used when `fastlocal` is the primary or fallback backend.

```json
"FastLocal": {
  "ModelPath": "fastlocal-model/rf-detr-nano.onnx",
  "DownloadUrl": "https://huggingface.co/PierreMarieCurie/rf-detr-onnx/resolve/main/rf-detr-nano.onnx",
  "DownloadSha256": "3fcbba0f68bad4939fdf1c38f432783b95691e2869af3be369780aa5be67abb2",
  "InputSize": 384,
  "ScoreThreshold": 0.5,
  "NotifyOnPerson": true,
  "NotifyOnVehicle": true,
  "NotifyOnAnimal": false
}
```

| Key | Meaning |
|---|---|
| `ModelPath` | Relative paths resolve under `Bridge:DataDirectory`. The ONNX session is built once per process, so changing this needs a restart. |
| `DownloadUrl` | Fetched once if `ModelPath` doesn't exist. Clear it to require the file be provisioned by hand. |
| `DownloadSha256` | Verified before the download is moved into place. Only applies to the download — a manually provisioned file is trusted as-is. |
| `InputSize` | Fallback only. A model that declares a static input shape wins, and RF-DETR Nano is fixed at 384×384, so this matters only for a substituted model with dynamic axes. |
| `ScoreThreshold` | Minimum score for a detection to count. Lower it if a camera misses distant or night subjects, at the cost of more false alerts. |
| `NotifyOnPerson` / `NotifyOnVehicle` / `NotifyOnAnimal` | Which subjects set `notification_recommended`. Animals default off — a detector can't tell a raccoon in the trash from a bird on the lawn. |

Note that the two thresholds are not on the same scale and are deliberately kept separate. `FastLocal:ScoreThreshold` gates each detection before it is mapped to an analysis; `VisionRouting:MinimumConfidence` gates a finished VLM analysis. The detector scores an *empty* frame 0.8 by convention and a real detection by its own max score, so a shared floor would suppress a 0.82 detection while passing the 0.8 empty frame.

## Run interactively

```bash
dotnet run --project SentinelCore/SentinelCore.csproj
```

Tests:

```bash
dotnet test
```

Health checks:

```bash
curl http://127.0.0.1:5080/health/live
curl http://127.0.0.1:5080/health/ready
```

## Upload a test image

```bash
export API_KEY='the-key-from-appsettings.json'
chmod +x scripts/test-upload.sh
./scripts/test-upload.sh /full/path/to/image.jpg FrontDoor
```

The response includes an ID and status URL. Retrieve it with:

```bash
curl -H "X-Api-Key: $API_KEY" http://127.0.0.1:5080/api/results/REPLACE-WITH-ID
```

## Install as a service

The installer detects the active `dotnet` path, including a Snap installation. It defaults to Linux user `media` and `/opt/sentinelcore`. Override those with `APP_USER` or `INSTALL_DIR` environment variables.

Then:

```bash
chmod +x scripts/install-linux.sh
./scripts/install-linux.sh
```

Logs:

```bash
journalctl -u sentinelcore -f
```

The service starts after and requires `ollama.service`.

## Firewall

Permit only the local subnet:

```bash
sudo ufw allow from 192.168.50.0/24 to any port 5080 proto tcp
```

## Blue Iris action

Copy `scripts/Send-BlueIrisAlert.ps1` to the Blue Iris computer, for example:

`C:\BlueIrisScripts\Send-BlueIrisAlert.ps1`

Create a Blue Iris alert action that runs `powershell.exe`. Supply the actual Blue Iris macros selected from your version's macro picker. Example parameters:

```text
-NoProfile -ExecutionPolicy Bypass -File "C:\BlueIrisScripts\Send-BlueIrisAlert.ps1" -ImagePath "&ALERT_PATH" -Camera "&CAM" -AlertId "&ALERT_DB" -Profile "default" -ApiKey "YOUR-LONG-KEY"
```

Blue Iris macro names can differ by release/configuration. The important requirement is that `-ImagePath` resolves to an existing JPEG/PNG/WebP file when the action runs.

For the front door, pass `-Profile "front-door"` to use that question set.

## Telegram notifications

Sends the alert summary (and photo, if `AttachImage` is true) to a Telegram chat, so you get a push notification anywhere you have cell signal.

1. Message [@BotFather](https://t.me/BotFather) on Telegram, run `/newbot`, and follow the prompts. It gives you a bot token like `123456789:AAExampleTokenTextGoesHere`.
2. Send any message to your new bot from the Telegram account you want alerts on.
3. Find your chat ID: visit `https://api.telegram.org/bot<your-token>/getUpdates` in a browser and look for `"chat":{"id":...}` in the response (or message [@userinfobot](https://t.me/userinfobot) to get your own numeric ID).
4. Configure:

```json
"Telegram": {
  "Enabled": true,
  "BotToken": "123456789:AAExampleTokenTextGoesHere",
  "ChatId": "123456789",
  "OnlyWhenNotificationRecommended": true,
  "MinimumConfidence": 0.6,
  "AttachImage": true,
  "TimeoutSeconds": 20
}
```

## Discord notifications

Discord alerts use an incoming channel webhook, so no Discord bot application, token exchange,
public callback URL, or inbound firewall rule is needed.

1. In Discord, open the destination channel's **Edit Channel → Integrations → Webhooks**.
2. Create a webhook and copy its URL.
3. In Sentinel Core, open **Settings → Discord Alerts**, paste the URL, enable it, and save.

Alerts use a Discord embed with the camera, summary, reason, confidence and optional trigger
snapshot. AI-generated text cannot mention `@everyone`, roles or users. The Discord settings
also have the same notification recommendation, confidence, quiet-hours and duplicate-cooldown
behavior as Telegram.

### Chatting with the bot

With `Telegram` configured as above, you can also text the bot questions - "what happened at the gate today?", "was anyone here last night?" - and get an answer built from `events.db` and a local Ollama model, from anywhere you have cell signal. This only ever answers messages from the `ChatId` configured above; anyone else who finds and messages the bot is silently ignored.

It works by long-polling Telegram's `getUpdates` API (outbound only), so no inbound port or public exposure is needed - the same reason the notifications already work over cellular.

```json
"TelegramChat": {
  "Enabled": true,
  "LookbackDays": 3,
  "MaxEventsInContext": 100,
  "Model": "gemma3:4b",
  "PollTimeoutSeconds": 25,
  "TimeoutSeconds": 60
}
```

`Model` can be any chat-capable model already pulled in Ollama (e.g. `qwen3:14b`) if you want better conversational answers than the vision model gives.

### Live photo on demand

Text `/photo` or `/snapshot` to the bot to get a live image pulled straight from Blue Iris (not the camera directly - Blue Iris already holds the connection to it), separate from the history-based Q&A above.

```json
"BlueIris": {
  "Enabled": true,
  "BaseUrl": "http://192.168.50.7:777",
  "Camera": "gateCamera",
  "Username": "",
  "Password": "",
  "TimeoutSeconds": 15
}
```

`Camera` is Blue Iris's internal short name for the camera - the same value already flowing through as `Camera=` on every alert. Leave `Username`/`Password` blank if Blue Iris's web server doesn't require login on your LAN.

## Self-monitoring

Watches for the two ways this service can silently stop protecting the camera: Ollama becoming unreachable, or the alert queue backing up faster than it's processed. Sends a Telegram message (using the same `Telegram:BotToken`/`ChatId` as above) only when the state actually *changes* - not on every check - and sends a follow-up when it recovers, so you're not paged on a momentary blip but you do find out about real degradation before you'd otherwise notice by just not getting alerts.

```json
"SelfMonitoring": {
  "Enabled": true,
  "CheckIntervalSeconds": 60,
  "ConsecutiveFailuresBeforeAlert": 3,
  "QueueBacklogThreshold": 5,
  "ConsecutiveBacklogChecksBeforeAlert": 3,
  "NotifyOnRecovery": true
}
```

## Web UI

`SentinelCore.Web` is a separate ASP.NET Core project (own subfolder, own process) that serves the event history from `events.db` - a dark, terminal-styled feed of recent events with thumbnails, click-to-zoom, auto-refresh, and a day-indexed trigger-clip timeline. The timeline overlays AI markers on short Blue Iris alert-linked clip windows, proxies timestamp frames for rapid review, and stores reviewed/unreviewed state in `timeline-reviews.db`. It does not save or expose continuous full-day video. It's a plain HTML/CSS/JS static site (no build step, no framework) served alongside a small JSON API.

It doesn't run the alert pipeline itself; it only reads the same SQLite database and image files the main service already writes, via a `ProjectReference` back to `SentinelCore` so it reuses `IEventStore` directly rather than duplicating any logic.

Trigger-clip review uses the main app's `BlueIris` URL and credentials. When an event's friendly
camera name is different from the Blue Iris short name, map it explicitly:

```json
"BlueIris": {
  "Enabled": true,
  "BaseUrl": "http://192.168.1.20:81",
  "Camera": "gateCamera",
  "CameraMappings": {
    "gate camera front door": "gateCamera"
  },
  "EventClipPreSeconds": 5,
  "EventClipPostSeconds": 10,
  "Username": "sentinel",
  "Password": "replace-me"
}
```

Runs on port 3320 (port 332 itself is a privileged port on Linux and would need root or `setcap` to bind - 3320 avoids that). Configure `Bridge:DataDirectory` in `SentinelCore.Web/appsettings.json` to point at the same `data/` directory the main service uses:

```json
"Urls": "http://0.0.0.0:3320",
"Bridge": {
  "DataDirectory": "/absolute/path/to/SentinelCore/data"
}
```

### Settings

The Settings tab edits the main app's `appsettings.json` directly (via `MainAppSettingsPath` in `SentinelCore.Web/appsettings.json`) - Bridge, Ollama tuning, image processing, prompt profiles (including full add/edit/delete of the AI prompts and questions per camera profile), email, daily digest, Telegram alerts/chat, Discord alerts, Blue Iris, self-monitoring, quiet hours, the webhook, and the Device Hub. Each section validates and saves independently.

Everything shown is picked up live via `IOptionsMonitor<T>` - .NET's built-in file-watch reload on `appsettings.json` - so saving a change takes effect within a couple seconds, no restart. Two things are deliberately left out of the UI because they can't be: `Bridge:QueueCapacity` and `Bridge:DataDirectory` size an in-memory queue and open a SQLite connection once at startup, so changing them always needs a restart regardless of how the value is stored. A few `*BaseUrl`/`*TimeoutSeconds` fields are similarly baked into an HTTP client at startup and are also left out rather than half-supported.

Run it interactively with `dotnet run --project SentinelCore.Web/SentinelCore.Web.csproj`, or install it as a systemd service the same way as the main app - see `systemd/sentinelcore-web.service`. Meant for LAN-only access; nothing about it is designed to be exposed to the Internet.

### Rebooting the machine

Off by default (`HostReboot:Enabled`). Restarts the host itself rather than a service - the one
recovery step that otherwise still needed SSH. Works on Linux, macOS and Windows, and the settings
panel reports which of the three it is on, whether the OS currently permits it, and the exact
commands to grant it if not - verified by a probe that never reboots to find out. See
`HostReboot.cs` for why the toggle and the OS grant are deliberately separate, and
`docs/DEPLOYMENT.md` §10a for the per-platform setup.

Reboots are scheduled with a cancellable window (60s by default), held in-process so cancelling
needs no privilege and behaves identically on all three platforms. Power-off is deliberately not
offered: nothing in software can turn a machine back on.

## Device Hub

Off by default. When `DeviceHub:Enabled` is on (and `Mqtt:Enabled` with it), a **DEVICE HUB** tab appears in the dashboard and in the iOS/macOS, Apple TV, and Android apps, for switching lights, plugs, fans, covers, and locks on the same MQTT broker the alert pipeline already publishes to. This is the only part of the app that *subscribes* to MQTT rather than only publishing: `MqttDeviceBus` runs its own client (separate from `MqttPublisher`, so device traffic can't disturb alert publishing), tracks each device's state, and sends commands. `MqttDeviceDiscovery` finds devices two ways - by reading the Home Assistant discovery configs that Zigbee2MQTT/Tasmota/ESPHome/Shelly publish, and by sweeping topics that look like a switch - and offers them as candidates the user chooses to add.

Devices are stored in `devices.db` as raw topics and payloads rather than per-vendor integrations, so anything that speaks MQTT is describable. `DeviceTopics` holds all the payload building and parsing, deliberately connection-free so every vendor quirk is testable without a broker.

Any signed-in account can switch a device; adding, editing, deleting, and discovery are admin-only. See `docs/USER_GUIDE.md` section 13a.

## The additional action

The built-in generic-webhook action is separate from Telegram and Discord. Configure:

```json
"Webhook": {
  "Enabled": true,
  "Url": "http://another-server:8080/alerts",
  "ApiKeyHeaderName": "X-Api-Key",
  "ApiKey": "another-secret",
  "OnlyWhenNotificationRecommended": true,
  "TimeoutSeconds": 20
}
```

To add a different action (MQTT, SMS, database, Home Assistant), implement `IAlertAction` and register it in `Program.cs`. It will run after a successful analysis without changing the receiver or Ollama client.

## Data

The ingest service writes under `SentinelCore/data/`:

- `data/images/YYYY-MM-DD/`: retained alert images, if enabled
- `data/results/`: queued, completed, or failed JSON results
- `data/events.db`: SQLite history of every alert (one row per alert, updated as it progresses from queued to completed/failed), with a link to its saved image file
- `data/devices.db`, `schedules.db`, `push-devices.db`, `automation-rules.db`, `package-watches.db`, `timeline-reviews.db`: one database per feature that keeps state of its own
- `data/fastlocal-model/`: the downloaded `FastLocal` detector weights

The dashboard writes separately, under `SentinelCore.Web/data/` (`CamerasDataDirectory`). In practice this is the bulk of the disk footprint — far more than the ingest service — so it is the first place to look when space runs short:

- `data/trigger-clips/<cameraId>/`: saved clips for confirmed triggers, the largest consumer by a wide margin
- `data/trigger-images/<cameraId>/`: trigger snapshots, plus `_latest.jpg` per camera
- `data/clip-buffer/<cameraId>/`: the rolling ~2s video segments trigger clips and Trigger Rewind are cut from, continuously recycled
- `data/triggers.db`, `cameras.db`, `accounts.db`, `live-order.db`, `gate-checks.db`

Two retention passes run, and they cover different data:

- `Bridge:RetentionDays` — every 12 hours, prunes saved alert images and result JSON. It does not remove old rows from `events.db`.
- `Bridge:UnverifiedMotionRetentionDays` (default 3, 0 keeps everything) — prunes motion that was never confirmed as a real subject: it deletes those rows from `triggers.db` as well as their clips and images, then sweeps any file under the trigger directories that no row points at any more. This is what keeps the *rejected* share of `trigger-clips` at a steady size rather than growing without limit. Note that a clip file is not owned by exactly one trigger: two triggers firing within one clip window share it, so a rejected trigger's clip is kept if a confirmed one still points at the same file.

**Confirmed and still-pending triggers are never pruned by anything, and their clips grow without bound.** This is worth knowing before sizing a disk. `RetentionDays` above does *not* cover them: it walks only `images/` and `results/` under the **ingest service's** data root, and `trigger-clips`/`trigger-images` live under the dashboard's `CamerasDataDirectory` — a different root entirely, which `RetentionWorker` deliberately never leaves. The unverified pass deletes only rows whose confirmation is neither `confirmed` nor `pending`, and the orphan sweep only removes files no row points at — a confirmed row keeps pointing at its clip forever. So every confirmed trigger's clip is kept permanently. On the gate camera this accumulates at roughly **0.8 GB/day** (~19 MB per clip). If that is not what you want, lower `ClipPostSeconds`, turn `RecordClips` off for cameras that don't need video, or prune `trigger-clips` yourself on a schedule.

Query the history with `sqlite3` or any SQLite client, for example:

```bash
sqlite3 data/events.db "SELECT OccurredAt, Camera, EventType, Summary, ImagePath FROM AlertEvents ORDER BY OccurredAt DESC LIMIT 20;"
```

## API

- `POST /api/blueiris/alert` — multipart fields: `image`, `camera`, `alertId`, `profile`, `occurredAt`
- `GET /api/results/{id}` — result status and structured analysis
- `GET /health/live` — process and queue status
- `GET /health/ready` — verifies Ollama is reachable

All `/api` endpoints require `X-Api-Key`. Health endpoints do not.
