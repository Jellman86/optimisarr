"""Synthetic fixtures exercise native decoding and metric coverage without private media."""
import binascii
import json
import shutil
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import zlib

TOOL = sys.argv.pop(1)


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', binascii.crc32(kind + data) & 0xffffffff)


def png(path, damaged=False, alpha=False, extra=b'', width=96, height=64, depth=8):
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            values = [x * 255 // width, y * 255 // height, (x * 13 + y * 7) % 256]
            if damaged:
                values = [0, 0, 0]
            if alpha:
                values.append(x * 255 // width)
            for value in values:
                rows.extend(bytes([value]) if depth == 8 else struct.pack('>H', value * 257))
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, depth, 6 if alpha else 2, 0, 0, 0))
                     + extra + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


class NativeImageQualityTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.TemporaryDirectory(prefix='image metric ü ')
        self.addCleanup(self.root.cleanup)
        self.reference = Path(self.root.name) / "reference '[ ü.png"
        self.candidate = Path(self.root.name) / 'candidate.png'
        png(self.reference)
        png(self.candidate)

    def measure(self, success=True):
        result = subprocess.run([TOOL, str(self.reference), str(self.candidate)], capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode == 0, success, result.stderr)
        if success:
            value = json.loads(result.stdout)
            self.assertEqual(value['metric'], 'ssimulacra2')
            self.assertEqual(value['revision'], 'a7a9c787341cf703dede03c2009fa460cae5e5df')
            self.assertEqual(value['preparation'], 'sdr-srgb-native-still-v1')
            return value
        self.assertFalse(result.stdout.strip(), result.stdout)

    def test_identity_and_deliberate_damage(self):
        self.assertAlmostEqual(self.measure()['score'], 100, places=6)
        png(self.candidate, damaged=True)
        self.assertLess(self.measure()['score'], 50)

    def test_transparency_is_measured_on_two_backgrounds_and_alpha_compared_separately(self):
        png(self.reference, alpha=True)
        png(self.candidate, alpha=True)
        value = self.measure()
        self.assertEqual(len(value['backgroundScores']), 2)
        self.assertEqual(value['maximumAlphaError'], 0)
        png(self.candidate)
        self.assertGreater(self.measure()['maximumAlphaError'], 0.9)

    def test_depth_animation_dimensions_and_bad_metadata_are_unavailable(self):
        for options in [dict(depth=16), dict(width=97), dict(width=7),
                        dict(extra=chunk(b'acTL', struct.pack('>II', 1, 0))),
                        dict(extra=chunk(b'eXIf', b'invalid metadata')),
                        dict(extra=chunk(b'eXIf', b'II\x2a\x00\x08\x00\x00\x00\x01\x00\x12\x01\x03\x00\x01\x00\x00\x00\x06\x00\x00\x00\x00\x00\x00\x00'))]:
            with self.subTest(options=options):
                png(self.candidate, **options)
                self.measure(success=False)

    def test_malformed_input_cannot_produce_a_score(self):
        self.candidate.write_bytes(b'not an image')
        self.measure(success=False)

    def test_jpeg_webp_and_embedded_srgb_profiles(self):
        fixtures = Path(__file__).with_name('fixtures')
        for name in ['colour.jpg', 'colour.webp', 'icc.jpg', 'icc.webp', 'alpha.webp']:
            with self.subTest(name=name):
                shutil.copyfile(fixtures / name, self.reference)
                shutil.copyfile(fixtures / name, self.candidate)
                self.assertAlmostEqual(self.measure()['score'], 100, places=6)
        for name in ['bad-icc.jpg', 'bad-icc.webp', 'rotated.jpg', 'rotated.webp', 'animated.webp']:
            with self.subTest(name=name):
                shutil.copyfile(fixtures / name, self.candidate)
                self.measure(success=False)

    def test_pinned_codec_and_metric_parity(self):
        for name, expected in [('colour.jpg', 44.7604820629), ('colour.webp', 43.035274496)]:
            with self.subTest(name=name):
                shutil.copyfile(Path(__file__).with_name('fixtures') / name, self.candidate)
                self.assertAlmostEqual(self.measure()['score'], expected, delta=0.005)

    def test_incomplete_jpeg_icc_and_conflicting_png_colour_are_unavailable(self):
        jpeg = (Path(__file__).with_name('fixtures') / 'colour.jpg').read_bytes()
        self.candidate.write_bytes(jpeg[:-2])
        self.measure(success=False)
        payload = b'ICC_PROFILE\0\x01\x02incomplete'
        self.candidate.write_bytes(jpeg[:2] + b'\xff\xe2' + struct.pack('>H', len(payload) + 2) + payload + jpeg[2:])
        self.measure(success=False)
        for extra in [chunk(b'gAMA', struct.pack('>I', 100000)),
                      chunk(b'cICP', bytes([1, 16, 0, 1])),
                      chunk(b'sRGB', b'\0') + chunk(b'cICP', bytes([1, 13, 0, 1])),
                      chunk(b'iCCP', b'bad\0\0' + zlib.compress(b'invalid profile'))]:
            png(self.candidate, extra=extra)
            self.measure(success=False)

    def test_png_metadata_budget_is_shared_across_chunks(self):
        payload = zlib.compress(b'x' * (3 * 1024 * 1024))
        png(self.candidate, extra=chunk(b'zTXt', b'First\0\0' + payload))
        self.assertAlmostEqual(self.measure()['score'], 100, places=6)
        png(self.candidate, extra=chunk(b'zTXt', b'First\0\0' + payload)
            + chunk(b'zTXt', b'Second\0\0' + payload))
        self.measure(success=False)

    def test_empty_exif_and_decompression_budgets_fail_before_pixel_decode(self):
        bomb = zlib.compress(b'x' * (4 * 1024 * 1024 + 1))
        for extra in [chunk(b'eXIf', b''), chunk(b'iCCP', b'profile\0\0' + bomb),
                      chunk(b'zTXt', b'Comment\0\0' + bomb),
                      chunk(b'iTXt', b'Comment\0\1\0\0\0' + bomb),
                      chunk(b'tEXt', b'Raw profile type icc\0ignored')]:
            png(self.candidate, extra=extra)
            self.measure(success=False)


if __name__ == '__main__':
    unittest.main()
