#!/usr/bin/env bash
# Build and restart the BlueIrisOllamaBridge services in place - matching how they're
# actually deployed (running directly from bin/Release/net10.0 via the systemd units in
# ../systemd, not published anywhere else). See install-linux.sh for the separate initial
# install flow, which is not what this script does.
#
# Usage:
#   ./scripts/deploy.sh            # build + restart both services
#   ./scripts/deploy.sh main       # build + restart only blueiris-ollama-bridge
#   ./scripts/deploy.sh web        # build + restart only blueiris-ollama-bridge-web
#
# Set SKIP_TESTS=1 to skip the test run (not recommended - it's the one thing standing
# between a bad build and the live alert pipeline).

set -euo pipefail

TARGET=${1:-all}
ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

case "$TARGET" in
  main|web|all) ;;
  *)
    echo "Unknown target '$TARGET'. Use: all, main, or web." >&2
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

# Two separate, fixed-argument sudo calls rather than one "restart a b" invocation - the
# NOPASSWD sudoers entries match on exact command lines, so this is what lets both the
# per-service and combined restarts work under the same two allowlisted commands.
restart_main() {
  echo "==> Restarting blueiris-ollama-bridge"
  sudo systemctl restart blueiris-ollama-bridge
}
restart_web() {
  echo "==> Restarting blueiris-ollama-bridge-web"
  sudo systemctl restart blueiris-ollama-bridge-web
}

case "$TARGET" in
  main) restart_main ;;
  web) restart_web ;;
  all) restart_main; restart_web ;;
esac

sleep 2
echo
echo "==> Status"
systemctl --no-pager --lines=0 status blueiris-ollama-bridge blueiris-ollama-bridge-web 2>/dev/null | grep -E "●|Active:" || true

echo
echo "==> Health checks"
curl -s -o /dev/null -w "main app     (5080): %{http_code}\n" http://127.0.0.1:5080/health/live || echo "main app     (5080): unreachable"
curl -s -o /dev/null -w "web dashboard(3320): %{http_code}\n" http://127.0.0.1:3320/api/health || echo "web dashboard(3320): unreachable"
