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
    read -r -p "Install them now? This uses apt and changes system packages. [y/N] " reply
  fi

  if [[ "$reply" =~ ^[Yy]$ ]]; then
    if [[ " ${missing[*]} " == *" ffmpeg "* ]]; then
      echo "==> Installing ffmpeg"
      apt-get update && apt-get install -y ffmpeg
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
if ! command -v ollama >/dev/null 2>&1; then
  echo "WARNING: Ollama is not installed. Either install it and run 'ollama pull gemma3:4b'," >&2
  echo "         or configure a cloud vision provider in the dashboard's setup wizard." >&2
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

systemctl daemon-reload
systemctl enable --now sentinelcore
systemctl enable --now sentinelcore-web
[[ -n "$NODE_PATH" && -d "$INSTALL_DIR/homekit-bridge" ]] && systemctl enable --now sentinelcore-homekit

sleep 3
echo
echo "==> Status"
systemctl --no-pager --lines=0 status sentinelcore sentinelcore-web 2>/dev/null | grep -E "●|Active:" || true

echo
echo "==> Health checks"
curl -s -o /dev/null -w "main app     ($MAIN_PORT): %{http_code}\n" "http://127.0.0.1:$MAIN_PORT/health/live" || echo "main app     ($MAIN_PORT): unreachable"
curl -s -o /dev/null -w "web dashboard($WEB_PORT): %{http_code}\n" "http://127.0.0.1:$WEB_PORT/api/health" || echo "web dashboard($WEB_PORT): unreachable"

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

    2. Updating. This isn't a git checkout - to update, download the next release .zip and
       re-run this script from inside it with the same INSTALL_DIR (it overwrites the units and
       restarts the services; your data in SentinelCore/data and SentinelCore.Web/data is
       untouched since it isn't part of the release archive).
NEXT
