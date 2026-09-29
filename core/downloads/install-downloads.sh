#!/bin/bash
# Sets up movie downloads on a Linux SentinelCore server (core/docs/MOVIE_DOWNLOADS.md):
# qBittorrent and Prowlarr, confined to a network namespace whose only way out is your VPN, as
# systemd units SentinelCore starts and stops by itself. No Docker. Run once, with sudo:
#
#   sudo ./install-downloads.sh
#   sudo ./install-downloads.sh --vpn-config ~/Downloads/mullvad-se-sto.conf
#   sudo ./install-downloads.sh --vpn-config ~/nordvpn/us1234.nordvpn.com.udp.ovpn --vpn-user X --vpn-pass Y
#
# With no VPN file, pick the VPN afterwards in SentinelCore (Settings -> MOVIE DOWNLOADS -> VPN
# Provider): a Mullvad account number is all Mullvad needs, and other WireGuard providers take
# five values from their config page. A file given here is the "installed" choice there, and the
# only way to use an OpenVPN provider. It is what your provider's website gives you for "manual"
# or "router" setup:
#   WireGuard .conf - Mullvad, ProtonVPN, IVPN, Surfshark, Windscribe, AirVPN, ...
#   OpenVPN .ovpn   - NordVPN, PIA, ExpressVPN, CyberGhost, and nearly everyone else
#                     (--vpn-user/--vpn-pass are the provider's *service* credentials, which
#                     most show on the same page, not your website login).
# Change provider or server later with:
#   sudo /usr/local/lib/sentinelcore-downloads/sentinelcore-vpn set-config NEWFILE
#
# Other options:
#   --user NAME      the account SentinelCore runs as (default: whoever ran sudo)
#   --skip-test      do not start everything once at the end to prove it works
#   --uninstall      remove the units, the permission and the namespace (keeps downloads and
#                    Prowlarr's site list in /var/lib/sentinelcore-downloads)
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
LIB=/usr/local/lib/sentinelcore-downloads
DATA=/var/lib/sentinelcore-downloads
PROWLARR_DIR=/opt/sentinelcore-prowlarr
FLARESOLVERR_DIR=/opt/sentinelcore-flaresolverr
UNITS=(sentinelcore-downloads.target sentinelcore-vpn.service sentinelcore-vpn-watch.service sentinelcore-qbittorrent.service sentinelcore-prowlarr.service sentinelcore-flaresolverr.service)

APP_USER=${SUDO_USER:-}
VPN_CONFIG=""
VPN_USER=""
VPN_PASS=""
SKIP_TEST=0
UNINSTALL=0

while [ $# -gt 0 ]; do
    case "$1" in
        --vpn-config) VPN_CONFIG=$2; shift 2 ;;
        --vpn-user) VPN_USER=$2; shift 2 ;;
        --vpn-pass) VPN_PASS=$2; shift 2 ;;
        --user) APP_USER=$2; shift 2 ;;
        --skip-test) SKIP_TEST=1; shift ;;
        --uninstall) UNINSTALL=1; shift ;;
        -h|--help) sed -n '2,29p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
done

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
die() { printf '\n\033[31mError: %s\033[0m\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run this with sudo"
command -v systemctl >/dev/null || die "this needs a systemd Linux"

if [ "$UNINSTALL" -eq 1 ]; then
    say "Removing movie downloads"
    systemctl stop sentinelcore-downloads.target 2>/dev/null || true
    for unit in "${UNITS[@]}"; do rm -f "/etc/systemd/system/$unit"; done
    rm -f /etc/polkit-1/rules.d/50-sentinelcore-downloads.rules
    [ -x "$LIB/sentinelcore-vpn" ] && "$LIB/sentinelcore-vpn" teardown || true
    rm -rf "$LIB" /etc/netns/scvpn
    systemctl daemon-reload
    echo "Done. Kept: $DATA (Prowlarr's sites, qBittorrent's settings), $PROWLARR_DIR, $FLARESOLVERR_DIR and /etc/sentinelcore/vpn."
    exit 0
fi

[ -n "$APP_USER" ] && id "$APP_USER" >/dev/null 2>&1 || die "say which account SentinelCore runs as with --user NAME"
if [ -z "$VPN_CONFIG" ] && [ ! -f /etc/sentinelcore/vpn/vpn.conf ] && [ ! -f /etc/sentinelcore/vpn/vpn.ovpn ] && [ ! -f "$DATA/vpn/wireguard.conf" ]; then
    # Fine: Mullvad or another WireGuard provider can be chosen in SentinelCore's settings
    # afterwards. There is just nothing to test the tunnel with yet.
    echo "No VPN file given: choose the VPN in SentinelCore under Settings -> MOVIE DOWNLOADS -> VPN Provider afterwards."
    SKIP_TEST=1
fi
[ -z "$VPN_CONFIG" ] || [ -f "$VPN_CONFIG" ] || die "$VPN_CONFIG does not exist"

say "Installing packages (WireGuard, OpenVPN, qBittorrent)"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq wireguard-tools openvpn qbittorrent-nox curl sqlite3 iproute2 xvfb >/dev/null
# Prowlarr is a .NET program that needs ICU; the package name carries the version.
ICU=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -1)
[ -n "$ICU" ] && apt-get install -y -qq "$ICU" >/dev/null

say "Installing Prowlarr"
if [ ! -x "$PROWLARR_DIR/Prowlarr" ]; then
    case "$(uname -m)" in
        x86_64) ARCH=x64 ;;
        aarch64) ARCH=arm64 ;;
        armv7l) ARCH=arm ;;
        *) die "no Prowlarr build for $(uname -m)" ;;
    esac
    TMP=$(mktemp -d)
    curl -fsSL -o "$TMP/prowlarr.tar.gz" "https://prowlarr.servarr.com/v1/update/master/updatefile?os=linux&runtime=netcore&arch=$ARCH"
    tar -xzf "$TMP/prowlarr.tar.gz" -C "$TMP"
    rm -rf "$PROWLARR_DIR"
    mv "$TMP/Prowlarr" "$PROWLARR_DIR"
    rm -rf "$TMP"
    echo "Installed to $PROWLARR_DIR"
else
    echo "Already installed in $PROWLARR_DIR (it keeps itself up to date)"
fi
# Prowlarr updates itself in place, so it must own its folder.
chown -R "$APP_USER:$APP_USER" "$PROWLARR_DIR"

say "Installing FlareSolverr (gets Prowlarr past Cloudflare checks)"
# MIT-licensed (github.com/FlareSolverr/FlareSolverr). The Linux build bundles its own Chrome;
# xvfb (installed above) gives that Chrome a virtual screen. x64 only - other machines skip it,
# and sites behind Cloudflare simply fail to add there.
if [ -x "$FLARESOLVERR_DIR/flaresolverr" ]; then
    echo "Already installed in $FLARESOLVERR_DIR"
elif [ "$(uname -m)" = x86_64 ]; then
    TMP=$(mktemp -d)
    curl -fsSL -o "$TMP/flaresolverr.tar.gz" "https://github.com/FlareSolverr/FlareSolverr/releases/latest/download/flaresolverr_linux_x64.tar.gz"
    tar -xzf "$TMP/flaresolverr.tar.gz" -C "$TMP"
    rm -rf "$FLARESOLVERR_DIR"
    mv "$TMP/flaresolverr" "$FLARESOLVERR_DIR"
    rm -rf "$TMP"
    echo "Installed to $FLARESOLVERR_DIR"
else
    echo "Skipped: FlareSolverr has no build for $(uname -m)"
fi
[ -d "$FLARESOLVERR_DIR" ] && chown -R "$APP_USER:$APP_USER" "$FLARESOLVERR_DIR"

say "Preparing settings for qBittorrent and Prowlarr"
mkdir -p "$DATA/qbittorrent/qBittorrent/config" "$DATA/prowlarr" "$DATA/flaresolverr"
QB_CONF="$DATA/qbittorrent/qBittorrent/config/qBittorrent.conf"
if [ ! -f "$QB_CONF" ]; then
    # Web UI on the namespace's own address only, and no sign-in for the one address SentinelCore
    # calls from (the host end of the veth pair). Nothing else can reach it.
    cat > "$QB_CONF" <<'EOF'
[LegalNotice]
Accepted=true

[BitTorrent]
Session\AnonymousModeEnabled=true
Session\DefaultSavePath=/var/lib/sentinelcore-downloads/qbittorrent/downloads
Session\QueueingSystemEnabled=false
Session\GlobalMaxRatio=-1

[Preferences]
General\Locale=en
WebUI\Address=10.250.0.2
WebUI\Port=8080
WebUI\LocalHostAuth=false
WebUI\AuthSubnetWhitelistEnabled=true
WebUI\AuthSubnetWhitelist=10.250.0.1/32
WebUI\HostHeaderValidation=false
WebUI\CSRFProtection=false
WebUI\UseUPnP=false
Connection\UPnP=false
EOF
fi
PROWLARR_CONF="$DATA/prowlarr/config.xml"
if [ ! -f "$PROWLARR_CONF" ]; then
    KEY=$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n')
    cat > "$PROWLARR_CONF" <<EOF
<Config>
  <BindAddress>10.250.0.2</BindAddress>
  <Port>9696</Port>
  <SslPort>6969</SslPort>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>$KEY</ApiKey>
  <AuthenticationMethod>External</AuthenticationMethod>
  <AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
  <Branch>master</Branch>
  <LogLevel>info</LogLevel>
  <UrlBase></UrlBase>
  <InstanceName>Prowlarr</InstanceName>
  <UpdateMechanism>BuiltIn</UpdateMechanism>
  <AnalyticsEnabled>False</AnalyticsEnabled>
</Config>
EOF
fi
# Settings -> MOVIE DOWNLOADS -> Use VPN writes this; the tunnel reads it when it starts.
[ -f "$DATA/network-mode" ] || echo vpn > "$DATA/network-mode"
# ...and VPN Provider writes its WireGuard file here.
install -d -m 700 "$DATA/vpn"
chown -R "$APP_USER:$APP_USER" "$DATA"
chmod 700 "$DATA/prowlarr"

say "Installing the VPN tunnel and the services"
install -d -m 755 "$LIB"
install -m 755 "$HERE/sentinelcore-vpn" "$LIB/sentinelcore-vpn"
for unit in "${UNITS[@]}"; do
    sed "s/__APP_USER__/$APP_USER/g" "$HERE/systemd/$unit" > "/etc/systemd/system/$unit"
    chmod 644 "/etc/systemd/system/$unit"
done
install -d -m 755 /etc/polkit-1/rules.d
sed "s/__APP_USER__/$APP_USER/g" "$HERE/50-sentinelcore-downloads.rules" > /etc/polkit-1/rules.d/50-sentinelcore-downloads.rules
chmod 644 /etc/polkit-1/rules.d/50-sentinelcore-downloads.rules
systemctl daemon-reload

if [ -n "$VPN_CONFIG" ]; then
    say "Installing the VPN configuration"
    ARGS=("$VPN_CONFIG")
    [ -n "$VPN_USER" ] && ARGS+=(--user "$VPN_USER" --pass "$VPN_PASS")
    "$LIB/sentinelcore-vpn" set-config "${ARGS[@]}"
fi

if [ "$SKIP_TEST" -eq 0 ]; then
    say "Testing: starting the VPN, qBittorrent and Prowlarr"
    HOME_IP=$(curl -fsS --max-time 10 https://api.ipify.org || echo "?")
    TEST_START=$(date '+%Y-%m-%d %H:%M:%S')
    # Earlier failed attempts can still count as restarts (reset-failed does not always clear
    # the counter), so only restarts during this test count.
    systemctl reset-failed "${UNITS[@]:1}" 2>/dev/null || true
    RESTARTS_BEFORE=$(systemctl show -p NRestarts --value sentinelcore-vpn.service)
    RESTARTS_BEFORE=${RESTARTS_BEFORE:-0}
    # Started as $APP_USER, the way SentinelCore will, which also proves the permission works.
    sudo -u "$APP_USER" systemctl --no-ask-password start sentinelcore-downloads.target \
        || die "could not start the services as $APP_USER - see: journalctl -u sentinelcore-vpn -n 50"
    VPN_IP=""
    for _ in $(seq 1 45); do
        # A tunnel that failed to start is retried by systemd, so it shows as restarting rather
        # than failed; either way there is no point waiting out the full minute and a half.
        if systemctl is-failed --quiet sentinelcore-vpn.service || [ "$(systemctl show -p NRestarts --value sentinelcore-vpn.service)" -gt "$RESTARTS_BEFORE" ]; then break; fi
        if [ -f /run/sentinelcore-vpn/status.json ] && VPN_IP=$(grep -o '"ip": *"[^"]*"' /run/sentinelcore-vpn/status.json | sed 's/.*"\([^"]*\)"$/\1/') && [ -n "$VPN_IP" ]; then break; fi
        sleep 2
    done
    QB=""
    PR=""
    if [ -n "$VPN_IP" ]; then
        QB=$(curl -fsS --max-time 5 http://10.250.0.2:8080/api/v2/app/version 2>/dev/null || true)
        for _ in $(seq 1 30); do
            PR=$(curl -fsS --max-time 5 -H "X-Api-Key: $(sed -n 's:.*<ApiKey>\(.*\)</ApiKey>.*:\1:p' "$PROWLARR_CONF")" http://10.250.0.2:9696/api/v1/system/status 2>/dev/null | grep -o '"version": *"[^"]*"' || true)
            [ -n "$PR" ] && break
            sleep 2
        done
        FS=""
        if [ -x "$FLARESOLVERR_DIR/flaresolverr" ]; then
            for _ in $(seq 1 20); do
                FS=$(curl -fsS --max-time 5 http://10.250.0.2:8191/ 2>/dev/null | grep -o '"version": *"[^"]*"' || true)
                [ -n "$FS" ] && break
                sleep 3
            done
        fi
    else
        journalctl -u sentinelcore-vpn -u sentinelcore-vpn-watch --since "$TEST_START" --no-pager | tail -20
        [ -f /run/sentinelcore-vpn/status.json ] && echo "  VPN check says: $(cat /run/sentinelcore-vpn/status.json)"
    fi
    sudo -u "$APP_USER" systemctl --no-ask-password stop sentinelcore-downloads.target || true

    echo "  Home address:        $HOME_IP"
    echo "  Address through VPN: ${VPN_IP:-none}"
    echo "  qBittorrent:         ${QB:-did not answer}"
    echo "  Prowlarr:            ${PR:-did not answer}"
    echo "  FlareSolverr:        ${FS:-did not answer (only sites behind Cloudflare need it; see journalctl -u sentinelcore-flaresolverr)}"
    [ -n "$VPN_IP" ] || die "the VPN did not connect. Check the configuration file, then: journalctl -u sentinelcore-vpn -n 50"
    if [ "$(tr -d '[:space:]' < "$DATA/network-mode")" = direct ]; then
        echo "  Note: Use VPN is turned OFF in SentinelCore's settings, so this test used the home connection."
    else
        [ "$VPN_IP" != "$HOME_IP" ] || die "traffic is NOT going through the VPN (same address as home). Do not use downloads until this is fixed."
    fi
    [ -n "$QB" ] || die "qBittorrent did not answer - see: journalctl -u sentinelcore-qbittorrent -n 50"
    [ -n "$PR" ] || die "Prowlarr did not answer - see: journalctl -u sentinelcore-prowlarr -n 50"
    echo "  Everything works, and the services are stopped again until they are needed."
fi

say "Done"
echo "Next: in SentinelCore, turn on Settings -> MOVIE DOWNLOADS, then Movies -> Downloads -> Sites to add torrent sites."
