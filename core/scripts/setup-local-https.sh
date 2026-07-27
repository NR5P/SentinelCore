#!/usr/bin/env bash
# Sets up HTTPS for the dashboard using a certificate authority generated on this machine.
#
#   ./scripts/setup-local-https.sh
#
# Why a local CA rather than a normal certificate: no public authority will issue a certificate
# for a private address like 192.168.x.x, so Let's Encrypt and friends simply can't help here.
# The alternatives are a domain you own (see docs/DEPLOYMENT.md) or this - your own authority,
# trusted by your own devices.
#
# A plain self-signed certificate is NOT the same thing and is actively worse: browsers show a
# full-page "your connection is not private" warning every visit. A certificate signed by a CA
# your device trusts shows a normal padlock.
#
# The CA is generated here, on your machine, and is unique to this install. It is deliberately
# not shipped with the product: a bundled CA would mean every install shared one private key, so
# anyone who owned a copy could forge certificates for everybody else.
#
# Safe to re-run. It keeps the existing CA (so devices stay trusted) and reissues only the
# server certificate.

set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)
CERT_DIR=${CERT_DIR:-$ROOT_DIR/certs}

CA_KEY="$CERT_DIR/ca.key"
CA_CERT="$CERT_DIR/ca.crt"
SERVER_KEY="$CERT_DIR/server.key"
SERVER_CERT="$CERT_DIR/server.crt"

CA_DAYS=3650
# Apple caps certificates at 825 days for *every* CA, including ones you add yourself. (Their
# better-known 398-day limit only applies to Apple's own preinstalled roots.) Going over this
# makes iPhones reject the certificate no matter how correctly the CA is trusted.
SERVER_DAYS=825

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl was not found. Install it and run this again." >&2
  exit 1
fi

mkdir -p "$CERT_DIR"
chmod 700 "$CERT_DIR"

# ---------------------------------------------------------------------------
# Names the certificate has to cover
# ---------------------------------------------------------------------------
# Browsers ignore the old Common Name field entirely and match only against these Subject
# Alternative Names. IP addresses need explicit IP: entries - listing one under DNS: silently
# fails to match, which is the classic reason a hand-rolled certificate "looks right" but is
# still rejected.

HOSTNAME_SHORT=$(hostname -s 2>/dev/null || hostname)
LAN_IPS=$(ip -4 addr show scope global 2>/dev/null | grep -oP 'inet \K[\d.]+' || true)

ALT_NAMES="DNS:localhost,DNS:${HOSTNAME_SHORT},DNS:${HOSTNAME_SHORT}.local"
for ip in $LAN_IPS; do
  ALT_NAMES="${ALT_NAMES},IP:${ip}"
done
ALT_NAMES="${ALT_NAMES},IP:127.0.0.1,IP:::1"

# Any extra names you want covered, e.g. EXTRA_NAMES="DNS:sentinel.home,IP:10.0.0.5"
if [[ -n "${EXTRA_NAMES:-}" ]]; then
  ALT_NAMES="${ALT_NAMES},${EXTRA_NAMES}"
fi

# ---------------------------------------------------------------------------
# The authority
# ---------------------------------------------------------------------------

if [[ -f "$CA_KEY" && -f "$CA_CERT" ]]; then
  echo "==> Reusing the existing authority at $CA_CERT"
  echo "    (devices that already trust it stay trusted)"
else
  echo "==> Creating a certificate authority for this machine (valid $CA_DAYS days)"
  openssl req -x509 -newkey rsa:4096 -sha256 -days "$CA_DAYS" -nodes \
    -keyout "$CA_KEY" -out "$CA_CERT" \
    -subj "/CN=Sentinel Core Local CA (${HOSTNAME_SHORT})/O=Sentinel Core" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  chmod 600 "$CA_KEY"
fi

# ---------------------------------------------------------------------------
# The server certificate
# ---------------------------------------------------------------------------

echo "==> Issuing the server certificate (valid $SERVER_DAYS days)"
echo "    covering: $ALT_NAMES"

CSR=$(mktemp); EXT=$(mktemp)
trap 'rm -f "$CSR" "$EXT"' EXIT

openssl req -newkey rsa:2048 -sha256 -nodes \
  -keyout "$SERVER_KEY" -out "$CSR" \
  -subj "/CN=${HOSTNAME_SHORT}.local/O=Sentinel Core" 2>/dev/null

cat > "$EXT" <<EXTENSIONS
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=${ALT_NAMES}
EXTENSIONS

openssl x509 -req -in "$CSR" -CA "$CA_CERT" -CAkey "$CA_KEY" -CAcreateserial \
  -out "$SERVER_CERT" -days "$SERVER_DAYS" -sha256 -extfile "$EXT" 2>/dev/null

chmod 600 "$SERVER_KEY"
chmod 644 "$SERVER_CERT" "$CA_CERT"

EXPIRES=$(openssl x509 -in "$SERVER_CERT" -noout -enddate | cut -d= -f2)

cat <<NEXT

==> Done. Certificate expires $EXPIRES

    1. Point the dashboard at it - in BlueIrisOllamaBridge.Web/appsettings.json:

         "Https": {
           "CertificatePath": "$SERVER_CERT",
           "KeyPath": "$SERVER_KEY",
           "Port": 3443
         }

       then restart it:  sudo systemctl restart blueiris-ollama-bridge-web
       Plain HTTP keeps working on its usual port - nothing breaks by doing this.

    2. Trust the authority on each device you browse from. The file to copy is:

         $CA_CERT

       Linux:   sudo cp "$CA_CERT" /usr/local/share/ca-certificates/sentinel-core.crt
                sudo update-ca-certificates
       macOS:   double-click it, then in Keychain Access set it to "Always Trust"
       Windows: double-click -> Install Certificate -> Local Machine ->
                place in "Trusted Root Certification Authorities"
       iOS:     email or AirDrop it to yourself, open it, install the profile, THEN go to
                Settings > General > About > Certificate Trust Settings and switch it on.
                That last step is easy to miss and nothing works without it.
       Android: Settings > Security > Encryption & credentials > Install a certificate > CA

    3. Browse to https://${HOSTNAME_SHORT}.local:3443 and bookmark it.

    To renew automatically before this expires, see docs/DEPLOYMENT.md.
NEXT
