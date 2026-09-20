#!/usr/bin/env bash
# Archive the Apple TV app in Release and upload it to App Store Connect (TestFlight).
# Same prerequisites as ios/scripts/release.sh - see docs/STORE_DISTRIBUTION.md.
set -euo pipefail

DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
BUILD="$DIR/build"
mkdir -p "$BUILD"

echo "==> Archiving SentinelCoreTV (tvOS)"
xcodebuild -project "$DIR/SentinelCoreTV.xcodeproj" -scheme SentinelCoreTV -configuration Release \
  -destination "generic/platform=tvOS" -archivePath "$BUILD/SentinelCoreTV.xcarchive" \
  -allowProvisioningUpdates archive | grep -E "error:|warning: .*signing|\*\* ARCHIVE" || true
[[ -d "$BUILD/SentinelCoreTV.xcarchive" ]] || { echo "Archive failed - see above." >&2; exit 1; }

echo "==> Uploading to App Store Connect"
xcodebuild -exportArchive -archivePath "$BUILD/SentinelCoreTV.xcarchive" \
  -exportOptionsPlist "$DIR/scripts/ExportOptions.plist" -exportPath "$BUILD/SentinelCoreTV-export" \
  -allowProvisioningUpdates | grep -E "error:|Upload|EXPORT" || true
echo "==> Done. Check App Store Connect → TestFlight."
