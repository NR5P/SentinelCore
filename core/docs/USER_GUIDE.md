# Sentinel Core — User Guide

This is the guide to actually *using* the system day to day: the dashboard, the AI alerts,
the trigger zones, the Telegram bot, and every setting you can tune. For installing or
rebuilding the software itself, see [DEPLOYMENT.md](DEPLOYMENT.md) instead.

## Contents

1. [What this system does](#1-what-this-system-does)
2. [Opening the dashboard](#2-opening-the-dashboard)
3. [The FEED tab — Blue Iris alerts](#3-the-feed-tab--blue-iris-alerts)
4. [The LIVE tab — viewing your own cameras](#4-the-live-tab--viewing-your-own-cameras)
5. [The CAMERAS tab — adding your own cameras](#5-the-cameras-tab--adding-your-own-cameras)
6. [The TRIGGERS tab — motion zones on your own cameras](#6-the-triggers-tab--motion-zones-on-your-own-cameras)
7. [The SETTINGS tab](#7-the-settings-tab)
8. [Vision analysis backend — local, cloud, or both](#8-vision-analysis-backend--local-cloud-or-both)
9. [Prompt profiles — what the AI is told to look for](#9-prompt-profiles--what-the-ai-is-told-to-look-for)
10. [License plate recognition & known vehicles](#10-license-plate-recognition--known-vehicles)
11. [The Telegram bot](#11-the-telegram-bot)
12. [Email](#12-email)
13. [MQTT & Home Assistant](#13-mqtt--home-assistant)
14. [Apple HomeKit](#14-apple-homekit)
15. [Why didn't I get an alert? (rate limiting & suppression, explained)](#15-why-didnt-i-get-an-alert-rate-limiting--suppression-explained)
16. [Self-monitoring](#16-self-monitoring)
17. [Bug reports](#17-bug-reports)
18. [Where your data lives](#18-where-your-data-lives)
19. [Troubleshooting](#19-troubleshooting)

---

## 1. What this system does

There are two independent pieces running on your home server:

- **The bridge** (port 5080) — receives snapshot images from Blue Iris whenever it detects
  motion, sends each one to an AI vision model with a set of questions, decides whether it's
  worth telling you about, and sends alerts (email, Telegram, etc.) when it is. By default this
  uses a local model (Ollama) and never leaves your network. An offsite cloud model can be used
  instead, or as an automatic backup if the local one is unavailable — see
  [section 8](#8-vision-analysis-backend--local-cloud-or-both).
- **The dashboard** (port 3320, "SENTINEL_CORE") — a web page for browsing what the bridge has
  seen, adding your own extra cameras (separate from Blue Iris), drawing motion-detection
  zones on them, and editing every setting above without touching a config file by hand.

Both pieces can also independently trigger the *same* AI analysis and the *same* alerts
(email/Telegram) — Blue Iris images always have, and your own added cameras can too if you
turn that on for a given camera's trigger zone. See [section 6](#6-the-triggers-tab--motion-zones-on-your-own-cameras).

## 2. Opening the dashboard

Point a browser at `http://<your-server's-LAN-IP>:3320`. There's no login by default — it's
designed for LAN-only use, so don't expose port 3320 (or 5080) to the internet unless you've
turned on the login (see below) and put it behind HTTPS yourself.

Across the top you'll see a status indicator ("LINK ESTABLISHED" / "LINK LOST") showing
whether the dashboard can currently reach its own backend, and five tabs: **feed**, **live**,
**cameras**, **triggers**, **settings**.

The first time you open the dashboard (or after clicking **Re-Run Setup Wizard** in
Settings → Bridge), a short setup wizard walks through the essentials instead: an API key for
Blue Iris (generated for you automatically), which AI backend analyzes snapshots, adding at
least one camera, optional notifications, and whether to require a login. Every step past the
welcome screen can be skipped, and everything it touches can also be changed later in SETTINGS
- nothing about it is one-way or final.

**Requiring a login.** Off by default - if this dashboard never leaves your own network, there's
nothing to set up. If it might ever be reachable elsewhere, turn it on either during setup or
later in SETTINGS → LOGIN, with a username and password. Once on, opening the dashboard shows a
sign-in screen instead of your data. Signing out, changing the username/password, or turning it
back off again are all in SETTINGS → LOGIN too. There's a short lockout after repeated wrong
attempts from the same device. Signing in issues a session that's remembered for 30 days, but
note that it lives only in the server's memory - restarting the service (which happens on every
update) signs everyone out.

If a purchased install is missing its `license.lic` file (or it's been altered), a thin amber
notice appears under the top bar, and an "UNLICENSED" watermark appears across snapshot images,
live video, and trigger clips. Both are informational only - every feature, including viewing
those images and video, keeps working exactly as normal either way, nothing is blocked. To clear
it, open SETTINGS → LICENSE and upload the `.lic` file you were emailed - it's verified and
written to disk immediately, no restart needed.

## 3. The FEED tab — Blue Iris alerts

This is the history of everything Blue Iris has sent over, newest first, refreshing itself
every 30 seconds (or click **refresh** to force it). Each card shows:

- The snapshot image (click to view it full-size)
- Which Blue Iris camera it came from
- How long ago it happened, and the exact time
- The AI's plain-English description of what it saw
- The event type the AI classified it as (e.g. person at gate, vehicle entering)
- A green **notified** badge if this event actually triggered an alert — hover it to see why

If a card has no image, either `Bridge:SaveImages` is off or the image failed to save; the
AI's text analysis is still recorded either way.

**Ask a question.** The box above the filters lets you ask in plain English - "what happened
last night?", "did anyone come to the gate today?", or a how-to/configuration question like
"how do I turn on quiet hours?". The same local AI that analyzes your alerts answers using
either your recent event history or this User Guide, whichever the question is actually about -
never both at once, which keeps answers fast and on-topic. It's the same underlying feature as
the Telegram bot's chat (Settings → Telegram Chat controls it either way - off there means off
here too), just usable without a Telegram account. Questions about your cameras answer in
seconds; how-to/configuration questions read through part of this guide first and can take
closer to a minute on a modest local machine - both are normal.

**Searching and filtering.** Above the feed is a filter bar:

- A text box that searches the AI's description (e.g. searching "package" finds every event
  whose summary mentions one), debounced so it doesn't refetch on every keystroke
- A **camera** dropdown and an **event type** dropdown, both populated from everything that's
  ever appeared in history — not just what's currently shown
- **From** / **to** date fields to bound the search to a range
- A **notified only** toggle to hide everything that didn't actually alert

Every filter combines with the others (all must match), and the count at the top of the tab
reflects the current filter, not the total history. **clear** resets everything back to the
unfiltered, most-recent-50 view.

## 4. The LIVE tab — viewing your own cameras

Shows live video from cameras you've added yourself (see the next section) — **not** your
Blue Iris cameras, and this view has nothing to do with alerts or the AI. Streams only start
while you have this tab open and stop the moment you leave it, so it's not something you'd
leave open all the time.

## 5. The CAMERAS tab — adding your own cameras

Click **+ add camera** to add an IP camera by its stream URL (`rtsp://` or `http://`), with
optional username/password and a toggle for whether to include audio. Once added, a camera
shows up on the **LIVE** tab for viewing and on the **TRIGGERS** tab for motion detection.

This is completely separate from your Blue Iris setup — Blue Iris keeps working exactly as it
always has, whether or not you add any cameras here.

## 6. The TRIGGERS tab — motion zones on your own cameras

This is where you set up motion detection on cameras you added in the CAMERAS tab. Click a
camera's trigger-zone icon to open the zone editor, which has two parts:

**Draw the zone** — click points on the reference frame to outline the area that should be
watched. Three or more points make a shape; anything outside it is ignored, so you can leave
out a road, a neighbor's yard, or a tree that sways in the wind. Use **undo point** / **clear
zone** to redo it.

**Size the smallest object** — drag the handle on the circle to roughly match the size of a
person or car at that distance. Anything smaller is ignored, which filters out birds, cats,
and blowing debris.

Below that, the settings for this zone:

| Setting | What it does |
|---|---|
| Seconds between triggers | Quiet period after a trigger fires, so one car moving through the zone doesn't fire over and over. |
| Max triggers per minute | Hard ceiling as a safety net — e.g. for a windy day when the zone otherwise wouldn't stop firing. |
| Shadow sensitivity | Filters out shadows (which dim a surface without changing its color) from triggering. Raise it if shadows still cause triggers, lower it if real motion gets missed, 0 turns it off. |
| Save a video clip around each trigger | Turns on continuous background recording for this camera so footage from just before a trigger can be included in the clip. Uses disk space while on — the recording itself isn't tied to whether you have the LIVE tab open. |
| Seconds before / after trigger | How much footage to keep on each side of the trigger moment. The clip only appears once the "after" portion has finished recording. |
| **Send triggers to the AI and alert pipeline** | Off by default. When on, a trigger from this zone is sent through the *same* AI analysis and the *same* alert actions (email, Telegram, webhook) that Blue Iris alerts already use — see below. |
| Alert prompt profile | Which prompt profile (see [section 9](#9-prompt-profiles--what-the-ai-is-told-to-look-for)) analyzes this camera's triggers. Only shown/used when the toggle above is on. |
| Enabled | Master switch for the whole zone. Off means nothing is watched or recorded, but your shape and settings are kept. |

**About "send to alert pipeline":** Blue Iris images and your own trigger-zone cameras can
both be active and both alert independently — nothing about turning this on for a camera
stops or changes how Blue Iris alerts work. This is useful for comparing two cameras that
cover overlapping ground (e.g. Blue Iris's gate camera and your own gate-facing camera both
set to the `gate` profile), or for adding AI-backed alerting to a camera Blue Iris doesn't
know about at all — a chicken coop camera on the `chicken-coop-predator` profile, for
instance.

The **TRIGGERS** tab itself (separate from the zone editor) shows a simple history of motion
events caught by any zone, each with a thumbnail — click it to view the snapshot, or play the
clip if one was recorded (marked with a ▶ clip badge).

## 7. The SETTINGS tab

Every section below saves independently and applies live — no restart needed, except where
noted. Click a section header to expand it. Hover any label for a one-line explanation, which
this guide mostly mirrors:

- **Bridge** — the API key Blue Iris authenticates with, the max accepted image size, how
  long events/images are kept before automatic cleanup, whether images are saved at all, and
  which image content-types are accepted.
- **Ollama** — which local vision model analyzes snapshots (must already be pulled in
  Ollama), how long it's kept loaded in memory between alerts, and inference tuning
  (temperature, top-K/top-P, output token limit, retries).
- **Cloud Vision** and **Vision Routing** — an offsite AI model (OpenAI, Anthropic, or Google)
  that can analyze alerts instead of, or as an automatic backup for, the local Ollama model.
  Its own section below. See
  [section 8](#8-vision-analysis-backend--local-cloud-or-both).
- **Image Processing** — whether/how snapshots are resized before the AI sees them (usually
  faster with no real accuracy loss), and JPEG quality.
- **Email** — SMTP server details, sender/recipient addresses, a duplicate-cooldown so one
  visitor doesn't fill your inbox, a minimum-confidence gate, and whether to attach the image.
- **Daily Summary** — a twice-daily digest email of everything that happened (separate from
  individual alerts), with configurable send hours and a cap on attached images.
- **Telegram Alerts** — push alerts to your phone via a Telegram bot: token, chat ID,
  confidence gate, duplicate cooldown, and whether to attach the image.
- **Telegram Chat** — lets you text the bot questions about recent activity; how many days
  back it looks, how many events it considers, and which Ollama model answers.
- **Blue Iris Snapshot** — lets the `/photo` bot command pull a live image straight from Blue
  Iris on demand.
- **Self-Monitoring** — watches the bridge's own health; see [section 16](#16-self-monitoring).
- **Bug Reports** — emails the developer when something breaks internally. See
  [section 17](#17-bug-reports).
- **Quiet Hours** — during a window you set (e.g. overnight), raises how confident the AI must
  be before Email or Telegram will still ping you. It never fully mutes either channel — a
  confident detection still alerts you at 3am — and it has no effect on the Feed, the daily
  digest, or self-monitoring, which all keep working exactly as normal. See
  [section 15](#15-why-didnt-i-get-an-alert-rate-limiting--suppression-explained) for how this
  fits with the other rate-limiting layers.
- **Known Vehicles** — the list a transcribed license plate is checked against. Its own
  section below. See [section 10](#10-license-plate-recognition--known-vehicles).
- **MQTT / Home Assistant** — publishes events over MQTT with Home Assistant auto-discovery,
  optionally using this app as its own broker. Its own section below. See
  [section 13](#13-mqtt--home-assistant).
- **Apple HomeKit** — shows your cameras in Apple's Home app as motion sensors, for native
  iPhone notifications. Shows the pairing QR code here. Its own section below. See
  [section 14](#14-apple-homekit).
- **Webhook** — POSTs each analyzed event to another system (home automation, logging, etc.).
- **Backup** — exports everything on this list (plus cameras and trigger zones) as one JSON
  file, and restores from a previous export. Includes secrets, so treat the downloaded file
  like a password. Restoring overlays onto your current setup — it never deletes a camera or
  setting the file doesn't mention.
- **Login** — an optional username/password gate for this dashboard, off by default. See
  [section 2](#2-opening-the-dashboard).
- **Prompt Profiles** — the actual instructions given to the AI, per camera. Its own section
  below, since there's a lot here.

Two things are deliberately *not* editable from Settings because they're baked in at process
startup no matter how they're stored: `Bridge:QueueCapacity`/`Bridge:DataDirectory` (sizes an
in-memory queue and opens a database connection once), and a few `*BaseUrl`/`*TimeoutSeconds`
fields that size an HTTP client. Changing those needs a restart regardless — see
[DEPLOYMENT.md](DEPLOYMENT.md).

## 8. Vision analysis backend — local, cloud, or both

Every alert (Blue Iris or your own trigger zones) gets analyzed by one of two kinds of AI
vision backend:

- **Ollama** — a model running on your own hardware. Free to run, keeps every image on your
  own network, but needs a machine capable of it (ideally a GPU) and can be slow on modest
  hardware.
- **Cloud Vision** — an offsite model, one of:
  - **OpenAI** (e.g. `gpt-4o-mini`)
  - **Anthropic** (e.g. `claude-3-5-sonnet-latest`)
  - **Google** (e.g. `gemini-1.5-flash`)

  Pick which via the **Provider** field, with an API key for that provider. Needs internet
  access and sends each snapshot to that provider's servers, but works well on any machine
  regardless of local hardware, and is usually faster than a slow local GPU (or none at all) -
  this is the point of using it if your computer is on the slower side.

If you run something else that speaks OpenAI's request format - Azure OpenAI, OpenRouter,
Together.ai, Groq, or a self-hosted server like vLLM/LM Studio/LocalAI - set **Provider** to
OpenAI and point `CloudVision:BaseUrl` in `appsettings.json` at it instead of the default
`https://api.openai.com/v1/`. This needs a restart to take effect, the same as Ollama's own
address - see [DEPLOYMENT.md](DEPLOYMENT.md). Anthropic and Google each only have the one real
endpoint, so there's no equivalent override for those.

**Vision Routing** decides which one actually gets used:

- **Primary Backend** — which one analyzes alerts first. If it's set to Cloud Vision but the
  Cloud Vision section above isn't enabled with a valid API key, Ollama is used instead
  automatically rather than every alert failing outright.
- **Fall Back To The Other Backend** — if the primary fails (after its own retries), instead of
  giving up on the alert, retry once against whichever backend isn't primary. This is what lets
  you run Cloud Vision as your everyday backend with Ollama as a safety net if the internet or
  OpenAI itself is briefly unavailable — or the reverse, Ollama as primary with Cloud Vision as
  backup if your local machine occasionally struggles to keep up.

Whichever backend actually analyzed a given alert is recorded with it as `model`, visible in
the raw result at `/api/results/{id}` (not currently shown in the FEED tab itself). A `model`
of `pending` means analysis hasn't finished yet; `unavailable` means both the primary and (if
configured) the fallback failed and the alert was never analyzed.

## 9. Prompt profiles — what the AI is told to look for

A **prompt profile** is the actual instructions given to the AI for a particular camera or
situation: what the scene normally looks like, what counts as worth telling you about, and a
specific list of questions it must answer about every image.

Every Blue Iris alert carries a `profile` name (set on the Blue Iris side, in the alert
action's parameters — see [DEPLOYMENT.md](DEPLOYMENT.md)); every trigger zone has one too, set
in its zone editor. If a name doesn't match any profile, or is left blank, `default` is used
automatically.

The Prompt Profiles settings section ships with **103 ready-to-use profiles** covering town
and suburban spots (front porch, backyard, garage, pool, apartment balcony, mailbox...),
country/rural property (entrance gate, tree line, pole barn, pond, well house, propane tank...),
and homestead/livestock setups (goat pen, horse pasture, cattle barn, beehives, greenhouse,
and a dedicated **Chicken Coop Predator Watch** that specifically looks for foxes, raccoons,
opossums, weasels, mink, coyotes, stray dogs, hawks, owls, and snakes, plus signs of an attack
like an open latch, torn wire, feathers, or blood).

Because there are so many, the section:

- Shows a **count** and a **search box** — type part of a name (`"chicken"`, `"garage"`,
  `"gate"`) to filter the list instantly.
- Keeps profiles **collapsed** by default, showing just their friendly name and technical
  name; click one to expand and edit it.

Each profile has:

| Field | Meaning |
|---|---|
| Display Name | Friendly label shown in this list and in the trigger-zone profile picker. Optional. |
| Name (technical) | The stable identifier Blue Iris and camera profile pickers actually match against. Changing this **breaks** anything already pointing at the old name (a Blue Iris alert action, a saved trigger-zone profile). |
| System Prompt | The instructions given to the AI before it ever sees an image. |
| Questions | The specific things the AI must answer about each image — these become the structured fields you see on the Feed tab. |
| Use crop region | Optionally send only part of the image to the AI, e.g. to cut a road or sky out of frame so the model focuses on what matters. |
| License plate recognition | Off by default. See [section 10](#10-license-plate-recognition--known-vehicles). |

Your original gate prompt is untouched and still keyed as `gate` — it's just labeled
**"Nathan's Default Gate Prompt"** in the list now so it's easy to tell apart from the generic
starter profiles. That `gate` name specifically also gets one extra behavior: when the AI
reports the gate as closed, a person or vehicle that's clearly still on the inside/camera side
won't trigger a notification just for being there — but someone on the outside/road side, an
active delivery, a newly-left package, or an animal still will. See
[section 15](#15-why-didnt-i-get-an-alert-rate-limiting--suppression-explained) for the full
explanation.

**Adding your own profile:** click **+ add profile**, fill in a Name and at least one
question, and save. **Deleting** works the same way as any other profile card — the `default`
profile is required and can't be removed, since it's the fallback for anything unmatched.

## 10. License plate recognition & known vehicles

Off by default, and worth turning on only once a camera actually has the resolution and angle
to make a plate legible — on a low-res or heavily-angled camera this mostly won't read
anything, which is harmless but not useful either.

**Turning it on:** each prompt profile has its own **License plate recognition** switch (see
[section 9](#9-prompt-profiles--what-the-ai-is-told-to-look-for)). It's per-profile rather than
a single global switch because profiles are effectively already "per camera" in how this
system works — the same way a profile's crop region only makes sense for one physical camera,
plate recognition only makes sense to turn on for the specific camera(s) that can actually
resolve a plate. Turning it on for one profile has no effect on any other.

**What happens when it's on:** the AI is additionally asked to transcribe a vehicle's plate
if — and only if — it's clearly legible in that frame; it's told explicitly not to guess. That
reading is then checked against the **Known Vehicles** list in Settings: a shared, global list
of plate/label pairs (e.g. `ABC1234` → "Nathan's Truck") that every plate-recognition-enabled
profile checks against. Add, edit, or remove entries there like any other settings list.

**Where you'll see it:** when a plate was read, it's appended to the Email and Telegram
notification for that event — `Plate: ABC1234 (known: Nathan's Truck)` if it matched
something in your list, or `Plate: XYZ9999 (unrecognized)` if it didn't. Nothing is shown when
recognition is off for that profile or no plate was legible in the frame, which is most events.

**What it does *not* do:** a known or unknown plate never changes whether an alert fires or
gets suppressed — it's informational only, appended after the fact. This is deliberate: OCR
from a general-purpose local vision model on a security-camera frame is inherently hit or
miss, especially at lower resolutions, so it isn't trustworthy enough to gate real security
decisions on. Treat it as a helpful hint on top of an alert you'd already be getting, not as
its own detection layer.

## 11. The Telegram bot

Once **Telegram Alerts** is configured (bot token from [@BotFather](https://t.me/BotFather),
your chat ID), the bot pushes a message — and the photo, if enabled — for every alert-worthy
event, so you get it anywhere you have cell signal.

With **Telegram Chat** also enabled, you can message the bot free-text questions like *"what
happened at the gate today?"* or *"was anyone here last night?"* and it answers using recent
event history plus a local Ollama model. It only ever responds to the one chat ID configured
in Settings — anyone else who finds and messages the bot is silently ignored.

Two special commands:

- `/photo` or `/snapshot` — pulls a fresh, live image straight from Blue Iris on demand (needs
  **Blue Iris Snapshot** configured in Settings), separate from the history-based Q&A above.

Everything else you type is treated as a free-text question.

## 12. Email

**Email** sends individual alerts the same way Telegram does, with its own duplicate-cooldown
and confidence threshold. **Daily Summary** is a separate, additional digest email — sent
twice a day (configurable hours) regardless of whether individual alerts fired — covering
everything that happened, with a capped number of attached images so it doesn't get huge.

## 13. MQTT & Home Assistant

Off by default. When enabled, every processed event — Blue Iris or a trigger zone routed
through the AI pipeline — is published over MQTT, with Home Assistant auto-discovery so
cameras show up as devices with their own entities automatically, no manual YAML or UI setup
required on the Home Assistant side.

**Broker options.** You need an MQTT broker for this to talk to. Two ways to get one:

- **Run your own broker separately** (e.g. Mosquitto, or Home Assistant's own Mosquitto
  add-on if you're using Home Assistant OS/Supervised) and point **Broker Host** / **Broker
  Port** / **Username** / **Password** at it.
- **Let this app be the broker.** Turn on **Run Built-In Broker** and it hosts its own MQTT
  broker on **Built-In Broker Port** — nothing else to install. Point Home Assistant's MQTT
  integration (or any other MQTT client) at this host on that port instead. This setting and
  its port need a restart to take effect if changed, the same as other settings that bind a
  network socket at startup.

Either way, this app's own publisher connects to whichever broker is active and stays
connected, reconnecting automatically if the connection drops.

**What shows up in Home Assistant.** Each camera that has sent at least one event becomes a
device named `<camera> (Sentinel Core)` with two entities:

- **Notified** (`binary_sensor`) — turns on briefly whenever an event fires that would have
  alerted Email/Telegram, then automatically resets after 30 seconds. Good for triggering
  automations (lights, announcements, etc.) off a notable event.
- **Last Event** (`sensor`) — the event type as its state, with the full analysis (summary,
  confidence, camera, occurred-at time, and license plate info if that's enabled for the
  camera) available as entity attributes for building your own dashboards or automations.

An availability topic tracks whether the bridge itself is reachable, so Home Assistant shows
these entities as "unavailable" rather than silently stale if the app stops running.

**What gets published, and when.** By default, *every* processed event is published (not just
alert-worthy ones) — turn on **Only When Notification Recommended** if you'd rather Home
Assistant only hear about the same events Email/Telegram would alert on. Either way, MQTT
publishing never affects whether Email/Telegram/the Feed treat an event as alert-worthy — it's
purely an additional output, not another suppression layer. See
[section 15](#15-why-didnt-i-get-an-alert-rate-limiting--suppression-explained).

## 14. Apple HomeKit

Off by default. When enabled, each camera appears in Apple's **Home** app as a motion sensor,
so your iPhone gets a native notification the moment an alert-worthy event happens — no
Telegram account, no email, and no Home Assistant in between.

**What you get, and what you don't.** This is alerts only. Live video *through* HomeKit isn't
included: Apple gates camera streaming behind HomeKit Secure Video, which requires a paid MFi
licence. Watching your cameras live still works exactly as it always has, in the dashboard's
LIVE tab. Everything else about HomeKit — notifications, automations triggered by motion,
seeing which camera fired — works normally.

**Setting it up.** HomeKit needs a small companion service (the "sidecar") running alongside
the app; see [DEPLOYMENT.md](DEPLOYMENT.md) for the one-time install. Once it's running:

1. Open **SETTINGS → APPLE HOMEKIT** in the dashboard and turn **enabled** on, then **save**.
2. A QR code appears, with the numeric pairing code beneath it.
3. On your iPhone, open **Home** → **+** → **Add Accessory**, and scan the code. (If the
   camera won't focus on your screen, tap "More options…" and type the numeric code instead.)
4. Sentinel Core appears as a bridge, with a motion sensor for each camera.

**Cameras appear as they're seen.** A camera only shows up in Home after its first alert-worthy
event — there's no list to configure. That means a brand-new setup starts empty and fills in
naturally over the first day or two. Each camera keeps its identity across restarts, so once
it's appeared it stays put and your automations keep working.

**What triggers a notification.** Only events that would also have alerted Email/Telegram —
the same judgement described in
[section 15](#15-why-didnt-i-get-an-alert-rate-limiting--suppression-explained). Raw
trigger-zone motion does *not* fire HomeKit on its own; if it did, every passing car and moving
shadow would become a push notification. The sensor stays "detected" for 10 seconds and then
resets, which is what Home uses to decide a notification is warranted.

To get notified, make sure notifications are enabled for the accessory in the Home app itself
(tap the sensor → settings → **Status and Notifications**) — Apple keeps that switch on the
phone, not here.

**If the sidecar isn't running**, the settings section says so plainly and nothing else breaks:
alerts keep flowing to email, Telegram, MQTT and the Feed exactly as before. HomeKit is purely
an additional output, never a suppression layer.

**To unpair**, remove the bridge from the Home app on your iPhone. Turning **enabled** off here
just stops sending motion; it doesn't undo the pairing.

## 15. Why didn't I get an alert? (rate limiting & suppression, explained)

There are several independent layers that can each suppress a notification. If something
didn't alert when you expected it to, it's almost always one of these:

1. **The AI didn't think it was worth telling you about.** Every analysis includes a
   yes/no judgment call; if the AI says no, nothing downstream even considers alerting.
2. **Minimum Confidence** (Email/Telegram settings) — even when the AI does recommend a
   notification, if it isn't confident enough, that channel skips it. Different channels can
   have different thresholds.
3. **Quiet Hours** (Settings, if enabled) — during the window you configure, the effective
   Minimum Confidence for Email/Telegram is raised to whichever is higher: the channel's own
   setting, or Quiet Hours' own Minimum Confidence. It's a floor, never a ceiling — it can only
   make the bar harder to clear during that window, never easier, and it never fully mutes a
   channel the way a phone's do-not-disturb would.
4. **Duplicate Cooldown** (Email/Telegram settings, per-camera-per-category) — once a
   given kind of event (e.g. "vehicle at gate") has alerted for a camera, the same kind of
   event won't alert again on that camera until the cooldown expires. This is what stops one
   car sitting at the gate from spamming you repeatedly. A different kind of event (e.g. a
   person showing up while that vehicle is still there) still alerts normally.
5. **Gate-closed suppression** (only applies to the `gate` profile specifically) — when the
   gate is reported closed, a person or vehicle that's clearly settled on the inside/camera
   side is expected and doesn't alert by itself. Anyone on the outside/road side, an active
   delivery, a newly-left package, or an animal is still independently alert-worthy even with
   the gate closed. A single still image usually can't prove a vehicle is "arriving" vs.
   "already parked," so this logic is deliberately lenient toward vehicles — only a vehicle in
   a clearly settled resting spot is suppressed.
6. **Trigger zone rate limiting** (Triggers tab, per zone — separate from the two above,
   and it applies before the AI is even involved) — "Seconds between triggers" and "Max
   triggers per minute" limit how often *the zone itself* fires at all, regardless of what the
   AI would have said about any particular frame.
7. **Zone coverage** — a trigger zone only watches inside the polygon you drew. If someone
   stood just outside the drawn shape, nothing fires — check the zone shape in the TRIGGERS
   tab if a spot never seems to trigger.
8. **Wrong prompt profile assigned** — if a trigger zone or Blue Iris alert is pointed at a
   profile whose questions don't match what's actually in frame (e.g. a driveway camera using
   the chicken-coop profile), you may get an analysis that never flags anything as
   alert-worthy simply because it's asking the wrong questions.

Note that license plate recognition ([section 10](#10-license-plate-recognition--known-vehicles))
is *not* in this list on purpose — a known or unknown plate never suppresses or forces an
alert, it only annotates one that was already going to fire.

## 16. Self-monitoring

Watches for the two ways the bridge can silently stop protecting you: Ollama becoming
unreachable, or the alert queue backing up faster than it's processed. It only messages you
(via the same Telegram bot) when the state actually *changes* — not on every check — so a
momentary blip doesn't page you, but real degradation gets caught before you'd otherwise
notice by just... not getting alerts. It also tells you when things recover, if
**Notify On Recovery** is on.

## 17. Bug reports

Off by default. When enabled, both the bridge and the dashboard automatically email a
developer address (fixed, not something you set) whenever something in the code hits an
internal error — not routine stuff like a camera being temporarily unreachable, but genuine
bugs: unhandled exceptions caught in the bridge's processing loop, the dashboard's trigger-zone
detection, and similar. Each report includes which process and part of the code it came from,
the error message, and the technical details (stack trace) needed to actually fix it.

It reuses whichever SMTP account is already configured under **Email** — there's no separate
mail setup for this, and if Email isn't configured, bug reports simply can't send (nothing
else breaks). A repeating error only emails once per hour rather than once per occurrence, so a
persistent issue doesn't flood the inbox.

## 18. Where your data lives

- `BlueIrisOllamaBridge/data/images/YYYY-MM-DD/` — saved Blue Iris alert snapshots (if
  **Save Images** is on)
- `BlueIrisOllamaBridge/data/events.db` — SQLite history of every Blue Iris alert
- `BlueIrisOllamaBridge.Web/data/cameras.db` — your added cameras
- `BlueIrisOllamaBridge.Web/data/triggers.db` — trigger zone configs and trigger events
- Recorded clips live alongside the trigger event data
- `homekit-bridge/persist/` — HomeKit pairing keys and this bridge's identity, if you're using
  HomeKit. Deleting it unpairs every iPhone and forces re-pairing with a new code, so it's
  worth keeping if you ever move the install to another machine.

Retention cleanup runs automatically according to **Retention Days** in Bridge settings; it
removes old images but not the `events.db` history rows themselves.

## 19. Troubleshooting

**A setting I just changed doesn't seem to be doing anything (Cameras/Triggers).** Zone and
camera config changes are picked up within about ten seconds of saving — give it a moment,
then check the browser actually reloaded the page (a stale cached page is a common cause of
"it's not showing up").

**I'm not seeing a toggle/field that should be there.** Hard-refresh the browser
(Ctrl+Shift+R / Cmd+Shift+R) — the dashboard is a plain static page with no build step, so a
cached old copy is the usual culprit.

**No image on a Feed card.** Either **Save Images** is off in Bridge settings, or the image
failed to save for that particular alert — the AI's text analysis is still recorded either
way.

**A clip never appears on the Triggers tab.** Clips only show up once the "seconds after
trigger" portion has finished recording — check back after that many seconds. Also confirm
**Save a video clip around each trigger** was on *before* the trigger happened; it can't
retroactively record footage that wasn't being captured yet.

**The bot won't answer me on Telegram.** It only responds to the exact Chat ID configured in
Telegram settings — confirm that's actually your chat, not a different one.

**License plate recognition never shows a plate.** Check camera resolution and angle first —
a general vision model needs the plate to be sharp and roughly frontal to read it at all. Also
confirm the toggle is actually on for the specific profile that camera uses, not just that
Known Vehicles has entries.

**No devices show up in Home Assistant.** Confirm Home Assistant's own MQTT integration is
configured and pointed at the same broker this app is publishing to, and that its discovery
prefix matches **Discovery Prefix** in Settings (both default to `homeassistant`, so this
usually only matters if either side was customized). Discovery messages are only published the
first time each camera sends an event after startup, so a camera needs at least one processed
event before it appears.

For anything that looks like the software itself is unhealthy (not starting, crashing,
unreachable), see the Troubleshooting section of [DEPLOYMENT.md](DEPLOYMENT.md).
