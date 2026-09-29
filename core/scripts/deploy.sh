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
#   ./scripts/deploy.sh relay      # build + restart only sentinelcore-push-relay (vendor host only)
#
# Set SKIP_TESTS=1 to skip the test run (not recommended - it's the one thing standing
# between a bad build and the live alert pipeline).

set -euo pipefail

TARGET=${1:-all}
ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

case "$TARGET" in
  main|web|homekit|relay|all) ;;
  *)
    echo "Unknown target '$TARGET'. Use: all, main, web, homekit, or relay." >&2
    exit 1
    ;;
esac

# Check we can actually restart before spending three minutes building something we then can't
# load. A deploy that dies at the restart is not a no-op: it leaves fresh binaries in bin/Release
# with the old processes still serving, which reads exactly like success. Worse here than on most
# projects, because wwwroot is served from the source tree rather than from bin - so a build that
# never gets loaded still ships its JavaScript, and the browser starts calling endpoints the
# running server does not have yet. Failing before the build keeps the tree and the processes
# telling the same story.
#
# Linux/systemd only. macOS installs go through launchd, where the equivalent allowlist is a
# different shape, and nobody has hit this there.
restart_blocked() {
  local unit=$1
  # A cached sudo timestamp or a blanket NOPASSWD covers everything; no need to inspect further.
  sudo -n true 2>/dev/null && return 1
  # Otherwise the restart only goes through if this exact command is allowlisted. The trailing
  # boundary matters: without it "restart sentinelcore" matches the sentinelcore-web entry.
  sudo -n -l 2>/dev/null | grep -qE "NOPASSWD:.*systemctl restart ${unit}( |\$)" && return 1
  return 0
}

if [[ "$(uname -s)" != "Darwin" && "${SKIP_RESTART:-0}" != "1" ]]; then
  units=()
  case "$TARGET" in
    main) units=(sentinelcore) ;;
    web) units=(sentinelcore-web) ;;
    homekit) units=(sentinelcore-homekit) ;;
    relay) units=(sentinelcore-push-relay) ;;
    all) units=(sentinelcore sentinelcore-web) ;;
  esac

  blocked=()
  for unit in "${units[@]}"; do
    # The HomeKit sidecar and the push relay are optional units, skipped at restart time when not
    # installed - so don't demand rights for them either.
    if [[ "$unit" == "sentinelcore-homekit" || "$unit" == "sentinelcore-push-relay" ]] && ! systemctl cat "$unit" >/dev/null 2>&1; then
      continue
    fi
    restart_blocked "$unit" && blocked+=("$unit")
  done

  # At a terminal a password is not a blocker, it's a prompt - so ask for it now rather than
  # letting the build run first and stopping for input three minutes later, when whoever started
  # it has already looked away. Caching the timestamp up front also means the restart itself
  # won't stop to ask.
  if (( ${#blocked[@]} > 0 )) && [[ -t 0 ]]; then
    echo "==> Restarting ${blocked[*]} will need sudo - authenticating now, before the build"
    if sudo -v; then
      blocked=()
    fi
  fi

  if (( ${#blocked[@]} > 0 )); then
    echo "==> Cannot restart ${blocked[*]}: sudo wants a password and there is no terminal to ask on." >&2
    echo "    Stopping before the build." >&2
    echo >&2
    echo "    Nothing was built, so this run has not moved bin/Release further ahead of them." >&2
    echo >&2
    # The failure mode seen on this machine was an allowlist left behind by the rename, which
    # looks fine until you read the unit names in it - so say so rather than just printing the fix.
    if sudo -n -l 2>/dev/null | grep -q "systemctl restart blueiris-ollama-bridge"; then
      echo "    The sudoers allowlist covers the pre-rename units (blueiris-ollama-bridge*), which" >&2
      echo "    no longer exist. It needs updating to the current names." >&2
      echo >&2
    fi
    echo "    Fix (see docs/DEPLOYMENT.md section 10):" >&2
    echo "      sudo visudo -f /etc/sudoers.d/blueiris-deploy" >&2
    for unit in sentinelcore sentinelcore-web sentinelcore-homekit sentinelcore-push-relay; do
      echo "      media ALL=(root) NOPASSWD: /usr/bin/systemctl restart $unit" >&2
    done
    echo >&2
    echo "    Or run the build alone with SKIP_RESTART=1, then restart by hand." >&2
    exit 1
  fi
fi

echo "==> Clearing stale build servers (works around an intermittent CLR crash seen here before)"
dotnet build-server shutdown >/dev/null 2>&1 || true

echo "==> Building solution (Release)"
# The .NET SDK on the media box sometimes dies at the very start of a build with "Internal CLR
# error (0x80131506)" - before anything is compiled, so a retry is safe. The retries also turn off
# the build servers and parallel nodes, which is what has always got past it by hand.
built=0
for attempt in 1 2 3; do
  if [[ $attempt -eq 1 ]]; then
    dotnet build -c Release && { built=1; break; }
  else
    echo "==> Build failed (attempt $((attempt - 1))); retrying without build servers or parallel nodes"
    dotnet build-server shutdown >/dev/null 2>&1 || true
    dotnet build -c Release --disable-build-servers -m:1 && { built=1; break; }
  fi
done
if [[ $built -ne 1 ]]; then
  echo "ERROR: the build failed three times. Nothing was restarted." >&2
  exit 1
fi

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

# The push relay is only ever installed on the vendor's own host (docs/PUSH_RELAY.md) - a missing
# unit is the normal case everywhere else, so like the HomeKit sidecar it's skipped, not an error.
# Linux only: nothing runs the relay from a Mac.
restart_relay() {
  if [[ "$IS_MACOS" == "1" ]]; then
    echo "==> Skipping push relay (not run on macOS)"
    return
  fi
  if ! systemctl cat sentinelcore-push-relay >/dev/null 2>&1; then
    echo "==> Skipping push relay (not installed)"
    return
  fi
  echo "==> Restarting sentinelcore-push-relay"
  sudo systemctl restart sentinelcore-push-relay
}

if [[ "${SKIP_RESTART:-0}" == "1" ]]; then
  # Deliberately loud, and deliberately the last thing printed. This leaves the machine in the
  # state the preflight above exists to prevent - new binaries, old processes, and wwwroot already
  # serving the new frontend out of the source tree - which is fine when it's a choice you made
  # and is silently wrong when you forget.
  echo
  echo "==> Skipping restart (SKIP_RESTART=1)"
  echo "    bin/Release is now AHEAD of the running services. wwwroot is served from source, so"
  echo "    the dashboard is already running the new frontend against the old backend."
  echo "    Finish with:"
  case "$TARGET" in
    main) echo "      sudo systemctl restart sentinelcore" ;;
    web) echo "      sudo systemctl restart sentinelcore-web" ;;
    homekit) echo "      sudo systemctl restart sentinelcore-homekit" ;;
    relay) echo "      sudo systemctl restart sentinelcore-push-relay" ;;
    all) echo "      sudo systemctl restart sentinelcore sentinelcore-web" ;;
  esac
  exit 0
fi

case "$TARGET" in
  main) restart_main ;;
  web) restart_web ;;
  homekit) restart_homekit ;;
  relay) restart_relay ;;
  all) restart_main; restart_web; restart_homekit; restart_relay ;;
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
