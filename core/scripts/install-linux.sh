#!/usr/bin/env bash
set -euo pipefail

APP_USER=${APP_USER:-media}
INSTALL_DIR=${INSTALL_DIR:-/opt/blueiris-ollama-bridge}
SERVICE_NAME=blueiris-ollama-bridge

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet was not found. Install the .NET 10 SDK first." >&2
  exit 1
fi

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT_DIR=$(cd -- "$SCRIPT_DIR/.." && pwd)
PUBLISH_DIR=$(mktemp -d)
trap 'rm -rf "$PUBLISH_DIR"' EXIT

dotnet restore "$ROOT_DIR/BlueIrisOllamaBridge/BlueIrisOllamaBridge.csproj"
dotnet test "$ROOT_DIR/BlueIrisOllamaBridge.Tests/BlueIrisOllamaBridge.Tests.csproj" -c Release
dotnet publish "$ROOT_DIR/BlueIrisOllamaBridge/BlueIrisOllamaBridge.csproj" -c Release -o "$PUBLISH_DIR"

DOTNET_PATH=$(command -v dotnet)
sudo systemctl stop "$SERVICE_NAME" 2>/dev/null || true
sudo mkdir -p "$INSTALL_DIR/data"
EXISTING_CONFIG=$(mktemp)
HAD_CONFIG=false
if sudo test -f "$INSTALL_DIR/appsettings.json"; then
  sudo cp "$INSTALL_DIR/appsettings.json" "$EXISTING_CONFIG"
  HAD_CONFIG=true
fi
sudo cp -a "$PUBLISH_DIR/." "$INSTALL_DIR/"
if [[ "$HAD_CONFIG" == true ]]; then
  sudo cp "$EXISTING_CONFIG" "$INSTALL_DIR/appsettings.json"
fi
rm -f "$EXISTING_CONFIG"
sudo chown -R "$APP_USER:$APP_USER" "$INSTALL_DIR"
sed "s|__DOTNET_PATH__|$DOTNET_PATH|g; s|__APP_USER__|$APP_USER|g; s|__INSTALL_DIR__|$INSTALL_DIR|g" \
  "$ROOT_DIR/systemd/$SERVICE_NAME.service" | sudo tee "/etc/systemd/system/$SERVICE_NAME.service" >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable --now "$SERVICE_NAME"
sudo systemctl --no-pager status "$SERVICE_NAME"
