#!/usr/bin/env bash
# One-command install for Sentinel Core on macOS - the launchd counterpart to install-linux.sh.
# Builds everything from this checkout and installs all three components as LaunchDaemons, so
# they start at boot without anyone being logged in (the same thing systemd gives on Linux).
#
#   ./scripts/install-macos.sh
#
# Services installed:
#   com.sentinelcore.bridge    the alert pipeline           (port 5080)
#   com.sentinelcore.web       the dashboard                (port 3320)
#   com.sentinelcore.homekit   Apple Home sidecar, optional (needs Node.js)
#
# The HomeKit sidecar is installed whenever Node.js is present, but it stays completely off the
# network until HomeKit is switched on in the dashboard - installing this shouldn't put an
# unpaired accessory on anyone's network uninvited. If Node isn't installed, it's skipped and
# everything else still works.
#
# Services run in place from this checkout (bin/Release), same as install-linux.sh - so updating
# is "git pull && ./scripts/deploy.sh" with nothing to re-copy.
#
# Override any of these if your setup differs:
#   APP_USER=someone INSTALL_DIR=/opt/sentinel MAIN_PORT=5080 WEB_PORT=3320 ./scripts/install-macos.sh
#
# Must be run with sudo - LaunchDaemons (system-wide, start-at-boot services) live under
# /Library/LaunchDaemons, which only root can write to.

set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script is for macOS. Use install-linux.sh or install-windows.ps1 instead." >&2
  exit 1
fi

if [[ "${EUID}" -ne 0 ]]; then
  echo "This must be run with sudo - LaunchDaemons live under /Library/LaunchDaemons, which" >&2
  echo "only root can write to. Re-run as: sudo ./scripts/install-macos.sh" >&2
  exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)

# sudo normally clears/changes $HOME and the invoking user's identity - APP_USER has to come
# from who actually ran sudo, not root, or the services would end up owned by root and every
# data file they create would need sudo to touch again afterwards.
APP_USER=${APP_USER:-${SUDO_USER:-$(id -un)}}
INSTALL_DIR=${INSTALL_DIR:-$ROOT_DIR}
MAIN_PORT=${MAIN_PORT:-5080}
WEB_PORT=${WEB_PORT:-3320}
LOG_DIR=${LOG_DIR:-$INSTALL_DIR/logs}

# ---------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------
# Nothing here is installed without asking first (or ASSUME_YES=1 for unattended runs) - this
# touches the whole machine, not just this directory.

if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew was not found. Install it from https://brew.sh and run this again." >&2
  exit 1
fi
# brew must run as the invoking (non-root) user, not root - it refuses to run as root entirely.
BREW_AS_USER=(sudo -u "$APP_USER" -H brew)

missing=()
command -v dotnet >/dev/null 2>&1 || missing+=("dotnet")
command -v ffmpeg >/dev/null 2>&1 || missing+=("ffmpeg")
command -v node   >/dev/null 2>&1 || missing+=("node")
command -v ollama >/dev/null 2>&1 || missing+=("ollama")

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
    echo "Not running interactively. Re-run with ASSUME_YES=1 to install these automatically," >&2
    echo "or install them yourself and run this again." >&2
    exit 1
  else
    read -r -p "Install them now with Homebrew? [y/N] " reply
  fi

  if [[ "$reply" =~ ^[Yy]$ ]]; then
    for dep in "${missing[@]}"; do
      case "$dep" in
        dotnet) echo "==> Installing .NET SDK";  "${BREW_AS_USER[@]}" install --cask dotnet-sdk ;;
        ffmpeg) echo "==> Installing ffmpeg";     "${BREW_AS_USER[@]}" install ffmpeg ;;
        node)   echo "==> Installing Node.js";    "${BREW_AS_USER[@]}" install node ;;
        ollama) echo "==> Installing Ollama";     "${BREW_AS_USER[@]}" install ollama ;;
      esac
    done
  else
    echo "Skipping dependency install."
  fi
fi

# Homebrew installs land in a path root's default PATH doesn't include (/opt/homebrew or
# /usr/local depending on chip) - refresh from the invoking user's shell rather than assuming.
eval "$(sudo -u "$APP_USER" -H "$SHELL" -lc 'command -v brew >/dev/null 2>&1 && echo export PATH="$PATH"' 2>/dev/null || true)"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is still not available. Install the .NET 10 SDK and run this again." >&2
  exit 1
fi
DOTNET_PATH=$(command -v dotnet)

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "WARNING: ffmpeg is not installed. Live view, trigger zones and clip recording won't work." >&2
fi

if ! command -v ollama >/dev/null 2>&1; then
  echo "WARNING: Ollama is not installed. Either install it and run 'ollama pull gemma4:e2b-it-qat'," >&2
  echo "         or configure a cloud vision provider in the dashboard's setup wizard." >&2
fi

NODE_PATH=""
if command -v node >/dev/null 2>&1; then
  NODE_PATH=$(command -v node)
fi

echo "==> Installing from $INSTALL_DIR as user $APP_USER"
mkdir -p "$LOG_DIR"
chown "$APP_USER" "$LOG_DIR"

echo "==> Clearing stale build servers (works around an intermittent CLR crash seen on Linux)"
sudo -u "$APP_USER" -H "$DOTNET_PATH" build-server shutdown >/dev/null 2>&1 || true

echo "==> Building (Release)"
sudo -u "$APP_USER" -H "$DOTNET_PATH" build "$ROOT_DIR" -c Release

# appsettings.json is untracked live configuration (the dashboard writes every credential into
# it); a fresh clone has only the checked-in, secret-free appsettings.defaults.json. Create the
# live file before either service starts, because the dashboard reads it too. Never overwrite one.
if [[ ! -f "$INSTALL_DIR/SentinelCore/appsettings.json" ]]; then
  echo "==> Creating SentinelCore/appsettings.json from appsettings.defaults.json"
  sudo -u "$APP_USER" cp "$INSTALL_DIR/SentinelCore/appsettings.defaults.json" "$INSTALL_DIR/SentinelCore/appsettings.json"
fi

if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
  echo "==> Running tests"
  sudo -u "$APP_USER" -H "$DOTNET_PATH" test "$ROOT_DIR" -c Release
else
  echo "==> Skipping tests (SKIP_TESTS=1)"
fi

if [[ -n "$NODE_PATH" ]]; then
  echo "==> Installing HomeKit sidecar dependencies"
  (cd "$ROOT_DIR/homekit-bridge" && sudo -u "$APP_USER" -H npm install --omit=dev --no-audit --no-fund)
else
  echo "==> Skipping HomeKit sidecar (Node.js not found - install Node 22+ and re-run to add it)"
fi

# ---------------------------------------------------------------------------
# LaunchDaemons
# ---------------------------------------------------------------------------
# A plist per component rather than templating a static file (unlike the systemd units) - the
# array-typed ProgramArguments key doesn't lend itself to sed substitution, and this keeps the
# logic in one place alongside install-windows.ps1's equivalent New-Service construction.

write_plist() {
  local label=$1 workdir=$2 program=$3 dll=$4
  local plist_path="/Library/LaunchDaemons/${label}.plist"
  shift 5 || true # the 5th positional arg is just a separator before the env pairs
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
$( [[ -n "$dll" ]] && echo "        <string>${dll}</string>" )
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
  # bootout is allowed to fail (nothing loaded yet on a first install).
  launchctl bootout system "/Library/LaunchDaemons/${label}.plist" >/dev/null 2>&1 || true
  launchctl bootstrap system "/Library/LaunchDaemons/${label}.plist"
  launchctl enable "system/${label}"
}

write_plist com.sentinelcore.bridge \
  "$INSTALL_DIR/SentinelCore" "$DOTNET_PATH" "$INSTALL_DIR/SentinelCore/bin/Release/net10.0/SentinelCore.dll" - \
  ASPNETCORE_ENVIRONMENT Production ASPNETCORE_URLS "http://0.0.0.0:$MAIN_PORT" DOTNET_NOLOGO true

write_plist com.sentinelcore.web \
  "$INSTALL_DIR/SentinelCore.Web" "$DOTNET_PATH" "$INSTALL_DIR/SentinelCore.Web/bin/Release/net10.0/SentinelCore.Web.dll" - \
  ASPNETCORE_ENVIRONMENT Production ASPNETCORE_URLS "http://0.0.0.0:$WEB_PORT"

install_daemon com.sentinelcore.bridge
install_daemon com.sentinelcore.web

if [[ -n "$NODE_PATH" ]]; then
  write_plist com.sentinelcore.homekit \
    "$INSTALL_DIR/homekit-bridge" "$NODE_PATH" "$INSTALL_DIR/homekit-bridge/index.js" - \
    NODE_ENV production HOMEKIT_MAIN_SETTINGS "$INSTALL_DIR/SentinelCore/appsettings.json"
  install_daemon com.sentinelcore.homekit
fi

sleep 3
echo
echo "==> Status"
launchctl print system/com.sentinelcore.bridge 2>/dev/null | grep -E "state = |pid = " || true
launchctl print system/com.sentinelcore.web 2>/dev/null | grep -E "state = |pid = " || true
[[ -n "$NODE_PATH" ]] && (launchctl print system/com.sentinelcore.homekit 2>/dev/null | grep -E "state = |pid = " || true)

echo
echo "==> Health checks"
curl -s -o /dev/null -w "main app     ($MAIN_PORT): %{http_code}\n" "http://127.0.0.1:$MAIN_PORT/health/live" || echo "main app     ($MAIN_PORT): unreachable"
curl -s -o /dev/null -w "web dashboard($WEB_PORT): %{http_code}\n" "http://127.0.0.1:$WEB_PORT/api/health" || echo "web dashboard($WEB_PORT): unreachable"

cat <<NEXT

==> Done. Open the dashboard at http://<this-machine's-LAN-IP>:$WEB_PORT
    The setup wizard walks through the rest (API key, vision backend, cameras, notifications).
    HTTPS is already on too, at the same address on port 3443, using a certificate this install
    generated for itself - see docs/DEPLOYMENT.md section 14a to trust it on your other devices
    and drop the browser warning.

    Logs: $LOG_DIR/com.sentinelcore.{bridge,web}.log (and .err.log for stderr)

    One thing this script deliberately does NOT do, because it depends on your network:
    Firewall (macOS's Application Firewall is off by default, so usually nothing to do here -
    if you've turned it on, allow incoming connections for the dotnet/SentinelCore binaries in
    System Settings -> Network -> Firewall).
NEXT
