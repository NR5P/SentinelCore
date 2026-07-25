# Gate Watch — User Guide

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
8. [Prompt profiles — what the AI is told to look for](#8-prompt-profiles--what-the-ai-is-told-to-look-for)
9. [The Telegram bot](#9-the-telegram-bot)
10. [Email](#10-email)
11. [Why didn't I get an alert? (rate limiting & suppression, explained)](#11-why-didnt-i-get-an-alert-rate-limiting--suppression-explained)
12. [Self-monitoring](#12-self-monitoring)
13. [Where your data lives](#13-where-your-data-lives)
14. [Troubleshooting](#14-troubleshooting)

---

## 1. What this system does

There are two independent pieces running on your home server:

- **The bridge** (port 5080) — receives snapshot images from Blue Iris whenever it detects
  motion, sends each one to a local AI vision model (Ollama) with a set of questions, decides
  whether it's worth telling you about, and sends alerts (email, Telegram, etc.) when it is.
  This never leaves your network — the image and the AI both stay local.
- **The dashboard** (port 3320, "GATE_WATCH") — a web page for browsing what the bridge has
  seen, adding your own extra cameras (separate from Blue Iris), drawing motion-detection
  zones on them, and editing every setting above without touching a config file by hand.

Both pieces can also independently trigger the *same* AI analysis and the *same* alerts
(email/Telegram) — Blue Iris images always have, and your own added cameras can too if you
turn that on for a given camera's trigger zone. See [section 6](#6-the-triggers-tab--motion-zones-on-your-own-cameras).

## 2. Opening the dashboard

Point a browser at `http://<your-server's-LAN-IP>:3320`. There's no login — it's designed for
LAN-only use, so don't expose port 3320 (or 5080) to the internet.

Across the top you'll see a status indicator ("LINK ESTABLISHED" / "LINK LOST") showing
whether the dashboard can currently reach its own backend, and five tabs: **feed**, **live**,
**cameras**, **triggers**, **settings**.

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
| Alert prompt profile | Which prompt profile (see [section 8](#8-prompt-profiles--what-the-ai-is-told-to-look-for)) analyzes this camera's triggers. Only shown/used when the toggle above is on. |
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
- **Self-Monitoring** — watches the bridge's own health; see [section 12](#12-self-monitoring).
- **Webhook** — POSTs each analyzed event to another system (home automation, logging, etc.).
- **Prompt Profiles** — the actual instructions given to the AI, per camera. Its own section
  below, since there's a lot here.

Two things are deliberately *not* editable from Settings because they're baked in at process
startup no matter how they're stored: `Bridge:QueueCapacity`/`Bridge:DataDirectory` (sizes an
in-memory queue and opens a database connection once), and a few `*BaseUrl`/`*TimeoutSeconds`
fields that size an HTTP client. Changing those needs a restart regardless — see
[DEPLOYMENT.md](DEPLOYMENT.md).

## 8. Prompt profiles — what the AI is told to look for

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

Your original gate prompt is untouched and still keyed as `gate` — it's just labeled
**"Nathan's Default Gate Prompt"** in the list now so it's easy to tell apart from the generic
starter profiles. That `gate` name specifically also gets one extra behavior: when the AI
reports the gate as closed, a person or vehicle that's clearly still on the inside/camera side
won't trigger a notification just for being there — but someone on the outside/road side, an
active delivery, a newly-left package, or an animal still will. See
[section 11](#11-why-didnt-i-get-an-alert-rate-limiting--suppression-explained) for the full
explanation.

**Adding your own profile:** click **+ add profile**, fill in a Name and at least one
question, and save. **Deleting** works the same way as any other profile card — the `default`
profile is required and can't be removed, since it's the fallback for anything unmatched.

## 9. The Telegram bot

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

## 10. Email

**Email** sends individual alerts the same way Telegram does, with its own duplicate-cooldown
and confidence threshold. **Daily Summary** is a separate, additional digest email — sent
twice a day (configurable hours) regardless of whether individual alerts fired — covering
everything that happened, with a capped number of attached images so it doesn't get huge.

## 11. Why didn't I get an alert? (rate limiting & suppression, explained)

There are several independent layers that can each suppress a notification. If something
didn't alert when you expected it to, it's almost always one of these:

1. **The AI didn't think it was worth telling you about.** Every analysis includes a
   yes/no judgment call; if the AI says no, nothing downstream even considers alerting.
2. **Minimum Confidence** (Email/Telegram settings) — even when the AI does recommend a
   notification, if it isn't confident enough, that channel skips it. Different channels can
   have different thresholds.
3. **Duplicate Cooldown** (Email/Telegram settings, per-camera-per-category) — once a
   given kind of event (e.g. "vehicle at gate") has alerted for a camera, the same kind of
   event won't alert again on that camera until the cooldown expires. This is what stops one
   car sitting at the gate from spamming you repeatedly. A different kind of event (e.g. a
   person showing up while that vehicle is still there) still alerts normally.
4. **Gate-closed suppression** (only applies to the `gate` profile specifically) — when the
   gate is reported closed, a person or vehicle that's clearly settled on the inside/camera
   side is expected and doesn't alert by itself. Anyone on the outside/road side, an active
   delivery, a newly-left package, or an animal is still independently alert-worthy even with
   the gate closed. A single still image usually can't prove a vehicle is "arriving" vs.
   "already parked," so this logic is deliberately lenient toward vehicles — only a vehicle in
   a clearly settled resting spot is suppressed.
5. **Trigger zone rate limiting** (Triggers tab, per zone — separate from the two above,
   and it applies before the AI is even involved) — "Seconds between triggers" and "Max
   triggers per minute" limit how often *the zone itself* fires at all, regardless of what the
   AI would have said about any particular frame.
6. **Zone coverage** — a trigger zone only watches inside the polygon you drew. If someone
   stood just outside the drawn shape, nothing fires — check the zone shape in the TRIGGERS
   tab if a spot never seems to trigger.
7. **Wrong prompt profile assigned** — if a trigger zone or Blue Iris alert is pointed at a
   profile whose questions don't match what's actually in frame (e.g. a driveway camera using
   the chicken-coop profile), you may get an analysis that never flags anything as
   alert-worthy simply because it's asking the wrong questions.

## 12. Self-monitoring

Watches for the two ways the bridge can silently stop protecting you: Ollama becoming
unreachable, or the alert queue backing up faster than it's processed. It only messages you
(via the same Telegram bot) when the state actually *changes* — not on every check — so a
momentary blip doesn't page you, but real degradation gets caught before you'd otherwise
notice by just... not getting alerts. It also tells you when things recover, if
**Notify On Recovery** is on.

## 13. Where your data lives

- `BlueIrisOllamaBridge/data/images/YYYY-MM-DD/` — saved Blue Iris alert snapshots (if
  **Save Images** is on)
- `BlueIrisOllamaBridge/data/events.db` — SQLite history of every Blue Iris alert
- `BlueIrisOllamaBridge.Web/data/cameras.db` — your added cameras
- `BlueIrisOllamaBridge.Web/data/triggers.db` — trigger zone configs and trigger events
- Recorded clips live alongside the trigger event data

Retention cleanup runs automatically according to **Retention Days** in Bridge settings; it
removes old images but not the `events.db` history rows themselves.

## 14. Troubleshooting

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

For anything that looks like the software itself is unhealthy (not starting, crashing,
unreachable), see the Troubleshooting section of [DEPLOYMENT.md](DEPLOYMENT.md).
