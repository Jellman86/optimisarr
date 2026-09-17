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
if [[ ! "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "usage: $0 <version>   e.g. $0 0.2.13" >&2
  exit 2
fi

# Source archives come from HEAD; modified or untracked build inputs would ship different code.
# Local development builds remain available through make-app.sh.
if ! git diff --quiet HEAD -- || [[ -n "$(git ls-files --others --exclude-standard)" ]]; then
  echo "error: public packaging requires a clean committed worktree matching its source archive" >&2
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

# A reused vendor cache still requires its exact corresponding sources. Never sign a package
# whose source archive was generated for a different application commit or media-tool build.
SIDECAR_SOURCE_PACKAGE="${SIDECAR_SOURCE_PACKAGE:-$(pwd)/build/release-sources}"
export SIDECAR_SOURCE_PACKAGE
if [[ ! -e "${SIDECAR_SOURCE_PACKAGE}" ]]; then
  python3 ../../scripts/package_sidecar_sources.py --platform macos \
    --mac-sources "$(pwd)/.build-ffmpeg" --output "${SIDECAR_SOURCE_PACKAGE}"
fi
python3 - "${SIDECAR_SOURCE_PACKAGE}" "${VERSION}" <<'PYVALIDATE'
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path('../../scripts').resolve()))
from package_sidecar_sources import read_mac_revisions, sha256

package = Path(sys.argv[1]).resolve()
version = sys.argv[2]
info = Path('vendor/BUILD-INFO.txt')
if (package / 'BUILD-INFO.txt').read_bytes() != info.read_bytes():
    raise SystemExit('Source package BUILD-INFO does not match the bundled media tools. Regenerate the source package.')
manifest = json.loads((package / 'source-manifest.json').read_text())
if manifest.get('platform') != 'macos' or manifest.get('version') != version:
    raise SystemExit('Source package platform/version does not match this release.')
records = manifest.get('sources', [])
revisions = {item['name']: item['revision'] for item in records}
expected = read_mac_revisions(info)
expected['optimisarr'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
if len(revisions) != len(records) or revisions != expected:
    raise SystemExit('Source package revisions do not match this application and its bundled media tools.')
parts = list(package.glob(f'OptimisarrSidecar-{version}-macos-sources-*.tar'))
if not parts:
    raise SystemExit('Corresponding source archive parts are missing.')
for part in parts:
    checksum = part.with_name(part.name + '.sha256').read_text().split()
    if checksum != [sha256(part), part.name]:
        raise SystemExit(f'Invalid corresponding-source checksum: {part.name}')
for name in ['THIRD-PARTY-NOTICES.txt', 'SOURCE-README.txt']:
    if not (package / name).is_file() or not (package / name).stat().st_size:
        raise SystemExit(f'Missing source distribution notice: {name}')
if not any((package / 'licenses').rglob('*')):
    raise SystemExit('Bundled licence copies are missing.')
print('Corresponding sources match the release commit and bundled media tools.')
PYVALIDATE

echo "==> Building and signing ${VERSION}"
APP_VERSION="${VERSION}" ./scripts/make-app.sh release
./scripts/verify-app.sh "${BUNDLE}" "${VERSION}"

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
# Hash the final distributed bytes only after both stapling operations are complete.
for artifact in "${ARCHIVE}" "${DMG}"; do
  (cd "$(dirname "${artifact}")" && shasum -a 256 "$(basename "${artifact}")") > "${artifact}.sha256"
done

echo "Done:"
echo "  ${ARCHIVE}"
echo "  ${DMG}"
echo "Attach both packages and their .sha256 files to the GitHub Release."
