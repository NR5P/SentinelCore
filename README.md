# Blue Iris Ollama Bridge

A .NET 10 ASP.NET Core service that receives Blue Iris alert images over HTTP, queues them, asks a local Ollama vision model configurable questions, stores structured JSON results, and optionally posts completed analyses to a webhook.

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

## The additional action

The built-in generic-webhook action is separate from Telegram. Configure:

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
