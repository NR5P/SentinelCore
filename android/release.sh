#!/usr/bin/env bash
# Build the signed release bundle and, when a Play service-account key is configured, upload it
# straight to the internal testing track - the Android counterpart of ios/scripts/release.sh.
#
#   android/release.sh            # build + upload to the internal track
#   android/release.sh --no-upload
#
# Needs android/keystore.properties (see docs/STORE_DISTRIBUTION.md §5). The upload additionally
# needs playServiceAccountJson= in that same file, pointing at the service-account key; without
# it the script stops after the build and tells you where the .aab is for a manual upload.
set -euo pipefail
DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cd "$DIR"
[[ -f keystore.properties ]] || { echo "android/keystore.properties is missing - the bundle would be debug-signed and Play would refuse it. See docs/STORE_DISTRIBUTION.md." >&2; exit 1; }

./gradlew --quiet bundleRelease
AAB=app/build/outputs/bundle/release/app-release.aab
echo "==> $AAB ($(du -h "$AAB" | cut -f1))"

KEY=$(sed -n 's/^playServiceAccountJson=//p' keystore.properties | head -1)
if [[ "${1:-}" == "--no-upload" ]]; then
  echo "==> Skipping upload (--no-upload)"
elif [[ -z "$KEY" ]]; then
  echo "==> No playServiceAccountJson in keystore.properties - upload it by hand:"
  echo "    Play Console → Testing → Internal testing → Create new release → drop $DIR/$AAB"
elif [[ ! -f "${KEY/#\~/$HOME}" ]]; then
  echo "==> playServiceAccountJson points at $KEY, which doesn't exist - upload by hand (see above), or fix the path." >&2
  exit 1
else
  python3 "$DIR/scripts/play-upload.py" --key "$KEY" --bundle "$AAB"
fi
