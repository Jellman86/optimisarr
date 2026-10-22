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

# The version people will quote back at you in a bug report. Kept here rather than in a checked-in
# Info.plist because the plist is generated below; release-app.sh passes the tag through.
APP_VERSION="${APP_VERSION:-$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' ../../Directory.Build.props)}"
[[ "${APP_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "error: invalid application version" >&2; exit 2; }
# A build number must increase for each upload of the same version, so derive one that always does.
BUILD_NUMBER="${BUILD_NUMBER:-$(date -u +%Y%m%d%H%M)}"
BUNDLE="build/${APP_NAME}.app"

# Build with the selected Xcode (CI selects the newest, whose SDK has the menu's glass controls).
# The Command Line Tools have no macOS app SDK, so fall back to the default Xcode when they are
# what is selected.
if [[ -z "${DEVELOPER_DIR:-}" ]]; then
  DEVELOPER_DIR="$(xcode-select -p 2>/dev/null || true)"
  [[ -z "${DEVELOPER_DIR}" || "${DEVELOPER_DIR}" == */CommandLineTools ]] && DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
export DEVELOPER_DIR

echo "Building (${CONFIGURATION})…"
swift build --configuration "${CONFIGURATION}"
BINARY="$(swift build --configuration "${CONFIGURATION}" --show-bin-path)/${APP_NAME}"

rm -rf "${BUNDLE}"
mkdir -p "${BUNDLE}/Contents/MacOS" "${BUNDLE}/Contents/Resources"
cp "${BINARY}" "${BUNDLE}/Contents/MacOS/${APP_NAME}"
for resource in "$(dirname "${BINARY}")"/*.bundle; do
  [[ -d "${resource}" ]] && cp -R "${resource}" "${BUNDLE}/Contents/Resources/"
done

# Keep the native metric next to FFmpeg so the worker owns all opt-in audio reads.
../../scripts/build_audio_quality.sh "$(pwd)/build/audio-quality" "$(pwd)/build/audio-quality-install"
cp build/audio-quality-install/bin/optimisarr-audio-quality "${BUNDLE}/Contents/Resources/"
mkdir -p "${BUNDLE}/Contents/Resources/licenses/audio-quality"
cp build/audio-quality-install/share/optimisarr-audio-quality/* "${BUNDLE}/Contents/Resources/licenses/audio-quality/"

# Package the same qualified metric used by the shared native engine.
../../scripts/build_image_quality.sh "$(pwd)/build/image-quality" "$(pwd)/build/image-quality-install"
cp build/image-quality-install/bin/optimisarr-image-quality "${BUNDLE}/Contents/Resources/"
mkdir -p "${BUNDLE}/Contents/Resources/licenses/image-quality"
cp -R build/image-quality-install/share/optimisarr-image-quality/. "${BUNDLE}/Contents/Resources/licenses/image-quality/"

# The Swift bridge delegates native work over private pipes; users need no installed .NET SDK.
python3 ../../scripts/macos_native_package.py publish "${BUNDLE}/Contents/Resources/native-quality"
python3 ../../scripts/macos_metadata_package.py "${BUNDLE}/Contents/Resources/metadata"

# Build the Finder icon from the shared Precession artwork.
if [[ -f Resources/AppIcon.png ]]; then
  ./scripts/make-icon.sh >/dev/null
  cp build/AppIcon.icns "${BUNDLE}/Contents/Resources/AppIcon.icns"
fi

# The bundled ffmpeg, if it has been built. Without it the app still runs and pairs; it simply
# proves no encoders and is never offered work, which is the honest state rather than a broken one.
if [[ -x vendor/ffmpeg ]]; then
  cp vendor/ffmpeg "${BUNDLE}/Contents/Resources/ffmpeg"
  [[ -x vendor/ffprobe ]] && cp vendor/ffprobe "${BUNDLE}/Contents/Resources/ffprobe"
  [[ -f vendor/BUILD-INFO.txt ]] && cp vendor/BUILD-INFO.txt "${BUNDLE}/Contents/Resources/"
  [[ -f vendor/BUILD-PATCHES.txt ]] && cp vendor/BUILD-PATCHES.txt "${BUNDLE}/Contents/Resources/"
  echo "Bundled ffmpeg: $(vendor/ffmpeg -hide_banner -version | head -1)"
else
  echo "warning: no vendor/ffmpeg — run scripts/build-ffmpeg.sh; the app will prove no encoders" >&2
fi

cp ../../LICENSE "${BUNDLE}/Contents/Resources/LICENSE.txt"
if [[ -n "${SIDECAR_SOURCE_PACKAGE:-}" ]]; then
  cp -R "${SIDECAR_SOURCE_PACKAGE}/licenses" "${BUNDLE}/Contents/Resources/"
  cp "${SIDECAR_SOURCE_PACKAGE}/THIRD-PARTY-NOTICES.txt" "${BUNDLE}/Contents/Resources/"
  cp "${SIDECAR_SOURCE_PACKAGE}/source-manifest.json" "${BUNDLE}/Contents/Resources/"
fi

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
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleIdentifier</key><string>uk.optimisarr.sidecar</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>__APP_VERSION__</string>
  <key>CFBundleVersion</key><string>__BUILD_NUMBER__</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHumanReadableCopyright</key><string>Optimisarr</string>
</dict>
PLIST
echo '</plist>' >> "${BUNDLE}/Contents/Info.plist"

# Substituted after the fact so the plist above stays a plain, readable heredoc.
/usr/bin/sed -i '' \
  -e "s/__APP_VERSION__/${APP_VERSION}/" \
  -e "s/__BUILD_NUMBER__/${BUILD_NUMBER}/" \
  "${BUNDLE}/Contents/Info.plist"

# Sign every runtime library and tool before sealing the app. Only the managed helper
# needs JIT; the Swift app and media tools retain the ordinary hardened runtime.
signing_args=(--identity "${SIGNING_IDENTITY:--}")
if [[ -n "${SIGNING_KEYCHAIN:-}" ]]; then signing_args+=(--keychain "$SIGNING_KEYCHAIN"); fi
python3 ../../scripts/macos_native_package.py sign "${BUNDLE}" "${signing_args[@]}"

echo "Built ${BUNDLE}"
echo "Run it with: open ${BUNDLE}"
