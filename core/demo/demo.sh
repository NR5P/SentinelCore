#!/usr/bin/env bash
# The App Review demo server on the media box: a second, walled-off Core behind demo.nr5p.com.
# Run as root (sudo). See core/demo/README.md for what it is and why each wall is there.
#
#   sudo core/demo/demo.sh install       once: user, folders, license, units, first build
#   sudo core/demo/demo.sh refresh-app   after a deploy: copy the new binaries, then reset
#   sudo core/demo/demo.sh reset         rebuild the demo's data from scratch (the timer does this nightly)
#   sudo core/demo/demo.sh login         print the reviewer login for App Store Connect / Play Console
#   sudo core/demo/demo.sh status
set -euo pipefail

ROOT=${SCDEMO_ROOT:-/srv/scdemo}
DEMO_USER=scdemo
SRC=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)        # the repo checkout this script is in
REAL_USER=${SUDO_USER:-media}
UNITS=(sentinelcore-demo-cams sentinelcore-demo sentinelcore-demo-web)

as_demo() { runuser -u "$DEMO_USER" -- env SCDEMO_ROOT="$ROOT" HOME="$ROOT" "$@"; }

need_root() { [[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }; }

# The two build outputs, without the real install's appsettings.json (live credentials), its
# data folder, or other platforms' native libraries. appsettings.defaults.json is moved aside so
# neither app's startup seeder writes a settings file into the read-only app folder.
copy_app() {
  local app="$ROOT/app" project
  rm -rf "$app.new"
  mkdir -p "$app.new"
  for project in SentinelCore SentinelCore.Web; do
    rsync -a --delete \
      --exclude 'appsettings*.json' --exclude '/data/' --exclude '*.pdb' --exclude '*.lic' \
      --exclude '/osx-*/' --exclude '/win-*/' --exclude '/linux-arm64/' \
      --exclude '/runtimes/osx*/' --exclude '/runtimes/win*/' \
      "$SRC/core/$project/bin/Release/net10.0/" "$app.new/$project/"
  done
  rsync -a "$SRC/core/SentinelCore.Web/wwwroot/" "$app.new/SentinelCore.Web/wwwroot/"
  rsync -a "$SRC/core/SentinelCore.Web/Fonts/" "$app.new/SentinelCore.Web/Fonts/"
  rsync -a "$SRC/core/docs/" "$app.new/docs/"
  cp "$SRC/core/SentinelCore/appsettings.defaults.json" "$app.new/ingest-defaults.json"
  rm -rf "$app.old"; [[ -d $app ]] && mv "$app" "$app.old"; mv "$app.new" "$app"; rm -rf "$app.old"
  rsync -a --delete --exclude '__pycache__' "$SRC/core/demo/" "$ROOT/kit/"
  chown -R root:"$DEMO_USER" "$app" "$ROOT/kit"
  chmod -R u=rwX,g=rX,o= "$app" "$ROOT/kit"
}

reset() {
  systemctl stop sentinelcore-demo-web sentinelcore-demo 2>/dev/null || true
  as_demo python3 "$ROOT/kit/demo.py" media
  as_demo python3 "$ROOT/kit/demo.py" build-live
  systemctl start sentinelcore-demo sentinelcore-demo-web
  as_demo python3 "$ROOT/kit/demo.py" seed
}

issue_license() {
  local key="/home/$REAL_USER/sentinelcore-license-keys/private.pem" out
  [[ -f $key ]] || { echo "No license key at $key - the demo runs unlicensed (a watermark on camera images)."; return; }
  out=$(mktemp -d)
  chown "$REAL_USER" "$out"
  runuser -u "$REAL_USER" -- dotnet run --project "$SRC/core/LicenseGenerator" -c Release -- issue \
    --key "$key" --customer "Sentinel Core App Review demo" --email "demo@nr5p.com" --output "$out/license.lic"
  install -o "$DEMO_USER" -g "$DEMO_USER" -m 600 "$out/license.lic" "$ROOT/secrets/license.lic"
  rm -rf "$out"
}

install_units() {
  local unit
  for unit in "$SRC"/core/demo/systemd/*; do
    sed "s|@ROOT@|$ROOT|g" "$unit" > "/etc/systemd/system/$(basename "$unit")"
  done
  systemctl daemon-reload
  systemctl enable sentinelcore-demo-cams sentinelcore-demo sentinelcore-demo-web sentinelcore-demo-reset.timer
}

case "${1:-}" in
  install)
    need_root
    command -v rsync >/dev/null || { echo "Needs rsync (apt install rsync)." >&2; exit 1; }
    id "$DEMO_USER" &>/dev/null || useradd --system --home-dir "$ROOT" --shell /usr/sbin/nologin "$DEMO_USER"
    install -d -o "$DEMO_USER" -g "$DEMO_USER" -m 750 "$ROOT"
    copy_app
    as_demo python3 "$ROOT/kit/demo.py" secrets
    [[ -f $ROOT/secrets/license.lic ]] || issue_license
    install_units
    systemctl restart sentinelcore-demo-cams
    systemctl start sentinelcore-demo-reset.timer
    reset
    echo
    echo "The demo is up on http://127.0.0.1:3360 (loopback only)."
    echo "Next: in Cloudflare Zero Trust add the public hostname demo.nr5p.com -> HTTP 127.0.0.1:3360."
    as_demo python3 "$ROOT/kit/demo.py" login
    ;;
  refresh-app) need_root; copy_app; install_units; systemctl restart sentinelcore-demo-cams; reset ;;
  reset) need_root; reset ;;
  login) need_root; as_demo python3 "$ROOT/kit/demo.py" login ;;
  status) systemctl --no-pager status "${UNITS[@]}" sentinelcore-demo-reset.timer | grep -E "●|Active:|Trigger:" ;;
  *) sed -n '2,10p' "$0"; exit 1 ;;
esac
