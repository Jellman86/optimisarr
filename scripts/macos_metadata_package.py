#!/usr/bin/env python3
"""Bundle pinned ExifTool source and modules using the Mac's system Perl."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import tarfile
import urllib.request

from package_sidecar_sources import MAC_METADATA_SOURCES

REVISION = MAC_METADATA_SOURCES['exiftool'][1]
SOURCE_URL = f'https://codeload.github.com/exiftool/exiftool/tar.gz/{REVISION}'
SOURCE_SHA256 = 'fbda54fe81de806214f050bdc6a9d4dc27f314a969bcd863fe191c419620d7ea'


def extract(data, destination, expected_hash=SOURCE_SHA256):
    if hashlib.sha256(data).hexdigest() != expected_hash:
        raise ValueError('ExifTool source checksum mismatch')
    files = {}
    names = set()
    total = 0
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as archive:
        for entry in archive:
            path = PurePosixPath(entry.name)
            if not path.parts or path.is_absolute() or '..' in path.parts or path.parts[0] != f'exiftool-{REVISION}':
                raise ValueError('Invalid ExifTool archive path')
            if path in names:
                raise ValueError('Duplicate ExifTool archive path')
            names.add(path)
            if entry.isdir():
                continue
            if not entry.isfile():
                raise ValueError('Unexpected ExifTool archive link or special file')
            relative = PurePosixPath(*path.parts[1:])
            if not relative.parts:
                raise ValueError('Invalid ExifTool archive file path')
            if str(relative) not in ('exiftool', 'LICENSE', 'README') and relative.parts[0] != 'lib':
                continue
            total += entry.size
            if not 0 < entry.size <= 5 * 1024 * 1024 or total > 100 * 1024 * 1024:
                raise ValueError('ExifTool source exceeds its package budget')
            files[str(relative)] = archive.extractfile(entry).read()
    if not {'exiftool', 'lib/Image/ExifTool.pm', 'LICENSE', 'README'} <= files.keys():
        raise ValueError('ExifTool source is incomplete')
    script = files['exiftool']
    if not script.startswith(b'#!') or b'\n' not in script:
        raise ValueError('ExifTool source has no interpreter line')
    files['exiftool'] = b'#!/usr/bin/perl\n' + script.split(b'\n', 1)[1]
    destination.mkdir(parents=True, exist_ok=False)
    for name, content in files.items():
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
        target.chmod(0o755 if name == 'exiftool' else 0o644)
    (destination / 'source.json').write_text(json.dumps({'repository': MAC_METADATA_SOURCES['exiftool'][0],
        'revision': REVISION, 'archiveSha256': expected_hash, 'interpreter': '/usr/bin/perl'}, indent=2) + '\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    with urllib.request.urlopen(SOURCE_URL, timeout=120) as response:
        data = response.read(20 * 1024 * 1024 + 1)
    if len(data) > 20 * 1024 * 1024:
        raise ValueError('ExifTool archive exceeds its download budget')
    extract(data, args.destination.resolve())
