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

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

echo "Building (${CONFIGURATION})…"
swift build --configuration "${CONFIGURATION}"
BINARY="$(swift build --configuration "${CONFIGURATION}" --show-bin-path)/${APP_NAME}"

rm -rf "${BUNDLE}"
mkdir -p "${BUNDLE}/Contents/MacOS" "${BUNDLE}/Contents/Resources"
cp "${BINARY}" "${BUNDLE}/Contents/MacOS/${APP_NAME}"
for resource in "$(dirname "${BINARY}")"/*.bundle; do
  [[ -d "${resource}" ]] && cp -R "${resource}" "${BUNDLE}/Contents/Resources/"
done

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

# Signing.
#
# The Keychain decides what an app may read from the identity in its signature, so a *stable*
# signature is what lets a paired credential survive a rebuild. An ad-hoc signature is derived
# from the binary and therefore changes every single build, which makes every rebuilt copy a
# different application to the Keychain — the credential becomes unreadable and, on the legacy
# keychain, macOS asks the operator for a password to reach it. Set SIGNING_IDENTITY to a real
# certificate and that stops for good:
#
#     SIGNING_IDENTITY="Developer ID Application: Your Name (TEAMID)" ./scripts/make-app.sh release
#
# `security find-identity -v -p codesigning` lists what this Mac holds. Ad-hoc remains the default
# so a fresh clone builds and runs with no certificate at all.
SIGNING_IDENTITY="${SIGNING_IDENTITY:-}"

if [[ -n "${SIGNING_IDENTITY}" ]]; then
  # keychain-access-groups moves the credential to the data protection keychain, which decides
  # access by signature and never raises a dialog. It is a restricted entitlement: macOS kills an
  # ad-hoc binary that claims it, which is exactly why it is written only when signing for real.
  # No entitlements are claimed.
  #
  # keychain-access-groups would move the credential to the data protection keychain, which never
  # raises a dialog — but it is a restricted entitlement and macOS refuses to launch an app that
  # claims it without a matching provisioning profile (a bare "Launchd job spawn failed"). It is
  # not needed: the prompting was caused by the signature changing on every build, and a stable
  # certificate fixes that on the legacy keychain too. `CredentialStore` tries the data protection
  # keychain first regardless, so a future profiled build gets it with no code change.

  # Inside out: the bundled ffmpeg and ffprobe are separate Mach-O executables and must each carry
  # their own signature before the bundle that contains them is sealed. The hardened runtime and a
  # secure timestamp are both required for notarisation.
  for tool in ffmpeg ffprobe; do
    if [[ -f "${BUNDLE}/Contents/Resources/${tool}" ]]; then
      codesign --force --options runtime --timestamp \
        --sign "${SIGNING_IDENTITY}" "${BUNDLE}/Contents/Resources/${tool}"
    fi
  done

  codesign --force --options runtime --timestamp \
    --sign "${SIGNING_IDENTITY}" "${BUNDLE}"

  echo "Signed with: ${SIGNING_IDENTITY}"
  codesign --verify --deep --strict --verbose=2 "${BUNDLE}" 2>&1 | sed 's/^/  /'
else
  # Enough to run locally. The signature changes on every build, so a pairing does not survive one.
  codesign --force --sign - "${BUNDLE}" >/dev/null 2>&1 || {
    echo "warning: ad-hoc codesign failed; the app will still run but Keychain access may prompt" >&2
  }
  echo "Ad-hoc signed (development). A pairing will not survive a rebuild —"
  echo "set SIGNING_IDENTITY to a real certificate to keep one."
fi

echo "Built ${BUNDLE}"
echo "Run it with: open ${BUNDLE}"
