#!/usr/bin/env bash
# The root half of Sentinel Core's automatic updates (docs/AUTO_UPDATE.md). install.sh copies this
# to /usr/local/libexec/sentinelcore-apply-update, root-owned, and sets up a systemd path unit
# that runs it whenever the dashboard drops <install>/updates/pending.json.
#
# By then the dashboard has already downloaded the bundle, checked its signature and hash, and
# unpacked it into <install>/updates/staged/<build>/. This script only:
#   1. stops the services
#   2. backs up every file the new release replaces, then copies the new files in
#   3. starts the services and waits for both to report the new build on their health endpoints
#   4. if they don't, puts the backed-up files back and starts the old build again
#   5. writes updates/result.json, which the dashboard reports to the owner
#
# Security: the install folder belongs to the unprivileged app user, so nothing in it is trusted.
# Settings come from /etc/sentinelcore/update.conf (root-owned), only a build number is read out
# of pending.json, and every file operation runs as the app user via runuser - so a symlink
# planted in the install folder can't trick root into overwriting anything outside it. The only
# things done as root are systemctl and running this same root-owned file as the app user.
#
#   sentinelcore-apply-update            what the path unit runs (as root)
#   sentinelcore-apply-update --files apply|rollback <build>   internal, runs as the app user

set -uo pipefail

CONF=/etc/sentinelcore/update.conf
SELF=$(readlink -f "$0")

log() { echo "sentinelcore-apply-update: $*"; }

# ---------------------------------------------------------------------------------------------
# File operations - always run as the app user.
# ---------------------------------------------------------------------------------------------
if [[ "${1:-}" == "--files" ]]; then
  mode=$2 build=$3 install_dir=$4
  updates="$install_dir/updates"
  staged="$updates/staged/$build"
  previous="$updates/previous"

  case "$mode" in
    apply)
      [[ -d "$staged" ]] || { echo "nothing staged at $staged" >&2; exit 1; }
      rm -rf "$previous"
      mkdir -p "$previous/files"
      : > "$previous/added.list"
      # Every file the new release carries: back up the one it replaces (or note that it's new,
      # so a rollback removes it), then copy it in. Copy-to-temp-then-rename, so no file is ever
      # half written even if this is interrupted.
      (cd "$staged" && find . -type f -print0) | while IFS= read -r -d '' rel; do
        rel=${rel#./}
        target="$install_dir/$rel"
        if [[ -e "$target" || -L "$target" ]]; then
          mkdir -p "$previous/files/$(dirname "$rel")"
          cp -P -p "$target" "$previous/files/$rel" || exit 1
        else
          printf '%s\n' "$rel" >> "$previous/added.list"
        fi
        mkdir -p "$(dirname "$target")"
        cp -p "$staged/$rel" "$target.update-tmp" || exit 1
        mv -f "$target.update-tmp" "$target" || exit 1
      done || exit 1
      chmod +x "$install_dir/SentinelCore/SentinelCore" "$install_dir/SentinelCore.Web/SentinelCore.Web" 2>/dev/null
      [[ -f "$install_dir/install.sh" ]] && chmod +x "$install_dir/install.sh"
      exit 0
      ;;
    rollback)
      [[ -d "$previous/files" ]] || { echo "no backup at $previous" >&2; exit 1; }
      while IFS= read -r rel; do
        [[ -n "$rel" ]] && rm -f "$install_dir/$rel"
      done < "$previous/added.list"
      cp -P -p -R "$previous/files/." "$install_dir/" || exit 1
      exit 0
      ;;
    homekit-deps)
      # Only when the sidecar's dependencies changed - npm is slow and needs the network.
      cd "$install_dir/homekit-bridge" && npm install --omit=dev --no-audit --no-fund >/dev/null
      exit $?
      ;;
    result)
      # $5 is the JSON, written by temp-and-rename like everything else the dashboard reads.
      printf '%s\n' "$5" > "$updates/result.json.tmp" && mv -f "$updates/result.json.tmp" "$updates/result.json"
      exit $?
      ;;
  esac
  echo "unknown --files mode $mode" >&2
  exit 2
fi

# ---------------------------------------------------------------------------------------------
# The orchestration - root.
# ---------------------------------------------------------------------------------------------
if [[ "$EUID" -ne 0 ]]; then
  echo "must run as root (it is started by sentinelcore-update.service)" >&2
  exit 1
fi
[[ -f "$CONF" ]] || { log "no $CONF - run install.sh from a release"; exit 1; }

INSTALL_DIR="" APP_USER="" MAIN_PORT=5080 WEB_PORT=3320
# Plain KEY=value lines only, read without sourcing - nothing in a config file gets executed.
while IFS='=' read -r key value; do
  case "$key" in
    INSTALL_DIR) INSTALL_DIR=$value ;;
    APP_USER) APP_USER=$value ;;
    MAIN_PORT) [[ "$value" =~ ^[0-9]+$ ]] && MAIN_PORT=$value ;;
    WEB_PORT) [[ "$value" =~ ^[0-9]+$ ]] && WEB_PORT=$value ;;
  esac
done < "$CONF"
[[ -d "$INSTALL_DIR" && -n "$APP_USER" ]] || { log "$CONF is missing INSTALL_DIR or APP_USER"; exit 1; }

UPDATES="$INSTALL_DIR/updates"
PENDING="$UPDATES/pending.json"

as_app() { runuser -u "$APP_USER" -- "$SELF" --files "$@"; }

[[ -e "$PENDING" || -L "$PENDING" ]] || exit 0
# Read as the app user (it could be a symlink to anything), and only the build number out of it.
BUILD=$(runuser -u "$APP_USER" -- cat "$PENDING" 2>/dev/null | grep -o '"build": *[0-9]\+' | head -1 | grep -o '[0-9]\+$')
FROM_BUILD=$(runuser -u "$APP_USER" -- cat "$PENDING" 2>/dev/null | grep -o '"fromBuild": *[0-9]\+' | head -1 | grep -o '[0-9]\+$')
# Removed first, always: the path unit fires again for as long as the file exists, and a bad
# request must not turn into a loop.
rm -f "$PENDING"
FROM_BUILD=${FROM_BUILD:-0}

write_result() {
  local outcome=$1 message=$2
  local json
  json=$(printf '{"build":%s,"fromBuild":%s,"outcome":"%s","message":"%s","finishedAt":"%s"}' \
    "${BUILD:-0}" "$FROM_BUILD" "$outcome" "${message//\"/\'}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)")
  as_app result "${BUILD:-0}" "$INSTALL_DIR" "$json" || log "could not write result.json"
  log "$outcome: $message"
}

if [[ ! "${BUILD:-}" =~ ^[0-9]+$ ]]; then
  write_result failed "The update request was unreadable."
  exit 1
fi
if [[ ! -d "$UPDATES/staged/$BUILD" ]]; then
  write_result failed "Build $BUILD wasn't staged."
  exit 1
fi

SERVICES=(sentinelcore sentinelcore-web)
# Only if it's running now - an owner who stopped the HomeKit sidecar shouldn't find it back on.
systemctl is-active --quiet sentinelcore-homekit && SERVICES+=(sentinelcore-homekit)

# Both services report their build number on their health endpoint; anything else answering
# (the old build, a crash loop between restarts) doesn't count.
healthy() {
  local deadline=$((SECONDS + 120))
  local main="" web=""
  while (( SECONDS < deadline )); do
    main=$(curl -s --max-time 3 "http://127.0.0.1:$MAIN_PORT/health/live" || true)
    web=$(curl -s --max-time 3 "http://127.0.0.1:$WEB_PORT/api/health" || true)
    if grep -q "\"build\":$1[,}]" <<<"$main" && grep -q "\"build\":$1[,}]" <<<"$web"; then
      return 0
    fi
    sleep 2
  done
  return 1
}

homekit_deps_changed() {
  [[ -f "$UPDATES/staged/$BUILD/homekit-bridge/package.json" ]] || return 1
  ! cmp -s "$UPDATES/staged/$BUILD/homekit-bridge/package.json" "$INSTALL_DIR/homekit-bridge/package.json"
}

log "installing build $BUILD over build $FROM_BUILD"
NEEDS_NPM=0
homekit_deps_changed && command -v npm >/dev/null 2>&1 && NEEDS_NPM=1

systemctl stop "${SERVICES[@]}"

if ! as_app apply "$BUILD" "$INSTALL_DIR"; then
  log "copying the new files failed - restoring the previous ones"
  as_app rollback "$BUILD" "$INSTALL_DIR"
  systemctl start "${SERVICES[@]}"
  write_result rolledBack "Copying the new files failed, so nothing changed."
  exit 1
fi

[[ "$NEEDS_NPM" == "1" ]] && { as_app homekit-deps "$BUILD" "$INSTALL_DIR" || log "npm install for the HomeKit sidecar failed"; }

systemctl start "${SERVICES[@]}"

if healthy "$BUILD"; then
  write_result installed "Build $BUILD is running."
  exit 0
fi

log "build $BUILD did not come up healthy - rolling back"
journalctl -u sentinelcore -u sentinelcore-web -n 30 --no-pager 2>/dev/null | sed 's/^/    /'
systemctl stop "${SERVICES[@]}"
as_app rollback "$BUILD" "$INSTALL_DIR"
systemctl start "${SERVICES[@]}"
if healthy "$FROM_BUILD"; then
  write_result rolledBack "The previous build is running again."
else
  write_result failed "The previous build didn't come back either - check journalctl -u sentinelcore -u sentinelcore-web."
fi
exit 1
