#!/usr/bin/env bash
# Archive the iPhone/iPad app and/or the Mac app in Release and upload each to App Store Connect,
# where it appears under TestFlight within a few minutes (internal testers get it immediately;
# external testers after the first Beta App Review).
#
#   ios/scripts/release.sh            # iOS + macOS
#   ios/scripts/release.sh ios
#   ios/scripts/release.sh macos
#
# Prerequisites (one-time): Xcode signed in to the team under Settings → Accounts, the app record
# created in App Store Connect, and the build number bumped past the last upload
# (scripts/bump-build.sh). See docs/STORE_DISTRIBUTION.md.
set -euo pipefail

TARGET=${1:-all}
DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
BUILD="$DIR/build"
mkdir -p "$BUILD"

archive_and_upload() {
  local platform=$1 name=$2
  echo "==> Archiving $name ($platform)"
  xcodebuild -project "$DIR/SentinelCore.xcodeproj" -scheme SentinelCore -configuration Release \
    -destination "generic/platform=$platform" -archivePath "$BUILD/$name.xcarchive" \
    -allowProvisioningUpdates archive | grep -E "error:|warning: .*signing|\*\* ARCHIVE" || true
  [[ -d "$BUILD/$name.xcarchive" ]] || { echo "Archive failed - see above." >&2; exit 1; }
  echo "==> Uploading $name to App Store Connect"
  xcodebuild -exportArchive -archivePath "$BUILD/$name.xcarchive" \
    -exportOptionsPlist "$DIR/scripts/ExportOptions.plist" -exportPath "$BUILD/$name-export" \
    -allowProvisioningUpdates | grep -E "error:|Upload|EXPORT" || true
}

case "$TARGET" in
  ios)   archive_and_upload iOS   SentinelCore-iOS ;;
  macos) archive_and_upload macOS SentinelCore-macOS ;;
  all)   archive_and_upload iOS   SentinelCore-iOS; archive_and_upload macOS SentinelCore-macOS ;;
  *) echo "Usage: $0 [ios|macos|all]" >&2; exit 1 ;;
esac
echo "==> Done. Check App Store Connect → TestFlight; processing takes a few minutes."
