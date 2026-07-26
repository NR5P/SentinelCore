#!/usr/bin/env bash
# One-command install for Sentinel Core: builds everything, installs all three systemd
# services, and starts them.
#
#   ./scripts/install-linux.sh
#
# Services installed:
#   blueiris-ollama-bridge          the alert pipeline           (port 5080)
#   blueiris-ollama-bridge-web      the dashboard                (port 3320)
#   blueiris-ollama-bridge-homekit  Apple Home sidecar, optional (needs Node.js)
#
# The HomeKit sidecar is installed whenever Node.js is present, but it stays completely off the
# network until HomeKit is switched on in the dashboard - installing this shouldn't put an
# unpaired accessory on anyone's network uninvited. If Node isn't installed, it's skipped and
# everything else still works.
#
# Services run in place from this checkout (bin/Release), which is the same layout deploy.sh
# rebuilds into - so updating is "git pull && ./scripts/deploy.sh" with nothing to re-copy.
#
# Override any of these if your setup differs:
#   APP_USER=someone INSTALL_DIR=/srv/sentinel MAIN_PORT=5080 WEB_PORT=3320 ./scripts/install-linux.sh

set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)

APP_USER=${APP_USER:-$(id -un)}
INSTALL_DIR=${INSTALL_DIR:-$ROOT_DIR}
MAIN_PORT=${MAIN_PORT:-5080}
WEB_PORT=${WEB_PORT:-3320}

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet was not found. Install the .NET 10 SDK first." >&2
  exit 1
fi
DOTNET_PATH=$(command -v dotnet)

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "WARNING: ffmpeg was not found. Live view, trigger zones and clip recording need it." >&2
fi

# Node is only needed for HomeKit. Missing Node is a skip, not a failure.
NODE_PATH=""
if command -v node >/dev/null 2>&1; then
  NODE_PATH=$(command -v node)
fi

echo "==> Installing from $INSTALL_DIR as user $APP_USER"

echo "==> Clearing stale build servers (works around an intermittent CLR crash seen here before)"
dotnet build-server shutdown >/dev/null 2>&1 || true

echo "==> Building (Release)"
dotnet build "$ROOT_DIR" -c Release

if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
  echo "==> Running tests"
  dotnet test "$ROOT_DIR" -c Release
else
  echo "==> Skipping tests (SKIP_TESTS=1)"
fi

if [[ -n "$NODE_PATH" ]]; then
  echo "==> Installing HomeKit sidecar dependencies"
  (cd "$ROOT_DIR/homekit-bridge" && npm install --omit=dev --no-audit --no-fund)
else
  echo "==> Skipping HomeKit sidecar (Node.js not found - install Node 22+ and re-run to add it)"
fi

install_unit() {
  local name=$1
  echo "==> Installing $name"
  sed -e "s|__DOTNET_PATH__|$DOTNET_PATH|g" \
      -e "s|__NODE_PATH__|$NODE_PATH|g" \
      -e "s|__APP_USER__|$APP_USER|g" \
      -e "s|__INSTALL_DIR__|$INSTALL_DIR|g" \
      -e "s|__MAIN_PORT__|$MAIN_PORT|g" \
      -e "s|__WEB_PORT__|$WEB_PORT|g" \
      "$ROOT_DIR/systemd/$name.service" | sudo tee "/etc/systemd/system/$name.service" >/dev/null
}

install_unit blueiris-ollama-bridge
install_unit blueiris-ollama-bridge-web
[[ -n "$NODE_PATH" ]] && install_unit blueiris-ollama-bridge-homekit

sudo systemctl daemon-reload
sudo systemctl enable --now blueiris-ollama-bridge
sudo systemctl enable --now blueiris-ollama-bridge-web
[[ -n "$NODE_PATH" ]] && sudo systemctl enable --now blueiris-ollama-bridge-homekit

sleep 3
echo
echo "==> Status"
systemctl --no-pager --lines=0 status blueiris-ollama-bridge blueiris-ollama-bridge-web 2>/dev/null | grep -E "●|Active:" || true
[[ -n "$NODE_PATH" ]] && (systemctl --no-pager --lines=0 status blueiris-ollama-bridge-homekit 2>/dev/null | grep -E "●|Active:" || true)

echo
echo "==> Health checks"
curl -s -o /dev/null -w "main app     ($MAIN_PORT): %{http_code}\n" "http://127.0.0.1:$MAIN_PORT/health/live" || echo "main app     ($MAIN_PORT): unreachable"
curl -s -o /dev/null -w "web dashboard($WEB_PORT): %{http_code}\n" "http://127.0.0.1:$WEB_PORT/api/health" || echo "web dashboard($WEB_PORT): unreachable"

cat <<NEXT

==> Done. Open the dashboard at http://<this-machine's-LAN-IP>:$WEB_PORT
    The setup wizard walks through the rest (API key, vision backend, cameras, notifications).

    Two things this script deliberately does NOT do, because they depend on your network:

    1. Firewall. These ports are LAN-only by design - never expose them to the internet:
         sudo ufw allow from <your-subnet>/24 to any port $MAIN_PORT proto tcp
         sudo ufw allow from <your-subnet>/24 to any port $WEB_PORT proto tcp
       Plus, only if you turn HomeKit on (iPhones connect to this port directly):
         sudo ufw allow from <your-subnet>/24 to any port 51826 proto tcp

    2. Passwordless restarts, so ./scripts/deploy.sh doesn't prompt each time.
       See docs/DEPLOYMENT.md, "Passwordless restarts".
NEXT
