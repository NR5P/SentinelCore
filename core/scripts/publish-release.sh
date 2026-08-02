#!/usr/bin/env bash
# Builds self-contained, single-file release bundles of Sentinel Core for Windows, Linux and
# macOS, ready to hand to someone who doesn't have (and shouldn't need) the .NET SDK, git, or
# any build tooling on their own machine.
#
#   ./scripts/publish-release.sh
#
# .NET's cross-compilation means this can build binaries for every target OS/architecture from
# a single machine - you do not need a Windows box to produce the Windows build, or vice versa.
#
# Output goes to core/dist/, one folder and one .zip per platform:
#
#   dist/win-x64/          SentinelCore\, SentinelCore.Web\, homekit-bridge\, docs\, install.ps1
#   dist/linux-x64/        SentinelCore/,  SentinelCore.Web/,  homekit-bridge/,  docs/,  install.sh
#   dist/linux-arm64/      (Raspberry Pi 4/5 and similar)
#   dist/osx-x64/          Intel Macs
#   dist/osx-arm64/        Apple Silicon Macs
#   dist/SentinelCore-<platform>.zip   <- give this file to the end user for that platform
#
# Each user just unzips theirs and runs the install script inside it (sudo ./install.sh, or as
# Administrator for install.ps1) - see scripts/release/dist-install-*.{sh,ps1} for what that
# does. This is a separate, simpler path from install-linux.sh/install-macos.sh/
# install-windows.ps1, which are for building from a git checkout with the SDK installed
# instead (e.g. for development, or updating an existing source install via deploy.sh).
#
# The same .zip is also how an existing install is upgraded - unpack it over the previous one and
# re-run the install script, which detects the existing install and takes an upgrade path instead
# (see the dist-install-* scripts). Nothing carrying user state is in this archive: data/ is
# excluded by both .csproj files, certs/ and homekit-bridge/persist/ are never staged, and the two
# appsettings.json files are shipped under a different name - see sanitize_release_config.
#
# Override PLATFORMS to build a subset, e.g.:
#   PLATFORMS="linux-x64 win-x64" ./scripts/publish-release.sh

set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)
DIST_DIR="$ROOT_DIR/dist"

PLATFORMS=${PLATFORMS:-"win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64"}

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not available. Install the .NET 10 SDK to build release bundles." >&2
  exit 1
fi

echo "==> Clearing stale build servers (works around an intermittent CLR crash seen here before)"
dotnet build-server shutdown >/dev/null 2>&1 || true

rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

publish_project() {
  local project=$1 rid=$2 out=$3
  # PublishSingleFile + IncludeNativeLibrariesForSelfExtract bundles the whole .NET runtime and
  # every dependency (including SQLitePCLRaw's native library) into one executable that
  # self-extracts natives to a temp dir at startup - nothing needs to be installed on the
  # target machine to run it. --self-contained is what makes that possible; without it the
  # target machine would need the matching .NET runtime pre-installed.
  # Deliberately NOT -p:EnableCompressionInSingleFile=true: it reproducibly crashed the
  # published macOS binaries with a native AccessViolationException on the first outbound
  # HttpClient call (confirmed while testing this script) - a known rough edge where the
  # compressed bundle's native-library self-extraction doesn't play well with macOS. The
  # uncompressed bundle costs some extra disk space and nothing else, so it's not worth the risk
  # on any platform.
  dotnet publish "$project" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none \
    -o "$out"
}

sanitize_release_config() {
  local stage=$1
  python3 - "$stage" <<'PYEOF'
import json
import os
import sys

stage = sys.argv[1]

# Both files are written as appsettings.defaults.json and the appsettings.json that `dotnet
# publish` produced is deleted, so that nothing in this archive shares a name with the live
# configuration of an existing install. Unpacking a new release over an old one therefore cannot
# clobber someone's cameras, credentials or alert channels - and the install scripts create
# appsettings.json from this file only when there isn't one already. ConfigDefaultsSeeder picks
# up anything this version added on the next boot.
DEFAULTS_NAME = "appsettings.defaults.json"

def write_defaults(directory, settings):
    with open(f"{directory}/{DEFAULTS_NAME}", "w") as f:
        json.dump(settings, f, indent=2)
        f.write("\n")
    os.remove(f"{directory}/appsettings.json")

main_path = f"{stage}/SentinelCore/appsettings.json"
with open(main_path) as f:
    main_settings = json.load(f)

main_settings.setdefault("Bridge", {})["ApiKey"] = ""
main_settings["Setup"] = {"Completed": False}

# Every alert-channel section that can hold a live credential or this maintainer's own
# personal/LAN details (a Blue Iris URL, a Telegram chat ID, ...) - turned off and blanked so a
# stranger installing this release starts from the same inert defaults docs/DEPLOYMENT.md
# describes ("all optional, all off by default"), not this maintainer's own live setup. Nothing
# else in this file is touched - Ollama/ImageProcessing/PromptProfiles etc. are genuine shipped
# defaults, not secrets.
credential_sections = {
    "CloudVision": ["ApiKey"],
    "Mqtt": ["Username", "Password"],
    "Webhook": ["Url", "ApiKey"],
    "Email": ["Username", "AppPassword", "FromAddress", "ToAddress"],
    "BlueIris": ["BaseUrl", "Username", "Password"],
    "Telegram": ["BotToken", "ChatId"],
    "Discord": ["WebhookUrl"],
    # No credentials of its own, but it's still this maintainer's own choice rather than a
    # shipped default - and its devices.db isn't in the release, so leaving it on would greet a
    # stranger with an empty Device Hub tab they never asked for.
    "DeviceHub": [],
}
for section_name, fields in credential_sections.items():
    section = main_settings.get(section_name)
    if not isinstance(section, dict):
        continue
    section["Enabled"] = False
    for field in fields:
        if field in section:
            section[field] = ""

write_defaults(f"{stage}/SentinelCore", main_settings)

web_path = f"{stage}/SentinelCore.Web/appsettings.json"
with open(web_path) as f:
    web_settings = json.load(f)
# Relative to SentinelCore.Web's own working directory - portable to wherever the release is
# unzipped, since SentinelCore/ and SentinelCore.Web/ are always installed as siblings.
web_settings.setdefault("Bridge", {})["DataDirectory"] = "../SentinelCore/data"
web_settings["MainAppSettingsPath"] = "../SentinelCore/appsettings.json"
write_defaults(f"{stage}/SentinelCore.Web", web_settings)
PYEOF
}

for platform in $PLATFORMS; do
  case "$platform" in
    win-x64)     rid=win-x64;     install_script=dist-install-windows.ps1; install_dest=install.ps1 ;;
    linux-x64)   rid=linux-x64;   install_script=dist-install-linux.sh;    install_dest=install.sh ;;
    linux-arm64) rid=linux-arm64; install_script=dist-install-linux.sh;    install_dest=install.sh ;;
    osx-x64)     rid=osx-x64;     install_script=dist-install-macos.sh;    install_dest=install.sh ;;
    osx-arm64)   rid=osx-arm64;   install_script=dist-install-macos.sh;    install_dest=install.sh ;;
    *)
      echo "Unknown platform '$platform' - expected one of: win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64" >&2
      exit 1
      ;;
  esac

  stage="$DIST_DIR/$platform"
  echo
  echo "==> Publishing $platform (rid: $rid)"
  mkdir -p "$stage"

  publish_project "$ROOT_DIR/SentinelCore/SentinelCore.csproj" "$rid" "$stage/SentinelCore"
  publish_project "$ROOT_DIR/SentinelCore.Web/SentinelCore.Web.csproj" "$rid" "$stage/SentinelCore.Web"

  # The HomeKit sidecar is plain Node.js, not something `dotnet publish` produces - ship the
  # source (minus node_modules, which is platform/arch-specific and must be installed on the
  # target machine, exactly like the install scripts already do for a git checkout).
  if [[ -d "$ROOT_DIR/homekit-bridge" ]]; then
    mkdir -p "$stage/homekit-bridge"
    rsync -a --exclude node_modules --exclude persist "$ROOT_DIR/homekit-bridge/" "$stage/homekit-bridge/"
  fi

  cp "$SCRIPT_DIR/release/$install_script" "$stage/$install_dest"
  chmod +x "$stage/$install_dest" 2>/dev/null || true

  # The install scripts and README below point people at docs/DEPLOYMENT.md and USER_GUIDE.md -
  # include them (and PUSH_NOTIFICATIONS.md, which the dashboard's own Docs tab also serves as a
  # PDF - see DocsEndpoints.cs) so that's actually true for someone who only has this .zip, not a
  # git checkout.
  mkdir -p "$stage/docs"
  cp "$ROOT_DIR/docs/DEPLOYMENT.md" "$ROOT_DIR/docs/USER_GUIDE.md" "$ROOT_DIR/docs/PUSH_NOTIFICATIONS.md" "$stage/docs/"

  # The appsettings.json files `dotnet publish` just copied are the ones checked into this repo
  # - which double as this maintainer's own live configuration for the in-place git-checkout
  # deployment (see docs/DEPLOYMENT.md section 3). That means they carry a real Bridge:ApiKey,
  # Setup:Completed=true (skips the setup wizard), and absolute paths specific to this
  # maintainer's own machine - all wrong to hand to someone else. Replace both with a clean,
  # portable, secret-free starting point instead; every other shipped default (PromptProfiles,
  # ports, etc.) is left untouched.
  #
  # This also renames them to appsettings.defaults.json and drops the appsettings.json publish
  # produced, so that no file in this archive can overwrite an existing install's live settings -
  # the whole reason a release .zip can be unpacked over a previous version at all.
  sanitize_release_config "$stage"

  cat > "$stage/README.txt" <<README
Sentinel Core - $platform release

1. $([[ "$platform" == win-x64 ]] && echo "Right-click install.ps1 and choose 'Run with PowerShell' as Administrator" || echo "Run: sudo ./install.sh")
2. Open the dashboard at the address the installer prints when it finishes.

This bundle needs no separate .NET install - the runtime is built into the SentinelCore and
SentinelCore.Web binaries. ffmpeg (and optionally Node.js for HomeKit, Ollama for local AI
vision) are still installed separately - the installer offers to do that for you.

Full documentation: docs/DEPLOYMENT.md and docs/USER_GUIDE.md in the source repository.
README

  echo "==> Zipping $platform"
  (cd "$DIST_DIR" && zip -rq "SentinelCore-$platform.zip" "$platform")
  echo "    -> dist/SentinelCore-$platform.zip ($(du -h "$DIST_DIR/SentinelCore-$platform.zip" | cut -f1))"
done

echo
echo "==> Done. Release bundles are in $DIST_DIR:"
ls -lh "$DIST_DIR"/*.zip
