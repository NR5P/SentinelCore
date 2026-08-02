#!/usr/bin/env bash
# Installs a prebuilt Sentinel Core release (this script ships inside the release .zip, next to
# the SentinelCore/ and SentinelCore.Web/ folders) as LaunchDaemons.
#
# Unlike ../install-macos.sh (which builds from a git checkout and needs the .NET SDK), this
# works against the self-contained binaries already sitting next to this script - no SDK,
# no build, no git. Just: unzip, then
#
#   sudo ./install.sh
#
# The same command upgrades an existing install: unpack the newer .zip over the old directory and
# re-run this. It detects the existing install, backs the configuration up, and restarts onto the
# new binaries rather than treating it as a first-time setup. Nothing in the archive shares a name
# with a file that holds user state, so the unpack step itself cannot destroy anything - see
# publish-release.sh.
#
# macOS quarantines anything downloaded by a browser - if the binaries refuse to run with an
# "unidentified developer" message, either right-click each binary in Finder and choose Open
# once, or clear the quarantine flag for the whole bundle before running this:
#   xattr -dr com.apple.quarantine .
#
# Services installed:
#   com.sentinelcore.bridge    the alert pipeline           (port 5080)
#   com.sentinelcore.web       the dashboard                (port 3320, and 3443 for HTTPS)
#   com.sentinelcore.homekit   Apple Home sidecar, optional (needs Node.js)
#
# Override any of these if your setup differs:
#   INSTALL_DIR=/opt/sentinelcore MAIN_PORT=5080 WEB_PORT=3320 sudo -E ./install.sh

set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script is for macOS. Use dist-install-linux.sh or dist-install-windows.ps1 instead." >&2
  exit 1
fi

if [[ "${EUID}" -ne 0 ]]; then
  echo "This must be run with sudo - LaunchDaemons live under /Library/LaunchDaemons, which" >&2
  echo "only root can write to. Re-run as: sudo ./install.sh" >&2
  exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
APP_USER=${APP_USER:-${SUDO_USER:-$(id -un)}}
INSTALL_DIR=${INSTALL_DIR:-$SCRIPT_DIR}
MAIN_PORT=${MAIN_PORT:-5080}
WEB_PORT=${WEB_PORT:-3320}
LOG_DIR=${LOG_DIR:-$INSTALL_DIR/logs}

BRIDGE_BIN="$INSTALL_DIR/SentinelCore/SentinelCore"
WEB_BIN="$INSTALL_DIR/SentinelCore.Web/SentinelCore.Web"
for required in "$BRIDGE_BIN" "$WEB_BIN"; do
  if [[ ! -x "$required" ]]; then
    echo "Expected a prebuilt binary at $required - is this script still next to the" >&2
    echo "SentinelCore/ and SentinelCore.Web/ folders from the release .zip?" >&2
    exit 1
  fi
done

# Clear Gatekeeper's quarantine flag proactively - otherwise the daemons above fail to launch
# with no obvious error beyond "Operation not permitted" in the log.
xattr -dr com.apple.quarantine "$INSTALL_DIR" 2>/dev/null || true

# ---------------------------------------------------------------------------
# Dependencies - ffmpeg/Node/Ollama via Homebrew, same as ../install-macos.sh. The runtime
# itself is already bundled into the binaries above, so the .NET SDK is NOT needed here.
# ---------------------------------------------------------------------------

if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew was not found. Install it from https://brew.sh and run this again." >&2
  exit 1
fi
BREW_AS_USER=(sudo -u "$APP_USER" -H brew)

missing=()
command -v ffmpeg >/dev/null 2>&1 || missing+=("ffmpeg")
command -v node   >/dev/null 2>&1 || missing+=("node")
command -v ollama >/dev/null 2>&1 || missing+=("ollama")

describe_dependency() {
  case "$1" in
    ffmpeg) echo "ffmpeg - required for live view, trigger zones and clip recording" ;;
    node)   echo "Node.js 22+ - only for Apple HomeKit support; skippable" ;;
    ollama) echo "Ollama - local AI vision. Skippable if you'll use a cloud model instead" ;;
  esac
}

if [[ ${#missing[@]} -gt 0 ]]; then
  echo "The following are not installed:"
  for dep in "${missing[@]}"; do echo "  - $(describe_dependency "$dep")"; done
  echo

  if [[ "${ASSUME_YES:-0}" == "1" ]]; then
    reply=y
  elif [[ ! -t 0 ]]; then
    echo "Not running interactively. Re-run with ASSUME_YES=1 to install these automatically," >&2
    echo "or install them yourself and run this again." >&2
    exit 1
  else
    read -r -p "Install them now with Homebrew? [y/N] " reply
  fi

  if [[ "$reply" =~ ^[Yy]$ ]]; then
    for dep in "${missing[@]}"; do "${BREW_AS_USER[@]}" install "$dep"; done
  else
    echo "Skipping dependency install."
  fi
fi

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "WARNING: ffmpeg is not installed. Live view, trigger zones and clip recording won't work." >&2
fi
if ! command -v ollama >/dev/null 2>&1; then
  echo "WARNING: Ollama is not installed. Either install it and run 'ollama pull gemma3:4b'," >&2
  echo "         or configure a cloud vision provider in the dashboard's setup wizard." >&2
fi

NODE_PATH=""
if command -v node >/dev/null 2>&1; then
  NODE_PATH=$(command -v node)
  if [[ -d "$INSTALL_DIR/homekit-bridge" ]]; then
    echo "==> Installing HomeKit sidecar dependencies"
    (cd "$INSTALL_DIR/homekit-bridge" && sudo -u "$APP_USER" -H npm install --omit=dev --no-audit --no-fund)
  fi
fi

mkdir -p "$LOG_DIR"

# ---------------------------------------------------------------------------
# Fresh install or upgrade
# ---------------------------------------------------------------------------
# The archive ships its configuration as appsettings.defaults.json and contains no
# appsettings.json at all, so unpacking a new release over an existing install leaves that
# install's real settings - cameras, credentials, alert channels, whether setup is finished -
# exactly as they were. All that's left for this script is to create appsettings.json from the
# defaults when there genuinely isn't one, and never to touch it when there is. Settings a new
# version introduced are merged into the existing file by the apps themselves on their next
# start, add-only; see ConfigDefaultsSeeder.

MAIN_SETTINGS="$INSTALL_DIR/SentinelCore/appsettings.json"
WEB_SETTINGS="$INSTALL_DIR/SentinelCore.Web/appsettings.json"
IS_UPGRADE=0
[[ -f "$MAIN_SETTINGS" ]] && IS_UPGRADE=1

seed_settings() {
  local dir=$1 name=$2
  if [[ -f "$dir/appsettings.json" ]]; then
    echo "    $name/appsettings.json already exists - left untouched"
    return
  fi
  if [[ ! -f "$dir/appsettings.defaults.json" ]]; then
    echo "Expected $dir/appsettings.defaults.json in the release bundle - the .zip looks incomplete." >&2
    exit 1
  fi
  cp "$dir/appsettings.defaults.json" "$dir/appsettings.json"
  echo "    $name/appsettings.json created from the shipped defaults"
}

BACKUP_DIR=""
if [[ "$IS_UPGRADE" == "1" ]]; then
  BACKUP_DIR="$INSTALL_DIR/backups/$(date +%Y%m%d-%H%M%S)"
  echo "==> Existing install found - upgrading"
  # Only the configuration is copied. The databases and captured images under SentinelCore/data
  # can run to gigabytes, and duplicating them on every upgrade would fill the disk - they are
  # also never at risk here, since nothing in the archive is named like them. To capture cameras
  # and settings in one portable file before upgrading, use Settings -> Backup in the dashboard.
  mkdir -p "$BACKUP_DIR"
  cp "$MAIN_SETTINGS" "$BACKUP_DIR/SentinelCore.appsettings.json"
  [[ -f "$WEB_SETTINGS" ]] && cp "$WEB_SETTINGS" "$BACKUP_DIR/SentinelCore.Web.appsettings.json"
  echo "    Configuration backed up to $BACKUP_DIR"
else
  echo "==> No existing install found - setting up from scratch"
fi

echo "==> Configuration"
seed_settings "$INSTALL_DIR/SentinelCore" "SentinelCore"
seed_settings "$INSTALL_DIR/SentinelCore.Web" "SentinelCore.Web"

chown -R "$APP_USER" "$INSTALL_DIR"

# ---------------------------------------------------------------------------
# LaunchDaemons
# ---------------------------------------------------------------------------

write_plist() {
  local label=$1 workdir=$2 program=$3 extra_arg=$4
  local plist_path="/Library/LaunchDaemons/${label}.plist"
  shift 4
  local env_xml=""
  while [[ $# -gt 0 ]]; do
    env_xml+="        <key>${1}</key><string>${2}</string>"$'\n'
    shift 2
  done

  cat > "$plist_path" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>${label}</string>
    <key>UserName</key><string>${APP_USER}</string>
    <key>WorkingDirectory</key><string>${workdir}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${program}</string>
$( [[ -n "$extra_arg" ]] && echo "        <string>${extra_arg}</string>" )
    </array>
    <key>EnvironmentVariables</key>
    <dict>
${env_xml}    </dict>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>StandardOutPath</key><string>${LOG_DIR}/${label}.log</string>
    <key>StandardErrorPath</key><string>${LOG_DIR}/${label}.err.log</string>
</dict>
</plist>
PLIST

  chown root:wheel "$plist_path"
  chmod 644 "$plist_path"
}

install_daemon() {
  local label=$1
  echo "==> Installing $label"
  launchctl bootout system "/Library/LaunchDaemons/${label}.plist" >/dev/null 2>&1 || true
  launchctl bootstrap system "/Library/LaunchDaemons/${label}.plist"
  launchctl enable "system/${label}"
}

write_plist com.sentinelcore.bridge "$INSTALL_DIR/SentinelCore" "$BRIDGE_BIN" "" \
  ASPNETCORE_ENVIRONMENT Production ASPNETCORE_URLS "http://0.0.0.0:$MAIN_PORT"

write_plist com.sentinelcore.web "$INSTALL_DIR/SentinelCore.Web" "$WEB_BIN" "" \
  ASPNETCORE_ENVIRONMENT Production ASPNETCORE_URLS "http://0.0.0.0:$WEB_PORT"

install_daemon com.sentinelcore.bridge
install_daemon com.sentinelcore.web

if [[ -n "$NODE_PATH" && -d "$INSTALL_DIR/homekit-bridge" ]]; then
  write_plist com.sentinelcore.homekit "$INSTALL_DIR/homekit-bridge" "$NODE_PATH" "$INSTALL_DIR/homekit-bridge/index.js" \
    NODE_ENV production HOMEKIT_MAIN_SETTINGS "$INSTALL_DIR/SentinelCore/appsettings.json"
  install_daemon com.sentinelcore.homekit
fi

echo
echo "==> Status"
launchctl print system/com.sentinelcore.bridge 2>/dev/null | grep -E "state = |pid = " || true
launchctl print system/com.sentinelcore.web 2>/dev/null | grep -E "state = |pid = " || true

# Polled rather than a single check after a fixed sleep: first start after an upgrade can be
# slower than a normal restart (the single-file binaries re-extract their native libraries), and
# a spurious "unreachable" here is exactly the sort of thing that sends someone reinstalling a
# working system.
wait_for_health() {
  local label=$1 url=$2 code=""
  for _ in $(seq 1 30); do
    code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 3 "$url" || true)
    if [[ "$code" == "200" ]]; then
      echo "$label: 200"
      return 0
    fi
    sleep 1
  done
  echo "$label: ${code:-unreachable}"
  return 1
}

echo
echo "==> Health checks"
HEALTHY=1
wait_for_health "main app     ($MAIN_PORT)" "http://127.0.0.1:$MAIN_PORT/health/live" || HEALTHY=0
wait_for_health "web dashboard($WEB_PORT)" "http://127.0.0.1:$WEB_PORT/api/health" || HEALTHY=0

if [[ "$HEALTHY" != "1" ]]; then
  echo
  echo "One or more services did not come up. Logs:" >&2
  echo "  tail -n 50 $LOG_DIR/com.sentinelcore.bridge.err.log $LOG_DIR/com.sentinelcore.web.err.log" >&2
  if [[ "$IS_UPGRADE" == "1" ]]; then
    echo >&2
    echo "This was an upgrade. To go back to the previous version, unpack the release .zip you" >&2
    echo "were running before over this directory, re-run its install.sh, and then restore the" >&2
    echo "configuration this script saved:" >&2
    echo "  cp $BACKUP_DIR/SentinelCore.appsettings.json $MAIN_SETTINGS" >&2
    echo "Your cameras, events and captured images were never modified." >&2
  fi
  exit 1
fi

if [[ "$IS_UPGRADE" == "1" ]]; then
  cat <<NEXT

==> Upgrade complete. The dashboard is at http://<this-machine's-LAN-IP>:$WEB_PORT
    Your settings, cameras, events, captured images and HTTPS certificate all carried over -
    no need to run the setup wizard again, and devices that already trust this server's
    certificate stay trusted.

    Logs: $LOG_DIR/com.sentinelcore.{bridge,web}.log (and .err.log for stderr)

    Previous configuration saved at:
      $BACKUP_DIR

    Keep the .zip you were running before this one. It is what you would unpack to go back,
    and it is the only copy of the previous binaries once this archive has replaced them.
NEXT
else
  cat <<NEXT

==> Done. Open the dashboard at http://<this-machine's-LAN-IP>:$WEB_PORT
    The setup wizard walks through the rest (API key, vision backend, cameras, notifications).
    HTTPS is already on too, at the same address on port 3443 - the app generated its own
    certificate on first startup. See docs/DEPLOYMENT.md section 14a to trust it on your other
    devices and drop the browser warning.

    Logs: $LOG_DIR/com.sentinelcore.{bridge,web}.log (and .err.log for stderr)

    Updating: this isn't a git checkout - unpack the next release .zip over this directory and
    re-run its install.sh. It will detect this install, keep your settings and data, back the
    configuration up first, and restart onto the new binaries.
NEXT
fi
