#!/usr/bin/env bash
# Exercise only rendered fixtures from a relocated signed app, never pairing or live workers.
set -euo pipefail
if [[ $# -ne 2 ]]; then
  echo "usage: $0 <signed-app-bundle> <expected-version>" >&2
  exit 2
fi
BUNDLE="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
EXPECTED_VERSION="$2"
SMOKE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/optimisarr-app-smoke.XXXXXX")"
trap 'rm -rf "${SMOKE_ROOT}"' EXIT
/usr/bin/ditto "${BUNDLE}" "${SMOKE_ROOT}/OptimisarrSidecar.app"
/usr/bin/codesign --verify --deep --strict "${SMOKE_ROOT}/OptimisarrSidecar.app"
python3 - "${SMOKE_ROOT}" "${EXPECTED_VERSION}" <<'PY'
import os
from pathlib import Path
import plistlib
import struct
import subprocess
import sys

root = Path(sys.argv[1])
app = root / 'OptimisarrSidecar.app'
contents = app / 'Contents'
with (contents / 'Info.plist').open('rb') as stream:
    info = plistlib.load(stream)
if info.get('CFBundleShortVersionString') != sys.argv[2]:
    raise SystemExit('Packaged application version does not match the release.')
resources = contents / 'Resources'
for name in ['ffmpeg', 'ffprobe', 'AppIcon.icns']:
    path = resources / name
    if not path.is_file() or path.stat().st_size == 0:
        raise SystemExit(f'Missing packaged resource: {name}')
    if name in ('ffmpeg', 'ffprobe') and not os.access(path, os.X_OK):
        raise SystemExit(f'Packaged media tool is not executable: {name}')
for name in ['BrandMark.png', 'BrandMarkLight.png', 'BrandMotion.png', 'BrandMotionLight.png']:
    matches = list(resources.glob(f'OptimisarrSidecar_OptimisarrSidecar.bundle/**/{name}'))
    if len(matches) != 1 or not matches[0].resolve().is_relative_to(app.resolve()):
        raise SystemExit(f'Precession artwork must be embedded in the app: {name}')

environment = os.environ.copy()
for key in ['PACKAGE_RESOURCE_BUNDLE_PATH', 'PACKAGE_RESOURCE_BUNDLE_URL', 'OPTIMISARR_RENDER_FRAME']:
    environment.pop(key, None)
# The packaged resource loader deliberately cannot fall back to Bundle.module/build paths.
subprocess.run([str(contents / 'MacOS' / 'OptimisarrSidecar'), '--render-menu', str(root / 'rendered')],
               cwd=root, env=environment, check=True, timeout=90)
for state in ['unpaired', 'connected-idle', 'receiving', 'encoding', 'sending', 'two-jobs', 'details', 'preferences', 'revoked', 'unreachable']:
    for prefix in ['', 'light-']:
        path = root / 'rendered' / f'{prefix}{state}.png'
        data = path.read_bytes()
        if len(data) < 1024 or data[:8] != b'\x89PNG\r\n\x1a\n':
            raise SystemExit(f'Missing or invalid native fixture: {path.name}')
        width, height = struct.unpack('>II', data[16:24])
        if width < 390 or height < 100:
            raise SystemExit(f'Truncated native fixture: {path.name}')
print('Relocated signed app: version, embedded tools/artwork and all 20 native fixtures passed.')
PY
