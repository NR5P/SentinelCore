#!/usr/bin/env bash
# Installs a prebuilt Sentinel Core release (this script ships inside the release .zip, next to
# the SentinelCore/ and SentinelCore.Web/ folders) as systemd services.
#
# Unlike ../install-linux.sh (which builds from a git checkout and needs the .NET SDK), this
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
# Services installed:
#   sentinelcore          the alert pipeline           (port 5080)
#   sentinelcore-web      the dashboard                (port 3320, and 3443 for HTTPS)
#   sentinelcore-homekit  Apple Home sidecar, optional (needs Node.js)
#
# Override any of these if your setup differs:
#   INSTALL_DIR=/opt/sentinelcore MAIN_PORT=5080 WEB_PORT=3320 sudo -E ./install.sh

set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "This must be run with sudo - it installs systemd units under /etc/systemd/system." >&2
  echo "Re-run as: sudo ./install.sh" >&2
  exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
APP_USER=${APP_USER:-${SUDO_USER:-root}}
INSTALL_DIR=${INSTALL_DIR:-$SCRIPT_DIR}
MAIN_PORT=${MAIN_PORT:-5080}
WEB_PORT=${WEB_PORT:-3320}

# This folder is the install - the services run from it and keep every database, recording and
# setting inside it. Unzipped into Downloads or /tmp, one routine clean-up deletes the server.
case "$INSTALL_DIR" in
  */Downloads|*/Downloads/*|/tmp/*|/var/tmp/*)
    echo "Sentinel Core runs from this folder and keeps all of its data in it:"
    echo "  $INSTALL_DIR"
    echo "That's a temporary place - clear it out and the server and everything it recorded go too."
    echo "Move it somewhere permanent first, then run the installer from there:"
    echo "  sudo mv \"$INSTALL_DIR\" /opt/sentinelcore && cd /opt/sentinelcore && sudo ./install.sh"
    if [[ "${ASSUME_YES:-0}" != "1" ]]; then
      if [[ ! -t 0 ]]; then
        echo "Not running interactively - stopping. Set ASSUME_YES=1 to install here anyway." >&2
        exit 1
      fi
      read -r -p "Install here anyway? [y/N] " reply
      [[ "$reply" =~ ^[Yy]$ ]] || exit 1
    fi
    ;;
esac

BRIDGE_BIN="$INSTALL_DIR/SentinelCore/SentinelCore"
WEB_BIN="$INSTALL_DIR/SentinelCore.Web/SentinelCore.Web"
for required in "$BRIDGE_BIN" "$WEB_BIN"; do
  if [[ ! -x "$required" ]]; then
    echo "Expected a prebuilt binary at $required - is this script still next to the" >&2
    echo "SentinelCore/ and SentinelCore.Web/ folders from the release .zip?" >&2
    exit 1
  fi
done

# ---------------------------------------------------------------------------
# Dependencies - ffmpeg/Node/Ollama, same as ../install-linux.sh. The runtime itself is already
# bundled into the binaries above, so dotnet is NOT needed here.
# ---------------------------------------------------------------------------

# go2rtc is what makes live view real-time (WebRTC/MSE instead of HLS's few seconds of delay).
# Pinned and checksummed rather than "latest", so an install never runs a binary nobody has
# looked at. The dashboard finds it on the PATH by itself (Go2Rtc:Path) - no config to write.
GO2RTC_VERSION=v1.9.14
go2rtc_asset() {
  case "$(uname -m)" in
    x86_64)        echo "go2rtc_linux_amd64 32d616af226bd731678ffde328b94cfb94e30339bfefc469cfb76323144615a6" ;;
    aarch64|arm64) echo "go2rtc_linux_arm64 359fabade8a7a51e81a55fe6df6b0ef81764a5e1d63179577534eaaa71904b50" ;;
  esac
}

install_go2rtc() {
  local asset sha tmp
  read -r asset sha <<<"$(go2rtc_asset)"
  if [[ -z "$asset" ]]; then
    echo "WARNING: no go2rtc build for $(uname -m) - live view stays on HLS." >&2
    return 0
  fi
  echo "==> Installing go2rtc $GO2RTC_VERSION (real-time live view)"
  tmp=$(mktemp)
  if curl -fsSL -o "$tmp" "https://github.com/AlexxIT/go2rtc/releases/download/$GO2RTC_VERSION/$asset" \
    && echo "$sha  $tmp" | sha256sum -c --quiet -; then
    install -o root -g root -m 0755 "$tmp" /usr/local/bin/go2rtc
  else
    echo "WARNING: go2rtc download or checksum failed - live view stays on HLS until it's installed." >&2
  fi
  rm -f "$tmp"
}

missing=()
command -v ffmpeg >/dev/null 2>&1 || missing+=("ffmpeg")
command -v go2rtc >/dev/null 2>&1 || missing+=("go2rtc")
command -v node   >/dev/null 2>&1 || missing+=("node")
# SKIP_OLLAMA=1: you'll use a cloud vision provider, or run Ollama on another machine.
[[ "${SKIP_OLLAMA:-0}" == "1" ]] || command -v ollama >/dev/null 2>&1 || missing+=("ollama")

describe_dependency() {
  case "$1" in
    ffmpeg) echo "ffmpeg - required for live view, trigger zones and clip recording" ;;
    go2rtc) echo "go2rtc - real-time live view (without it live video runs a few seconds behind)" ;;
    node)   echo "Node.js 22+ - only for Apple HomeKit support; skippable" ;;
    ollama) echo "Ollama - runs the full local AI. Its model (~4.3 GB) only downloads if you pick it during setup. Skippable (SKIP_OLLAMA=1)" ;;
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
    read -r -p "Install them now? This uses apt and changes system packages. [y/N] " reply
  fi

  if [[ "$reply" =~ ^[Yy]$ ]]; then
    if [[ " ${missing[*]} " == *" ffmpeg "* ]]; then
      echo "==> Installing ffmpeg"
      apt-get update && apt-get install -y ffmpeg
    fi
    if [[ " ${missing[*]} " == *" go2rtc "* ]]; then
      install_go2rtc
    fi
    if [[ " ${missing[*]} " == *" node "* ]]; then
      echo "==> Installing Node.js LTS (NodeSource)"
      curl -fsSL https://deb.nodesource.com/setup_lts.x | bash -
      apt-get install -y nodejs
    fi
    if [[ " ${missing[*]} " == *" ollama "* ]]; then
      echo "==> Installing Ollama"
      curl -fsSL https://ollama.com/install.sh | sh
    fi
  else
    echo "Skipping dependency install."
  fi
fi

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "WARNING: ffmpeg is not installed. Live view, trigger zones and clip recording won't work." >&2
fi
if ! command -v go2rtc >/dev/null 2>&1; then
  echo "WARNING: go2rtc is not installed. Live view works, but a few seconds behind the camera." >&2
fi
if [[ "${SKIP_OLLAMA:-0}" == "1" ]]; then
  echo "Skipping Ollama (SKIP_OLLAMA=1) - pick a cloud vision provider in the setup wizard."
elif ! command -v ollama >/dev/null 2>&1; then
  echo "NOTE: Ollama is not installed. Alerts use the fast local detector until you set up a full AI -" >&2
  echo "      install Ollama (https://ollama.com) and pick it in the setup wizard, or pick a cloud provider." >&2
else
  # Ollama runs llama.cpp's llama-server, whose RAM prompt cache defaults to 8 GB. Camera images
  # never hit it, so it grows ~400 MB per alert until a 16 GB box runs out of memory every few
  # hours (the media box, 2026-10). Ollama passes its environment to llama-server; this turns
  # the cache off. Same file as core/systemd/ollama-cache-ram.conf - see docs/DEPLOYMENT.md.
  if systemctl cat ollama.service >/dev/null 2>&1; then
    echo "==> Turning off Ollama's RAM prompt cache"
    mkdir -p /etc/systemd/system/ollama.service.d
    printf '[Service]\nEnvironment="LLAMA_ARG_CACHE_RAM=0"\n' | tee /etc/systemd/system/ollama.service.d/cache-ram.conf >/dev/null
    systemctl daemon-reload
    systemctl restart ollama
  fi
  # No model is pulled here. A new install starts on the fast local detector, and the 4.3 GB
  # vision model is downloaded by the app itself the moment someone picks Ollama in the setup
  # wizard or Settings (OllamaModelProvisioner) - so a machine that never uses it never pays for it.
fi

NODE_PATH=""
if command -v node >/dev/null 2>&1; then
  NODE_PATH=$(command -v node)
  if [[ -d "$INSTALL_DIR/homekit-bridge" ]]; then
    echo "==> Installing HomeKit sidecar dependencies"
    (cd "$INSTALL_DIR/homekit-bridge" && npm install --omit=dev --no-audit --no-fund)
  fi
fi

id -u "$APP_USER" >/dev/null 2>&1 || { echo "User $APP_USER does not exist - set APP_USER to an existing account." >&2; exit 1; }

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
  # can run to gigabytes, and duplicating them on every upgrade would fill the disk of exactly
  # the small machines this tends to run on - they are also never at risk here, since nothing in
  # the archive is named like them. To capture cameras and settings in one portable file before
  # upgrading, use Settings -> Backup in the dashboard.
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
# systemd units - written directly rather than templated from a separate file, since this
# script is meant to stand alone inside the release .zip.
# ---------------------------------------------------------------------------

install_unit() {
  local name=$1 description=$2 workdir=$3 exec_start=$4 extra_env=$5 after=$6
  cat > "/etc/systemd/system/${name}.service" <<UNIT
[Unit]
Description=${description}
Wants=network-online.target
After=network-online.target${after}

[Service]
Type=simple
User=${APP_USER}
Group=${APP_USER}
WorkingDirectory=${workdir}
ExecStart=${exec_start}

Environment=ASPNETCORE_ENVIRONMENT=Production
${extra_env}

Restart=always
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=${name}

[Install]
WantedBy=multi-user.target
UNIT
}

echo "==> Installing sentinelcore"
install_unit sentinelcore "SentinelCore AI Vision Engine" \
  "$INSTALL_DIR/SentinelCore" "$BRIDGE_BIN" \
  "Environment=ASPNETCORE_URLS=http://0.0.0.0:$MAIN_PORT" " ollama.service"

echo "==> Installing sentinelcore-web"
install_unit sentinelcore-web "SentinelCore Web Dashboard" \
  "$INSTALL_DIR/SentinelCore.Web" "$WEB_BIN" \
  "Environment=ASPNETCORE_URLS=http://0.0.0.0:$WEB_PORT" " sentinelcore.service"

if [[ -n "$NODE_PATH" && -d "$INSTALL_DIR/homekit-bridge" ]]; then
  echo "==> Installing sentinelcore-homekit"
  cat > "/etc/systemd/system/sentinelcore-homekit.service" <<UNIT
[Unit]
Description=SentinelCore HomeKit Sidecar
Wants=network-online.target
After=network-online.target avahi-daemon.service sentinelcore.service

[Service]
Type=simple
User=${APP_USER}
Group=${APP_USER}
WorkingDirectory=${INSTALL_DIR}/homekit-bridge
ExecStart=${NODE_PATH} ${INSTALL_DIR}/homekit-bridge/index.js

Environment=NODE_ENV=production
Environment=HOMEKIT_MAIN_SETTINGS=${INSTALL_DIR}/SentinelCore/appsettings.json

Restart=always
RestartSec=5
TimeoutStopSec=30
SyslogIdentifier=sentinelcore-homekit

[Install]
WantedBy=multi-user.target
UNIT
fi

# ---------------------------------------------------------------------------
# Automatic updates (docs/AUTO_UPDATE.md). The dashboard downloads and verifies a release into
# $INSTALL_DIR/updates and drops pending.json there; this path unit then runs the root-owned
# installer, which swaps the files in, restarts, and rolls back if the new build doesn't come up.
# Turn it off in the dashboard (Settings -> AUTOMATIC UPDATES), not here.
# ---------------------------------------------------------------------------

if [[ -f "$SCRIPT_DIR/sentinelcore-apply-update.sh" ]]; then
  echo "==> Setting up automatic updates"
  mkdir -p "$INSTALL_DIR/updates"
  chown "$APP_USER" "$INSTALL_DIR/updates"
  # Root-owned and outside the install folder: the app user can write everything in there, so
  # the code that runs as root must not live in it.
  install -D -o root -g root -m 0755 "$SCRIPT_DIR/sentinelcore-apply-update.sh" /usr/local/libexec/sentinelcore-apply-update
  mkdir -p /etc/sentinelcore
  cat > /etc/sentinelcore/update.conf <<CONF
INSTALL_DIR=${INSTALL_DIR}
APP_USER=${APP_USER}
MAIN_PORT=${MAIN_PORT}
WEB_PORT=${WEB_PORT}
CONF
  chmod 0644 /etc/sentinelcore/update.conf

  cat > /etc/systemd/system/sentinelcore-update.path <<UNIT
[Unit]
Description=Watch for a staged Sentinel Core update

[Path]
PathExists=${INSTALL_DIR}/updates/pending.json
Unit=sentinelcore-update.service

[Install]
WantedBy=multi-user.target
UNIT

  cat > /etc/systemd/system/sentinelcore-update.service <<UNIT
[Unit]
Description=Install a staged Sentinel Core update

[Service]
Type=oneshot
ExecStart=/usr/local/libexec/sentinelcore-apply-update
TimeoutStartSec=15min
SyslogIdentifier=sentinelcore-update
UNIT
fi

systemctl daemon-reload
[[ -f /etc/systemd/system/sentinelcore-update.path ]] && systemctl enable --now sentinelcore-update.path >/dev/null

# enable, then restart - deliberately not `enable --now`, which only *starts* a service and so
# leaves an already-running one on the previous release's binaries. That made re-running this
# script look like a successful upgrade while the old code kept serving. `restart` starts a
# stopped service too, so it is also correct on a first install.
start_service() {
  local name=$1
  systemctl enable "$name" >/dev/null
  systemctl restart "$name"
}

start_service sentinelcore
start_service sentinelcore-web
[[ -n "$NODE_PATH" && -d "$INSTALL_DIR/homekit-bridge" ]] && start_service sentinelcore-homekit

echo
echo "==> Status"
systemctl --no-pager --lines=0 status sentinelcore sentinelcore-web 2>/dev/null | grep -E "●|Active:" || true

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
  echo "  journalctl -u sentinelcore -u sentinelcore-web -n 50 --no-pager" >&2
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

    Two things this script deliberately does NOT do, because they depend on your network:

    1. Firewall. These ports are LAN-only by design - never expose them to the internet:
         sudo ufw allow from <your-subnet>/24 to any port $MAIN_PORT proto tcp
         sudo ufw allow from <your-subnet>/24 to any port $WEB_PORT proto tcp
       Plus, only if you turn HomeKit on (iPhones connect to this port directly):
         sudo ufw allow from <your-subnet>/24 to any port 51826 proto tcp

    2. Updating. This server updates itself overnight once a license is installed (Settings ->
       AUTOMATIC UPDATES to change when, or switch it off). To update by hand instead, unzip the
       next release somewhere else, copy it over this folder with cp -a --remove-destination and re-run
       install.sh here (docs/DEPLOYMENT.md, "Upgrading an existing install") - it keeps your
       settings and data, backs the configuration up first, and restarts onto the new binaries.
NEXT
fi
