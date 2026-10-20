import os
import struct
import sys
import tempfile
import unittest
import zlib
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.image_metadata import read_icc_profile, add_synthetic_image_metadata

REPO = Path(__file__).resolve().parents[2]
FIXTURES = REPO / "tools/image-quality-native/fixtures"

def chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))

def png(extra=b""):
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)) + extra + chunk(b"IDAT", zlib.compress(b"\x00\x00\x00\x00")) + chunk(b"IEND", b"")

class ImageMetadataAcceptanceTests(unittest.TestCase):
    def test_public_synthetic_jpeg_and_webp_carry_the_same_complete_icc_bytes(self):
        jpeg = read_icc_profile(FIXTURES / "icc.jpg")
        self.assertEqual(jpeg, read_icc_profile(FIXTURES / "icc.webp"))
        self.assertEqual(b"acsp", jpeg[36:40])
        self.assertIsNone(read_icc_profile(FIXTURES / "colour.webp"))

    def test_png_fixture_gets_a_real_binary_profile_and_an_exif_artist(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "source.png"; path.write_bytes(png())
            add_synthetic_image_metadata(path, FIXTURES / "icc.webp")
            self.assertEqual(read_icc_profile(FIXTURES / "icc.webp"), read_icc_profile(path))
            self.assertIn(b"eXIf", path.read_bytes())
            self.assertIn(b"Optimisarr acceptance", path.read_bytes())
            with self.assertRaises(AssertionError):
                add_synthetic_image_metadata(path, FIXTURES / "icc.webp")

    @unittest.skipUnless(os.name == "posix", "Container fixture ownership is a POSIX boundary")
    def test_container_owned_or_readonly_fixture_is_prepared_by_replacing_its_inode(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "source.png"; path.write_bytes(png())
            original_inode = path.stat().st_ino
            path.chmod(0o444)
            add_synthetic_image_metadata(path, FIXTURES / "icc.webp")
            self.assertNotEqual(original_inode, path.stat().st_ino)
            self.assertEqual(0o644, path.stat().st_mode & 0o777)
            self.assertIsNotNone(read_icc_profile(path))

    def test_incomplete_jpeg_profile_fragments_never_claim_retained_colour_metadata(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "partial.jpg"
            payload = b"ICC_PROFILE\0\x01\x02partial"
            path.write_bytes(b"\xff\xd8\xff\xe2" + struct.pack(">H", len(payload) + 2) + payload + b"\xff\xd9")
            with self.assertRaises(AssertionError): read_icc_profile(path)

    def test_compressed_profile_bombs_and_bad_png_checksums_are_refused(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "profile.png"
            for data in [png(chunk(b"iCCP", b"sRGB\0\0" + zlib.compress(b"x" * (1024 * 1024 + 1)))), png()[:-1] + b"x"]:
                path.write_bytes(data)
                with self.assertRaises(AssertionError): read_icc_profile(path)
