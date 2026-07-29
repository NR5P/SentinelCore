#!/usr/bin/env bash
# Build and restart the SentinelCore services in place - matching how they're
# actually deployed (running directly from bin/Release/net10.0 via the systemd units in
# ../systemd, not published anywhere else). See install-linux.sh for the separate initial
# install flow, which is not what this script does.
#
# Usage:
#   ./scripts/deploy.sh            # build + restart all services
#   ./scripts/deploy.sh main       # build + restart only sentinelcore
#   ./scripts/deploy.sh web        # build + restart only sentinelcore-web
#   ./scripts/deploy.sh homekit    # restart only the HomeKit sidecar (Node, nothing to build)
#
# Set SKIP_TESTS=1 to skip the test run (not recommended - it's the one thing standing
# between a bad build and the live alert pipeline).

set -euo pipefail

TARGET=${1:-all}
ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

case "$TARGET" in
  main|web|homekit|all) ;;
  *)
    echo "Unknown target '$TARGET'. Use: all, main, web, or homekit." >&2
    exit 1
    ;;
esac

echo "==> Clearing stale build servers (works around an intermittent CLR crash seen here before)"
dotnet build-server shutdown >/dev/null 2>&1 || true

echo "==> Building solution (Release)"
dotnet build -c Release

if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
  echo "==> Running tests"
  dotnet test
else
  echo "==> Skipping tests (SKIP_TESTS=1)"
fi

# Separate, fixed-argument sudo calls rather than one "restart a b c" invocation - the
# NOPASSWD sudoers entries match on exact command lines, so this is what lets both the
# per-service and combined restarts work under the same allowlisted commands.
#
# macOS (installed via install-macos.sh) uses launchd LaunchDaemons instead of systemd - same
# job, different tool, so every restart_* function below branches on uname rather than existing
# twice. launchctl kickstart -k forcibly restarts a running daemon in one step, same as
# `systemctl restart`.
IS_MACOS=0
[[ "$(uname -s)" == "Darwin" ]] && IS_MACOS=1

restart_main() {
  echo "==> Restarting sentinelcore"
  if [[ "$IS_MACOS" == "1" ]]; then
    sudo launchctl kickstart -k system/com.sentinelcore.bridge
  else
    sudo systemctl restart sentinelcore
  fi
}
restart_web() {
  echo "==> Restarting sentinelcore-web"
  if [[ "$IS_MACOS" == "1" ]]; then
    sudo launchctl kickstart -k system/com.sentinelcore.web
  else
    sudo systemctl restart sentinelcore-web
  fi
}
# Optional - only installed if HomeKit is being used, so a missing unit isn't an error.
restart_homekit() {
  if [[ "$IS_MACOS" == "1" ]]; then
    if ! launchctl print system/com.sentinelcore.homekit >/dev/null 2>&1; then
      echo "==> Skipping HomeKit sidecar (not installed)"
      return
    fi
    echo "==> Restarting sentinelcore-homekit"
    sudo launchctl kickstart -k system/com.sentinelcore.homekit
    return
  fi

  if ! systemctl list-unit-files sentinelcore-homekit.service >/dev/null 2>&1 ||
     ! systemctl cat sentinelcore-homekit >/dev/null 2>&1; then
    echo "==> Skipping HomeKit sidecar (not installed)"
    return
  fi
  echo "==> Restarting sentinelcore-homekit"
  sudo systemctl restart sentinelcore-homekit
}

case "$TARGET" in
  main) restart_main ;;
  web) restart_web ;;
  homekit) restart_homekit ;;
  all) restart_main; restart_web; restart_homekit ;;
esac

sleep 2
echo
echo "==> Status"
if [[ "$IS_MACOS" == "1" ]]; then
  launchctl print system/com.sentinelcore.bridge 2>/dev/null | grep -E "state = |pid = " || true
  launchctl print system/com.sentinelcore.web 2>/dev/null | grep -E "state = |pid = " || true
else
  systemctl --no-pager --lines=0 status sentinelcore sentinelcore-web 2>/dev/null | grep -E "●|Active:" || true
fi

echo
echo "==> Health checks"
curl -s -o /dev/null -w "main app     (5080): %{http_code}\n" http://127.0.0.1:5080/health/live || echo "main app     (5080): unreachable"
curl -s -o /dev/null -w "web dashboard(3320): %{http_code}\n" http://127.0.0.1:3320/api/health || echo "web dashboard(3320): unreachable"
# Loopback-only and optional - "unreachable" here just means HomeKit isn't set up.
curl -s -o /dev/null -w "homekit      (3321): %{http_code}\n" http://127.0.0.1:3321/api/status || echo "homekit      (3321): not running"
