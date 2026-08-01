window.GateWatchSettings = (function () {
  const SECTIONS = [
    {
      key: "bridge", title: "BRIDGE", endpoint: "/api/settings/bridge",
      basic: true,
      description: "How Blue Iris talks to this app, and how long history is kept. The API key here has to match what you entered in Blue Iris - if alerts stop arriving, check that first. Re-run setup wizard walks you through the essentials again without losing anything you've already configured.",
      fields: [
        { name: "apiKey", label: "API Key", type: "password", tip: "The secret Blue Iris must send with each alert. If this doesn't match what Blue Iris is configured with, alerts are rejected." },
        { name: "maximumImageBytes", label: "Max Image Bytes", type: "number", min: 1024, max: 104857600, tip: "Largest snapshot accepted from Blue Iris. Anything bigger is refused instead of eating memory." },
        { name: "retentionDays", label: "Retention Days", type: "number", min: 1, max: 3650, tip: "How long events and their images are kept before being cleaned up automatically." },
        { name: "saveImages", label: "Save Images", type: "checkbox", tip: "Keep a copy of each snapshot on disk. Turn off to record only the AI's text analysis." },
        { name: "allowedImageContentTypes", label: "Allowed Content Types (comma separated)", type: "csv", tip: "Image formats accepted from Blue Iris, e.g. image/jpeg. Anything else is rejected." }
      ],
      extraAction: {
        label: "re-run setup wizard",
        tip: "Bring back the first-run setup wizard (vision backend, a camera, notifications) without losing anything already configured.",
        async onClick() {
          await fetch("/api/setup/reset", { method: "POST" });
          window.location.reload();
        }
      }
    },
    {
      key: "ollama", title: "OLLAMA", endpoint: "/api/settings/ollama",
      basic: false,
      description: "Tuning for the local AI that looks at your snapshots. The defaults are picked to keep descriptions literal and consistent, which is what you want for security alerts. You can ignore this section entirely unless answers are coming back wrong or too slowly.",
      fields: [
        { name: "model", label: "Model", type: "text", tip: "Which local vision model analyses the snapshots, e.g. gemma3:4b. It must already be pulled in Ollama." },
        { name: "keepAlive", label: "Keep Alive", type: "text", tip: "How long Ollama keeps the model loaded in memory between alerts, e.g. 5m. Longer means faster replies but more RAM held." },
        { name: "temperature", label: "Temperature", type: "number", min: 0, max: 2, step: 0.1, tip: "How much the model is allowed to improvise. Low values keep descriptions consistent and literal, which is what you want here." },
        { name: "maximumOutputTokens", label: "Max Output Tokens", type: "number", min: 32, max: 4096, tip: "Ceiling on the length of the model's reply. Too low and the analysis gets cut off mid-sentence." },
        { name: "topK", label: "Top K", type: "number", min: 1, max: 100, tip: "Limits the model to the K most likely next words. Lower is more predictable." },
        { name: "topP", label: "Top P", type: "number", min: 0, max: 1, step: 0.05, tip: "Another way to limit word choice, by probability mass rather than count. Lower is more predictable." },
        { name: "maximumRetries", label: "Max Retries", type: "number", min: 0, max: 10, tip: "How many times to retry when Ollama fails or returns something unparseable." },
        { name: "retryDelaySeconds", label: "Retry Delay (s)", type: "number", min: 1, max: 300, tip: "How long to wait between those retries." }
      ]
    },
    {
      key: "cloudVision", title: "CLOUD VISION", endpoint: "/api/settings/cloud-vision",
      basic: false,
      description: "Use an outside AI service instead of, or as a backup to, the local model. It's faster and more accurate than a small local model, but it costs money per image and sends your snapshots off your network. Only needed if the local model isn't keeping up.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Allow this cloud account to be used for alert analysis, either as the primary backend or as a fallback - see VISION ROUTING below to choose which." },
        { name: "provider", label: "Provider", type: "select", options: [
            { value: "openai", label: "OpenAI (or compatible)" },
            { value: "anthropic", label: "Anthropic" },
            { value: "google", label: "Google" }
          ], tip: "Which cloud AI account the API key and model below belong to. 'OpenAI (or compatible)' also covers any other endpoint that speaks the same request format - Azure OpenAI, OpenRouter, Together.ai, Groq, or a self-hosted vLLM/LM Studio/LocalAI server - by changing CloudVision:BaseUrl in appsettings.json (needs a restart, same as Ollama's own address)." },
        { name: "apiKey", label: "API Key", type: "password", tip: "An API key for the provider selected above, with access to a vision-capable model." },
        { name: "model", label: "Model", type: "text", tip: "Which model analyses the snapshots - must match the provider above, e.g. gpt-4o-mini (OpenAI), claude-3-5-sonnet-latest (Anthropic), or gemini-1.5-flash (Google)." },
        { name: "maximumRetries", label: "Max Retries", type: "number", min: 0, max: 10, tip: "How many times to retry when the provider fails or returns something unparseable." },
        { name: "retryDelaySeconds", label: "Retry Delay (s)", type: "number", min: 1, max: 300, tip: "How long to wait between those retries." }
      ]
    },
    {
      key: "fastLocal", title: "FAST LOCAL DETECTION", endpoint: "/api/settings/fast-local",
      basic: false,
      description: "A small object detector that answers 'is a person, vehicle, or animal there?' in well under a second, even on a CPU-only machine that would take minutes to run Ollama's full model. It can't describe the scene, read gate state, or spot packages - pair it with escalation in VISION ROUTING below to get the full analysis only for alerts that actually contain something. The detector model (~100MB) downloads automatically the first time it's used.",
      fields: [
        { name: "scoreThreshold", label: "Score Threshold", type: "number", min: 0, max: 1, step: 0.05, tip: "Minimum detection confidence for a subject to count, 0-1. Lower it if the detector misses distant or nighttime subjects; raise it if shadows and headlights are causing false detections." },
        { name: "notifyOnPerson", label: "Notify On Person", type: "checkbox", tip: "Recommend a notification when a person is detected." },
        { name: "notifyOnVehicle", label: "Notify On Vehicle", type: "checkbox", tip: "Recommend a notification when a vehicle is detected. Unlike the full AI, the detector can't tell passing road traffic from a vehicle at your gate - draw your trigger zones so the road isn't included, or turn this off for road-facing cameras." },
        { name: "notifyOnAnimal", label: "Notify On Animal", type: "checkbox", tip: "Recommend a notification when an animal is detected. Off by default - the detector can't judge which animals matter to you." }
      ]
    },
    {
      key: "visionRouting", title: "VISION ROUTING", endpoint: "/api/settings/vision-routing",
      basic: false,
      description: "Which AI analyses a snapshot first, whether to fall back to another one if it fails, and - when Fast Local Detection is primary - whether alerts with something in them get escalated to a full AI for the detailed description.",
      fields: [
        { name: "primary", label: "Primary Backend", type: "select", options: [
            { value: "ollama", label: "Ollama (local)" },
            { value: "cloud", label: "Cloud Vision (OpenAI)" },
            { value: "fastlocal", label: "Fast Local Detection" }
          ], tip: "Which backend analyses alerts first. Fast Local Detection is the right choice for hardware too slow to run Ollama's full model - pair it with escalation below so alerts with something in them still get a full description. If Cloud Vision is picked but not enabled/configured above, Ollama is used instead automatically. Gate/door open checks always use a full AI (cloud if configured, otherwise Ollama) - a plain detector can't read whether a gate is open." },
        { name: "fallbackEnabled", label: "Fall Back To Another Backend", type: "checkbox", tip: "If the primary backend fails after its own retries, automatically retry once against another one before giving up - slower, but the alert still gets analyzed instead of being lost." },
        { name: "escalationEnabled", label: "Escalate Fast Detections For Full Analysis", type: "checkbox", tip: "Only applies when the primary is Fast Local Detection: when the detector finds a person, vehicle, or animal, re-analyze that alert with a full AI to get the detailed description (delivery company, gate state, animal species). Empty alerts - the vast majority - never reach the expensive model at all. If escalation fails, the fast result still stands." },
        { name: "escalationTarget", label: "Escalation Target", type: "select", options: [
            { value: "auto", label: "Auto (cloud if configured, else Ollama)" },
            { value: "cloud", label: "Cloud Vision only" },
            { value: "ollama", label: "Ollama (local)" }
          ], tip: "Which full AI escalated alerts go to. 'Cloud Vision only' never falls back to Ollama - the right choice on hardware where a surprise multi-minute local analysis is worse than a missing description." }
      ]
    },
    {
      key: "imageProcessing", title: "IMAGE PROCESSING", endpoint: "/api/settings/image-processing",
      basic: false,
      description: "Snapshots are shrunk before going to the AI, which is faster and usually just as accurate. Lower quality means quicker answers but a blurrier image for the model to read. Best left alone unless analysis is too slow or the AI is missing obvious things.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Shrink snapshots before sending them to the AI. Usually faster with no real loss in accuracy." },
        { name: "maxWidth", label: "Max Width", type: "number", min: 256, max: 4096, tip: "Images wider than this are scaled down before analysis." },
        { name: "maxHeight", label: "Max Height", type: "number", min: 256, max: 4096, tip: "Images taller than this are scaled down before analysis." },
        { name: "jpegQuality", label: "JPEG Quality", type: "number", min: 1, max: 100, tip: "Quality of the resized image. Lower means smaller and faster but blurrier for the model to read." },
        { name: "autoOrient", label: "Auto Orient", type: "checkbox", tip: "Rotate images upright based on their orientation tag, so the model isn't looking at a sideways scene." }
      ]
    },
    {
      key: "email", title: "EMAIL", endpoint: "/api/settings/email",
      basic: true,
      description: "Emails you when something worth knowing about happens. Most providers - Gmail included - need an app password here rather than your normal one. Only events the AI judges alert-worthy are sent, so you won't get a message every time a shadow moves.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Send email when an alert is worth telling you about." },
        { name: "smtpHost", label: "SMTP Host", type: "text", tip: "Outgoing mail server, e.g. smtp.gmail.com." },
        { name: "smtpPort", label: "SMTP Port", type: "number", min: 1, max: 65535, tip: "Port for the mail server. Usually 587 with SSL on." },
        { name: "enableSsl", label: "Enable SSL", type: "checkbox", tip: "Encrypt the connection to the mail server. Leave on unless your server can't do it." },
        { name: "username", label: "Username", type: "text", tip: "Account used to log in to the mail server, usually the full email address." },
        { name: "appPassword", label: "App Password", type: "password", tip: "For Gmail this is an app password, not your normal one. Regular passwords are refused." },
        { name: "fromAddress", label: "From Address", type: "text", tip: "Address the alert appears to come from." },
        { name: "fromName", label: "From Name", type: "text", tip: "Display name shown as the sender, e.g. Sentinel Core." },
        { name: "toAddress", label: "To Address", type: "text", tip: "Where alerts are sent." },
        { name: "duplicateCooldownMinutes", label: "Duplicate Cooldown (min)", type: "number", min: 0, max: 1440, tip: "Suppress repeat emails about the same kind of event for this long, so one visitor doesn't fill your inbox." },
        { name: "minimumConfidence", label: "Minimum Confidence", type: "number", min: 0, max: 1, step: 0.05, tip: "How sure the AI must be before emailing. Raise it if you get false alarms, lower it if real events slip through." },
        { name: "attachImage", label: "Attach Image", type: "checkbox", tip: "Include the snapshot in the email rather than text only." }
      ]
    },
    {
      key: "dailySummary", title: "DAILY SUMMARY", endpoint: "/api/settings/daily-summary",
      basic: true,
      description: "A twice-daily email digest of everything the cameras saw. This is the “what did I miss” roundup rather than an urgent alert, and it arrives even on quiet days. Needs Email above to be working first.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Email a digest of everything that happened, instead of only alerting on notable events." },
        { name: "morningHour", label: "Morning Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour of day the morning digest is sent, in 24-hour time. 7 means 7am." },
        { name: "eveningHour", label: "Evening Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour of day the evening digest is sent, in 24-hour time. 19 means 7pm." },
        { name: "maxImagesAttached", label: "Max Images Attached", type: "number", min: 0, max: 50, tip: "Cap on how many snapshots ride along in the digest, to keep the email a sane size." }
      ]
    },
    {
      key: "telegram", title: "TELEGRAM ALERTS", endpoint: "/api/settings/telegram",
      basic: true,
      description: "Sends alerts to your phone through a Telegram bot - usually faster and harder to miss than email. You'll need to create a bot by messaging @BotFather in Telegram, then paste the token it gives you here.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Push alerts to your phone through a Telegram bot." },
        { name: "botToken", label: "Bot Token", type: "password", tip: "The token BotFather gave you when you created the bot." },
        { name: "chatId", label: "Chat ID", type: "text", tip: "Which Telegram conversation to send to. This is the only chat the bot will talk to." },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox", tip: "Only message when the AI judges the event worth your attention, rather than for every single one." },
        { name: "minimumConfidence", label: "Minimum Confidence", type: "number", min: 0, max: 1, step: 0.05, tip: "How sure the AI must be before messaging you." },
        { name: "attachImage", label: "Attach Image", type: "checkbox", tip: "Send the snapshot with the message rather than text only." },
        { name: "duplicateCooldownMinutes", label: "Duplicate Cooldown (min)", type: "number", min: 0, max: 1440, tip: "Suppress repeat Telegram messages about the same kind of event for this long, so one vehicle sitting at the gate doesn't message you over and over. 0 disables this." }
      ]
    },
    {
      key: "telegramChat", title: "TELEGRAM CHAT", endpoint: "/api/settings/telegram-chat",
      basic: false,
      description: "Lets you text the bot questions like “what happened last night?” and get an answer from your camera history. This also powers the ask box on the Feed tab, so turning it off here turns off both.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Let you message the bot and ask questions about recent gate activity." },
        { name: "lookbackDays", label: "Lookback Days", type: "number", min: 1, max: 30, tip: "How far back the bot looks when answering a question." },
        { name: "maxEventsInContext", label: "Max Events In Context", type: "number", min: 1, max: 500, tip: "How many events the bot can consider at once. Higher gives fuller answers but is slower." },
        { name: "model", label: "Model", type: "text", tip: "Which Ollama model answers your questions. Can differ from the vision model used for images." }
      ]
    },
    {
      key: "discord", title: "DISCORD ALERTS", endpoint: "/api/settings/discord",
      basic: true,
      description: "Posts alert cards and snapshots to a Discord channel using an incoming webhook. In Discord, open the channel settings, choose Integrations, create a Webhook, and paste its URL here. No bot application or public server is needed.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Post camera alerts to a Discord channel." },
        { name: "webhookUrl", label: "Webhook URL", type: "password", tip: "The Discord channel webhook URL. Treat it like a password: anyone with it can post to that channel." },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox", tip: "Only post when the AI judges the event worth your attention." },
        { name: "minimumConfidence", label: "Minimum Confidence", type: "number", min: 0, max: 1, step: 0.05, tip: "How sure the AI must be before posting the alert." },
        { name: "attachImage", label: "Attach Image", type: "checkbox", tip: "Include the trigger snapshot in the Discord alert card." },
        { name: "duplicateCooldownMinutes", label: "Duplicate Cooldown (min)", type: "number", min: 0, max: 1440, tip: "Suppress repeat posts for the same camera and event category during this window. 0 disables this." }
      ]
    },
    {
      key: "blueIris", title: "BLUE IRIS MEDIA", endpoint: "/api/settings/blue-iris",
      basic: false,
      description: "Connects Sentinel to Blue Iris for on-demand live snapshots (used by the Telegram bot's /photo command). Sentinel's own Timeline uses its own recorded trigger clips instead and does not need Blue Iris to be running.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Allow on-demand snapshots through Blue Iris." },
        { name: "baseUrl", label: "Blue Iris URL", type: "text", tip: "The Blue Iris web server URL reachable from Sentinel Core, including its port." },
        { name: "camera", label: "Camera Short Name", type: "text", tip: "The camera's short name in Blue Iris, e.g. gateCamera. Must match exactly." },
        { name: "username", label: "Username", type: "text", tip: "Blue Iris login used to request the snapshot." },
        { name: "password", label: "Password", type: "password", tip: "Password for the Blue Iris account used by Core; it is never sent to a mobile client." }
      ]
    },
    {
      key: "multiFrameAnalysis", title: "MULTI-FRAME ANALYSIS", endpoint: "/api/settings/multi-frame-analysis",
      basic: false,
      description: "Off by default. When on, a trigger zone that sends to the alert pipeline captures 2-3 frames a fraction of a second apart instead of one, and the AI is told to use consistency across them to ignore a shadow, reflection, headlight sweep, or insect near the lens that only shows up in a single frame. Only applies to your own added cameras' trigger zones - never Blue Iris alerts, which always send one image. This is a real cost, not just an extra option: it multiplies AI processing time per alert, and a single image can already be a meaningful load on modest local hardware - leave this off unless a single image already analyzes comfortably fast for you.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Capture and analyze multiple frames per trigger instead of one. Increases AI processing time/cost per alert." },
        { name: "extraFrameCount", label: "Extra Frames (1-2)", type: "number", min: 1, max: 2, tip: "How many additional frames to capture beyond the one at the trigger moment. Total frames sent to the AI = 1 + this." },
        { name: "frameIntervalMs", label: "Frame Interval (ms)", type: "number", min: 150, max: 1000, tip: "How far apart, in milliseconds, each captured frame is." }
      ]
    },
    {
      key: "packageTheftWatch", title: "PACKAGE THEFT WATCH", endpoint: "/api/settings/package-theft-watch",
      basic: false,
      description: "Off by default. When on, an alert showing a package delivered or already present starts a watch on that camera. If a later alert on the same camera shows the package gone and whoever's there isn't a recognized household face or vehicle, it's flagged as suspected theft and forced to notify even if it otherwise wouldn't have. This adds no AI processing cost - it only reuses package/face/vehicle detection your other settings already produce - but it's a heuristic guess, not proof: a stranger picking it up isn't necessarily a thief, and this can only catch a removal that itself trips a new motion alert on that camera. A package taken with no interceding motion event won't be caught.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Watch for packages disappearing without a recognized pickup, and force a notification when suspected." },
        { name: "watchWindowHours", label: "Watch Window (hours)", type: "number", min: 1, max: 72, tip: "How long after a package is seen delivered the watch stays open. Most legitimate pickups happen same-day." }
      ]
    },
    {
      key: "semanticSearch", title: "SEMANTIC SEARCH", endpoint: "/api/settings/semantic-search",
      basic: false,
      description: "Off by default. When on, each alert's image is run through a local CLIP model that turns it into a vector capturing what's actually in the photo, so the Feed's search box can find events by visual content (\"red truck\", \"person in a blue jacket\") instead of only matching words already in the AI's saved description. This downloads a ~577MB model to this machine the first time it's used, and adds a per-alert cost similar to Face Recognition - a real cost on modest hardware, not just an extra toggle. Searching itself stays fast regardless of history size, since only the one-time-per-alert encoding is expensive.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Encode each alert's image for visual search. Downloads a one-time ~577MB model on first use and adds per-alert processing time." },
        { name: "minimumSimilarity", label: "Minimum Similarity", type: "number", min: 0, max: 1, step: 0.01, tip: "How close a visual match must be to appear in results. Lower finds more (including weaker matches); higher shows only close matches." }
      ]
    },
    {
      key: "selfMonitoring", title: "SELF-MONITORING", endpoint: "/api/settings/self-monitoring",
      basic: false,
      description: "Watches the app's own health and warns you if it stops keeping up - the AI going unresponsive, or alerts backing up faster than they're processed. The defaults are sensible; these numbers decide how quickly it calls something genuinely broken rather than briefly slow.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Watch the bridge's own health and tell you if it stops working properly." },
        { name: "checkIntervalSeconds", label: "Check Interval (s)", type: "number", min: 10, max: 3600, tip: "How often to run the health check." },
        { name: "consecutiveFailuresBeforeAlert", label: "Consecutive Failures Before Alert", type: "number", min: 1, max: 20, tip: "How many checks must fail in a row before you're told, so one hiccup doesn't wake you." },
        { name: "queueBacklogThreshold", label: "Queue Backlog Threshold", type: "number", min: 1, max: 10000, tip: "How many unprocessed alerts count as falling behind. Usually means the AI can't keep up." },
        { name: "consecutiveBacklogChecksBeforeAlert", label: "Consecutive Backlog Checks Before Alert", type: "number", min: 1, max: 20, tip: "How many checks in a row must show a backlog before alerting, ignoring brief bursts." },
        { name: "notifyOnRecovery", label: "Notify On Recovery", type: "checkbox", tip: "Also tell you when things go back to normal, not just when they break." }
      ]
    },
    {
      key: "startupNotification", title: "OUTAGE ALERTS", endpoint: "/api/settings/startup-notification",
      basic: true,
      description: "Emails you when the server comes back after being off - so a power cut doesn't go unnoticed until you next check the feed. The email goes to the same address as your alerts, and says how long it was down and whether it was shut down on purpose or lost power. Note this can only tell you it came back: if the machine stays off, nothing is running to send the email.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Email you on startup when the server has been down longer than the threshold below." },
        { name: "minimumDowntimeMinutes", label: "Minimum Downtime (min)", type: "number", min: 1, max: 1440, tip: "Ignore outages shorter than this. Stops routine restarts and updates, which take seconds, from emailing you every time." },
        { name: "heartbeatIntervalSeconds", label: "Heartbeat Interval (s)", type: "number", min: 10, max: 3600, tip: "How often the server records that it's alive. Also how precise the reported downtime is - the real outage can be up to this much longer than the email says." }
      ]
    },
    {
      key: "internetOutage", title: "INTERNET OUTAGE ALERTS", endpoint: "/api/settings/internet-outage",
      basic: true,
      description: "Off by default. When on, the server checks every so often that it can still reach the internet, and tells you once the connection comes back — by email and Telegram, with how long you were offline. Like the outage alerts above, it can only report this afterwards: while the line is down nothing can be sent to anyone. Leave this off if this machine is deliberately kept off the internet.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Watch the internet connection and tell you, once it's back, how long it was down." },
        { name: "checkIntervalSeconds", label: "Check Interval (s)", type: "number", min: 10, max: 3600, tip: "How often to check that the internet is reachable." },
        { name: "consecutiveFailuresBeforeAlert", label: "Consecutive Failures Before Outage", type: "number", min: 1, max: 20, tip: "How many checks must fail in a row before it counts as a real outage, so a momentary blip is ignored." },
        { name: "minimumOutageMinutes", label: "Minimum Outage (min)", type: "number", min: 1, max: 1440, tip: "Ignore outages shorter than this, so a brief drop while you were asleep doesn't notify you." },
        { name: "probeTimeoutSeconds", label: "Probe Timeout (s)", type: "number", min: 1, max: 60, tip: "How long to wait for each address to answer before treating it as unreachable. Every address has to time out before a check counts as failed, so keep this times the number of addresses well under the check interval." },
        { name: "probeUrls", label: "Probe Addresses (comma separated)", type: "csv", tip: "Addresses used to prove the connection is alive. Any reply at all counts, and only failing every one of them counts as an outage. Leave empty to use the built-in list." }
      ]
    },
    {
      key: "quietHours", title: "QUIET HOURS", endpoint: "/api/settings/quiet-hours",
      basic: true,
      description: "During hours you choose, the AI has to be more confident before it wakes you. It never fully silences anything - something clearly worth knowing about still gets through at 3am - and it has no effect on the Feed or the daily summary.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "During the window below, raise the confidence bar for Email and Telegram alerts so only confident detections still ping you. Nothing is ever fully muted, and the Feed and daily digest are unaffected." },
        { name: "startHour", label: "Start Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour the quiet window begins, in 24-hour time. 22 means 10pm." },
        { name: "endHour", label: "End Hour (0-23)", type: "number", min: 0, max: 23, tip: "Hour the quiet window ends, in 24-hour time. 6 means 6am. Can be earlier than Start Hour to span midnight, e.g. 22 to 6." },
        { name: "minimumConfidence", label: "Minimum Confidence During Window", type: "number", min: 0, max: 1, step: 0.05, tip: "How sure the AI must be to still alert during quiet hours. Only used if it's higher than the channel's own Minimum Confidence in Email/Telegram settings - it can raise the bar, never lower it." }
      ]
    },
    {
      key: "webhook", title: "WEBHOOK", endpoint: "/api/settings/webhook",
      basic: false,
      description: "Sends each analysed event to another system as JSON, for home automation, logging, or anything you're building yourself. Only useful if you have somewhere specific to send it.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "POST each analysed event to another system, for home automation or logging." },
        { name: "url", label: "URL", type: "text", tip: "Address the event data is posted to." },
        { name: "apiKeyHeaderName", label: "API Key Header Name", type: "text", tip: "Name of the header the key is sent in, e.g. X-API-Key. Whatever the receiving end expects." },
        { name: "apiKey", label: "API Key", type: "password", tip: "Secret sent in that header so the receiving end knows it's you." },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox", tip: "Only post events the AI flagged as notable, rather than every one." }
      ]
    },
    {
      key: "mqtt", title: "MQTT / HOME ASSISTANT", endpoint: "/api/settings/mqtt",
      basic: false,
      description: "Publishes events over MQTT so Home Assistant, or anything else that speaks MQTT, can see your cameras. Home Assistant picks them up automatically with no manual YAML. If you don't already run a broker this app can be one; if you don't use Home Assistant, skip this.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Publish every analysed event over MQTT, with Home Assistant auto-discovery so cameras show up as devices with no manual setup on the Home Assistant side." },
        { name: "runEmbeddedBroker", label: "Run Built-In Broker", type: "checkbox", tip: "Have this app act as its own MQTT broker on the port below, so you don't need a separate one (like Mosquitto) running anywhere. Leave off to connect to a broker you already have - e.g. Home Assistant's own Mosquitto add-on. Needs a restart to take effect if changed." },
        { name: "embeddedBrokerPort", label: "Built-In Broker Port", type: "number", min: 1, max: 65535, tip: "Port the built-in broker listens on, if enabled above. Point other MQTT clients (including this app's own publisher) at this host on this port." },
        { name: "brokerHost", label: "Broker Host", type: "text", tip: "Address of the MQTT broker to connect to. Ignored if Run Built-In Broker is on - this app connects to its own broker instead." },
        { name: "brokerPort", label: "Broker Port", type: "number", min: 1, max: 65535, tip: "Port of the external broker. 1883 is the standard unencrypted MQTT port." },
        { name: "username", label: "Username", type: "text", tip: "Leave blank if the broker allows anonymous connections (Mosquitto's default is often anonymous on a LAN)." },
        { name: "password", label: "Password", type: "password", tip: "Only needed if the broker requires a username above." },
        { name: "useTls", label: "Use TLS", type: "checkbox", tip: "Encrypt the connection to an external broker. Leave off for a local/LAN broker without TLS configured." },
        { name: "topicPrefix", label: "Topic Prefix", type: "text", tip: "Root of every topic this app publishes, e.g. 'sentinelcore' produces sentinelcore/gateCamera/event/state." },
        { name: "homeAssistantDiscovery", label: "Home Assistant Discovery", type: "checkbox", tip: "Automatically publish Home Assistant's MQTT discovery config so each camera appears as a device with its own entities - no manual YAML or UI entity setup needed." },
        { name: "homeAssistantDiscoveryPrefix", label: "Discovery Prefix", type: "text", tip: "Must match the discovery prefix configured in Home Assistant's own MQTT integration. 'homeassistant' is Home Assistant's default - only change this if you changed it there too." },
        { name: "onlyWhenNotificationRecommended", label: "Only When Notification Recommended", type: "checkbox", tip: "Only publish events the AI flagged as notable. Leave off if you want every event in Home Assistant to build your own history or automations from." }
      ]
    },
    {
      key: "deviceHub", title: "DEVICE HUB", endpoint: "/api/settings/device-hub",
      basic: false,
      description: "Turns the MQTT connection above into two-way control: a DEVICE HUB tab appears where you can switch lights, plugs, fans, and anything else on your broker on and off, from here or from the phone, Mac, and Apple TV apps. Requires the MQTT section above to be turned on first - that's where the broker is configured. Use the tab's discover button to find what's already on your broker rather than typing topics by hand.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Show the DEVICE HUB tab and start watching your devices' MQTT topics. Requires MQTT above to be enabled - saving this on with MQTT off is refused, since there'd be no broker to talk to." },
        { name: "stateTimeoutSeconds", label: "State Timeout (s)", type: "number", min: 10, max: 86400, tip: "For devices with no Availability Topic only: how long one can go silent before it's shown as unavailable. A device that does have an Availability Topic ignores this entirely - what that topic says is trusted instead, so a lamp that's simply been on all day is never wrongly marked offline." },
        { name: "discoverySeconds", label: "Discovery Scan Seconds", type: "number", min: 2, max: 60, tip: "How long the discover button listens to the broker. Devices that announce themselves for Home Assistant appear instantly; chattier ones need long enough to say something, so raise this if a device you know is there doesn't turn up." },
        { name: "commandTimeoutSeconds", label: "Command Confirm Timeout (s)", type: "number", min: 1, max: 60, tip: "How long to wait for a device to confirm a command before the tile says 'not confirmed'. The command is still sent either way - this only affects how long the UI waits to hear back." }
      ]
    },
    {
      key: "bugReports", title: "BUG REPORTS", endpoint: "/api/settings/bug-reports",
      basic: true,
      description: "Emails the developer when something breaks inside the app, using the email account configured above. Off by default - nothing is ever sent unless you turn it on.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Email the developer when something in the bridge or dashboard hits an internal error, using the same account configured under Email above. Goes to a fixed developer address, not one you set - there's nothing else to configure here." }
      ]
    },
    {
      key: "loginAlerts", title: "LOGIN ALERTS", endpoint: "/api/settings/login-alerts",
      basic: true,
      description: "Emails and/or Telegrams you when someone enters the wrong password for this dashboard, using the accounts configured under Email and Telegram above. Off by default. One alert is sent per source IP per cooldown window, no matter how many usernames or passwords are tried against it, so a brute-force burst doesn't flood you.",
      fields: [
        { name: "enabled", label: "Enabled", type: "checkbox", tip: "Alert you by Email and/or Telegram when someone fails to log in to this dashboard, using the accounts configured under Email and Telegram above." },
        { name: "cooldownMinutes", label: "Cooldown (min)", type: "number", min: 1, max: 1440, tip: "After alerting on a failed login from a given address, suppress further alerts from that same address for this many minutes - so one attacker hammering the login page sends one alert, not one per attempt." }
      ]
    }
  ];

  const WIDE_TYPES = new Set(["checkbox", "password", "csv"]);

  const SECTION_TIPS = {
    bridge: "How alerts arrive from Blue Iris, and how long they're kept.",
    ollama: "The local AI model that looks at each snapshot, and how it's tuned.",
    cloudVision: "An offsite AI model (OpenAI, Anthropic, or Google) that can analyse alerts instead of, or as a backup for, the local Ollama model.",
    fastLocal: "A lightweight person/vehicle/animal detector for hardware too slow to run a full AI model - sub-second answers on a plain CPU.",
    visionRouting: "Which vision backend handles alerts first, plus fallback on failure and escalation of fast detections to a full AI.",
    imageProcessing: "Resizing snapshots before the AI sees them, to speed up analysis.",
    email: "Email alerts when something notable happens.",
    dailySummary: "A twice-daily digest of everything that happened, separate from individual alerts.",
    telegram: "Push alerts to your phone through a Telegram bot.",
    telegramChat: "Lets you message the bot to ask questions about recent activity.",
    discord: "Post alert cards and trigger snapshots to a Discord channel through a webhook.",
    blueIris: "On-demand Blue Iris snapshots for the Telegram bot. Sentinel's Timeline uses its own local trigger clips instead.",
    multiFrameAnalysis: "Analyze 2-3 frames per trigger instead of one, to reduce false alarms from shadows/reflections. Off by default - costs more AI processing time per alert.",
    packageTheftWatch: "Flags a package that disappears without a recognized pickup, and forces a notification. Off by default - no extra AI cost, but a heuristic guess, not proof.",
    semanticSearch: "Search the Feed by visual content (\"red truck\") instead of only saved text. Off by default - ~577MB one-time model download, plus per-alert processing time.",
    selfMonitoring: "Watches the bridge itself and warns you if it stops keeping up.",
    quietHours: "Raise the bar for Email, Telegram, and Discord alerts during a window (e.g. overnight) without ever fully muting them.",
    webhook: "Forward each analysed event to another system.",
    mqtt: "Publish events over MQTT with Home Assistant auto-discovery, optionally using this app as its own broker.",
    deviceHub: "Adds a DEVICE HUB tab for switching lights, plugs, and other MQTT devices on and off. Needs MQTT above turned on first.",
    bugReports: "Email the developer when something breaks internally, using the Email account above.",
    loginAlerts: "Email and/or Telegram you when someone enters the wrong password for this dashboard.",
    internetExposed: "A second, dedicated port for reaching this dashboard from outside your home network (e.g. through a Cloudflare Tunnel) - always requires login and HTTPS, regardless of the Login/HTTPS settings above."
  };

  // Not part of the bulk /api/settings snapshot - it configures this project's own Kestrel
  // binding (same reason LOGIN/HTTPS aren't either), so it fetches its own data. Rendered
  // through the same generic renderSection() as the schema-driven sections above; only the data
  // source differs.
  const INTERNET_EXPOSED_SCHEMA = {
    key: "internetExposed", title: "INTERNET EXPOSED MODE", endpoint: "/api/settings/internet-exposed",
    basic: false,
    description: "For reaching this dashboard from outside your home network - e.g. through a Cloudflare Tunnel. When enabled, this server listens on a SECOND port that always requires login and only accepts HTTPS connections, no matter how Login and HTTPS above are configured. The normal port for local network use is completely unaffected by this - point your tunnel or reverse proxy at the new port instead, not the regular one. Requires local HTTPS to already be configured (see docs/DEPLOYMENT.md §14a) and at least one account to already exist. Takes effect after a restart.",
    fields: [
      { name: "enabled", label: "Enabled", type: "checkbox", tip: "Turn on the dedicated internet-exposed port. Requires HTTPS to already be configured and at least one account to exist. Needs a restart to take effect." },
      { name: "port", label: "Port", type: "number", min: 1, max: 65535, tip: "Port for internet-facing traffic. Point your Cloudflare Tunnel (or other reverse proxy) here instead of the normal port. Must be different from the HTTPS port. Needs a restart to take effect." }
    ]
  };

  function buildField(field, value) {
    const row = document.createElement("div");
    row.className = "field-row" + (WIDE_TYPES.has(field.type) ? " field-row-wide" : "");
    // The tip lives on the row because <input> can't render the ::after pseudo-element.
    if (field.tip) row.dataset.tip = field.tip;

    if (field.type === "checkbox") {
      row.classList.add("field-checkbox-row");
      const label = document.createElement("label");
      label.className = "switch";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.checked = !!value;
      input.dataset.field = field.name;
      const track = document.createElement("span");
      track.className = "switch-track";
      label.append(input, track);
      const text = document.createElement("span");
      text.className = "field-label";
      text.textContent = field.label;
      row.append(label, text);
      return row;
    }

    const label = document.createElement("label");
    label.className = "field-label";
    label.textContent = field.label;
    row.appendChild(label);

    if (field.type === "password") {
      const wrap = document.createElement("div");
      wrap.className = "field-password-row";
      const input = document.createElement("input");
      input.type = "password";
      input.className = "field-input";
      input.value = value ?? "";
      input.dataset.field = field.name;
      input.autocomplete = "off";
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "field-toggle-btn";
      toggle.textContent = "show";
      toggle.setAttribute("data-tip-right", "");
      toggle.dataset.tip = "Reveal this secret so you can check it.";
      toggle.addEventListener("click", () => {
        input.type = input.type === "password" ? "text" : "password";
        toggle.textContent = input.type === "password" ? "show" : "hide";
      });
      wrap.append(input, toggle);
      row.appendChild(wrap);
      return row;
    }

    if (field.type === "csv") {
      const input = document.createElement("input");
      input.type = "text";
      input.className = "field-input";
      input.value = Array.isArray(value) ? value.join(", ") : "";
      input.dataset.field = field.name;
      row.appendChild(input);
      return row;
    }

    if (field.type === "select") {
      const select = document.createElement("select");
      select.className = "field-input";
      select.dataset.field = field.name;
      for (const option of field.options) {
        const opt = document.createElement("option");
        opt.value = option.value;
        opt.textContent = option.label;
        if (option.value === value) opt.selected = true;
        select.appendChild(opt);
      }
      row.appendChild(select);
      return row;
    }

    const input = document.createElement("input");
    input.type = field.type === "number" ? "number" : "text";
    input.className = "field-input";
    input.value = value ?? "";
    input.dataset.field = field.name;
    if (field.type === "number") {
      if (field.min !== undefined) input.min = field.min;
      if (field.max !== undefined) input.max = field.max;
      input.step = field.step !== undefined ? field.step : "any";
    }
    row.appendChild(input);
    return row;
  }

  // Which of the six hand-written sections belong in basic mode. The schema sections carry
  // their own `basic` flag; these don't have a schema, so they're listed here instead.
  const BASIC_CUSTOM_SECTIONS = new Set(["APPLE HOMEKIT", "BACKUP", "LOGIN", "LICENSE", "SYSTEM HEALTH"]);

  // Advanced is opt-in and remembered per device, so the owner can run advanced on a desktop
  // while someone else's phone stays simple. Purely a display preference - nothing here is
  // security-relevant, and every setting is still reachable either way.
  const ADVANCED_MODE_KEY = "gatewatch.settings.advanced";

  function isAdvancedMode() {
    try {
      return localStorage.getItem(ADVANCED_MODE_KEY) === "true";
    } catch {
      // Private browsing and some locked-down setups throw on access rather than returning
      // null. Falling back to basic is the safe default.
      return false;
    }
  }

  function setAdvancedMode(advanced) {
    try {
      localStorage.setItem(ADVANCED_MODE_KEY, advanced ? "true" : "false");
    } catch {
      // Preference just won't persist; the toggle still works for this visit.
    }
  }

  function applyMode(advanced) {
    const root = document.getElementById("settings-root");
    for (const section of root.querySelectorAll(".settings-section")) {
      section.classList.toggle("hidden", !advanced && section.dataset.basic !== "true");
    }

    // Whatever was open may now be hidden, so open the first section actually on screen -
    // otherwise basic mode can load with everything collapsed.
    const visible = [...root.querySelectorAll(".settings-section")].filter(s => !s.classList.contains("hidden"));
    if (visible.length > 0 && !visible.some(s => s.classList.contains("open"))) {
      visible[0].classList.add("open");
    }

    const label = document.getElementById("mode-label");
    if (label) label.textContent = advanced ? "advanced" : "basic";

    // The MutationObserver in app.js only watches childList, so a class-only change like this
    // won't reposition tooltips on its own.
    window.GateWatchTooltips.schedule();
  }

  // Real text rather than a tooltip: the per-field tips are hover-only by design (so a tap
  // doesn't leave one stuck open), which means they're invisible on a phone. This paragraph is
  // what actually orients someone, on any device.
  function buildSectionIntro(text) {
    const intro = document.createElement("p");
    intro.className = "settings-hint settings-section-intro";
    intro.textContent = text;
    return intro;
  }

  function renderSection(schema, data) {
    const section = document.createElement("div");
    section.className = "settings-section";
    if (schema.basic) section.dataset.basic = "true";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">${schema.title}</span><span class="settings-section-caret">&#9656;</span>`;
    if (SECTION_TIPS[schema.key]) header.dataset.tip = SECTION_TIPS[schema.key];
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";

    if (schema.description) body.appendChild(buildSectionIntro(schema.description));

    const grid = document.createElement("div");
    grid.className = "field-grid";
    for (const field of schema.fields) {
      grid.appendChild(buildField(field, data ? data[field.name] : undefined));
    }
    body.appendChild(grid);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save this section. Changes take effect immediately - no restart needed.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn);
    if (schema.extraAction) {
      const actionBtn = document.createElement("button");
      actionBtn.className = "btn";
      actionBtn.textContent = schema.extraAction.label;
      if (schema.extraAction.tip) actionBtn.dataset.tip = schema.extraAction.tip;
      actionBtn.addEventListener("click", () => schema.extraAction.onClick());
      footer.append(actionBtn);
    }
    footer.append(errors);
    body.appendChild(footer);

    saveBtn.addEventListener("click", () => saveSection(schema, section, errors, saveBtn));

    section.append(header, body);
    return section;
  }

  async function saveSection(schema, section, errorsEl, saveBtn) {
    const data = {};
    for (const field of schema.fields) {
      const input = section.querySelector(`[data-field="${field.name}"]`);
      if (!input) continue;
      if (field.type === "checkbox") data[field.name] = input.checked;
      else if (field.type === "number") data[field.name] = input.value === "" ? 0 : Number(input.value);
      else if (field.type === "csv") data[field.name] = input.value.split(",").map(s => s.trim()).filter(s => s.length > 0);
      else data[field.name] = input.value;
    }

    await submit(schema.endpoint, data, schema.title, errorsEl, saveBtn);
  }

  function buildQuestionRow(value) {
    const row = document.createElement("div");
    row.className = "question-row";
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.value = value;
    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = "Remove this question from the profile.";
    removeBtn.addEventListener("click", () => row.remove());
    row.append(input, removeBtn);
    return row;
  }

  function buildProfileCard(name, profile, options) {
    profile = profile || { displayName: "", systemPrompt: "", questions: [], crop: null };
    const startExpanded = !!(options && options.startExpanded);
    const card = document.createElement("div");
    card.className = "profile-card" + (startExpanded ? " open" : "");
    card.dataset.searchText = `${profile.displayName || ""} ${name}`.toLowerCase();

    const header = document.createElement("div");
    header.className = "profile-card-header";
    header.dataset.tip = "Click to expand and edit this profile.";

    const caret = document.createElement("span");
    caret.className = "profile-card-caret";
    caret.textContent = "▸";

    const summary = document.createElement("span");
    summary.className = "profile-card-summary";
    summary.textContent = profile.displayName || name || "(untitled profile)";

    const summaryKey = document.createElement("span");
    summaryKey.className = "profile-card-summary-key";
    summaryKey.textContent = name;

    const deleteBtn = document.createElement("button");
    deleteBtn.type = "button";
    deleteBtn.className = "icon-btn";
    deleteBtn.textContent = "✕";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Delete this whole profile. The 'default' profile is required and can't be removed.";
    deleteBtn.addEventListener("click", e => {
      e.stopPropagation();
      card.remove();
    });

    header.append(caret, summary, summaryKey, deleteBtn);
    header.addEventListener("click", () => {
      card.classList.toggle("open");
      caret.textContent = card.classList.contains("open") ? "▾" : "▸";
    });

    const body = document.createElement("div");
    body.className = "profile-card-body";

    const displayNameRow = document.createElement("div");
    displayNameRow.className = "field-row";
    displayNameRow.dataset.tip = "Friendly label shown in this list and in the trigger-zone profile picker. Optional - falls back to the technical name below.";
    const displayNameLabel = document.createElement("label");
    displayNameLabel.className = "field-label";
    displayNameLabel.textContent = "Display Name";
    const displayNameInput = document.createElement("input");
    displayNameInput.type = "text";
    displayNameInput.className = "field-input";
    displayNameInput.value = profile.displayName || "";
    displayNameInput.placeholder = "e.g. Chicken Coop Predator Watch";
    displayNameInput.addEventListener("input", () => {
      summary.textContent = displayNameInput.value.trim() || nameInput.value.trim() || "(untitled profile)";
      card.dataset.searchText = `${displayNameInput.value} ${nameInput.value}`.toLowerCase();
    });
    displayNameRow.append(displayNameLabel, displayNameInput);

    const nameRow = document.createElement("div");
    nameRow.className = "field-row";
    nameRow.dataset.tip = "Technical name. Blue Iris and camera profile pickers select a profile by this name, and 'default' is used for anything that doesn't match.";
    const nameLabel = document.createElement("label");
    nameLabel.className = "field-label";
    nameLabel.textContent = "Name (technical)";
    const nameInput = document.createElement("input");
    nameInput.type = "text";
    nameInput.className = "field-input profile-name-input";
    nameInput.value = name;
    nameInput.placeholder = "profile-name";
    nameInput.addEventListener("input", () => {
      summaryKey.textContent = nameInput.value.trim();
      if (!displayNameInput.value.trim()) summary.textContent = nameInput.value.trim() || "(untitled profile)";
      card.dataset.searchText = `${displayNameInput.value} ${nameInput.value}`.toLowerCase();
    });
    nameRow.append(nameLabel, nameInput);

    const promptRow = document.createElement("div");
    promptRow.className = "field-row";
    promptRow.dataset.tip = "Instructions sent to the AI with every image from this camera. Sets what it looks for and how it should reply.";
    const promptLabel = document.createElement("label");
    promptLabel.className = "field-label";
    promptLabel.textContent = "System Prompt";
    const promptArea = document.createElement("textarea");
    promptArea.className = "field-textarea";
    promptArea.rows = 4;
    promptArea.value = profile.systemPrompt || "";
    promptRow.append(promptLabel, promptArea);

    const questionsWrap = document.createElement("div");
    questionsWrap.className = "field-row";
    questionsWrap.dataset.tip = "Specific things to ask about each image. The answers become the structured fields you see on the Feed tab.";
    const qLabel = document.createElement("label");
    qLabel.className = "field-label";
    qLabel.textContent = "Questions";
    const qList = document.createElement("div");
    qList.className = "questions-list";
    (profile.questions || []).forEach(q => qList.appendChild(buildQuestionRow(q)));
    const addQBtn = document.createElement("button");
    addQBtn.type = "button";
    addQBtn.className = "icon-btn add";
    addQBtn.textContent = "+";
    addQBtn.dataset.tip = "Add another question for the AI to answer about each image.";
    addQBtn.addEventListener("click", () => qList.appendChild(buildQuestionRow("")));
    questionsWrap.append(qLabel, qList, addQBtn);

    const cropWrap = document.createElement("div");
    cropWrap.className = "field-row";
    cropWrap.dataset.tip = "Send only part of the image to the AI. Useful for cutting out a road or sky so the model focuses on the area you care about.";
    const cropToggleRow = document.createElement("div");
    cropToggleRow.className = "field-checkbox-row";
    const cropSwitch = document.createElement("label");
    cropSwitch.className = "switch";
    const cropInput = document.createElement("input");
    cropInput.type = "checkbox";
    cropInput.checked = !!profile.crop;
    const cropTrack = document.createElement("span");
    cropTrack.className = "switch-track";
    cropSwitch.append(cropInput, cropTrack);
    const cropText = document.createElement("span");
    cropText.className = "field-label";
    cropText.textContent = "Use crop region";
    cropToggleRow.append(cropSwitch, cropText);

    const cropGrid = document.createElement("div");
    cropGrid.className = "crop-grid";
    cropGrid.style.display = profile.crop ? "grid" : "none";
    const crop = profile.crop || { left: 0, top: 0, right: 1, bottom: 1 };
    for (const key of ["left", "top", "right", "bottom"]) {
      const r = document.createElement("div");
      r.className = "field-row";
      const l = document.createElement("label");
      l.className = "field-label";
      l.textContent = key;
      const i = document.createElement("input");
      i.type = "number";
      i.className = "field-input";
      i.dataset.cropField = key;
      i.min = 0;
      i.max = 1;
      i.step = 0.01;
      i.value = crop[key] ?? (key === "right" || key === "bottom" ? 1 : 0);
      r.append(l, i);
      cropGrid.appendChild(r);
    }
    cropInput.addEventListener("change", () => {
      cropGrid.style.display = cropInput.checked ? "grid" : "none";
    });
    cropWrap.append(cropToggleRow, cropGrid);

    const plateWrap = document.createElement("div");
    plateWrap.className = "field-row field-checkbox-row";
    plateWrap.dataset.tip = "Also ask the AI to transcribe a vehicle's license plate when clearly legible, and check it against the Known Vehicles list below. Off by default - needs a camera with enough resolution and angle to actually read plates, and never affects whether an alert fires.";
    const plateSwitch = document.createElement("label");
    plateSwitch.className = "switch";
    const plateInput = document.createElement("input");
    plateInput.type = "checkbox";
    plateInput.checked = !!profile.licensePlateRecognitionEnabled;
    const plateTrack = document.createElement("span");
    plateTrack.className = "switch-track";
    plateSwitch.append(plateInput, plateTrack);
    const plateText = document.createElement("span");
    plateText.className = "field-label";
    plateText.textContent = "License plate recognition";
    plateWrap.append(plateSwitch, plateText);

    const faceWrap = document.createElement("div");
    faceWrap.className = "field-row field-checkbox-row";
    faceWrap.dataset.tip = "Also run local face detection/matching against the Known Faces list below, entirely on-device (no image is sent anywhere for this). Off by default - never affects whether an alert fires.";
    const faceSwitch = document.createElement("label");
    faceSwitch.className = "switch";
    const faceInput = document.createElement("input");
    faceInput.type = "checkbox";
    faceInput.checked = !!profile.faceRecognitionEnabled;
    const faceTrack = document.createElement("span");
    faceTrack.className = "switch-track";
    faceSwitch.append(faceInput, faceTrack);
    const faceText = document.createElement("span");
    faceText.className = "field-label";
    faceText.textContent = "Face recognition";
    faceWrap.append(faceSwitch, faceText);

    body.append(displayNameRow, nameRow, promptRow, questionsWrap, cropWrap, plateWrap, faceWrap);
    card.append(header, body);
    card._fields = { displayNameInput, nameInput, promptArea, qList, cropInput, cropGrid, plateInput, faceInput };
    return card;
  }

  function collectProfile(card) {
    const f = card._fields;
    const name = f.nameInput.value.trim();
    const displayName = f.displayNameInput.value.trim();
    const systemPrompt = f.promptArea.value.trim();
    const questions = Array.from(f.qList.querySelectorAll(".question-row input"))
      .map(i => i.value.trim())
      .filter(v => v.length > 0);

    let crop = null;
    if (f.cropInput.checked) {
      crop = {};
      for (const input of f.cropGrid.querySelectorAll("[data-crop-field]")) {
        crop[input.dataset.cropField] = parseFloat(input.value);
      }
    }

    const licensePlateRecognitionEnabled = f.plateInput.checked;
    const faceRecognitionEnabled = f.faceInput.checked;

    return { name, profile: { displayName, systemPrompt, questions, crop, licensePlateRecognitionEnabled, faceRecognitionEnabled } };
  }

  function buildKnownVehicleRow(vehicle) {
    vehicle = vehicle || { plate: "", label: "" };
    const row = document.createElement("div");
    row.className = "field-row-wide known-vehicle-row";

    const plateInput = document.createElement("input");
    plateInput.type = "text";
    plateInput.className = "field-input";
    plateInput.placeholder = "plate, e.g. ABC1234";
    plateInput.value = vehicle.plate || "";

    const labelInput = document.createElement("input");
    labelInput.type = "text";
    labelInput.className = "field-input";
    labelInput.placeholder = "label, e.g. Nathan's Truck";
    labelInput.value = vehicle.label || "";

    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = "Remove this vehicle.";
    removeBtn.addEventListener("click", () => row.remove());

    row.append(plateInput, labelInput, removeBtn);
    row._fields = { plateInput, labelInput };
    return row;
  }

  function renderBackupSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">BACKUP</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Export everything configurable here - settings, cameras, trigger zones, known vehicles, prompt profiles - as one file, or restore from a previous export.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Saves everything configurable here - settings, cameras, trigger zones, known vehicles, prompt profiles - into one file you can restore later or move to another machine. Worth doing before any big change, and after you first get things working the way you like."));

    const warning = document.createElement("div");
    warning.className = "settings-hint";
    warning.textContent = "Includes secrets (passwords, API keys, tokens) for a complete restore - store the downloaded file like a password. Restoring overlays onto your current setup; it never deletes a camera or setting the backup doesn't mention.";
    body.appendChild(warning);

    const exportRow = document.createElement("div");
    exportRow.className = "profiles-toolbar";
    const exportBtn = document.createElement("button");
    exportBtn.className = "btn";
    exportBtn.textContent = "export backup";
    exportBtn.dataset.tip = "Download everything configurable here as one JSON file.";
    exportRow.appendChild(exportBtn);
    body.appendChild(exportRow);

    const importRow = document.createElement("div");
    importRow.className = "field-row field-row-wide";
    const importLabel = document.createElement("label");
    importLabel.className = "field-label";
    importLabel.textContent = "Restore from backup";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = ".json";
    fileInput.id = "backup-file-input";
    importRow.append(importLabel, fileInput);
    body.appendChild(importRow);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    exportBtn.addEventListener("click", async () => {
      exportBtn.disabled = true;
      try {
        const response = await fetch("/api/backup/export");
        if (!response.ok) {
          showToast("Export failed", false);
          return;
        }
        const data = await response.json();
        const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = `sentinelcore-backup-${new Date().toISOString().slice(0, 10)}.json`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
        showToast("Backup downloaded", true);
      } catch {
        showToast("Export failed", false);
      } finally {
        exportBtn.disabled = false;
      }
    });

    fileInput.addEventListener("change", async () => {
      const file = fileInput.files[0];
      if (!file) return;
      errors.innerHTML = "";
      try {
        const contents = await file.text();
        const response = await fetch("/api/backup/import", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: contents
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, payload.errors || ["Could not restore that file."]);
          showToast("Restore failed", false);
          return;
        }
        showToast(`Restored ${payload.camerasRestored} camera(s), ${payload.triggerZonesRestored} trigger zone(s)`, true);
        window.location.reload();
      } catch {
        showFieldErrors(errors, ["Network error while restoring."]);
      } finally {
        fileInput.value = "";
      }
    });

    section.append(header, body);
    return section;
  }

  // Custom rather than a generic SECTIONS entry because the useful half of this section isn't
  // config at all - it's the pairing QR, which comes from the sidecar rather than appsettings.
  // The config fields themselves still go through buildField/submit like everywhere else.
  function renderHomeKitSection(data) {
    data = data || {};

    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">APPLE HOMEKIT</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Show your cameras in the Apple Home app and get iPhone notifications when an alert fires. Scan the pairing code below with your iPhone. Live video isn't included - HomeKit only gets motion alerts.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Puts your cameras in Apple's Home app as motion sensors, so alert-worthy events become native iPhone notifications. Live video isn't included - that needs Apple's paid HomeKit Secure Video licensing - but everything else works. Nothing is put on your network until you turn this on."));

    const fields = [
      {
        name: "enabled", label: "enabled", type: "checkbox",
        tip: "Send a motion alert to Apple Home whenever an alert is worth notifying you about. Needs the HomeKit sidecar service running."
      },
      {
        name: "sidecarBaseUrl", label: "sidecar url", type: "text",
        tip: "Where the HomeKit sidecar's control API is listening. The default is right unless you changed its port."
      },
      {
        name: "minimumConfidence", label: "minimum confidence", type: "number", min: 0, max: 1, step: 0.05,
        tip: "How sure the AI must be before this trips a HomeKit motion alert on your phone. Raise it if you get woken by false alarms; lower it if real events are being missed. Quiet Hours can raise this further overnight."
      }
    ];

    const grid = document.createElement("div");
    grid.className = "field-grid";
    for (const field of fields) grid.appendChild(buildField(field, data[field.name]));
    body.appendChild(grid);

    // --- pairing panel ---
    const pairing = document.createElement("div");
    pairing.className = "field-row-wide";

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking HomeKit sidecar…";
    pairing.appendChild(status);

    const diagnostics = document.createElement("div");
    diagnostics.className = "settings-hint";
    pairing.appendChild(diagnostics);

    const qrWrap = document.createElement("div");
    qrWrap.className = "homekit-qr";
    pairing.appendChild(qrWrap);

    body.appendChild(pairing);

    async function refreshStatus() {
      qrWrap.innerHTML = "";
      try {
        const response = await fetch("/api/homekit/status");
        const payload = await response.json();

        if (!payload.running) {
          status.textContent = payload.error || "The HomeKit sidecar isn't running.";
          diagnostics.textContent = "";
          return;
        }
        const lastTrigger = payload.lastTriggerAt
          ? ` Last motion sent for ${payload.lastTriggerCamera || "a camera"} at ${new Date(payload.lastTriggerAt).toLocaleString()}.`
          : " No motion has been sent since this sidecar started.";
        diagnostics.textContent = `HomeKit endpoint: TCP ${payload.hapPort || 51826}; advertiser: ${payload.advertiser || "unknown"}.${lastTrigger}`;
        if (!payload.enabled) {
          // The sidecar is installed and healthy, it just isn't advertising anything yet -
          // nothing is on your network until the toggle above goes on.
          status.textContent = "turned off - switch enabled on and save to get a pairing code.";
          return;
        }
        if (payload.paired) {
          const names = (payload.accessories || []).join(", ");
          status.textContent = names
            ? `paired - sending motion for: ${names}`
            : "paired - cameras appear here after their first alert.";
          return;
        }

        status.textContent = "not paired yet - open the Home app on your iPhone, tap +, then Add Accessory, and scan this code:";

        const img = document.createElement("img");
        // Cache-busted so re-pairing after a sidecar reset never shows a stale code.
        img.src = `/api/homekit/qr?t=${Date.now()}`;
        img.alt = "HomeKit pairing QR code";
        img.className = "homekit-qr-image";
        qrWrap.appendChild(img);

        if (payload.pairingCode) {
          const code = document.createElement("div");
          code.className = "homekit-pairing-code";
          // Some people type the code in rather than scanning, and the Home app asks for it
          // by hand if the camera can't focus on the screen.
          code.textContent = payload.pairingCode;
          qrWrap.appendChild(code);
        }
      } catch {
        status.textContent = "could not check the HomeKit sidecar.";
      }
    }

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save this section. Changes take effect immediately - no restart needed.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";

    const refreshBtn = document.createElement("button");
    refreshBtn.className = "btn";
    refreshBtn.textContent = "refresh";
    refreshBtn.dataset.tip = "Re-check the sidecar's pairing status.";
    refreshBtn.addEventListener("click", () => refreshStatus());

    const resetBtn = document.createElement("button");
    resetBtn.className = "btn";
    resetBtn.textContent = "reset pairing";
    resetBtn.dataset.tip = "Create a completely new HomeKit bridge identity when Apple Home says the existing accessory is not responding. You will need to remove the old bridge from Home and scan a new QR code.";
    resetBtn.addEventListener("click", async () => {
      const confirmed = window.confirm(
        "This erases Sentinel Core's HomeKit pairing and creates a new bridge identity. Remove the old Sentinel Core bridge from Apple Home, then click OK. You will need to scan a new QR code."
      );
      if (!confirmed) return;

      resetBtn.disabled = true;
      errors.innerHTML = "";
      try {
        const response = await fetch("/api/homekit/reset", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ confirm: "RESET" })
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, [payload.error || "Could not reset HomeKit pairing."]);
          return;
        }

        status.textContent = "pairing reset - waiting for the HomeKit service to restart…";
        diagnostics.textContent = "";
        // systemd/Windows Service restarts the sidecar. Wait for the new unpaired identity and
        // QR rather than making the user guess when it is ready.
        for (let attempt = 0; attempt < 20; attempt++) {
          await new Promise(resolve => setTimeout(resolve, 1500));
          await refreshStatus();
          if (section.querySelector(".homekit-qr-image")) {
            showToast("HomeKit pairing reset - scan the new code", true);
            return;
          }
        }
        showFieldErrors(errors, ["Pairing was reset, but the sidecar has not restarted yet. Restart the HomeKit service, then press refresh."]);
      } catch {
        showFieldErrors(errors, ["Network error while resetting HomeKit pairing."]);
      } finally {
        resetBtn.disabled = false;
      }
    });

    footer.append(saveBtn, refreshBtn, resetBtn, errors);
    body.appendChild(footer);

    saveBtn.addEventListener("click", async () => {
      const payload = {
        enabled: section.querySelector('[data-field="enabled"]').checked,
        sidecarBaseUrl: section.querySelector('[data-field="sidecarBaseUrl"]').value,
        timeoutSeconds: data.timeoutSeconds || 10
      };
      await submit("/api/settings/homekit", payload, "Apple HomeKit", errors, saveBtn);

      // The sidecar re-reads the config on its own schedule, so the QR isn't ready the instant
      // this returns. Poll briefly rather than showing "turned off" to someone who just turned
      // it on and is waiting for a code to scan.
      if (payload.enabled) {
        status.textContent = "starting HomeKit…";
        for (let attempt = 0; attempt < 8; attempt++) {
          await new Promise(resolve => setTimeout(resolve, 2000));
          await refreshStatus();
          if (section.querySelector(".homekit-qr-image")) return;
        }
      }
      await refreshStatus();
    });

    refreshStatus();

    section.append(header, body);
    return section;
  }

  const SYSTEM_STATUS_POLL_MS = 5_000;

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
  }

  function formatUptime(seconds) {
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${minutes}m`;
    return `${minutes}m`;
  }

  function barSeverityClass(percent) {
    if (percent >= 90) return "danger";
    if (percent >= 70) return "warn";
    return "";
  }

  function buildStatBar(labelText, percent, detailText) {
    const row = document.createElement("div");
    row.className = "system-stat-row";

    const label = document.createElement("div");
    label.className = "system-stat-label";
    label.innerHTML = `<span>${labelText}</span><strong>${detailText}</strong>`;

    const track = document.createElement("div");
    track.className = "system-stat-bar";
    const fill = document.createElement("div");
    fill.className = `system-stat-bar-fill ${barSeverityClass(percent)}`.trim();
    fill.style.width = `${Math.min(100, Math.max(0, percent)).toFixed(0)}%`;
    track.appendChild(fill);

    row.append(label, track);
    return row;
  }

  function buildStatPlain(labelText, detailText) {
    const row = document.createElement("div");
    row.className = "system-stat-plain";
    row.innerHTML = `<span>${labelText}</span><strong>${detailText}</strong>`;
    return row;
  }

  // Read-only telemetry, not configuration - no save button, just a live snapshot polled on
  // its own interval so it stays current without reloading the whole settings form (which
  // would blow away any in-progress edits elsewhere on the page).
  function renderSystemStatusSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">SYSTEM HEALTH</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "How hard this server is working right now - CPU, memory, disk, and the analysis queue. Read-only; nothing here is configuration.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("How hard this server is currently working. Useful for checking whether it's keeping up before you add more cameras or lean on cloud vision less."));

    const grid = document.createElement("div");
    grid.className = "system-status-grid";
    grid.innerHTML = `<div class="empty-state">checking system status&hellip;</div>`;
    body.appendChild(grid);

    async function refresh() {
      try {
        const response = await fetch("/api/system/status");
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const s = await response.json();

        grid.innerHTML = "";
        grid.appendChild(buildStatBar("CPU", s.cpuUsagePercent, `${s.cpuUsagePercent.toFixed(0)}% of ${s.processorCount} core${s.processorCount === 1 ? "" : "s"}`));
        const memPercent = s.memoryTotalBytes > 0 ? (s.memoryUsedBytes / s.memoryTotalBytes) * 100 : 0;
        grid.appendChild(buildStatBar("Memory", memPercent, `${formatBytes(s.memoryUsedBytes)} / ${formatBytes(s.memoryTotalBytes)}`));
        const diskUsed = s.diskTotalBytes - s.diskFreeBytes;
        const diskPercent = s.diskTotalBytes > 0 ? (diskUsed / s.diskTotalBytes) * 100 : 0;
        grid.appendChild(buildStatBar("Disk", diskPercent, `${formatBytes(diskUsed)} / ${formatBytes(s.diskTotalBytes)}`));
        grid.appendChild(buildStatPlain("Analysis queue", `${s.queuedAlerts} pending`));
        grid.appendChild(buildStatPlain("Vision backend", s.visionBackendHealthy ? "reachable" : "unreachable"));
        grid.appendChild(buildStatPlain("Uptime", formatUptime(s.uptimeSeconds)));
      } catch {
        grid.innerHTML = `<div class="empty-state">could not load system status.</div>`;
      }
    }

    refresh();
    setInterval(refresh, SYSTEM_STATUS_POLL_MS);

    section.append(header, body);
    return section;
  }

  function renderLicenseSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">LICENSE</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Install the .lic file you received after purchase. Missing or invalid only shows a notice - every feature keeps working either way.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Installs the licence file you were emailed after purchase. Missing or invalid shows a notice and a watermark, and nothing else - every feature keeps working either way, so this is never urgent."));

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking license…";
    body.appendChild(status);

    const row = document.createElement("div");
    row.className = "field-row-wide";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = ".lic";
    fileInput.id = "license-file-input";
    row.appendChild(fileInput);
    body.appendChild(row);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    async function refreshStatus() {
      try {
        const response = await fetch("/api/license/status");
        const payload = await response.json();
        status.textContent = payload.valid
          ? `licensed to ${payload.customerName}`
          : "no valid license installed - running unlicensed.";
      } catch {
        status.textContent = "could not check license status.";
      }
    }

    fileInput.addEventListener("change", async () => {
      const file = fileInput.files[0];
      if (!file) return;
      errors.innerHTML = "";
      status.textContent = "installing…";
      try {
        const contents = await file.text();
        const response = await fetch("/api/license/install", {
          method: "POST",
          headers: { "Content-Type": "text/plain" },
          body: contents
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, [payload.error || "Could not install that file."]);
          showToast("License not installed", false);
        } else {
          showToast(`Licensed to ${payload.customerName}`, true);
          window.GateWatchLicense.check();
        }
      } catch {
        showFieldErrors(errors, ["Network error while uploading."]);
      } finally {
        fileInput.value = "";
        await refreshStatus();
      }
    });

    refreshStatus();

    section.append(header, body);
    return section;
  }

  function renderAuthSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">LOGIN</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Require a username and password to open this dashboard. Off by default - anyone who can reach this network can otherwise use it freely.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Requires a username and password to open this dashboard. Off by default, which is fine on a home network you trust - but turn it on before this is reachable from anywhere else. Sessions live in memory, so everyone is signed out whenever the app restarts."));

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking login status…";
    body.appendChild(status);

    const formWrap = document.createElement("div");
    body.appendChild(formWrap);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    function renderEnableForm() {
      formWrap.innerHTML = `
        <div class="field-row field-row-wide">
          <label class="field-label">Username</label>
          <input type="text" class="field-input" id="auth-new-username" autocomplete="username" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Password</label>
          <input type="password" class="field-input" id="auth-new-password" autocomplete="new-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Confirm Password</label>
          <input type="password" class="field-input" id="auth-new-confirm" autocomplete="new-password" />
        </div>
        <button class="btn" id="auth-enable-btn">require login</button>`;

      document.getElementById("auth-enable-btn").addEventListener("click", async () => {
        errors.innerHTML = "";
        const username = document.getElementById("auth-new-username").value.trim();
        const password = document.getElementById("auth-new-password").value;
        const confirmPassword = document.getElementById("auth-new-confirm").value;
        if (password !== confirmPassword) {
          showFieldErrors(errors, ["Passwords do not match."]);
          return;
        }
        try {
          const response = await fetch("/api/auth/enable", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
          });
          const payload = await response.json().catch(() => ({}));
          if (!response.ok) {
            showFieldErrors(errors, payload.errors || ["Could not enable login."]);
            showToast("Login not enabled", false);
            return;
          }
          showToast("Login required", true);
          window.GateWatchAuth.check();
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });
    }

    function renderChangeForm(currentUsername) {
      formWrap.innerHTML = `
        <div class="field-row field-row-wide">
          <label class="field-label">Current Password</label>
          <input type="password" class="field-input" id="auth-current-password" autocomplete="current-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Username</label>
          <input type="text" class="field-input" id="auth-username" value="${escapeHtml(currentUsername)}" autocomplete="username" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">New Password (leave blank to keep current)</label>
          <input type="password" class="field-input" id="auth-password" autocomplete="new-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Confirm New Password</label>
          <input type="password" class="field-input" id="auth-confirm" autocomplete="new-password" />
        </div>
        <div class="settings-section-footer">
          <button class="btn" id="auth-update-btn">save</button>
          <button class="btn" id="auth-disable-btn">turn off login</button>
        </div>`;

      document.getElementById("auth-update-btn").addEventListener("click", async () => {
        errors.innerHTML = "";
        const currentPassword = document.getElementById("auth-current-password").value;
        const newUsername = document.getElementById("auth-username").value.trim();
        const newPassword = document.getElementById("auth-password").value;
        const confirmNewPassword = document.getElementById("auth-confirm").value;
        if (newPassword !== confirmNewPassword) {
          showFieldErrors(errors, ["Passwords do not match."]);
          return;
        }
        try {
          const response = await fetch("/api/auth/update", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ currentPassword, newUsername, newPassword: newPassword || null })
          });
          const payload = await response.json().catch(() => ({}));
          if (!response.ok) {
            showFieldErrors(errors, payload.errors || ["Could not save."]);
            showToast("Not saved", false);
            return;
          }
          showToast("Login updated", true);
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });

      document.getElementById("auth-disable-btn").addEventListener("click", async () => {
        if (!confirm("Turn off login? Anyone on the network will then have full access to this dashboard.")) return;
        try {
          const response = await fetch("/api/auth/disable", { method: "POST" });
          if (!response.ok) {
            showToast("Could not turn off login", false);
            return;
          }
          showToast("Login turned off", true);
          window.GateWatchAuth.check();
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });
    }

    async function refreshStatus() {
      errors.innerHTML = "";
      try {
        const response = await fetch("/api/auth/status");
        const payload = await response.json();
        if (payload.enabled) {
          status.textContent = "login is required to open this dashboard.";
          renderChangeForm(payload.username || "");
        } else {
          status.textContent = "no login required - open to anyone on the network.";
          renderEnableForm();
        }
      } catch {
        status.textContent = "could not check login status.";
      }
    }

    refreshStatus();

    section.append(header, body);
    return section;
  }

  function renderKnownVehiclesSection(vehicles) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">KNOWN VEHICLES</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Vehicles checked against a transcribed plate, for cameras/profiles with license plate recognition turned on. Informational only - a match or non-match never changes whether an alert fires.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Vehicles you recognise, matched against plates the AI reads. This is informational only - a match or non-match is noted in the alert but never changes whether one fires. Only does anything for cameras using a prompt profile with licence plate recognition turned on."));

    const hint = document.createElement("div");
    hint.className = "settings-hint";
    hint.textContent = "Shared across every camera/profile that has license plate recognition enabled.";
    body.appendChild(hint);

    const rowsWrap = document.createElement("div");
    rowsWrap.className = "known-vehicles-list";
    for (const vehicle of vehicles || []) {
      rowsWrap.appendChild(buildKnownVehicleRow(vehicle));
    }
    body.appendChild(rowsWrap);

    const toolbar = document.createElement("div");
    toolbar.className = "profiles-toolbar";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add vehicle";
    addBtn.dataset.tip = "Add another known vehicle by plate and label.";
    addBtn.addEventListener("click", () => rowsWrap.appendChild(buildKnownVehicleRow(null)));
    toolbar.appendChild(addBtn);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save the known vehicle list.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);

    saveBtn.addEventListener("click", async () => {
      const list = Array.from(rowsWrap.querySelectorAll(".known-vehicle-row"))
        .map(row => ({ plate: row._fields.plateInput.value.trim(), label: row._fields.labelInput.value.trim() }))
        .filter(v => v.plate.length > 0);
      await submit("/api/settings/known-vehicles", list, "Known vehicles", errors, saveBtn);
    });

    body.append(toolbar, footer);
    section.append(header, body);
    return section;
  }

  function renderKnownFacesSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">KNOWN FACES</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "People recognized from a photo, for cameras/profiles with face recognition turned on. Runs entirely on-device - informational only, a match or non-match never changes whether an alert fires.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("People you recognize, matched against faces detected in alert photos using a local, on-device model - no image ever leaves your network for this. Informational only - a match or non-match is noted in the alert but never changes whether one fires. Only does anything for cameras using a prompt profile with face recognition turned on."));

    const listWrap = document.createElement("div");
    listWrap.className = "known-faces-list";
    body.appendChild(listWrap);

    const errors = document.createElement("ul");
    errors.className = "field-errors";

    async function refresh() {
      listWrap.innerHTML = `<div class="empty-state">loading&hellip;</div>`;
      try {
        const response = await fetch("/api/known-faces");
        const list = response.ok ? await response.json() : [];
        listWrap.innerHTML = "";
        if (list.length === 0) {
          listWrap.innerHTML = `<div class="empty-state">No known faces enrolled yet.</div>`;
        }
        for (const face of list) listWrap.appendChild(buildKnownFaceCard(face, refresh, errors));
      } catch {
        listWrap.innerHTML = `<div class="empty-state">Could not load known faces.</div>`;
      }
    }

    const addRow = document.createElement("div");
    addRow.className = "field-row-wide known-vehicle-row";
    const labelInput = document.createElement("input");
    labelInput.type = "text";
    labelInput.className = "field-input";
    labelInput.placeholder = "name, e.g. Nathan";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = "image/*";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add person";
    addBtn.dataset.tip = "Enroll a new known person from a clear, front-facing photo of just them.";
    addBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      const label = labelInput.value.trim();
      const file = fileInput.files[0];
      if (!label) { showFieldErrors(errors, ["A name is required."]); return; }
      if (!file) { showFieldErrors(errors, ["Choose a photo first."]); return; }

      addBtn.disabled = true;
      const originalText = addBtn.textContent;
      addBtn.textContent = "adding...";
      try {
        const form = new FormData();
        form.append("label", label);
        form.append("image", file);
        const response = await fetch("/api/known-faces", { method: "POST", body: form });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, [payload.error || "Could not add that person."]);
          showToast("Known faces: not saved", false);
        } else {
          labelInput.value = "";
          fileInput.value = "";
          showToast("Known faces saved", true);
          await refresh();
        }
      } catch {
        showFieldErrors(errors, ["Network error while adding."]);
      } finally {
        addBtn.disabled = false;
        addBtn.textContent = originalText;
      }
    });
    addRow.append(labelInput, fileInput, addBtn);

    body.append(addRow, errors);
    section.append(header, body);
    refresh();
    return section;
  }

  function buildKnownFaceCard(face, onChange, sharedErrors) {
    const card = document.createElement("div");
    card.className = "profile-card";

    const header = document.createElement("div");
    header.className = "profile-card-header";
    header.style.cursor = "default";
    const name = document.createElement("span");
    name.className = "profile-card-summary";
    name.textContent = face.label;
    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = `Remove ${face.label} and all their enrolled photos.`;
    removeBtn.addEventListener("click", async () => {
      if (!confirm(`Remove ${face.label} from known faces?`)) return;
      await fetch(`/api/known-faces/${face.id}`, { method: "DELETE" });
      await onChange();
    });
    header.append(name, removeBtn);

    const photos = document.createElement("div");
    photos.className = "known-face-photos";
    for (const enrollment of face.enrollments) {
      const thumbWrap = document.createElement("div");
      thumbWrap.className = "known-face-photo";
      const img = document.createElement("img");
      img.src = enrollment.photoUrl;
      img.alt = face.label;
      const del = document.createElement("button");
      del.type = "button";
      del.className = "icon-btn known-face-photo-remove";
      del.textContent = "✕";
      del.dataset.tip = "Remove this photo.";
      del.addEventListener("click", async () => {
        await fetch(`/api/known-faces/${face.id}/enrollments/${enrollment.id}`, { method: "DELETE" });
        await onChange();
      });
      thumbWrap.append(img, del);
      photos.appendChild(thumbWrap);
    }

    const addPhotoInput = document.createElement("input");
    addPhotoInput.type = "file";
    addPhotoInput.accept = "image/*";
    addPhotoInput.className = "known-face-add-photo";
    addPhotoInput.dataset.tip = "Add another reference photo of this person, for better matching.";
    addPhotoInput.addEventListener("change", async () => {
      const file = addPhotoInput.files[0];
      if (!file) return;
      const form = new FormData();
      form.append("image", file);
      const response = await fetch(`/api/known-faces/${face.id}/enrollments`, { method: "POST", body: form });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        showFieldErrors(sharedErrors, [payload.error || "Could not add that photo."]);
      } else {
        await onChange();
      }
    });

    card.append(header, photos, addPhotoInput);
    return card;
  }

  function renderPromptProfilesSection(profiles) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">PROMPT PROFILES</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "What the AI is told to look for on each camera - what matters, what to ignore, and what's worth alerting you about. Around a hundred ready-made profiles are included.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Prompt profiles are the instructions given to the AI for each camera - what to look at, what to ignore, and what counts as worth alerting you about. Around a hundred ready-made profiles ship with the app covering common camera placements; pick one per trigger zone rather than writing your own unless you have a specific need."));

    const count = Object.keys(profiles || {}).length;
    const hint = document.createElement("div");
    hint.className = "settings-hint";
    hint.textContent = `The AI prompts sent to Ollama per camera profile (${count} available). A 'default' profile is required as a fallback for unrecognized profile names. Click a profile to expand it.`;
    body.appendChild(hint);

    const searchRow = document.createElement("div");
    searchRow.className = "field-row profiles-search-row";
    const searchInput = document.createElement("input");
    searchInput.type = "text";
    searchInput.className = "field-input";
    searchInput.placeholder = "search profiles by name (e.g. \"chicken\", \"gate\", \"garage\")…";
    searchRow.appendChild(searchInput);
    body.appendChild(searchRow);

    const cardsWrap = document.createElement("div");
    cardsWrap.className = "settings-root";
    for (const [name, profile] of Object.entries(profiles || {})) {
      cardsWrap.appendChild(buildProfileCard(name, profile));
    }

    searchInput.addEventListener("input", () => {
      const term = searchInput.value.trim().toLowerCase();
      for (const card of cardsWrap.querySelectorAll(".profile-card")) {
        const matches = !term || card.dataset.searchText.includes(term);
        card.classList.toggle("filtered-out", !matches);
      }
    });

    const toolbar = document.createElement("div");
    toolbar.className = "profiles-toolbar";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add profile";
    addBtn.dataset.tip = "Create another prompt profile. Blue Iris picks one per camera by name.";
    addBtn.addEventListener("click", () => {
      const card = buildProfileCard("", null, { startExpanded: true });
      cardsWrap.appendChild(card);
      card.scrollIntoView({ behavior: "smooth", block: "center" });
    });
    toolbar.appendChild(addBtn);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save every profile. New prompts apply to the next alert that comes in.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);

    saveBtn.addEventListener("click", async () => {
      const dict = {};
      for (const card of cardsWrap.querySelectorAll(".profile-card")) {
        const { name, profile } = collectProfile(card);
        if (name) dict[name] = profile;
      }
      await submit("/api/settings/prompt-profiles", dict, "Prompt profiles", errors, saveBtn);
    });

    body.append(cardsWrap, toolbar, footer);
    section.append(header, body);
    return section;
  }

  async function submit(endpoint, body, label, errorsEl, saveBtn) {
    errorsEl.innerHTML = "";
    saveBtn.disabled = true;
    const originalText = saveBtn.textContent;
    saveBtn.textContent = "saving...";
    try {
      const response = await fetch(endpoint, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        showFieldErrors(errorsEl, payload.errors || ["Save failed."]);
        showToast(`${label}: not saved`, false);
      } else {
        showToast(`${label} saved`, true);
      }
    } catch {
      showFieldErrors(errorsEl, ["Network error while saving."]);
      showToast("Save failed", false);
    } finally {
      saveBtn.disabled = false;
      saveBtn.textContent = originalText;
    }
  }

  function showFieldErrors(el, errors) {
    el.innerHTML = "";
    for (const message of errors) {
      const li = document.createElement("li");
      li.textContent = message;
      el.appendChild(li);
    }
  }

  let toastTimeout;
  function showToast(message, success) {
    const toast = document.getElementById("toast");
    clearTimeout(toastTimeout);
    toast.textContent = message;
    toast.className = "toast " + (success ? "success" : "error");
    toastTimeout = setTimeout(() => { toast.className = "toast hidden"; }, 3000);
  }

  async function load() {
    const root = document.getElementById("settings-root");
    root.innerHTML = `<div class="empty-state">loading settings&hellip;</div>`;
    try {
      const response = await fetch("/api/settings");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const data = await response.json();

      root.innerHTML = "";
      for (const schema of SECTIONS) {
        root.appendChild(renderSection(schema, data[schema.key]));
      }
      root.appendChild(renderSystemStatusSection());
      root.appendChild(renderHomeKitSection(data.homeKit));
      root.appendChild(renderBackupSection());
      root.appendChild(renderAuthSection());
      const internetExposedData = await fetch("/api/settings/internet-exposed").then(r => r.ok ? r.json() : null).catch(() => null);
      root.appendChild(renderSection(INTERNET_EXPOSED_SCHEMA, internetExposedData));
      root.appendChild(renderLicenseSection());
      root.appendChild(renderKnownVehiclesSection(data.knownVehicles));
      root.appendChild(renderKnownFacesSection());
      root.appendChild(renderPromptProfilesSection(data.promptProfiles));

      // The hand-written sections don't go through renderSection, so tag them here by title.
      for (const section of root.querySelectorAll(".settings-section")) {
        const title = section.querySelector(".settings-section-title")?.textContent ?? "";
        if (BASIC_CUSTOM_SECTIONS.has(title)) section.dataset.basic = "true";
      }

      const toggle = document.getElementById("advanced-mode-toggle");
      if (toggle) {
        toggle.checked = isAdvancedMode();
        toggle.onchange = () => {
          setAdvancedMode(toggle.checked);
          applyMode(toggle.checked);
        };
      }
      applyMode(isAdvancedMode());
    } catch (err) {
      root.innerHTML = `<div class="empty-state">failed to load settings: ${err.message}</div>`;
    }
  }

  return { load };
})();
