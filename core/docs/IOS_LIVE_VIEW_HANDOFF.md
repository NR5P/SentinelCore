# iPhone live view stuttering — handoff notes

Written 2026-08-05 from the Linux server. Everything below was measured on the server side; nothing
has been tried on a Mac or a real device yet, because building and installing the app needs Xcode.

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

## Suspect 2 — the stream is very heavy for a phone

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
