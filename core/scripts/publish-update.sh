#!/usr/bin/env bash
# Publishes a Core update that installed boxes pick up on their own overnight (docs/AUTO_UPDATE.md).
# Run on the maintainer's Mac - the one machine that holds the update signing key.
#
#   ./scripts/publish-update.sh                          # stable channel, linux-x64 + linux-arm64
#   ./scripts/publish-update.sh --channel beta --notes "Fixes the live grid stall"
#   ./scripts/publish-update.sh --no-upload              # build + sign only, leave it in dist/updates/
#   ./scripts/publish-update.sh --minimum-build 640      # boxes older than 640 must reinstall by hand
#   ./scripts/publish-update.sh --generate-key           # one time: make the signing key
#
# What it does:
#   1. builds the release bundles with publish-release.sh, stamped with a build number (the
#      master commit count, so it only ever goes up)
#   2. writes a manifest naming the bundle's size and SHA-256 and signs it with the update key
#   3. copies the bundle, then the manifest, to the push relay's updates folder on the media box,
#      which serves them at https://updates.nr5p.com/v1/updates/<channel>/<platform>
#
# The manifest goes up last and by rename, so a box checking mid-upload either sees the old
# release or the complete new one, never a manifest pointing at half a file.
#
# The signing key never leaves this Mac. The relay only hands files out; every install verifies
# the signature itself against the public key compiled into SentinelCore.Web
# (UpdateSigningPublicKey.cs), so whoever controls the relay box still can't ship code.
#
# Overrides:
#   UPDATE_SIGNING_KEY  private key PEM  (default ~/sentinelcore-update-keys/update-signing-key.pem)
#   UPDATE_HOST         ssh destination  (default user@server)
#   UPDATE_DIR          relay updates folder on that host
#                       (default Desktop/BlueIrisOllamaBridge/core/SentinelCore.PushRelay/updates)
#   PLATFORMS           (default "linux-x64 linux-arm64")
#   ALLOW_DIRTY=1       publish from a tree with uncommitted changes (don't, for a real release)

set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)
OUT_DIR="$ROOT_DIR/dist/updates"

KEY=${UPDATE_SIGNING_KEY:-$HOME/sentinelcore-update-keys/update-signing-key.pem}
UPDATE_HOST=${UPDATE_HOST:-user@server}
UPDATE_DIR=${UPDATE_DIR:-Desktop/BlueIrisOllamaBridge/core/SentinelCore.PushRelay/updates}
PLATFORMS=${PLATFORMS:-"linux-x64 linux-arm64"}

CHANNEL=stable
NOTES=""
UPLOAD=1
MINIMUM_BUILD=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --channel) CHANNEL=${2:?--channel needs a name}; shift 2 ;;
    --notes) NOTES=${2:?--notes needs text}; shift 2 ;;
    --notes-file) NOTES=$(cat "${2:?--notes-file needs a path}"); shift 2 ;;
    --no-upload) UPLOAD=0; shift ;;
    # Installs older than this build won't auto-install it and are told to run install.sh by
    # hand - for a release that changes systemd units, dependencies or the apply script itself.
    --minimum-build)
      MINIMUM_BUILD=${2:?--minimum-build needs a build number}
      [[ "$MINIMUM_BUILD" =~ ^[0-9]+$ ]] || { echo "--minimum-build must be a number" >&2; exit 1; }
      shift 2 ;;
    --generate-key)
      if [[ -f "$KEY" ]]; then
        echo "$KEY already exists - not overwriting it. Every install trusts that key." >&2
        exit 1
      fi
      mkdir -p "$(dirname "$KEY")"
      chmod 700 "$(dirname "$KEY")"
      openssl ecparam -name prime256v1 -genkey -noout -out "$KEY"
      chmod 600 "$KEY"
      echo "Created $KEY. Back it up somewhere safe - losing it means no install can be updated"
      echo "until each one is reinstalled with a new public key. Paste this into"
      echo "SentinelCore.Web/UpdateSigningPublicKey.cs:"
      echo
      openssl ec -in "$KEY" -pubout 2>/dev/null
      exit 0
      ;;
    *) echo "Unknown option '$1' - see the top of this script." >&2; exit 1 ;;
  esac
done

if [[ ! "$CHANNEL" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]]; then
  echo "Channel must be lower-case letters, digits and dashes (stable, beta, ...)." >&2
  exit 1
fi
if [[ ! -f "$KEY" ]]; then
  echo "No signing key at $KEY. Run with --generate-key once, or set UPDATE_SIGNING_KEY." >&2
  exit 1
fi
for tool in openssl shasum python3 git; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is required." >&2; exit 1; }
done

# The key on disk has to be the one installs trust, or every box would download the bundle and
# then refuse it - a silent failure on every customer's machine at 3am. Checked up front.
EXPECTED_PUBLIC=$(python3 - "$ROOT_DIR/SentinelCore.Web/UpdateSigningPublicKey.cs" <<'PY'
import re, sys
text = open(sys.argv[1]).read()
match = re.search(r"-----BEGIN PUBLIC KEY-----(.*?)-----END PUBLIC KEY-----", text, re.S)
print("".join(match.group(1).split()) if match else "")
PY
)
ACTUAL_PUBLIC=$(openssl ec -in "$KEY" -pubout 2>/dev/null | grep -v -- "-----" | tr -d '\n')
if [[ -z "$EXPECTED_PUBLIC" || "$EXPECTED_PUBLIC" != "$ACTUAL_PUBLIC" ]]; then
  echo "$KEY does not match the public key in SentinelCore.Web/UpdateSigningPublicKey.cs." >&2
  echo "Installs would refuse everything signed with it. Not publishing." >&2
  exit 1
fi

cd "$ROOT_DIR"
if [[ "${ALLOW_DIRTY:-0}" != "1" && -n "$(git status --porcelain --untracked-files=no -- .)" ]]; then
  echo "core/ has uncommitted changes. Commit first so the build can be traced to a commit," >&2
  echo "or set ALLOW_DIRTY=1 for a throwaway test publish." >&2
  exit 1
fi

BUILD=$(git rev-list --count HEAD)
COMMIT=$(git rev-parse --short=12 HEAD)
BRANCH=$(git rev-parse --abbrev-ref HEAD)
VERSION=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1)
if [[ "$BRANCH" != "master" ]]; then
  echo "WARNING: publishing from '$BRANCH', not master. Build numbers are master's commit count;" >&2
  echo "         a branch can produce a number master later reuses for different code." >&2
fi

echo "==> Building Core $VERSION build $BUILD ($COMMIT) for: $PLATFORMS"
# MSBuild reads environment variables as properties, so this reaches every `dotnet publish` that
# publish-release.sh runs and Directory.Build.props stamps it as the BuildNumber metadata.
SentinelBuildNumber=$BUILD PLATFORMS="$PLATFORMS" "$SCRIPT_DIR/publish-release.sh"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

for platform in $PLATFORMS; do
  src="$ROOT_DIR/dist/SentinelCore-$platform.zip"
  [[ -f "$src" ]] || { echo "publish-release.sh did not produce $src" >&2; exit 1; }

  folder="$OUT_DIR/$CHANNEL/$platform"
  mkdir -p "$folder"
  file="SentinelCore-$platform-b$BUILD.zip"
  cp "$src" "$folder/$file"

  sha=$(shasum -a 256 "$folder/$file" | cut -d' ' -f1)
  size=$(wc -c < "$folder/$file" | tr -d ' ')

  # The payload is exactly the bytes that get signed; the manifest carries it base64-encoded so
  # nothing between here and the install (JSON re-serialisation, a proxy) can change them.
  # Notes travel through the environment rather than being pasted into the script, so quotes or
  # backslashes in them can't break the Python.
  UPDATE_NOTES="$NOTES" python3 - "$folder/payload.json" <<PY
import json, os, sys, datetime
payload = {
    "schemaVersion": 1,
    "product": "SentinelCore",
    "channel": "$CHANNEL",
    "platform": "$platform",
    "version": "$VERSION",
    "build": $BUILD,
    "commit": "$COMMIT",
    "publishedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
    "file": "$file",
    "size": $size,
    "sha256": "$sha",
    "minimumBuild": $MINIMUM_BUILD,
    "notes": os.environ.get("UPDATE_NOTES", "").strip(),
}
open(sys.argv[1], "wb").write(json.dumps(payload, indent=2).encode())
PY

  openssl dgst -sha256 -sign "$KEY" -out "$folder/payload.sig" "$folder/payload.json"
  # Belt and braces: verify with the public key the installs carry before anything ships.
  openssl dgst -sha256 -verify <(openssl ec -in "$KEY" -pubout 2>/dev/null) \
    -signature "$folder/payload.sig" "$folder/payload.json" >/dev/null

  python3 - "$folder" <<'PY'
import base64, json, sys
folder = sys.argv[1]
manifest = {
    "payload": base64.b64encode(open(f"{folder}/payload.json", "rb").read()).decode(),
    "signature": base64.b64encode(open(f"{folder}/payload.sig", "rb").read()).decode(),
}
open(f"{folder}/manifest.json", "w").write(json.dumps(manifest, indent=2) + "\n")
PY
  rm "$folder/payload.sig"
  echo "    $CHANNEL/$platform: $file ($(du -h "$folder/$file" | cut -f1), sha256 ${sha:0:16}...)"
done

if [[ "$UPLOAD" != "1" ]]; then
  echo
  echo "==> Signed and left in $OUT_DIR (--no-upload)."
  exit 0
fi

echo
echo "==> Uploading to $UPDATE_HOST:$UPDATE_DIR"
for platform in $PLATFORMS; do
  folder="$OUT_DIR/$CHANNEL/$platform"
  remote="$UPDATE_DIR/$CHANNEL/$platform"
  file="SentinelCore-$platform-b$BUILD.zip"
  ssh "$UPDATE_HOST" "mkdir -p '$remote'"
  scp -q "$folder/$file" "$UPDATE_HOST:$remote/$file.part"
  scp -q "$folder/manifest.json" "$UPDATE_HOST:$remote/manifest.json.part"
  # Bundle first, then the manifest that points at it - each by rename, so neither is ever seen
  # half-written. Older bundles beyond the last three are cleared out; the newest few stay so a
  # box that read the previous manifest moments ago can still finish its download.
  ssh "$UPDATE_HOST" "cd '$remote' && mv '$file.part' '$file' && mv manifest.json.part manifest.json && ls -1t SentinelCore-*.zip | tail -n +4 | xargs -r rm -f"
  echo "    $CHANNEL/$platform -> build $BUILD live"
done

echo
echo "==> Done. Boxes on the '$CHANNEL' channel install build $BUILD in their next overnight window."
echo "    Check: curl -s -H \"X-Sentinel-License: \$(base64 < your.lic)\" https://updates.nr5p.com/v1/updates/$CHANNEL/linux-x64"
