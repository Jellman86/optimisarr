#!/usr/bin/env bash
# Builds, signs, notarises and staples a distributable OptimisarrSidecar.app, then zips it.
#
# Signing proves who built the app. Notarisation is Apple actually scanning it and issuing a
# ticket, and stapling attaches that ticket to the bundle so Gatekeeper can see it offline. Without
# the ticket, anyone who downloads the app gets "Apple could not verify this app is free of
# malware" and has to right-click-Open — which is exactly the moment people give up on an app.
#
#   SIGNING_IDENTITY="Developer ID Application: Name (TEAMID)" \
#   NOTARY_PROFILE=optimisarr-notary \
#   ./scripts/release-app.sh 0.1.0
#
# NOTARY_PROFILE is a keychain profile made once with:
#
#   xcrun notarytool store-credentials optimisarr-notary \
#     --key ~/private_keys/AuthKey_XXXXXXXX.p8 --key-id XXXXXXXX --issuer <issuer-uuid>
#
# In CI, set NOTARY_KEY / NOTARY_KEY_ID / NOTARY_ISSUER instead and no profile is needed.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:-}"
if [[ -z "${VERSION}" ]]; then
  echo "usage: $0 <version>   e.g. $0 0.1.0" >&2
  exit 2
fi

APP_NAME="OptimisarrSidecar"
BUNDLE="build/${APP_NAME}.app"
ARCHIVE="build/${APP_NAME}-${VERSION}.zip"

if [[ -z "${SIGNING_IDENTITY:-}" ]]; then
  echo "error: SIGNING_IDENTITY is not set. A notarised build needs a Developer ID." >&2
  echo "       security find-identity -v -p codesigning" >&2
  exit 2
fi

# Apple only notarises Developer ID builds. A development certificate signs and runs locally but
# the submission is rejected, so say so here rather than after a two-minute round trip.
if [[ "${SIGNING_IDENTITY}" != "Developer ID Application"* ]]; then
  echo "error: '${SIGNING_IDENTITY}' is not a Developer ID Application certificate." >&2
  echo "       Apple will not notarise anything else. See README → Signing." >&2
  exit 2
fi

echo "==> Building and signing ${VERSION}"
APP_VERSION="${VERSION}" ./scripts/make-app.sh release

echo
echo "==> Archiving"
# ditto, not zip: it preserves the bundle's symlinks and extended attributes, and the signature
# does not survive a plain zip.
rm -f "${ARCHIVE}"
/usr/bin/ditto -c -k --keepParent "${BUNDLE}" "${ARCHIVE}"
echo "    ${ARCHIVE} ($(du -h "${ARCHIVE}" | cut -f1))"

echo
echo "==> Notarising (Apple scans it; this usually takes a few minutes)"
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  xcrun notarytool submit "${ARCHIVE}" --keychain-profile "${NOTARY_PROFILE}" --wait
else
  : "${NOTARY_KEY:?set NOTARY_PROFILE, or NOTARY_KEY/NOTARY_KEY_ID/NOTARY_ISSUER}"
  : "${NOTARY_KEY_ID:?}"
  : "${NOTARY_ISSUER:?}"
  xcrun notarytool submit "${ARCHIVE}" \
    --key "${NOTARY_KEY}" --key-id "${NOTARY_KEY_ID}" --issuer "${NOTARY_ISSUER}" --wait
fi

echo
echo "==> Stapling the ticket to the bundle"
xcrun stapler staple "${BUNDLE}"

# Re-archive: the staple changed the bundle on disk, so the zip made before it is now the
# un-stapled version and would still prompt on a machine that is offline.
rm -f "${ARCHIVE}"
/usr/bin/ditto -c -k --keepParent "${BUNDLE}" "${ARCHIVE}"

echo
echo "==> Verifying the way Gatekeeper will"
xcrun stapler validate "${BUNDLE}"
spctl --assess --type execute --verbose=2 "${BUNDLE}"

# The disk image is built from the *stapled* bundle, so the app carries its own ticket even after
# someone drags it out. The image is then notarised in its own right, because the ticket that
# matters to a download is the one attached to the file that was downloaded.
DMG="build/${APP_NAME}-${VERSION}.dmg"
echo
echo "==> Building the disk image"
SIGNING_IDENTITY="${SIGNING_IDENTITY}" ./scripts/make-dmg.sh "${VERSION}"

echo
echo "==> Notarising the disk image"
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  xcrun notarytool submit "${DMG}" --keychain-profile "${NOTARY_PROFILE}" --wait
else
  xcrun notarytool submit "${DMG}" \
    --key "${NOTARY_KEY}" --key-id "${NOTARY_KEY_ID}" --issuer "${NOTARY_ISSUER}" --wait
fi

echo
echo "==> Stapling the disk image"
xcrun stapler staple "${DMG}"
xcrun stapler validate "${DMG}"
spctl --assess --type open --context context:primary-signature --verbose=2 "${DMG}"

echo
echo "Done:"
echo "  ${ARCHIVE}"
echo "  ${DMG}"
echo "Attach both to the GitHub Release."
