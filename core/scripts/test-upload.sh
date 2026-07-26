#!/usr/bin/env bash
set -euo pipefail
IMAGE=${1:?Usage: ./test-upload.sh /path/to/image.jpg [camera]}
CAMERA=${2:-TestCamera}
BRIDGE_URL=${BRIDGE_URL:-http://127.0.0.1:5080}
API_KEY=${API_KEY:?Set API_KEY first}

curl --fail-with-body -sS \
  -H "X-Api-Key: ${API_KEY}" \
  -F "image=@${IMAGE}" \
  -F "camera=${CAMERA}" \
  -F "alertId=test-$(date +%s)" \
  -F "profile=default" \
  -F "occurredAt=$(date --iso-8601=seconds)" \
  "${BRIDGE_URL}/api/blueiris/alert"
echo
