#!/usr/bin/env bash
# Wraps OptimisarrSidecar.app in the disk image people expect: the app on the left, a shortcut to
# Applications on the right, drag one onto the other.
#
#   ./scripts/make-dmg.sh 0.1.0
#
# A zip is perfectly serviceable, but it drops the app wherever the browser downloads it, and a
# menu-bar app run from ~/Downloads is one "tidy up" away from vanishing. The Applications
# shortcut is the whole point of the format.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:-}"
if [[ -z "${VERSION}" ]]; then
  echo "usage: $0 <version>   e.g. $0 0.1.0" >&2
  exit 2
fi

APP_NAME="OptimisarrSidecar"
VOLUME_NAME="Optimisarr Sidecar"
BUNDLE="build/${APP_NAME}.app"
DMG="build/${APP_NAME}-${VERSION}.dmg"
STAGING="build/dmg-staging"

if [[ ! -d "${BUNDLE}" ]]; then
  echo "error: ${BUNDLE} does not exist. Build it first with make-app.sh." >&2
  exit 2
fi

echo "==> Staging"
rm -rf "${STAGING}" "${DMG}"
mkdir -p "${STAGING}"
# ditto rather than cp: it keeps the bundle's symlinks, extended attributes and, importantly, any
# stapled notarisation ticket intact.
/usr/bin/ditto "${BUNDLE}" "${STAGING}/${APP_NAME}.app"
ln -s /Applications "${STAGING}/Applications"

# dmgbuild lays the window out by writing the .DS_Store directly, with no Finder involved.
# Scripting Finder was tried first and is not dependable: it times out under automation both here
# and on a CI runner, which would make the arrangement a coin toss. Without dmgbuild the image is
# still built and still works, just with the default arrangement.
DMGBUILD="${DMGBUILD:-$(command -v dmgbuild || true)}"

if [[ -n "${DMGBUILD}" ]]; then
  echo "==> Building the image, arranged"
  rm -rf "${STAGING}"
  DMG_APP="${PWD}/${BUNDLE}" "${DMGBUILD}" \
    -s scripts/dmg-settings.py \
    "${VOLUME_NAME}" \
    "${DMG}"
else
  echo "==> Building the image (plain: dmgbuild not installed, so no arrangement)"
  hdiutil create \
    -volname "${VOLUME_NAME}" \
    -srcfolder "${STAGING}" \
    -fs HFS+ \
    -format UDZO \
    -imagekey zlib-level=9 \
    -quiet \
    "${DMG}"
  rm -rf "${STAGING}"
  echo "    pip install dmgbuild for the app-and-Applications layout." >&2
fi

# Sign the image itself. Gatekeeper checks the app inside, but a signed image is what stops the
# download itself being flagged, and notarisation requires it.
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  echo "==> Signing the image"
  codesign --force --timestamp --sign "${SIGNING_IDENTITY}" "${DMG}"
  codesign --verify --strict --verbose=2 "${DMG}" 2>&1 | sed 's/^/  /'
fi

echo
echo "Built ${DMG} ($(du -h "${DMG}" | cut -f1))"
