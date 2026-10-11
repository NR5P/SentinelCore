# Automatic Core updates

Linux servers installed from a release bundle update themselves overnight. A new release is
published from the maintainer's Mac and served by the push relay at `https://updates.nr5p.com`.
Each box checks once a night, verifies the release, installs it, and rolls back on its own if the
new build doesn't start.

The maintainer's own media box runs from a git checkout and is **not** part of this. It keeps
updating through `git pull` + `scripts/deploy.sh`, and its Settings page says so.

## The pieces

| Where | What | File |
|---|---|---|
| Maintainer's Mac | Builds, signs and uploads a release | `scripts/publish-update.sh` |
| Mac only | Update signing key (ECDSA P-256) | `~/sentinelcore-update-keys/update-signing-key.pem` |
| Every install | The matching public key | `SentinelCore.Web/UpdateSigningPublicKey.cs` |
| Media box (relay) | Serves manifests and bundles to licensed installs | `SentinelCore.PushRelay/RelayUpdateEndpoints.cs`, files in `SentinelCore.PushRelay/updates/<channel>/<platform>/` |
| Customer box, dashboard (app user) | Checks, downloads, verifies, stages | `SentinelCore.Web/AutoUpdate.cs` |
| Customer box, root | Swaps files, restarts, health-checks, rolls back | `scripts/release/sentinelcore-apply-update.sh` → `/usr/local/libexec/sentinelcore-apply-update` |
| Customer box | Wiring, written by `install.sh` | `/etc/sentinelcore/update.conf`, `sentinelcore-update.path`, `sentinelcore-update.service` |

## Publishing a release

```bash
cd core
./scripts/publish-update.sh --notes "What changed, in a sentence or two"
```

- Builds `linux-x64` and `linux-arm64` and stamps them with a **build number**: the master
  commit count, so it only ever goes up. Installs compare build numbers.
- Refuses to run if `core/` has uncommitted changes (`ALLOW_DIRTY=1` overrides this for a
  throwaway test) or if the signing key doesn't match the public key compiled into the app.
- Uploads with `scp` to the relay box named by `UPDATE_HOST` (`user@host`; export it or put it
  in the gitignored `core/scripts/local.env`, which the script sources if present). The bundle goes up first and the manifest last, each
  renamed into place, so a box never sees a manifest pointing at a half-uploaded file. The
  newest three bundles are kept per platform.
- `--channel beta` publishes to boxes that chose Beta in Settings. `--no-upload` builds and
  signs into `dist/updates/` without uploading anything.

**Back up the signing key.** Every install only trusts the public key compiled into it. If the
private key is lost, the first release with a new key has to be installed by hand on every box.

## What a box does overnight

1. **The window.** It starts at the hour set in Settings (default 4:00, after the 3:00 nightly
   backup) and lasts two hours. Each box picks a fixed minute within the first hour, so they
   don't all hit the relay at once. The worker ticks every 5 minutes, so a busy box keeps
   retrying for the rest of the window.
2. **Check.** It sends `GET /v1/updates/<channel>/<platform>` with the license in
   `X-Sentinel-License`, just as push does. A blocked license gets a 403, so
   `Relay:BlockedLicenseIds` cuts off updates as well as push.
3. **Verify the manifest.** The signature must verify against `UpdateSigningPublicKey`, and the
   product, channel and platform must match this box. Only a higher build number is newer.
   `minimumBuild` lets a release say "too old to update automatically; reinstall by hand".
4. **Wait if busy.** It holds off while the alarm is arming, counting down or going off, while
   someone is watching a movie, during a manual recording or the nightly backup, or while a
   download is being copied into the library. An armed alarm, a live view or a torrent still
   downloading does not block. Night mode is armed at exactly that hour, wall kiosks show live
   view all the time, and qBittorrent runs in its own service.
5. **Download** to `<install>/updates/downloads/`. An interrupted download resumes from where it
   stopped using a Range request. It needs 4× the bundle size free, then checks the size and the
   SHA-256 against the signed manifest.
6. **Stage.** It unpacks to `updates/staged/<build>/` with the top `<platform>/` folder removed.
   It refuses a bundle without both binaries, or one that carries `appsettings.json`, `data/` or
   `updates/`.
7. **Hand over.** It writes `updates/pending.json`. The systemd path unit sees the file and runs
   `sentinelcore-apply-update` as root, which:
   - stops `sentinelcore`, `sentinelcore-web` and `sentinelcore-homekit`
   - as the app user, backs up every file the release replaces to `updates/previous/`, notes the
     files that are new, and copies the new ones in (temp file, then rename)
   - runs `npm install` for the HomeKit sidecar, only if its `package.json` changed
   - starts the services and waits up to 2 minutes for **both** health endpoints to report the
     new build number
   - if they don't, puts the previous files back, restarts, and confirms the old build is up
   - writes `updates/result.json`
8. **Report.** Whichever dashboard comes up next reads `result.json` once. It notifies the owner
   by email, Telegram and push, and clears out the staged and downloaded files. A build that
   rolled back is recorded in `SkippedBuild` and isn't retried overnight. "Install now" can still
   try it.

## Security

- **The relay can't ship code.** The signing key never touches the internet-facing box; the relay
  only hands out files that installs verify themselves.
- **Root trusts nothing in the install folder,** because the app user can write all of it. Its
  settings come from root-owned `/etc/sentinelcore/update.conf`, and only a build number is read
  out of `pending.json`. Every copy, backup and restore runs as the app user via `runuser`, so a
  symlink planted in the folder can't redirect a root write.
- **The installer doesn't update itself.** The root copy in `/usr/local/libexec` changes only
  when someone runs `install.sh` from a newer release. Installing an app-user-writable file as
  root code would hand root to the app user.

## Limits (what still needs a manual `install.sh`)

- Changes to the systemd units, the Ollama drop-in, apt dependencies or the apply script itself.
  For those, publish with `minimumBuild` set to the new build.
- Windows and macOS installs. Settings says so.
- **Database migrations have to be additive.** A rollback runs the previous build against the
  database the new build may already have migrated: add columns and tables; never rename or drop
  them in the same release.

## API

All admin-only.

- `GET /api/updates/status`: whether this install can update and why not, the running build,
  the channel, the current phase (`idle`, `checking`, `downloading`, `staging`, `installing`),
  the next window, the last check, any available build with its notes, and the last install
  result.
- `POST /api/updates/check`: checks only. Returns `{detail}`, or 400 `{error}`. This is the
  Settings "check now" button on every client.
- `POST /api/updates/install`: starts download and install in the background and returns 202.
  This is the web Settings "install now" button.

## Setup still to do (one time)

1. In Cloudflare Zero Trust, add a route for **updates.nr5p.com → HTTP 127.0.0.1:3330** on the
   existing `secrettunnel` tunnel, next to `push.nr5p.com`.
2. Deploy the relay on the media box (`./scripts/deploy.sh relay`) so it has the
   `/v1/updates` endpoints.
3. Publish the first release with `publish-update.sh`.
4. A customer box gets the updater the next time `install.sh` runs from a bundle built after
   this change.

## Testing

- `AutoUpdateTests`: verification, staging, deferral, resume, skipping a rolled-back build,
  reporting, the window maths, and the status endpoint on a source build.
- `RelayUpdateEndpointTests`: license gate, Range, `no-store`, and path safety.
- **Not yet run on a real Linux box:** the root orchestration in
  `sentinelcore-apply-update.sh` (systemctl, runuser, the health wait, the path unit). Its file
  modes (`--files apply|rollback|result`) were exercised on macOS. Run a first end-to-end test on
  a throwaway VM or spare machine, including a deliberately broken build to watch it roll back.
