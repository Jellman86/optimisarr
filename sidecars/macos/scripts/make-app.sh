#!/usr/bin/env bash
# Assembles OptimisarrSidecar.app from the Swift package build.
#
# Swift Package Manager produces a bare executable, but a menu-bar app wants a bundle so macOS
# treats it as a normal application — launchable from Finder, quittable from its own menu, and not
# tied to the terminal that started it. Assembling the bundle here rather than committing an
# .xcodeproj keeps the whole build reviewable as text.
set -euo pipefail

cd "$(dirname "$0")/.."

CONFIGURATION="${1:-debug}"
APP_NAME="OptimisarrSidecar"
BUNDLE="build/${APP_NAME}.app"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

echo "Building (${CONFIGURATION})…"
swift build --configuration "${CONFIGURATION}"
BINARY="$(swift build --configuration "${CONFIGURATION}" --show-bin-path)/${APP_NAME}"

rm -rf "${BUNDLE}"
mkdir -p "${BUNDLE}/Contents/MacOS" "${BUNDLE}/Contents/Resources"
cp "${BINARY}" "${BUNDLE}/Contents/MacOS/${APP_NAME}"

# LSUIElement keeps it out of the Dock and the app switcher. The app also sets its activation
# policy at startup, so it behaves correctly even when run straight from the build directory.
cat > "${BUNDLE}/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Optimisarr Sidecar</string>
  <key>CFBundleDisplayName</key><string>Optimisarr Sidecar</string>
  <key>CFBundleExecutable</key><string>OptimisarrSidecar</string>
  <key>CFBundleIdentifier</key><string>uk.optimisarr.sidecar</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHumanReadableCopyright</key><string>Optimisarr</string>
</dict>
PLIST
echo '</plist>' >> "${BUNDLE}/Contents/Info.plist"

# Ad-hoc signature so the Keychain gives the bundle a stable identity to store its credential
# against. Distribution needs a real Developer ID and notarisation; this is enough to run locally.
codesign --force --sign - "${BUNDLE}" >/dev/null 2>&1 || {
  echo "warning: ad-hoc codesign failed; the app will still run but Keychain access may prompt" >&2
}

echo "Built ${BUNDLE}"
echo "Run it with: open ${BUNDLE}"
