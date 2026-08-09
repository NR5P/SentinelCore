# iPhone live view stuttering — handoff notes

Written 2026-08-05 from the Linux server. Everything below was measured on the server side; nothing
has been tried on a Mac or a real device yet, because building and installing the app needs Xcode.

> **That last sentence is no longer true, and neither is some of the reasoning that follows from
> it.** The Mac follow-up happened the same day and did not reproduce the fault. Read
> [Follow-up on the Mac](#follow-up-on-the-mac-2026-08-05--not-reproduced) at the bottom before
> acting on anything above it — in particular, the dashboard comparison this file opens with turns
> out not to be a like-for-like test.

## The report

On an **iPhone, over Wi-Fi**, the Live view "refreshes every 10 seconds or so, seconds don't move on
display" — i.e. a frozen frame that jumps forward periodically, rather than moving video. The same
cameras looked fine in the **web dashboard** at the same time.

## What was ruled out on the server

The stream being produced is genuinely healthy. Checked on the gate camera while the phone was
misbehaving:

| Check | Result |
|---|---|
| HLS segments landing on schedule | yes, 2s apart, current to the second |
| Frames per 2-second segment | **60** (30fps), `IPPPP…` with 1s keyframes |
| Segment size | ~2.1 MB, i.e. the full bitrate — not a keyframe-only stream |
| `/api/cameras/{id}/stream/stream.m3u8` response | HTTP 401 in ~10 ms (auth expected; server is responsive) |
| go2rtc running | no — but `Go2RtcManager` starts it lazily on the first WebRTC offer, so that is normal, not a fault |

So the server is emitting full-motion video and serving it quickly. The problem is between the
server and the phone: either the app build, or bandwidth.

## Suspect 1 — the app build predates the fix (check this first)

`ios/SentinelCore/Views/LiveStreamPlayer.swift` carries a long comment describing **exactly this
symptom**: setting `automaticallyWaitsToMinimizeStalling = false` on a live HLS stream makes
AVPlayer decode one frame and stop, while still reporting `.playing` — "a frozen picture under a
LIVE badge, which is indistinguishable from a still image", with measurements showing 1 frame
decoded in 6 seconds.

That fix is already committed:

| Commit | Date | Subject |
|---|---|---|
| `a938f02` | 2026-07-30 | Fix iOS Live view freezing, and make the readiness probe honest |
| `749ab2b` | | Fix frozen live video: don't disable stall-minimizing on live HLS |
| `2bfd0cc` | | Keep live tiles at the live edge instead of drifting minutes behind |
| `982e306` | | Stop live tiles rebuffering every few seconds, and mark unlicensed apps |

**If the app on the phone was built before 30 July 2026, a rebuild is the whole fix.** Establish
this before changing any code — it costs one build and it is the most likely answer.

## Suspect 2 — the stream is very heavy for a phone — **CONFIRMED AND FIXED 2026-08-09**

Measured on the phone itself, via `AVPlayerItem.accessLog()`, against three cameras:

| gate camera | In the grid | Solo |
|---|---|---|
| throughput | 7.9 Mbit/s avg | 15.2 Mbit/s avg |
| stalls | 2 | 0 |
| reconnects | 1 | 0 |

Aggregate grid demand was **24.7 Mbit/s**, all three tiles degraded together, and every tile
reconnected at least once while neither solo view did. **Dropped frames were zero throughout**,
which is what rules out the decoder and names the link: the phone was starved for data, not short
of decode.

Fixed by giving live view a rendition choice. `StreamQuality` (`CameraSource`) picks Main or Sub;
`HlsStreamManager` keys sessions by camera *and* rendition; `/api/cameras/{id}/stream/sub/...`
serves the low one. Grid tiles on iOS, tvOS, Android and the dashboard's HLS tier ask for Sub, and
every full-screen view keeps Main. A camera with no derivable low stream, or one that turns out not
to serve it, falls back to Main and is remembered rather than retried per request.

**Deployment order matters:** the server must carry this before the apps do. Old clients work
against a new server (`/stream/{file}` still means Main), but a new client against an old server
gets a 404 on the extra path segment.

The original note follows.

### Original analysis

`HlsStreamManager` serves the camera's **main** stream with `-c:v copy`: on the gate camera that is
**2560x1920 at roughly 8.6 Mbit/s**, sustained, over Wi-Fi, while the server often sits at load
average 6–7 with `llama-server` at ~80% (the vision model). Phones can decode that, but it leaves no
headroom for a hiccup.

The cameras already expose a substream — Reolink `channel0_ext.bcs`, 896x672 — which
`TriggerZoneMonitor` uses for motion detection (see `TryDeriveDetectionUrl`). Serving that to the
live view instead would cut the bandwidth roughly tenfold. That change belongs on the **server**, in
`HlsStreamManager.BuildFfmpegArguments` / `StartSession`, and needs no app rebuild — so it can be
done from the Linux box, not the Mac.

Worth thinking through first: the live *grid* wants small cheap tiles, but a full-screen single
camera probably still wants the main stream. A per-camera or per-context choice is likely the right
shape rather than a blanket switch.

## Already changed on the server (may help on its own)

Commit `2010c94`, 2026-08-05 — "Halve the wait before a live tile shows anything". `-hls_time` went
2 → 1 and `-hls_list_size` 10 → 20. Measured cold-start to a usable playlist on the gate camera went
**5.00s → 2.29s**, and a player needs three segments produced before it starts, so the wait roughly
halves at both ends. Shorter segments also mean a stalled player loses less ground.

**Retest the phone after deploying that** before doing anything else — it changes the buffering
behaviour the app sees.

## Suggested order on the Mac

1. Pull `master`. Confirm the four commits above are in the working tree.
2. Rebuild and install on the phone. Retest Live over Wi-Fi.
3. If it is fixed — done, it was a stale build.
4. If it still stutters, capture what AVPlayer thinks is happening: `LiveStreamPlayer` already
   observes `playbackStalledNotification` and `failedToPlayToEndTimeNotification`. Log those plus
   `isPlaybackLikelyToKeepUp` and `loadedTimeRanges` on a stutter, so it is clear whether the phone
   is starving for data (bandwidth → suspect 2) or decoding one frame and stopping (player config).
5. Only then consider the substream change, and do it on the server.

## Things not to assume

- **Do not assume the server is at fault** without re-checking the table at the top — it was
  verified healthy at frame level while the phone was misbehaving.
- **Do not "fix" it by setting `automaticallyWaitsToMinimizeStalling = false`.** That is the cause of
  this exact symptom, not the cure, and the comment in `LiveStreamPlayer.swift` records the
  measurements proving it.
- The camera's own clock is set to **UTC-8** while the server runs **UTC-5**, a constant 3-hour
  offset. The burned-in timestamp is genuine and useful for diagnosing freezes — just remember it
  will never match the server clock.

## Follow-up on the Mac, 2026-08-05 — not reproduced

**The stutter stopped happening on the phone**, with nothing deployed to fix it. That alone moves
this from "a bug with two suspects" to "an intermittent fault with no confirmed cause", and the
notes above should be read in that light.

### What was measured

A stub server generated live HLS at the gate camera's exact profile — 2560x1920, **8.75 Mbit/s
measured**, 30fps, 1s segments — for two cameras at once, and the current app build watched it in
an iPhone 17 Pro simulator. Screenshots two seconds apart, cropped to a tile and hashed, gave **ten
distinct frames out of ten samples** with a steady request cadence and no stalls.

So at this install's real bitrate, on current app code, the Live grid plays smoothly. That does not
rule out the phone's radio, but it does rule out the stream itself and the player logic being
inherently at fault.

### The dashboard comparison at the top of this file is not a fair one

The report rests on "the web dashboard was fine at the same time". The dashboard tries WebRTC, then
MSE over WebSocket, and only reaches HLS if both fail — see `attachPlayer` in `cameras.js` — so on
this install it is almost certainly *not playing HLS at all*. It talks to go2rtc, which connects to
the camera directly. The two surfaces share a camera, not a code path, and the dashboard being
healthy says nothing about the HLS pipeline.

### Leading suspect now: a server that had just started

`/api/cameras` warms **every** camera at once, so a freshly started server spawns an ffmpeg per
camera simultaneously — RTSP connects, keyframe waits, remuxing — on a box that often sits at load
6–7 with `llama-server` at ~80%. Segments land late and irregularly for the first stretch; a player
that starts in that window stalls and falls behind, which looks exactly like a frozen frame jumping
every several seconds. It also survives the "segments were healthy when checked" measurement above,
because a player that has fallen far behind does not recover just because the server does.

This is untested. Testing it means restarting the server and watching the phone immediately, versus
watching it after things settle.

### Suspect 2 is built and parked, not merged

Branch `substream-live-tiles` (`53a762b`) serves live tiles the camera's substream and keeps the
main stream for full-screen, with per-quality sessions and fallbacks for cameras that have no
substream. Tested, but never run against the real server. Reach for it if the stutter comes back
under load; don't merge it on spec — it costs an extra camera connection while somebody is watching
full-screen, and a cold start on the paths that no longer get warmed.

That branch also carries `tools/live-view-test-harness/`, which is what produced the measurements
above and can throttle segment delivery to emulate a link that cannot keep up.

**It is a local branch on the Mac and has deliberately not been pushed**, so that commit will not
resolve anywhere else. If it turns out to be wanted, it needs pushing from there first; if this
note is old and nobody ever went back to it, the branch may simply be gone, and the description
above plus `HlsStreamManager` is enough to rebuild it.

### Note for anyone testing from this Mac

The Mac cannot reach the media box: instant TCP RST on every port, no ICMP, while the server is up
and still advertising `_sentinelcore._tcp` at `192.168.50.8:3443`. It is firewalled off — the
lasting consequence of an earlier round of failed SSH auth. Pointing a simulator at the real server
needs that lifted first.
