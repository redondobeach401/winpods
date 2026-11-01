#!/usr/bin/env bash
# Builds WinPodsAgent.app (menu-bar agent, no Dock icon) next to this script.
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/WinPodsAgent"

APP=WinPodsAgent.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/WinPodsAgent"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>app.winpods.agent</string>
    <key>CFBundleName</key><string>WinPodsAgent</string>
    <key>CFBundleExecutable</key><string>WinPodsAgent</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>0.1.0</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSUIElement</key><true/>
    <key>NSBluetoothAlwaysUsageDescription</key>
    <string>WinPods conecta y desconecta tus AirPods para pasar el audio entre el Mac y Windows.</string>
    <key>NSLocalNetworkUsageDescription</key>
    <string>WinPods se comunica con tu PC con Windows en la red local para coordinar el cambio de audio.</string>
</dict>
</plist>
PLIST
codesign --force --sign - "$APP"
echo "Creado $(pwd)/$APP — muévelo a /Applications y añádelo a Ítems de inicio de sesión."
