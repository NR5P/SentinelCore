# Blue Iris Ollama Bridge

A .NET 10 ASP.NET Core service that receives Blue Iris alert images over HTTP, queues them, asks a local Ollama vision model configurable questions, stores structured JSON results, and optionally posts completed analyses to a webhook.

**Full documentation:**
- [docs/USER_GUIDE.md](docs/USER_GUIDE.md) — using the dashboard, trigger zones, prompt profiles, Telegram bot, and understanding why an alert did or didn't fire
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — building, configuring, and running this from scratch on a new box, or updating an existing install, without any assistant's help

The rest of this file is a shorter, more code-oriented overview of the main app specifically.

## Architecture

Blue Iris (Windows) -> multipart HTTP POST -> bridge on port 5080 -> background queue -> Ollama at localhost:11434 -> JSON result/webhook.

The queue lets Blue Iris receive `202 Accepted` immediately instead of waiting for CPU inference to finish.

## Prerequisites

- Ollama running on the Linux host
- `gemma3:4b` installed: `ollama pull gemma3:4b`
- .NET 10 SDK
- Blue Iris can reach the Linux host over the LAN

## Install .NET 10

On the Ubuntu machine, the terminal's recommended Snap command is acceptable for this new service:

```bash
sudo snap install dotnet --classic
sudo snap alias dotnet.dotnet dotnet 2>/dev/null || true
dotnet --version
```

If `dotnet` installs somewhere other than `/usr/bin/dotnet`, run `command -v dotnet` and update `ExecStart` in the systemd file before installation.

## Configure

Edit `BlueIrisOllamaBridge/appsettings.json`:

1. Replace `Bridge:ApiKey` with a long random value, for example:
   `openssl rand -hex 32`
2. Change questions in `PromptProfiles`.
3. Keep `Ollama:Model` as `gemma3:4b`.
4. Optionally enable the webhook action.

Do not expose port 5080 directly to the Internet. The API key is intended for LAN authentication, not as a replacement for TLS on an untrusted network.

## Run interactively

```bash
dotnet run --project BlueIrisOllamaBridge/BlueIrisOllamaBridge.csproj
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

The installer detects the active `dotnet` path, including a Snap installation. It defaults to Linux user `media` and `/opt/blueiris-ollama-bridge`. Override those with `APP_USER` or `INSTALL_DIR` environment variables.

Then:

```bash
chmod +x scripts/install-linux.sh
./scripts/install-linux.sh
```

Logs:

```bash
journalctl -u blueiris-ollama-bridge -f
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

`BlueIrisOllamaBridge.Web` is a separate ASP.NET Core project (own subfolder, own process) that serves the event history from `events.db` - a dark, terminal-styled feed of recent events with thumbnails, click-to-zoom, auto-refresh, and a day-indexed trigger-clip timeline. The timeline overlays AI markers on short Blue Iris alert-linked clip windows, proxies timestamp frames for rapid review, and stores reviewed/unreviewed state in `timeline-reviews.db`. It does not save or expose continuous full-day video. It's a plain HTML/CSS/JS static site (no build step, no framework) served alongside a small JSON API.

It doesn't run the alert pipeline itself; it only reads the same SQLite database and image files the main service already writes, via a `ProjectReference` back to `BlueIrisOllamaBridge` so it reuses `IEventStore` directly rather than duplicating any logic.

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

Runs on port 3320 (port 332 itself is a privileged port on Linux and would need root or `setcap` to bind - 3320 avoids that). Configure `Bridge:DataDirectory` in `BlueIrisOllamaBridge.Web/appsettings.json` to point at the same `data/` directory the main service uses:

```json
"Urls": "http://0.0.0.0:3320",
"Bridge": {
  "DataDirectory": "/absolute/path/to/BlueIrisOllamaBridge/data"
}
```

### Settings

The Settings tab edits the main app's `appsettings.json` directly (via `MainAppSettingsPath` in `BlueIrisOllamaBridge.Web/appsettings.json`) - Bridge, Ollama tuning, image processing, prompt profiles (including full add/edit/delete of the AI prompts and questions per camera profile), email, daily digest, Telegram alerts/chat, Discord alerts, Blue Iris, self-monitoring, quiet hours, and the webhook. Each section validates and saves independently.

Everything shown is picked up live via `IOptionsMonitor<T>` - .NET's built-in file-watch reload on `appsettings.json` - so saving a change takes effect within a couple seconds, no restart. Two things are deliberately left out of the UI because they can't be: `Bridge:QueueCapacity` and `Bridge:DataDirectory` size an in-memory queue and open a SQLite connection once at startup, so changing them always needs a restart regardless of how the value is stored. A few `*BaseUrl`/`*TimeoutSeconds` fields are similarly baked into an HTTP client at startup and are also left out rather than half-supported.

Run it interactively with `dotnet run --project BlueIrisOllamaBridge.Web/BlueIrisOllamaBridge.Web.csproj`, or install it as a systemd service the same way as the main app - see `systemd/blueiris-ollama-bridge-web.service`. Meant for LAN-only access; nothing about it is designed to be exposed to the Internet.

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

- `data/images/YYYY-MM-DD/`: retained alert images, if enabled
- `data/results/`: queued, completed, or failed JSON results
- `data/events.db`: SQLite history of every alert (one row per alert, updated as it progresses from queued to completed/failed), with a link to its saved image file

Retention cleanup runs every 12 hours according to `Bridge:RetentionDays`. It does not remove old rows from `events.db`.

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
