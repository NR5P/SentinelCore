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
    live = f"{directory}/appsettings.json"
    if os.path.exists(live):
        os.remove(live)

# The main app's starting point is the checked-in appsettings.defaults.json, never the
# appsettings.json `dotnet publish` may also have copied: that one is untracked live config, and
# on a machine that runs the server from this checkout it holds that machine's real credentials.
# The blanking below is then only a second line of defence for anything that slipped into the
# defaults file.
main_path = f"{stage}/SentinelCore/{DEFAULTS_NAME}"
with open(main_path) as f:
    main_settings = json.load(f)

main_settings.setdefault("Bridge", {})["ApiKey"] = ""
main_settings["Setup"] = {"Completed": False}
# A stranger's install starts locked until its owner creates the first admin account (the
# dashboard's first screen) - never with this maintainer's own login choice.
main_settings["Auth"] = {"Enabled": True}

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

# The RID this machine can actually execute. .NET cross-compiles every other target from here,
# but it cannot run them, so only one of the bundles built by this script is ever startable.
detect_host_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux)  os=linux ;;
    *)      return ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    x86_64)        arch=x64 ;;
    *)             return ;;
  esac
  echo "$os-$arch"
}

# Starts the published binaries and fails the build if they don't come up.
#
# This exists because of a bug it would have caught immediately: FaceAiSharp resolved its ONNX
# models through Assembly.Location, which is an empty string in a single-file publish, so every
# release .zip died on startup with an ArgumentNullException - while every git-checkout build, the
# only kind anything ever ran, worked perfectly. "It compiles and the tests pass" says nothing
# about whether a *published* bundle can start, because a publish rearranges where everything
# lives. So: actually start it.
#
# Runs against a copy, never the staged directory itself. A booting server writes appsettings.json,
# data/ and possibly certs/, and none of that belongs in the archive that is about to be zipped.
smoke_test_bundle() {
  local stage=$1 platform=$2
  local host_rid
  host_rid=$(detect_host_rid)

  if [[ "$platform" != "$host_rid" ]]; then
    echo "    Skipping startup check - $platform binaries cannot run on this ${host_rid:-unknown} machine"
    return 0
  fi

  local work
  work=$(mktemp -d)
  cp -R "$stage" "$work/bundle"

  local bridge_port=${SMOKE_MAIN_PORT:-59080}
  local web_port=${SMOKE_WEB_PORT:-59081}
  local failed=0

  # --Urls on the command line rather than ASPNETCORE_URLS: command-line arguments outrank every
  # other configuration source, so this binds where it is told no matter what the bundled
  # appsettings says. Belt and braces for a check whose whole job is to be trustworthy.
  (cd "$work/bundle/SentinelCore" && exec env \
      ASPNETCORE_ENVIRONMENT=Production \
      ./SentinelCore --Urls "http://127.0.0.1:$bridge_port") >"$work/bridge.log" 2>&1 &
  local bridge_pid=$!

  # HTTPS and discovery off: this is a throwaway process on a maintainer's machine, and it has no
  # business generating a certificate authority or advertising itself to every phone on the LAN.
  (cd "$work/bundle/SentinelCore.Web" && exec env \
      ASPNETCORE_ENVIRONMENT=Production \
      ./SentinelCore.Web \
      --Urls "http://127.0.0.1:$web_port" \
      --Https:Enabled false \
      --Discovery:Enabled false) >"$work/web.log" 2>&1 &
  local web_pid=$!

  smoke_wait() {
    local label=$1 url=$2 pid=$3 log=$4
    for _ in $(seq 1 45); do
      if ! kill -0 "$pid" 2>/dev/null; then
        echo "    $label EXITED before answering:" >&2
        tail -n 15 "$log" >&2
        return 1
      fi
      if [[ "$(curl -s -o /dev/null -w "%{http_code}" --max-time 2 "$url" || true)" == "200" ]]; then
        echo "    $label started and answered $url"
        return 0
      fi
      sleep 1
    done
    echo "    $label did not answer $url within 45s:" >&2
    tail -n 15 "$log" >&2
    return 1
  }

  smoke_wait "bridge   " "http://127.0.0.1:$bridge_port/health/live" "$bridge_pid" "$work/bridge.log" || failed=1
  smoke_wait "dashboard" "http://127.0.0.1:$web_port/api/health" "$web_pid" "$work/web.log" || failed=1

  kill "$bridge_pid" "$web_pid" 2>/dev/null || true
  wait "$bridge_pid" "$web_pid" 2>/dev/null || true
  rm -rf "$work"

  if [[ "$failed" != "0" ]]; then
    echo "The published $platform binaries do not start. Not shipping this." >&2
    return 1
  fi
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

  # Deliberately only these two. SentinelCore.PushRelay is the vendor-operated service that holds
  # the real APNs/FCM keys (docs/PUSH_RELAY.md); it is never part of what a customer installs.
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

  # The root half of automatic updates (docs/AUTO_UPDATE.md) - Linux only, installed by
  # install.sh to /usr/local/libexec, where the copy that actually runs lives.
  if [[ "$platform" == linux-* ]]; then
    cp "$SCRIPT_DIR/release/sentinelcore-apply-update.sh" "$stage/sentinelcore-apply-update.sh"
    chmod +x "$stage/sentinelcore-apply-update.sh"
  fi

  # The install scripts and README below point people at docs/DEPLOYMENT.md and USER_GUIDE.md -
  # include them (and PUSH_NOTIFICATIONS.md, which the dashboard's own Docs tab also serves as a
  # PDF - see DocsEndpoints.cs) so that's actually true for someone who only has this .zip, not a
  # git checkout.
  mkdir -p "$stage/docs"
  cp "$ROOT_DIR/docs/DEPLOYMENT.md" "$ROOT_DIR/docs/USER_GUIDE.md" "$ROOT_DIR/docs/PUSH_NOTIFICATIONS.md" "$stage/docs/"

  # Attribution for the bundled third-party components. Several of them - the YuNet model (MIT),
  # hls.js and the ArcFace model (both Apache-2.0) - require their notices to travel with any
  # copy, so this belongs in the archive rather than only in the repo.
  cp "$ROOT_DIR/THIRD-PARTY-NOTICES.txt" "$stage/"

  # The licence terms travel with the software they cover - someone who only has this .zip has to
  # be able to read what they agreed to, and Section 10 in particular (face recognition, biometric
  # and recording compliance) is something an operator needs before they switch that feature on.
  cp "$ROOT_DIR/EULA.md" "$stage/"

  # The main app's appsettings.json that `dotnet publish` may have copied is untracked live
  # configuration for the in-place git-checkout deployment (see docs/DEPLOYMENT.md section 3) -
  # a real Bridge:ApiKey, alert-channel credentials, Setup:Completed=true - so the bundle is built
  # from the checked-in appsettings.defaults.json instead, and the dashboard's file gets its paths
  # made portable. Every other shipped default (PromptProfiles, ports, etc.) is left untouched.
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

  echo "==> Startup check for $platform"
  smoke_test_bundle "$stage" "$platform"

  echo "==> Zipping $platform"
  (cd "$DIST_DIR" && zip -rq "SentinelCore-$platform.zip" "$platform")
  echo "    -> dist/SentinelCore-$platform.zip ($(du -h "$DIST_DIR/SentinelCore-$platform.zip" | cut -f1))"
done

echo
echo "==> Done. Release bundles are in $DIST_DIR:"
ls -lh "$DIST_DIR"/*.zip
