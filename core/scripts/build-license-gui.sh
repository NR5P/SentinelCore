#!/usr/bin/env bash
# Builds the license GUI into something you can actually double-click.
#
#   ./scripts/build-license-gui.sh            # for this machine
#   ./scripts/build-license-gui.sh osx-arm64  # or osx-x64, linux-x64, linux-arm64
#
# On macOS the output is a real .app bundle. That isn't cosmetic: a bare Mach-O binary launched
# from a shell gets treated as a background process, which leaves it named "Avalonia Application"
# in the menu bar and makes it unreliable to activate or to drive with accessibility tooling. The
# bundle fixes all three.
#
# Output: core/dist/license-gui/<rid>/
#
# This tool is never shipped to customers - it holds the private key that signs their licenses.
# It's built here only so it can be dropped into /Applications on the machine you sell from.
set -euo pipefail

cd "$(dirname "$0")/.."

RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -s)/$(uname -m)" in
    Darwin/arm64)  RID="osx-arm64" ;;
    Darwin/x86_64) RID="osx-x64" ;;
    Linux/aarch64) RID="linux-arm64" ;;
    Linux/x86_64)  RID="linux-x64" ;;
    *) echo "Unrecognised platform $(uname -s)/$(uname -m) - pass a runtime identifier explicitly." >&2; exit 1 ;;
  esac
fi

OUT="dist/license-gui/$RID"
PUBLISH="$OUT/publish"

rm -rf "$OUT"
mkdir -p "$PUBLISH"

echo "Publishing LicenseGenerator.Gui for $RID..."
# Self-contained so the machine you sell from doesn't need a matching .NET runtime installed,
# and so the bundle keeps working across SDK upgrades. Not single-file: Avalonia ships native
# libraries that a single-file bundle only has to unpack to a temp directory again anyway.
dotnet publish LicenseGenerator.Gui/LicenseGenerator.Gui.csproj \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false \
  -o "$PUBLISH" >/dev/null

VERSION="$(grep -o '<VersionPrefix>[^<]*' Directory.Build.props | head -1 | cut -d'>' -f2)"
VERSION="${VERSION:-1.0.0}"

if [[ "$RID" == osx-* ]]; then
  APP="$OUT/SentinelCore Licenses.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

  cp -R "$PUBLISH"/* "$APP/Contents/MacOS/"
  sed "s/__VERSION__/$VERSION/g" LicenseGenerator.Gui/Info.plist.template > "$APP/Contents/Info.plist"

  # sips/iconutil are part of a stock macOS - no extra tooling to install.
  ICONSET="$(mktemp -d)/AppIcon.iconset"
  mkdir -p "$ICONSET"
  for size in 16 32 64 128 256 512; do
    sips -z $size $size ../branding/SentinelCoreAppIcon-1024.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null 2>&1
    sips -z $((size*2)) $((size*2)) ../branding/SentinelCoreAppIcon-1024.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null 2>&1
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
  rm -rf "$(dirname "$ICONSET")"

  # Ad-hoc signature. Without it, an unsigned bundle assembled by a script is killed on launch on
  # Apple Silicon rather than merely warned about.
  codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || \
    echo "  (codesign unavailable - if the app won't open, right-click it and choose Open)"

  rm -rf "$PUBLISH"
  echo
  echo "Built $APP"
  echo "Drag it to /Applications, or run: open \"$APP\""
else
  cat > "$OUT/sentinelcore-licenses" <<'LAUNCHER'
#!/usr/bin/env bash
exec "$(dirname "$(readlink -f "$0")")/publish/LicenseGenerator.Gui" "$@"
LAUNCHER
  chmod +x "$OUT/sentinelcore-licenses"
  echo
  echo "Built $OUT"
  echo "Run it with: $OUT/sentinelcore-licenses"
fi
