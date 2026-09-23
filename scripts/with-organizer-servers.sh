#!/usr/bin/env bash
# Runs a command with two throwaway SentinelCore servers up, for the Organizer's client tests on
# every platform: ORGANIZER_TEST_SERVER_A and ORGANIZER_TEST_SERVER_B name them. Two, so the
# "server died and was replaced" test has a replacement to switch to. Both are on loopback with
# scratch data - nothing touches the LAN ports, real certificates, mDNS or any real data directory.
#
#   scripts/with-organizer-servers.sh swift test
#   scripts/with-organizer-servers.sh ./gradlew testDebugUnitTest
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
core="$root/core"
scratch="$(mktemp -d -t organizer-servers)"
port_a="${ORGANIZER_PORT_A:-8911}"
port_b="${ORGANIZER_PORT_B:-8912}"
pids=()

cleanup() {
    for pid in "${pids[@]:-}"; do kill "$pid" 2>/dev/null || true; done
    wait 2>/dev/null || true
    rm -rf "$scratch"
}
trap cleanup EXIT

dotnet build "$core/SentinelCore.Web/SentinelCore.Web.csproj" -v quiet -nologo | tail -2

start_server() {
    local name="$1" port="$2"
    local data="$scratch/$name"
    mkdir -p "$data"
    echo '{"Setup":{"Completed":true}}' > "$data/appsettings.json"
    if lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
        echo "port $port is already in use - a server left over from an earlier run? Refusing to test against it." >&2
        return 1
    fi
    # The built DLL directly, not `dotnet run`: that wraps the server in a second process, and
    # killing the wrapper on exit left the real server holding the port - so the next run
    # silently tested against the previous run's server.
    (cd "$core/SentinelCore.Web" && exec dotnet bin/Debug/net10.0/SentinelCore.Web.dll \
        --Urls="http://127.0.0.1:$port" --Https:Enabled=false --Discovery:Enabled=false \
        --InternetExposed:Enabled=false --Bridge:DataDirectory="$data" --CamerasDataDirectory="$data" \
        --MainAppSettingsPath="$data/appsettings.json" > "$data/server.log" 2>&1) &
    pids+=($!)
    for _ in $(seq 1 60); do
        curl -fsS "http://127.0.0.1:$port/api/organizer/status" >/dev/null 2>&1 && return 0
        sleep 1
    done
    echo "server $name did not start; log:" >&2
    tail -40 "$data/server.log" >&2
    return 1
}

start_server a "$port_a"
start_server b "$port_b"

export ORGANIZER_TEST_SERVER_A="http://127.0.0.1:$port_a"
export ORGANIZER_TEST_SERVER_B="http://127.0.0.1:$port_b"
"$@"
