#!/usr/bin/env bash
# One-command install for Sentinel Core: builds everything, installs all three systemd
# services, and starts them.
#
#   ./scripts/install-linux.sh
#
# Services installed:
#   sentinelcore          the alert pipeline           (port 5080)
#   sentinelcore-web      the dashboard                (port 3320)
#   sentinelcore-homekit  Apple Home sidecar, optional (needs Node.js)
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
else
  # Ollama runs llama.cpp's llama-server, whose RAM prompt cache defaults to 8 GB. Camera images
  # never hit it, so it grows ~400 MB per alert until a 16 GB box runs out of memory every few
  # hours (the media box, 2026-10). Ollama passes its environment to llama-server; this turns
  # the cache off. Same file as core/systemd/ollama-cache-ram.conf - see docs/DEPLOYMENT.md.
  if systemctl cat ollama.service >/dev/null 2>&1; then
    echo "==> Turning off Ollama's RAM prompt cache"
    sudo mkdir -p /etc/systemd/system/ollama.service.d
    printf '[Service]\nEnvironment="LLAMA_ARG_CACHE_RAM=0"\n' | sudo tee /etc/systemd/system/ollama.service.d/cache-ram.conf >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl restart ollama
  fi
  for _ in $(seq 30); do ollama list >/dev/null 2>&1 && break; sleep 1; done
  if ! ollama list 2>/dev/null | grep -q '^gemma3:4b'; then
    echo "==> Downloading the gemma3:4b vision model (about 3.3 GB)"
    ollama pull gemma3:4b || echo "WARNING: 'ollama pull gemma3:4b' failed - run it by hand before the first alert." >&2
  fi
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

# appsettings.json is untracked live configuration (the dashboard writes every credential into
# it); a fresh clone has only the checked-in, secret-free appsettings.defaults.json. Create the
# live file before either service starts, because the dashboard reads it too. Never overwrite one.
if [[ ! -f "$INSTALL_DIR/SentinelCore/appsettings.json" ]]; then
  echo "==> Creating SentinelCore/appsettings.json from appsettings.defaults.json"
  cp "$INSTALL_DIR/SentinelCore/appsettings.defaults.json" "$INSTALL_DIR/SentinelCore/appsettings.json"
fi

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

install_unit sentinelcore
install_unit sentinelcore-web
[[ -n "$NODE_PATH" ]] && install_unit sentinelcore-homekit

sudo systemctl daemon-reload
sudo systemctl enable --now sentinelcore
sudo systemctl enable --now sentinelcore-web
[[ -n "$NODE_PATH" ]] && sudo systemctl enable --now sentinelcore-homekit

sleep 3
echo
echo "==> Status"
systemctl --no-pager --lines=0 status sentinelcore sentinelcore-web 2>/dev/null | grep -E "●|Active:" || true
[[ -n "$NODE_PATH" ]] && (systemctl --no-pager --lines=0 status sentinelcore-homekit 2>/dev/null | grep -E "●|Active:" || true)

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

    HTTPS is already on, at the same address on port 3443 - the app generated its own
    certificate on first startup, no setup needed. Browsers still show a warning until you
    trust that certificate's authority on each device; see docs/DEPLOYMENT.md 14a for the
    one-time steps per platform, or to point this at a real domain certificate instead.
NEXT
