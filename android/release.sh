#!/usr/bin/env bash
# Build the signed release bundle (app-release.aab) for upload to Google Play.
# Needs android/keystore.properties - see docs/STORE_DISTRIBUTION.md. Play uploads are manual
# (Play Console → Testing → Internal testing → Create release → drop the .aab).
set -euo pipefail
DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cd "$DIR"
[[ -f keystore.properties ]] || { echo "android/keystore.properties is missing - the bundle would be debug-signed and Play would refuse it. See docs/STORE_DISTRIBUTION.md." >&2; exit 1; }
./gradlew --quiet bundleRelease
AAB=app/build/outputs/bundle/release/app-release.aab
echo "==> $AAB ($(du -h "$AAB" | cut -f1))"
