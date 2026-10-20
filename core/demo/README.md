# App Review demo server

Apple and Google reviewers won't install Core, so the store listings point them at
**https://demo.nr5p.com**: a second, walled-off Core on the media box with invented cameras,
events, movies, music, organizer items and files, and a **Viewer** login for them. Every night at
03:30 (Central) it is rebuilt from scratch, which undoes anything a reviewer changed and keeps
the events, reminders and calendar dated around "today".

## Install (once, on the media box)

```bash
cd ~/Desktop/BlueIrisOllamaBridge && sudo core/demo/demo.sh install
```

Then add the public hostname in Cloudflare Zero Trust → the tunnel → Published application routes:
**demo.nr5p.com → HTTP 127.0.0.1:3360**. The reviewer login, for App Store Connect (App Review
Information → Sign-in required) and Play Console (App content → App access), comes from:

```bash
sudo core/demo/demo.sh login
```

After a box deploy, `sudo core/demo/demo.sh refresh-app` copies the new binaries in and resets.
`demo.sh reset` rebuilds the data on demand; `demo.sh status` shows the units and the timer.

## The walls

The demo faces the internet, and it shares a machine with the real install. Each of these closes
a specific hole found reading the code (2026-10-20):

- **Its own user and folder.** `scdemo`, no shell, no sudo. Everything lives in `/srv/scdemo`. The
  units add `ProtectHome=yes` (the real install, its keys and settings are under `/home/media`),
  `ProtectSystem=strict` with only `/srv/scdemo` writable, and `NoNewPrivileges`.
- **Its own copy of the binaries** (`app/`), without the real `appsettings.json` (the live file
  holds credentials, and several senders use credentials even when their channel is switched off),
  the bin `data/` folder or other platforms' native libraries.
- **Settings built from the shipped defaults, never from a live file.** `settings/ingest.overrides.json`
  turns off or dead-ends everything that reaches outside: Blue Iris, Ollama, cloud AI, email,
  Telegram, HomeKit, MQTT, push, auto-update, nightly export, downloads (`ManageServices: false`,
  so it never stops the real VPN units), host reboot. `settings/web.appsettings.json` repoints every
  dashboard default that names the real install: the ingest app (5080), the HomeKit sidecar
  (3321), go2rtc (off, so Live uses HLS), the updater and the download VPN paths. HTTPS is off,
  which is what makes Kestrel listen only on 127.0.0.1.
- **The admin exists before anything listens.** Requests through the tunnel arrive from
  127.0.0.1, which the server treats as the local network, so an empty server could be claimed
  from the internet with `POST /api/auth/enable`. The settings file carries `Auth` with a password
  hash, and the dashboard creates that admin at startup (`AccountMigration`). Its password stays
  on the box (`secrets/admin-password`); reviewers get the Viewer account `appreview`, which can't
  reach Settings, libraries, filesystem browsing or any other admin-only route.
- **Small limits on what a Viewer can fill up:** 25 MB uploads, 1 GB of podcast downloads, no
  transcripts. The nightly reset clears the rest.

Known limits: the login lockout is per address, and every tunnel visitor looks like 127.0.0.1, so
ten bad logins in five minutes lock the reviewer out briefly as well. The AI features (Ask, chat)
answer with an error because Ollama is deliberately unreachable, to keep reviewers off the box's
memory.

## What's in the kit

| | |
| --- | --- |
| `demo.sh` | root side: install, refresh-app, reset, login, status |
| `demo.py` | the demo user's side: secrets, media, build-live, seed (standard-library Python) |
| `fake-cameras.py` | serves the four camera clips on 127.0.0.1:9401-9404, any number of viewers |
| `catalog.json` | the invented movies, show and albums |
| `seed/art/` | their posters and covers, drawn by `tools/render-art.py` (Mac, headless Chrome) |
| `seed/cameras/` | cameras, 42 events with snapshots, 8 timeline clips, 4 live feeds (drawn scenes, nobody's real home) |
| `settings/` | the two settings templates above |
| `systemd/` | the cams, main app, dashboard, reset service and nightly timer |

The camera seed came from `video/demo/` (the 2026-08 filming demo), with paths made portable,
missing snapshots matched to drawn scenes of the same camera, and trigger zones switched off so
nothing holds a feed open.

## Trying it on the Mac

```bash
export SCDEMO_ROOT=/tmp/scdemo SCDEMO_WEB_PORT=3366 SCDEMO_INGEST_PORT=5097
```

Lay out `$SCDEMO_ROOT/app` the way `copy_app` in `demo.sh` does (keep the osx native folders), then
`demo.py secrets`, `media` and `build-live`. Start `fake-cameras.py`, then both apps with
`--contentRoot` set to `live/ingest` and `live/web`, and run `demo.py seed`. Without the license
key, camera images carry the UNLICENSED watermark.
