#!/usr/bin/env python3
"""Regenerate the synthetic, profiled worker image without imaging-library dependencies."""
from pathlib import Path
import hashlib
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]
source = (ROOT / 'tools/image-quality-native/fixtures/icc.webp').read_bytes()
position = 12
profile = None
while position + 8 <= len(source):
    size = struct.unpack_from('<I', source, position + 4)[0]
    if source[position:position + 4] == b'ICCP':
        profile = source[position + 8:position + 8 + size]
        break
    position += 8 + size + (size & 1)
assert profile and len(profile) < 65536

def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))

artist = b'Optimisarr capability fixture\0'
exif = b'II*\0' + struct.pack('<I', 8) + struct.pack('<H', 1) + struct.pack('<HHII', 315, 2, len(artist), 26) + struct.pack('<I', 0) + artist
pixels = b''.join(b'\0' + bytes(channel for x in range(32) for channel in (x * 8, y * 8, (x + y) * 4)) for y in range(32))
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 32, 32, 8, 2, 0, 0, 0))
png += chunk(b'iCCP', b'Optimisarr sRGB\0\0' + zlib.compress(profile)) + chunk(b'eXIf', exif) + chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b'')
target = ROOT / 'sidecars/windows/src/Optimisarr.Sidecar.Core/Capabilities/Fixtures/profile.png'
target.write_bytes(png)
print({'fixtureBytes': len(png), 'fixtureSha256': hashlib.sha256(png).hexdigest(), 'iccBytes': len(profile), 'iccSha256': hashlib.sha256(profile).hexdigest()})
