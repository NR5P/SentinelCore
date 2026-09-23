#!/usr/bin/env bash
# OrganizerKit's tests against two real, throwaway SentinelCore servers - see
# scripts/with-organizer-servers.sh at the repository root. Arguments go to `swift test`.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
cd "$here"
exec "$here/../../scripts/with-organizer-servers.sh" swift test "$@"
