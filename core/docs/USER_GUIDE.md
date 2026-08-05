# Sentinel Core — User Guide

This is the guide to actually *using* the system day to day: the dashboard, the AI alerts,
the trigger zones, the Telegram bot, and every setting you can tune. For installing or
rebuilding the software itself, see [DEPLOYMENT.md](DEPLOYMENT.md) instead.

## Contents

1. [What this system does](#1-what-this-system-does)
2. [Opening the dashboard](#2-opening-the-dashboard)
2a. [The iOS, Android, and Apple TV apps](#2a-the-ios-android-and-apple-tv-apps)
3. [The EVENTS tab — everything the cameras caught](#3-the-events-tab--everything-the-cameras-caught)
3a. [The TIMELINE tab — trigger-clip review](#3a-the-timeline-tab--trigger-clip-review)
4. [The LIVE tab — viewing your own cameras](#4-the-live-tab--viewing-your-own-cameras)
5. [The CAMERAS tab — adding your own cameras](#5-the-cameras-tab--adding-your-own-cameras)
5a. [Pan/Tilt/Zoom (PTZ) — moving cameras that support it](#5a-pantiltzoom-ptz--moving-cameras-that-support-it)
5b. [Gate/door open check — a safety check independent of motion alerts](#5b-gatedoor-open-check--a-safety-check-independent-of-motion-alerts)
5c. [Camera Groups — cross-camera recording & incident correlation](#5c-camera-groups--cross-camera-recording--incident-correlation)
6. [Motion zones on your own cameras](#6-motion-zones-on-your-own-cameras)
6a. [The AUTOMATIONS tab — custom MQTT & webhook rules](#6a-the-automations-tab--custom-mqtt--webhook-rules)
6ab. [Schedules — doing something at a time, at sunset, or when it gets dark](#6ab-schedules--doing-something-at-a-time-at-sunset-or-when-it-gets-dark)
6b. [Multi-frame analysis — reducing false positives from a single frame](#6b-multi-frame-analysis--reducing-false-positives-from-a-single-frame)
7. [The SETTINGS tab](#7-the-settings-tab)
7a. [System health — how hard the server is working](#7a-system-health--how-hard-the-server-is-working)
8. [Vision analysis backend — local, cloud, or both](#8-vision-analysis-backend--local-cloud-or-both)
9. [Prompt profiles — what the AI is told to look for](#9-prompt-profiles--what-the-ai-is-told-to-look-for)
10. [License plate recognition & known vehicles](#10-license-plate-recognition--known-vehicles)
10a. [Known Faces — local face recognition](#10a-known-faces--local-face-recognition)
11. [The Telegram bot](#11-the-telegram-bot)
11a. [Discord alerts](#11a-discord-alerts)
12. [Email](#12-email)
13. [MQTT & Home Assistant](#13-mqtt--home-assistant)
13a. [The DEVICE HUB tab — controlling lights & other MQTT devices](#13a-the-device-hub-tab--controlling-lights--other-mqtt-devices)
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
turn that on for a given camera's trigger zone. See [section 6](#6-motion-zones-on-your-own-cameras).

## 2. Opening the dashboard

Point a browser at `http://<your-server's-LAN-IP>:3320`. There's no login by default — it's
designed for LAN-only use, so don't expose port 3320 (or 5080) to the internet unless you've
turned on the login (see below) and put it behind HTTPS yourself.

**About the "Not Secure" label.** Browsers show this because the dashboard is served over plain
HTTP, which is fine on a home network you trust — nothing is broken. If it bothers you, or if
you've turned the login on and would rather your password wasn't sent in the clear, HTTPS is
available; see [DEPLOYMENT.md](DEPLOYMENT.md) section 14a. It isn't automatic, because no
certificate authority on the internet will issue a certificate for a private address like
`192.168.1.50`, so it takes either your own certificate authority or a domain you own. If you
ever see the dashboard warn that it was reached from *outside* your network, take that one
seriously — that's a different thing entirely, and means the port is more exposed than intended.

Across the top you'll see a status indicator ("LINK ESTABLISHED" / "LINK LOST") showing
whether the dashboard can currently reach its own backend, and eight tabs: **feed**,
**timeline**, **live**, **cameras**, **triggers**, **automations**, **docs**, **settings**.

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
notice appears under the dashboard's top bar, and an "UNLICENSED" watermark appears across
snapshot images, live video, and trigger clips. The watermark shows everywhere you can see a
camera, not just in the dashboard - the iPhone, iPad, Mac, Android and Apple TV apps all draw the
same mark, for everyone signed in, whatever their account role. Both are informational only -
every feature, including viewing those images and video, keeps working exactly as normal either
way, nothing is blocked. To clear it, open SETTINGS → LICENSE and upload the `.lic` file you were
emailed - it's verified and written to disk immediately, no restart needed. The apps pick the
change up on their next refresh.

## 2a. The iOS, Android, and Apple TV apps

Everything above describes the web dashboard, but native apps exist for iPhone/iPad, Android,
and Apple TV too. They talk to the same server over the same API — there's no separate account
system, and turning login on or off in the dashboard (see above) applies to every app equally.

**Finding your server.** The first time you open any of the apps, the setup screen searches
your local network automatically and shows *"Found on your network"* with any server it locates
by name — tap one to connect. This uses the same local-network discovery the server advertises
by default (`Discovery: Enabled` in `SentinelCore.Web/appsettings.json` — see
[DEPLOYMENT.md](DEPLOYMENT.md) section 14c); if that's been turned off, or the phone is on a
different network/VLAN than the server, nothing will show up. Either way, a manual address field
(`http://192.168.1.50:3320`) is always available right there too, so discovery is a convenience,
never a requirement.

**iOS and Android** are close to full parity with the web dashboard: Events, Timeline, Live
view, adding/editing cameras, PTZ control, trigger zones, Automations, and every Settings section
including [Known Faces](#10a-known-faces--local-face-recognition) enrollment (with a direct
camera-capture option the web UI doesn't have) and [System health](#7a-system-health--how-hard-the-server-is-working).

On both, the Live grid can be arranged the same way the dashboard's can: press and hold a tile,
then drag it over the one whose place you want it to take. The arrangement is stored against
your login on the server, not on the phone — so the order you set on your iPhone is the order
you'll find on your Android tablet and in your browser, and nobody else's grid moves. The Apple
TV app shows that same arrangement but can't change it — a remote is a poor drag device, so
arrange it on a phone or in the dashboard and the TV follows.

**Apple TV** is deliberately smaller in scope — live camera viewing, the alert feed, trigger
history, and settings, but no camera/account administration and no PTZ controls (those stay on
iOS/Android/web, where they're easier to use anyway). Its one feature the other apps don't have:
if a high-confidence alert fires while the Apple TV app itself is the one on screen, it
automatically switches to a full-screen live view of the camera that triggered — no need to
leave what you're doing and go find the right camera. If you're watching something else (Apple
TV+, another app), you still get a normal system notification banner, just not the automatic
full-screen switch — tvOS doesn't allow one app to draw over another app's video.

Push notifications (a phone/TV alert even when the app itself isn't open) are configured
separately — see [docs/PUSH_NOTIFICATIONS.md](PUSH_NOTIFICATIONS.md).

## 3. The EVENTS tab — everything the cameras caught

One list of everything that has happened, newest first, refreshing itself every 30 seconds (or
click **refresh** to force it). It covers both sources at once: alerts Blue Iris sent over, and
motion your own cameras caught in a trigger zone (section 6).

This used to be two tabs — FEED for analysed alerts and TRIGGERS for motion. A camera that
forwards its motion for AI analysis produces both records for a single moment, so the same event
appeared twice, once with the description and once with the clip. They are now joined into one
card per thing that happened.

Each card shows:

- The snapshot image
- Which camera it came from
- How long ago it happened, and the exact time
- A **▶ clip** badge when a clip was recorded around it — click the image to play it, rather
  than opening the still
- A green **notified** badge if this event actually triggered an alert — hover it to see why

What else a card shows depends on whether anything analysed the frame:

- **Analysed** — the AI's plain-English description of what it saw, and the event type it
  classified it as (e.g. person at gate, vehicle entering).
- **Motion only** — the name of the zone that caught it, in italics, and a `motion only` label
  instead of an event type. Nothing described it because nothing looked at it: that camera has
  "Send triggers to the AI and alert pipeline" switched off, or the hand-off was refused. Turn
  that toggle on for the camera (section 6) if you want descriptions for it.

If an analysed card has no image, either `Bridge:SaveImages` is off or the image failed to save;
the AI's text analysis is still recorded either way.

**show hidden** lists motion the AI rejected as nothing (marked ⚠ nothing found) plus anything
still awaiting a verdict (… checking) — see "Only showing triggers that turned out to be real"
in section 6.

**Ask a question.** The box above the filters lets you ask in plain English - "what happened
last night?", "did anyone come to the gate today?", or a how-to/configuration question like
"how do I turn on quiet hours?". The same local AI that analyzes your alerts answers using
either your recent event history or this User Guide, whichever the question is actually about -
never both at once, which keeps answers fast and on-topic. It's the same underlying feature as
the Telegram bot's chat (Settings → Telegram Chat controls it either way - off there means off
here too), just usable without a Telegram account. Questions about your cameras answer in
seconds; how-to/configuration questions read through part of this guide first and can take
closer to a minute on a modest local machine - both are normal.

**Searching and filtering.** Above the list is a filter bar:

- A text box that searches the AI's description (e.g. searching "package" finds every event
  whose summary mentions one), debounced so it doesn't refetch on every keystroke
- A **camera** dropdown and an **event type** dropdown, both populated from everything that's
  ever appeared in history — not just what's currently shown
- **From** / **to** date fields to bound the search to a range
- A **notified only** toggle to hide everything that didn't actually alert

The **event type**, **notified only**, and text-search filters describe the AI's own output, so
switching any of them on leaves out motion-only rows — nothing analysed those, so they can't
match. Camera and date filters apply to everything.

Every filter combines with the others (all must match), and the count at the top of the tab
reflects the current filter, not the total history. **clear** resets everything back to the
unfiltered, most-recent-50 view.

## 3a. The TIMELINE tab — trigger-clip review

This is a day index of Sentinel's AI events, not a continuous 24-hour recording. Pick a camera
and date to see its events across the local day. Each short green range is a saved trigger
clip, limited to the configured seconds before and after that event; no video is saved for the
space between green ranges. Colored marks show every AI event, including events whose source
clip has since been removed. Drag inside a green range or click an event to inspect it, use the
previous/next controls to move between events, or press **play** to review the clips in order.
Playback automatically skips every unsaved gap.

The object filter covers people, vehicles, animals, packages, plates, and other detections.
The severity filter is derived from the AI confidence and whether Sentinel recommended a
notification. Review state is stored by the Sentinel server, so marking an item reviewed on
an iPhone is immediately reflected on Android, macOS, iPad, and the web dashboard.

For Blue Iris cameras, the trigger recording remains in Blue Iris. Sentinel securely reads the
alert-linked clip and proxies timestamp frames, so Blue Iris credentials are never sent to a
phone or browser. The timeline's `EventClipPreSeconds` and `EventClipPostSeconds` settings
control the window Sentinel exposes; configure Blue Iris to record at least the same pre- and
post-trigger duration and to record only when triggered if that is your retention policy.
Sentinel does not create or retain a full-day Blue Iris copy.

For cameras recorded directly by Sentinel, its recorder maintains only a small rolling
pre-trigger buffer and writes an MP4 for the configured trigger window. It continuously
deletes unused buffer pieces rather than archiving the day.

For cameras whose friendly event name differs from the Blue Iris short name, add a mapping in
the main app's `BlueIris` settings:

```json
"CameraMappings": {
  "gate camera front door": "gateCamera",
  "driveway": "DrivewayHD"
}
```

Blue Iris's web server must be enabled and reachable from the Sentinel server. The configured
Blue Iris account must be allowed to view clips. If a day shows its AI marks but says
**trigger clips unavailable**, verify the Blue Iris URL, account, permissions, camera short
name, and mapping. If an individual event says **NO CLIP**, its Blue Iris alert record was not
found or its clip is no longer retained. The Blue Iris alert action must send `&ALERT_DB` in
the `alertId` field (the supplied PowerShell command already does). A mapping is needed for each
Blue Iris camera you want to review.

## 4. The LIVE tab — viewing your own cameras

Shows live video from cameras you've added yourself (see the next section) — **not** your
Blue Iris cameras, and this view has nothing to do with alerts or the AI. Streams only start
while you have this tab open and stop the moment you leave it, so it's not something you'd
leave open all the time.

**Arranging the tiles.** Grab the **⠿** handle in the top-right corner of any tile and drag it
where you want it — the camera you care about most can sit first instead of wherever the
alphabet put it. (Keyboard: focus a handle with Tab and use the arrow keys.) The order is saved
against **your own login**, so everyone with an account arranges their own grid and nobody
rearranges anyone else's. If login is turned off, there's only one arrangement, shared by
everyone using the dashboard. Cameras added later join the end of the grid rather than pushing
your tiles around, and deleting a camera leaves the rest where you put them.

### 4a. Making the live view real-time — install go2rtc

Out of the box, live tiles play over HLS: dependable everywhere, but structurally a few seconds
behind the camera — HLS downloads video in chunks, a chunk can't be downloaded until it has
finished being recorded, and chunks can only start on a keyframe, so the camera's own keyframe
interval sets a floor under the delay that no amount of tuning removes.

Installing **[go2rtc](https://github.com/AlexxIT/go2rtc)** on the server lifts that floor: the
dashboard automatically switches its live tiles to WebRTC, which pushes frames continuously and
typically sits a few hundred milliseconds behind the camera. Nothing else changes — recording,
motion detection, snapshots, and the phone/TV apps all keep working exactly as before, and any
tile that can't establish WebRTC (older browser, or viewing through a Cloudflare Tunnel, which
doesn't carry WebRTC's UDP traffic) quietly falls back to HLS on its own.

Setup is: install the single binary, restart the server. On a Mac `brew install go2rtc`; on
Linux download the binary for your architecture from the go2rtc releases page, `chmod +x` it,
and put it somewhere on the PATH (e.g. `/usr/local/bin/go2rtc`). If you'd rather keep it
elsewhere, set `Go2Rtc:Path` in `SentinelCore.Web/appsettings.json` to its full path. The
server starts and supervises it by itself — there is no go2rtc config to write, and its own API
is kept loopback-only (the dashboard always goes through the server's login). If another go2rtc
already runs on the box (Frigate bundles one), that's fine — this one uses its own ports
(11984/18554/18555, changeable under `Go2Rtc:` in `appsettings.json`).

### 4b. If you're staying on HLS: check the camera's keyframe interval

Without go2rtc, the one setting that genuinely reduces live delay is on the **camera itself**:
the keyframe / I-frame interval (Reolink calls it **Interframe Space**). Set it to 1 second —
or `1x` where it's expressed as a multiple of the frame rate. A camera sending a keyframe every
4 seconds forces roughly 8–12 s of delay; every 1 second brings that to roughly 3–4 s.

Do **not** lower the frame rate to chase latency: most cameras express the keyframe interval in
frames, so fewer frames per second means keyframes arrive *further apart in time* — more delay,
not less.

## 5. The CAMERAS tab — adding your own cameras

Click **+ add camera** to add an IP camera by its stream URL (`rtsp://` or `http://`), with
optional username/password and a toggle for whether to include audio. Once added, a camera
shows up on the **LIVE** tab for viewing and on the **TRIGGERS** tab for motion detection.

This is completely separate from your Blue Iris setup — Blue Iris keeps working exactly as it
always has, whether or not you add any cameras here.

**Don't know your camera's stream URL?** Two ways to find it without guessing:

- **Test Connection**, on the add-camera form — type in the camera's IP address (or as much of
  the URL as you know) and optional login, and click it. It checks the camera's own discovery
  protocol (ONVIF, which most IP cameras speak) first, and if that comes back empty, tries a
  handful of common stream address patterns. If it finds a working stream, it fills in the real
  URL and a suggested name for you — it never overwrites a URL you've already typed a real path
  into.
- **Discover Cameras**, next to Add Camera — scans your network for cameras all at once, the
  same way. It only runs when you click it — nothing scans automatically or on a schedule. You'll
  be asked which network adapter to scan on machines with more than one; cameras it already knows
  about are marked so they don't show up as new. Cameras it finds that need a login can't be
  added straight from the scan (there's no way to ask for creds for every camera before knowing
  how many there are) — click **add** and use Test Connection on the form to supply them.

Either way, this only ever probes addresses you asked about — a single IP for Test Connection,
or the network segments you explicitly selected for Discover Cameras.

## 5a. Pan/Tilt/Zoom (PTZ) — moving cameras that support it

Some IP cameras can physically move — pan left/right, tilt up/down, zoom in/out — and this
dashboard can control that, if the camera supports the ONVIF PTZ standard (most cameras with a
motorized head do; ones with no moving parts at all simply don't have this).

**Turning it on:** click **Test Connection** on the camera's add/edit form. If it finds a
working ONVIF PTZ service, the **Pan/Tilt/Zoom (PTZ) Joystick** toggle turns on by itself — you
don't need to do anything else. You can also flip it on by hand for a camera Test Connection
didn't detect PTZ for, but the on-screen controls won't do anything without one; the toggle
doesn't create PTZ support, it only shows controls for support that's already there.

**Using it:** open the camera in the **LIVE** tab and tap it to go full-screen (or, on the web
dashboard, look at the bottom-right corner of its tile directly) — a small joystick appears:
eight directional arrows around a home button, plus separate zoom in/out buttons. Press and hold
a direction to move the camera that way; release to stop. The camera keeps moving the whole time
a button is held down, the same way a physical joystick would, rather than moving a fixed amount
per tap. The home button (⌂) returns to the camera's saved home position, if it has one.

PTZ movement is treated the same as adding/editing/deleting a camera — it requires an admin
account when login is turned on, not just a viewer login.

## 5b. Gate/door open check — a safety check independent of motion alerts

Every other alert in this app is reactive: something has to move in front of a camera before
anything gets analyzed. A gate or door left open with nobody around never trips a motion alert,
so nothing would ever notice — this feature closes that gap. On its own schedule, independent of
Blue Iris and trigger zones, it grabs a snapshot from a camera and asks the AI whether whatever
it's watching is open or closed, and only notifies when it actually changes from closed to open
(not on every check, so a gate that's been open for hours doesn't spam you every few minutes).

**Turning it on:** on the **CAMERAS** tab, click **gate check** on the camera watching your gate
or door. Give it a short label describing what's in frame (e.g. "the front gate" or "the garage
door" — be specific if the camera sees more than one thing), and how often to check. Each check
is a real AI analysis, so shorter intervals mean more Ollama/cloud API load — every 10 minutes is
a reasonable default for most gates.

**Also check whether the camera's own view is blocked:** on by default alongside the open/closed
check, using the same snapshot (no extra AI calls). Notifies once if the lens appears covered,
spray-painted, badly out of focus, or physically moved — a normal dark nighttime frame doesn't
count as blocked.

**Reminders:** leave "remind again if still open" blank to only be notified once when it first
opens. Set it to a number of hours to also get a reminder if it's still open after that long —
useful for a gate that tends to get left open and forgotten rather than closed again quickly.

Notifications from this feature go through the same Email/Telegram/MQTT/HomeKit/push channels as
every other alert, and appear in the EVENTS tab like any other event — it's a different way of
noticing something worth telling you about, not a separate notification system.

## 5c. Camera Groups — cross-camera recording & incident correlation

A trigger zone (see the next section) watches one camera in isolation. **Camera Groups** are for
when several cameras cover the same physical path — a gate, then the driveway, then the front
door — and you want a trigger on one of them to pull the others in too, instead of getting three
separate, seemingly-unrelated events.

**Turning it on:** on the **CAMERAS** tab, click **camera groups**, then **+ add group**. Give it
a name, and pick at least two cameras to include — a camera can only belong to **one** group at
a time. Three more settings:

| Setting | What it does |
|---|---|
| Correlation window | How many seconds after one camera in the group fires count as "the same incident" for the others (default 45s — a rough walking-time estimate between typically-spaced cameras). |
| Record companion clips | On by default. When one camera fires, grabs a snapshot from every other camera in the group too, and records a video clip for it if that camera's own trigger zone has clip recording turned on. |
| Also analyze companion cameras with AI | Off by default. Sends every companion camera's snapshot through its own full AI analysis (using that camera's own prompt profile) and its own Feed entry/notification, rather than just a recorded clip. Costs proportionally more AI processing per trigger — leave it off unless you specifically want independent alerts from every camera in the group, not just the one that actually triggered. |

**What happens:** when a camera in a group fires its trigger zone, the other members are pulled
into the same incident — their current snapshot (and clip, if enabled) is captured immediately,
tagged as part of the same event. A camera with no trigger zone of its own, or with its zone
disabled, is simply skipped rather than causing an error. On the **TRIGGERS** tab, events that
share an incident show a **🔗 +N cameras** badge — hover it to see which other cameras were part
of the same incident. The triggering alert's notification also gets an extra line, *"Also
recorded: CameraA, CameraB"*, listing what else was captured alongside it.

**What it does not do:** grouping cameras never changes each camera's own trigger-zone settings
(shape, sensitivity, rate limiting) — it only adds companion recording/analysis on top of
whatever each camera was already going to do on its own.

## 6. Motion zones on your own cameras

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
| Trigger from a sensor | Fires this camera when a Device Hub sensor reports — see below. |
| Fire when the sensor reads / Sensor cooldown | Which sensor reading fires the camera, and how long to ignore repeats afterwards. |
| Trigger address | The web address that fires this camera from outside — see below. |

**About "send to alert pipeline":** Blue Iris images and your own trigger-zone cameras can
both be active and both alert independently — nothing about turning this on for a camera
stops or changes how Blue Iris alerts work. This is useful for comparing two cameras that
cover overlapping ground (e.g. Blue Iris's gate camera and your own gate-facing camera both
set to the `gate` profile), or for adding AI-backed alerting to a camera Blue Iris doesn't
know about at all — a chicken coop camera on the `chicken-coop-predator` profile, for
instance.

Motion caught by any zone appears on the [EVENTS tab](#3-the-events-tab--everything-the-cameras-caught)
alongside everything else, each with a thumbnail — click it to view the snapshot, or play the
clip if one was recorded (marked with a ▶ clip badge).

### Only showing triggers that turned out to be real

Motion detection is a frame comparison — it can tell that pixels changed, not what changed. On
a windy or rainy night that means branches, rain, moths on the IR illuminator, and headlight
sweep all land on the EVENTS tab looking exactly like a person would.

**AI-confirmed triggers** (SETTINGS → AI-CONFIRMED TRIGGERS, **on by default**) fixes that. A
trigger is held back until the AI has actually looked at the frame, and only appears on the tab
if it found a person, vehicle, package, or animal. The video and snapshot are still recorded
the moment motion fires — the AI decides whether you *see* it, never whether it's *kept*.

A few things worth knowing:

- **It only applies to cameras with "Send triggers to the AI and alert pipeline" on.** Nothing
  ever analyzes the frames from the other cameras, so their triggers appear immediately, as
  before. If you turn this on and a camera's triggers are still noisy, that camera's send-to-AI
  toggle is the thing to check.
- **Nothing is ever deleted.** Rejected triggers and their clips stay on disk. The **show
  hidden** button on the EVENTS tab lists them, marked ⚠ nothing found, so you can check the
  filter isn't hiding things it shouldn't.
- **Triggers appear with a delay** — as long as the analysis takes, which on a CPU-only box can
  be a minute or more. Until then they're marked "… checking" under **show hidden**.
- **It fails toward showing you things.** If the analysis is slow, errors, or can't be reached,
  the trigger is shown unjudged rather than hidden. Only the AI actually looking and finding
  nothing hides a trigger. "Give up after" sets how long to wait before giving up that way.

### Triggering a camera from something other than motion

A zone watches the video. These two let something *else* decide the camera should look — and
both work on a camera with no zone drawn at all. Either way the result is identical to a zone
trigger: a snapshot, a row on the EVENTS tab, and the same AI analysis and alerts.

**From a sensor.** Pick any sensor the [Device Hub](#13a-the-device-hub-tab--controlling-lights--other-mqtt-devices)
knows about in **Trigger from a sensor** — a proximity sensor at the gate, a PIR in a shed, a
contact switch on a door. The moment it reports, the camera is captured. Only a *change* fires
it, so a sensor that republishes its state every few seconds still triggers once, and the state
it holds when the server starts up is never treated as a fresh trigger. Requires MQTT and the
Device Hub to be on; only devices saved with kind **sensor** are offered.

**From a web address.** Turn on **TRIGGER API** under Settings and each camera's zone editor
shows a **Trigger address** — fetching it fires that camera. This is for anything that can open
a URL but can't log in: a doorbell, an NVR's alert action, a home-automation script, a cron
line.

```bash
curl "http://your-server:3320/api/cameras/<camera-id>/trigger?key=<your-key>"
```

Add `&profile=gate` to analyze it with a specific [prompt profile](#9-prompt-profiles--what-the-ai-is-told-to-look-for)
instead of the camera's usual one. You get `429` back while the camera is inside its cooldown.

The key is in the address, which means it ends up in logs and browser history — so it is its
own key, separate from the one Blue Iris uses, and clearing the field in Settings and saving
issues a new one (which stops every device still using the old one). The trigger address is
refused outright on the Internet Exposed port: it is for things on your own network.

## 6a. The AUTOMATIONS tab — custom MQTT & webhook rules

The MQTT and Webhook channels under [Settings](#13-mqtt--home-assistant) each send everything
to one fixed destination — every event to the same topic prefix, or every notifiable alert to
the same URL. The **AUTOMATIONS** tab is for when you want something more specific: *when
camera X sees Y, do Z* — a different MQTT topic per detection, a call to a different API
endpoint depending on which camera fired, or several rules covering different automations at
once. Any number of rules can be active at the same time.

Click **+ add rule** to create one:

| Field | What it does |
|---|---|
| Name | A label for your own reference, e.g. "Gate — package delivered". |
| Cameras | Comma-separated camera names this rule watches. Must match the name exactly as shown on the EVENTS tab (the Blue Iris camera name, or one of your own added cameras). Leave blank to match every camera. |
| Detected categories | Which kinds of detection fire the rule — person at gate, vehicle entering, package delivered, animal, etc. Leave every box unchecked to match any detection, including ones that don't fall into a named category. |
| Minimum confidence | Only fires when the AI's confidence is at least this. |
| Only when a notification is recommended | On by default — skips alerts the AI itself judged not worth telling anyone about. |
| Cooldown minutes | Suppresses repeats of the same camera/category within this window, so one lingering car doesn't fire the rule over and over. |
| Action | Either **Publish a custom MQTT message** or **Call an API endpoint (webhook)**. |

**MQTT action.** Requires the MQTT integration to be turned on under Settings first (rules
reuse that same broker connection). Set a **topic** and a **payload** — both accept the
`{{token}}` placeholders listed at the bottom of the form (camera, summary, confidence,
category, vehicle/person/package/animal details, and more), so a topic like
`home/sentinelcore/{{camera}}/{{category}}` or a JSON payload with `{{summary}}` embedded in it
both work. **Retain** keeps the broker holding the last message for any new subscriber — turn
it on for a status-style topic, leave it off for a one-off event notification.

**Webhook action.** Set a URL, HTTP method, optional headers (one `Name: Value` per line — also
token-aware, so something like `Authorization: Bearer {{camera}}` works if you needed it to),
and a body. The URL and body both accept the same `{{token}}` placeholders as MQTT.

Every rule card has a **test** button that fires it immediately against sample data — useful
for checking a topic/payload or URL/body actually work before waiting for a real detection. The
result (published topic, or the HTTP status the endpoint returned) shows next to the button.

## 6ab. Schedules — doing something at a time, at sunset, or when it gets dark

Automation rules above fire when a camera sees something. **Schedules** fire when the clock — or
the sun — says so, with no camera involved. They share the same "AUTOMATIONS" tab: switch to the
**at a time** view at the top of it. In the iPhone, Mac, and Android apps they live under
**Settings → Manage Schedules**.

Each schedule is two halves: **when** it fires, and **what** it does.

### When

| Timing | What it means |
|---|---|
| At a time of day | A wall-clock time, on the days you choose. It stays that time on both sides of a daylight-saving change — a 07:00 schedule is 07:00 in June and 07:00 in December. |
| Sunset / sunrise / darkness | Fires relative to where the sun actually is here. See below. |
| Every so often | Every N minutes or hours. Without a window, firings line up with the clock — every 15 minutes means :00, :15, :30, :45. With one, they run only between the two times you set, and a start later than the end means a window that crosses midnight (22:00 → 06:00). |
| Once | A single date and time. The schedule switches itself off afterwards, keeping its history. |
| Cron | A standard five-field cron expression (`0 6 * * 1-5` is 6am on weekdays) for anything the others can't express. A leading sixth field adds seconds. |

### Following the sun, and "this darkness every night"

This is the option to use for anything that should track the seasons on its own — a coop door,
outdoor lights, a gate that should be shut after dark. Rather than a clock time you would have to
keep adjusting, you pick **how dark it should be**, and the server works out what time that
happens here, today:

| Level | The sun is | Roughly |
|---|---|---|
| Sun on the horizon | 0° | Sunset itself — still broad daylight to look at. |
| Getting dark | 6° below | Civil twilight. Colours are fading, outdoor lights are coming on. **Chickens have gone in to roost by now**, which makes this the usual choice for shutting a coop. |
| Properly dark | 12° below | Nautical twilight. The horizon is no longer distinct. |
| Fully dark | 18° below | Astronomical twilight — the last of the sun's light is gone. |

You can also enter any angle in between. **Offset** shifts the firing either side of that moment —
20 minutes after "getting dark" gives the birds a few minutes to finish going in.

Because this is worked out from the sun's real position, the clock time moves through the year by
well over an hour by itself. Nothing needs changing in spring or autumn.

**This needs your location.** Set it once under **Settings → LOCATION**: type a town and press
Find, or enter the latitude and longitude yourself. Only the two numbers are saved, and they are
used for nothing but this calculation — it happens on your own machine, with nothing sent
anywhere. (Pressing Find is the one exception: the place name you typed goes to OpenStreetMap's
public lookup service to get the coordinates back. On a server with no internet access, that
button reports it can't reach the service and you enter the numbers directly.)

At high latitudes some darkness levels genuinely never happen in midsummer — the sun never gets
18° below the horizon in a Scottish June. A schedule set to one of those simply doesn't fire on
those nights, and picks up again when it can.

### What it does

| Action | Notes |
|---|---|
| Switch a device | Anything in your [Device Hub](#13a-the-device-hub-tab--controlling-lights--other-mqtt-devices) — a light on or off, a gate open or closed, a button pressed. The form offers only the actions that device understands, plus brightness for devices that dim. |
| Publish MQTT | A topic and payload of your choosing, exactly like an automation rule's MQTT action. |
| Call an API endpoint | A URL, method, headers, and body — again as per automation rules. |

MQTT and webhook actions accept `{{token}}` placeholders, though a scheduled firing has no camera
or analysis behind it, so the available set is a small one: `{{schedule_name}}`, `{{now}}`,
`{{due_at}}`, `{{run_id}}`, and a couple more listed under the form.

### If the server was off when it was due

Every schedule chooses what should happen to a firing it missed:

- **Skip it** (the default) — records that it was missed and moves on. Right for anything where
  being late is pointless, like flashing a light.
- **Run it once on the way back up** — right for anything where the *state it leaves behind* is
  the point, like arming at night or shutting a door.

Either way there's a limit on how late is too late (an hour by default, under **Settings →
SCHEDULES**), so a machine that was off overnight doesn't shut the coop at breakfast.

### Checking it actually worked

Every schedule shows when it next runs and how the last run went, and every firing is recorded —
successes, failures, skips, and misses alike. Press **history** on a schedule to see the recent
ones with what came of each ("switched Coop Door off", or the error if it didn't). If MQTT is
switched off, or a device has been deleted, the failed run says exactly that rather than the
schedule quietly doing nothing.

**Run now** does the action immediately, ignoring the timing — the way to test a new schedule, and
the way to shut the coop early on a particular evening. It really does switch the device.

## 6b. Multi-frame analysis — reducing false positives from a single frame

A single still image is sometimes ambiguous — a shadow, a headlight sweep, a reflection, or an
insect near the lens can look enough like a person or animal to fool a one-frame analysis. This
feature asks the AI to look at a couple of frames from the same trigger, a fraction of a second
apart, and reason about whether whatever it's looking at behaves like a real, physically
consistent subject rather than a one-frame artifact.

**Turning it on:** in **Settings → Advanced**, open **Multi-Frame Analysis** and enable it. Two
settings: how many extra frames to capture beyond the first (1 or 2), and how many milliseconds
apart to space them (150–1000ms, default 350ms).

**What happens:** all frames from that trigger — the original plus the extras — are sent to the
AI together in one combined analysis, in chronological order, with an instruction to only report
a person/vehicle/animal if it appears consistently across the frames rather than in just one of
them. This is one AI call per alert, not one call per frame, so it doesn't multiply notification
latency the way running several separate analyses would — but it does still cost somewhat more
processing time per alert than a single frame, since there's more image data to look at. Leave it
off if single-frame analysis is already comfortably fast and accurate for your cameras.

**What it applies to:** only your own added cameras' trigger zones that have **Send triggers to
the AI and alert pipeline** turned on (see [section 6](#6-motion-zones-on-your-own-cameras)).
Blue Iris alerts always arrive as a single image and are unaffected either way. Camera Groups'
optional companion-camera analysis (previous section) also always uses a single frame regardless
of this setting, to keep its own added cost predictable.

## 7. The SETTINGS tab

Every section below saves independently and applies live — no restart needed, except where
noted. Click a section header to expand it; each one opens with a short paragraph explaining
what it's for and whether you're likely to need it. Hovering a field label gives a one-line
explanation of that specific setting (on a desktop — hover tips don't exist on a touchscreen,
which is why the section paragraphs are there).

**Basic and advanced.** There's a switch at the top right of this tab. **Basic** — the default —
shows only the settings most people actually change: Bridge, Email, Daily Summary, Telegram
Alerts, Discord Alerts, Outage Alerts, Internet Outage Alerts, Quiet Hours, Bug Reports, Apple
HomeKit, Backup, Login and License. **Advanced** adds everything else: the AI tuning knobs,
image processing, self-monitoring thresholds, webhooks, MQTT, known vehicles, known faces,
multi-frame analysis, and prompt profiles.

Nothing is ever lost or reset by switching — advanced settings keep working exactly as
configured while hidden, and the choice is remembered on that device only, so a phone can stay
simple while a desktop shows everything.

The full list below covers both modes:

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
- **Discord Alerts** — posts alert cards and optional snapshots to a Discord channel using one
  incoming webhook URL; no bot application is required.
- **Blue Iris Snapshot** — lets the `/photo` bot command pull a live image straight from Blue
  Iris on demand.
- **Self-Monitoring** — watches the bridge's own health; see [section 16](#16-self-monitoring).
- **Outage Alerts** — emails you (at the same address as your alerts) when the server starts up
  after having been off for a while, so a power cut doesn't go unnoticed until you next open the
  Feed. The email says how long it was down and whether it was shut down deliberately or lost
  power. Two settings: a minimum downtime, so ordinary restarts and updates stay silent, and how
  often the server records that it's alive. Note the limit: this can only tell you the server
  *came back*. If the machine stays off, nothing is running to send the email — for that you'd
  need something outside the house watching.
- **Internet Outage Alerts** — **off by default.** The same idea as Outage Alerts, but for the
  line rather than the machine: every minute the server checks that it can still reach the
  internet, and once the connection comes back it emails you *and* messages your Telegram chat
  saying how long you were offline. Sentinel Core itself keeps running and recording throughout —
  what stops is anything that has to leave the building, so alerts raised during an outage were
  never delivered and are not re-sent afterwards. Check the Feed for what you missed. Settings:
  how often to check, how many failed checks in a row count as a real outage (so a brief blip is
  ignored), a minimum outage length worth telling you about, how long to wait for each address,
  and which addresses to test against — leave that last one empty to use the built-in list. It
  only ever counts as an outage if *every* address fails, so one website being down doesn't look
  like your internet being down. Same limit as above, for the same reason: it can only tell you
  the connection *came back*. Leave this off if the machine is deliberately kept off the
  internet, or it will report your working setup as broken.
- **Bug Reports** — emails the developer when something breaks internally. See
  [section 17](#17-bug-reports).
- **Trigger API** — **off by default.** Lets anything on your network fire a camera by fetching
  a web address, for hardware that can open a URL but can't log in. Turning it on generates the
  key; each camera's own address is on its trigger zone screen. See
  [section 6](#triggering-a-camera-from-something-other-than-motion).
- **Quiet Hours** — during a window you set (e.g. overnight), raises how confident the AI must
  be before Email or Telegram will still ping you. It never fully mutes either channel — a
  confident detection still alerts you at 3am — and it has no effect on the Feed, the daily
  digest, or self-monitoring, which all keep working exactly as normal. See
  [section 15](#15-why-didnt-i-get-an-alert-rate-limiting--suppression-explained) for how this
  fits with the other rate-limiting layers.
- **Known Vehicles** — the list a transcribed license plate is checked against. Its own
  section below. See [section 10](#10-license-plate-recognition--known-vehicles).
- **Known Faces** — enroll people so the system can note when it recognizes them. Its own
  section below. See [section 10a](#10a-known-faces--local-face-recognition).
- **Multi-Frame Analysis** — has the AI look at a couple of frames per trigger instead of one, to
  cut down on shadow/reflection false positives. See
  [section 6b](#6b-multi-frame-analysis--reducing-false-positives-from-a-single-frame).
- **System Health** — a read-only view of CPU, memory, disk, queue depth, and vision-backend
  reachability. See [section 7a](#7a-system-health--how-hard-the-server-is-working).
- **MQTT / Home Assistant** — publishes events over MQTT with Home Assistant auto-discovery,
  optionally using this app as its own broker. Its own section below. See
  [section 13](#13-mqtt--home-assistant).
- **Device Hub** — adds a DEVICE HUB tab for switching lights, plugs, and other MQTT devices on
  and off from here and from the phone/Mac/Apple TV apps. Requires MQTT above to be on first.
  Its own section below. See
  [section 13a](#13a-the-device-hub-tab--controlling-lights--other-mqtt-devices).
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

## 7a. System health — how hard the server is working

A read-only panel in Settings (and in the iOS/Android apps' own Settings screen) showing how the
server is doing right now: CPU usage (as a percentage of all cores), memory used, disk space
free, how many alerts are currently queued waiting for AI analysis, whether the vision backend
(Ollama or Cloud Vision, whichever is active) is currently reachable, and how long the server has
been running since its last restart.

There's nothing to configure here — it refreshes on its own every few seconds and never affects
any setting. It's mainly useful for spotting a machine that's struggling (a queue that keeps
growing, a vision backend that's gone unreachable) before it turns into missed alerts, or for a
quick sanity check after a restart or update that everything actually came back up healthy.

## 7b. Restarting the server machine

Under Settings → REBOOT SERVER. Off by default, and it restarts the whole computer the software
runs on — not just the app. It's there for the times a service restart isn't enough: the network
stack has wedged, a system update wants a reboot, or the AI backend is stuck in a way that only a
clean boot clears.

Switching it on is only half of it. The operating system also has to be told to permit it, which
is one command run once on the server itself — a phone can't grant itself that. You don't have to
go and look it up: turn the setting on and the panel below tells you exactly what this machine
needs, with your actual username and paths already filled in, and a **Copy Commands** button.
Paste them into a terminal on the server, come back, press **Check Again**, and it should turn
green. On Windows this is usually already done, and it'll say so.

That check is worth pressing now and then. The permission lives in the operating system, not in
this app, so a rebuilt machine or a system upgrade can quietly take it away — and the panel always
tells you what's true right now rather than what was true when you set it up.

When you do reboot, you get a 60-second countdown with a **Cancel Reboot** button (you can change
or remove that window in the same section). After that the machine goes down and comes back on its
own, usually inside a minute or two. If your recorder is a separate machine, it keeps recording the
whole time — what pauses is analysis, this dashboard, and notifications.

There's no shut-down button, on purpose. Software can turn a machine off but it can't turn one back
on, so that button's worst case is a drive to wherever the server lives. A reboot's worst case is
just a slow minute.

## 8. Vision analysis backend — local, cloud, or both

Every alert (Blue Iris or your own trigger zones) gets analyzed by one of three kinds of AI
vision backend:

- **Ollama** — a model running on your own hardware. Free to run, keeps every image on your
  own network, but needs a machine capable of it (ideally a GPU) and can be slow on modest
  hardware.
- **Fast Local Detection** — a small object detector, also running on your own hardware, that
  answers only "is a person, vehicle, or animal there?" — but does it in well under a second
  even on an older CPU-only machine where Ollama's full model would take minutes per alert.
  The detector model (~100MB) downloads automatically the first time it's used. It can't
  describe the scene, tell one animal species from another, read gate state, or spot packages;
  the escalation option below exists to fill exactly that gap.
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
- **Fall Back To Another Backend** — if the primary fails (after its own retries), instead of
  giving up on the alert, retry once against another backend. This is what lets you run Cloud
  Vision as your everyday backend with Ollama as a safety net if the internet or OpenAI itself
  is briefly unavailable — or the reverse, Ollama as primary with Cloud Vision as backup if
  your local machine occasionally struggles to keep up.
- **Escalate Fast Detections For Full Analysis** — only applies when the primary is Fast Local
  Detection: when the detector finds a person, vehicle, or animal, the same alert is
  re-analyzed by a full AI (the **Escalation Target** — cloud if configured, or Ollama) to get
  the detailed description. Empty alerts — shadows, headlights, rain, which are most of them —
  never reach the expensive model at all. This is the recommended setup for a low-powered
  server: sub-second local screening, with the full analysis reserved for alerts that actually
  contain something. If escalation fails, the detector's own result still stands.
- **Full AI Has The Final Say** — what happens when the detector finds someone but the escalated
  full AI comes back saying the frame is empty. Off (the default), the detector is believed and
  the event is kept: one full AI shrugging at a dark night-vision frame shouldn't be able to hide
  real footage. Turn it on if you're seeing a steady stream of events the full AI never confirmed
  — then the full AI's verdict stands, no notification is sent, and the event is filed as rejected
  so it drops off EVENTS unless you show hidden ones. This only applies to an actual verdict: if
  the full AI is unreachable or errors out, the detector's result still stands either way.
- **Full AI Minimum Confidence** — how sure the full AI has to be before what it reports counts as
  a real subject. Below this the alert is treated as empty — no notification, event filed as
  rejected — though the description it wrote is still kept on the event so you can see what it
  thought it saw. This is the full AI's counterpart to Fast Local Detection's **Score Threshold**;
  it doesn't affect the detector's own results or gate checks. 0 turns it off. Note that with Fast
  Local Detection as your primary, this only changes the outcome when the setting above is also
  on — otherwise the detector's result wins regardless.

Two notes on the fast detector's limits: gate/door open checks (section on Gate Checks) always
use a full AI even when Fast Local Detection is primary, since a plain detector can't read
whether a gate is open. And its vehicle notifications can't tell passing road traffic from a
vehicle at your gate the way the full AI's prompt rules do — draw trigger zones that exclude
the road, or turn **Notify On Vehicle** off in the Fast Local Detection section for
road-facing cameras.

Whichever backend actually analyzed a given alert is recorded with it as `model`, visible in
the raw result at `/api/results/{id}` (not currently shown in the EVENTS tab itself). A `model`
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
| Questions | The specific things the AI must answer about each image — these become the structured fields you see on the EVENTS tab. |
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

## 10a. Known Faces — local face recognition

Off by default, and — like license plate recognition — it's per prompt profile rather than a
single global switch, so you turn it on only for the camera(s) where it's actually useful (a
close-up front door or gate camera, say, not a wide field view where faces are too small or
distant to match reliably).

**Enrolling a person:** in **Settings → Known Faces**, click **+ add person**, give them a name,
and provide one clear reference photo. On iOS and Android you can take the photo directly with
the camera or choose one from your photo library; the web dashboard accepts a file upload only.
You can add more reference photos to an existing person at any time — more angles/lighting
generally improves matching. A person's name can't be edited after creation; delete and re-add
if you need to fix a typo.

**Turning it on for a camera:** open the relevant prompt profile (see
[section 9](#9-prompt-profiles--what-the-ai-is-told-to-look-for)) and enable **Face recognition**.

**What happens:** every alert image from a face-recognition-enabled profile is checked against
your enrolled faces entirely on-device — this runs locally regardless of whether Ollama or a
Cloud Vision provider is doing the descriptive analysis for that same alert, and no image or face
data is ever sent anywhere for this specific check. A match is noted on the alert as *"Recognized:
Name"*; a face that's detected but doesn't match anyone enrolled is noted as *"Face detected:
unrecognized"*.

**What it does not do:** exactly like license plate recognition, a known or unknown face never
changes whether an alert fires or gets suppressed — it's informational only, appended after the
AI has already decided the event was worth analyzing. Don't rely on it as its own security layer;
treat it as a helpful label on top of an alert you'd already be getting.

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

## 11a. Discord alerts

Discord uses an incoming webhook rather than a bot account. In the destination Discord
channel, open **Edit Channel → Integrations → Webhooks**, create a webhook, copy its URL, then
paste it into **Settings → Discord Alerts** and enable the integration.

Each post contains the camera, AI summary, reason and confidence, plus the trigger snapshot
when **Attach Image** is enabled. It follows the same notification recommendation, confidence,
quiet-hours and duplicate-cooldown rules as Telegram. Treat the webhook URL like a password:
anyone who has it can post to that channel.

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

## 13a. The DEVICE HUB tab — controlling lights & other MQTT devices

Everything in section 13 is *outbound*: Sentinel telling your smart home what its cameras saw.
The Device Hub is the other direction — it lets Sentinel **control** things on the same broker.
Lights, plugs, fans, blinds, and locks appear on their own tab where you can switch them on and
off, and dim the ones that support it.

Off by default. Turn on **Settings → DEVICE HUB → Enabled**. It requires the **MQTT / Home
Assistant** section above to be turned on first — that's where the broker is configured, and
saving Device Hub with MQTT off is refused rather than leaving you with a tab whose every
button fails. Once enabled, a **DEVICE HUB** tab appears here and in the iPhone, iPad, Mac,
Apple TV, and Android apps. Nothing about your camera alerts changes.

**Adding devices — use Discover first.** Click **discover** and Sentinel listens to your broker
for a few seconds, then offers what it found. Two things turn up:

- **Devices that announce themselves.** Zigbee2MQTT, Tasmota, ESPHome, and Shelly all publish
  a Home Assistant discovery message describing themselves. These come back complete — name,
  topics, payloads, brightness range — and are ready to add as-is. You do *not* need Home
  Assistant installed for this; Sentinel just reads the same announcements.
- **Devices found by their topics.** Anything that published something recognisable during the
  scan (e.g. a Tasmota `stat/<name>/POWER`) is offered as a best guess.

Nothing is added until you click **add** on it. **review** opens the add form pre-filled, so you
can check or change anything before saving. Devices already in the hub are marked as such.

**Adding a device by hand.** Click **+ add device** if you know the topics. The two that matter:

| Field | What it is |
|---|---|
| **Command Topic** | Where the device listens for commands, e.g. `cmnd/kitchen/POWER` (Tasmota) or `zigbee2mqtt/kitchen/set` (Zigbee2MQTT). |
| **State Topic** | Where it reports what it's currently doing, e.g. `stat/kitchen/POWER`. Leave blank only if the device never reports — and turn on **Optimistic** if so. |

**Payload Schema** is `raw` when the payload is just the text `ON` or `OFF` (Tasmota, ESPHome),
or `json` when it's an object like `{"state":"ON"}` (Zigbee2MQTT, Home Assistant's JSON light
schema). For `json`, **State Value Key** names the key holding ON/OFF — `state` for
Zigbee2MQTT, `POWER` for Tasmota. Dotted paths like `StatusSTS.POWER` work for nested payloads.

**Brightness.** Turn on **Supports brightness** for a slider. JSON devices usually carry
brightness on the same command topic, so leave the brightness topics blank. Tasmota-style
devices need their own **Brightness Command Topic** (`cmnd/<name>/Dimmer`). **Brightness Scale**
is the device's own value for full brightness — 255 for most Zigbee bulbs, 254 for Zigbee2MQTT,
100 for Tasmota. The slider is always 0–100 and Sentinel converts for you.

**Availability.** If a device publishes a last-will/availability topic (e.g. Tasmota's
`tele/<name>/LWT`), fill it in — that's what Sentinel trusts to decide whether the device is
reachable. A device *without* one is judged by silence instead, and **State Timeout** controls
how long it can go quiet before showing as unavailable. Devices with an availability topic
ignore that timeout entirely, so a lamp that's simply been on all day is never wrongly marked
offline.

**Who can do what.** Anyone signed in can switch and dim devices — that's the point of the tab
for the rest of the household. Adding, editing, deleting, and running a discovery scan are
**admin-only**, because those point a device at an arbitrary MQTT topic.

**Apple TV is control-only.** Devices are added and configured from the dashboard, iPhone, Mac,
or Android; the Apple TV app shows them as cards you click to switch, and adjusts brightness
with left/right on the remote. Typing an MQTT topic with the Siri remote isn't worth it.

**A note on broker security.** Device Hub commands travel over the same broker as everything
else. If you're using the built-in broker, it listens on your whole LAN — set **Username** and
**Password** under the MQTT section so switching your lights isn't open to anything else on the
network. When those are blank the built-in broker stays open to anonymous connections, exactly
as it always has. For an external broker reachable off-LAN, turn on **Use TLS** as well.

**Troubleshooting.** If a tile says *not confirmed*, the command was published but the device
never echoed its new state back — usually the Command Topic is right and the State Topic is
wrong, or the device is unplugged. If a tile says *state unknown*, nothing has ever arrived on
its State Topic: check the topic, or turn on **Optimistic** for a device that genuinely never
reports. If the toolbar says *broker disconnected*, nothing can be controlled at all — check
the MQTT section's broker settings first.

## 14. Apple HomeKit

Off by default. When enabled, each camera appears in Apple's **Home** app as a motion sensor,
so your iPhone gets a native notification the moment an alert-worthy event happens — no
Telegram account, no email, and no Home Assistant in between.

**What you get, and what you don't.** This is alerts only. Live video *through* HomeKit isn't
included: Apple gates camera streaming behind HomeKit Secure Video, which requires a paid MFi
licence. Watching your cameras live still works exactly as it always has, in the dashboard's
LIVE tab. Everything else about HomeKit — notifications, automations triggered by motion,
seeing which camera fired — works normally.

**Setting it up.**

1. Open **SETTINGS → APPLE HOMEKIT** in the dashboard and turn **enabled** on, then **save**.
2. Wait a few seconds — a QR code appears, with the numeric pairing code beneath it.
3. On your iPhone, open **Home** → **+** → **Add Accessory**, and scan the code. (If the
   camera won't focus on your screen, tap "More options…" and type the numeric code instead.)
4. Sentinel Core appears as a bridge, with a motion sensor for each camera.

That's it — no install step. The companion service HomeKit needs (the "sidecar") is set up
automatically, and until you turn the toggle on it stays completely off your network: nothing
advertised, nothing to find. If the settings section says the sidecar isn't running, Node.js
was missing when the app was installed; see [DEPLOYMENT.md](DEPLOYMENT.md) section 8a.

**Camera identity is durable.** Cameras from the Blue Iris camera mapping are registered before
the HomeKit bridge publishes. Any other camera is learned on its first alert and saved in
`homekit-bridge/persist/cameras.json`, then restored before every later publish. This keeps
HomeKit accessory IDs stable across restarts and prevents the first motion event from racing
Apple Home's accessory synchronization.

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

**If Home says “Accessory Not Responding” while the dashboard says paired**, first remove the
old Sentinel Core bridge from Apple Home. Then use **Settings → Apple HomeKit → Reset Pairing**.
The HomeKit sidecar restarts with fresh controller keys while retaining the known camera list;
wait for the new QR code and add it again. The settings panel also shows the HAP port,
advertiser, and most recent motion sent, which separates pairing trouble from an alert that
never reached HomeKit.

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
6. **Trigger zone rate limiting** (Cameras tab → zone editor, per zone — separate from the two above,
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

- `SentinelCore/data/images/YYYY-MM-DD/` — saved Blue Iris alert snapshots (if
  **Save Images** is on)
- `SentinelCore/data/events.db` — SQLite history of every Blue Iris alert
- `SentinelCore.Web/data/cameras.db` — your added cameras
- `SentinelCore.Web/data/triggers.db` — trigger zone configs and trigger events
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

**A clip never appears on the EVENTS tab.** Clips only show up once the "seconds after
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
