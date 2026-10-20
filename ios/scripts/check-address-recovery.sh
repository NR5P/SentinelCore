#!/usr/bin/env bash
# Checks the "home address stopped answering" recovery (SentinelCore/Networking/AddressRecovery.swift)
# with a real URLSession: a local address with nothing listening, a stub server as the remote
# address. The app has no unit-test target, so this compiles the rules on their own with swiftc.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'kill "${stub:-0}" 2>/dev/null || true; rm -rf "$work"' EXIT

free_port() { python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])'; }
dead_port="$(free_port)"
live_port="$(free_port)"

python3 "$here/address-recovery/stub_server.py" "$live_port" &
stub=$!
for _ in $(seq 50); do curl -s "http://127.0.0.1:$live_port/counts" >/dev/null && break; sleep 0.1; done

swiftc -swift-version 6 -O -o "$work/check" \
    "$here/../SentinelCore/Networking/AddressRecovery.swift" \
    "$here/address-recovery/main.swift"
"$work/check" "http://127.0.0.1:$dead_port" "http://127.0.0.1:$live_port"
