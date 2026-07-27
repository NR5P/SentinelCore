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

# ---------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------
# Nothing here is installed without asking first (or ASSUME_YES=1 for unattended runs) -
# this touches the whole machine, not just this directory.

missing=()
command -v dotnet  >/dev/null 2>&1 || missing+=("dotnet")
command -v ffmpeg  >/dev/null 2>&1 || missing+=("ffmpeg")
command -v node    >/dev/null 2>&1 || missing+=("node")
command -v ollama  >/dev/null 2>&1 || missing+=("ollama")

describe_dependency() {
  case "$1" in
    dotnet) echo ".NET 10 SDK      - required, the app is built and run with it" ;;
    ffmpeg) echo "ffmpeg           - required for live view, trigger zones and clip recording" ;;
    node)   echo "Node.js 22+      - only for Apple HomeKit support; skippable" ;;
    ollama) echo "Ollama           - local AI vision. Skippable if you'll use a cloud model instead" ;;
  esac
}

if [[ ${#missing[@]} -gt 0 ]]; then
  echo "The following are not installed:"
  for dep in "${missing[@]}"; do echo "  - $(describe_dependency "$dep")"; done
  echo

  if [[ "${ASSUME_YES:-0}" == "1" ]]; then
    reply=y
  elif [[ ! -t 0 ]]; then
    # No terminal to prompt on, and no explicit consent - don't silently change the machine.
    echo "Not running interactively. Re-run with ASSUME_YES=1 to install these automatically," >&2
    echo "or install them yourself and run this again." >&2
    exit 1
  else
    read -r -p "Install them now? This uses sudo and changes system packages. [y/N] " reply
  fi

  if [[ "$reply" =~ ^[Yy]$ ]]; then
    apt_packages=()
    for dep in "${missing[@]}"; do
      case "$dep" in
        dotnet) apt_packages+=("dotnet-sdk-10.0") ;;
        ffmpeg) apt_packages+=("ffmpeg") ;;
      esac
    done

    if [[ ${#apt_packages[@]} -gt 0 ]]; then
      echo "==> Installing: ${apt_packages[*]}"
      sudo apt-get update
      sudo apt-get install -y "${apt_packages[@]}"
    fi

    # Node comes from NodeSource rather than apt - distro packages are often far too old for
    # the HomeKit library, which needs 22+.
    if [[ " ${missing[*]} " == *" node "* ]]; then
      echo "==> Installing Node.js LTS (NodeSource)"
      curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash -
      sudo apt-get install -y nodejs
    fi

    if [[ " ${missing[*]} " == *" ollama "* ]]; then
      echo "==> Installing Ollama"
      curl -fsSL https://ollama.com/install.sh | sh
    fi
  else
    echo "Skipping dependency install."
  fi
fi

# .NET is the one hard requirement - without it there's nothing to build.
if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is still not available. Install the .NET 10 SDK and run this again." >&2
  exit 1
fi
DOTNET_PATH=$(command -v dotnet)

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "WARNING: ffmpeg is not installed. Live view, trigger zones and clip recording won't work." >&2
fi

if ! command -v ollama >/dev/null 2>&1; then
  echo "WARNING: Ollama is not installed. Either install it and run 'ollama pull gemma3:4b'," >&2
  echo "         or configure a cloud vision provider in the dashboard's setup wizard." >&2
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

    Optional: browsers label the dashboard "Not Secure" because it runs over plain HTTP, which
    is fine on a home network. To get a padlock instead, run ./scripts/setup-local-https.sh -
    it explains what's involved. No public certificate authority will issue for a private
    address, so this needs either your own authority or a domain; see docs/DEPLOYMENT.md 14a.
NEXT
