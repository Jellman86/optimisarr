"""Independent, bounded ICC extraction and public synthetic metadata fixtures."""
import tempfile
import struct
import zlib
from pathlib import Path
from .core import require

MAX_PROFILE = 1024 * 1024
PNG = b"\x89PNG\r\n\x1a\n"

def _chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))

def read_icc_profile(path):
    path = Path(path)
    require(path.stat().st_size <= 128 * 1024 * 1024, "Image exceeds the fixture read budget")
    data = path.read_bytes(); profiles = []
    if data.startswith(PNG):
        offset = 8
        while offset < len(data):
            require(offset + 12 <= len(data), "Truncated PNG metadata")
            length = struct.unpack_from(">I", data, offset)[0]
            end = offset + 12 + length
            require(end <= len(data), "Truncated PNG chunk")
            kind, payload = data[offset + 4:offset + 8], data[offset + 8:end - 4]
            require(zlib.crc32(kind + payload) == struct.unpack_from(">I", data, end - 4)[0], "PNG metadata checksum differs")
            if kind == b"iCCP":
                separator = payload.find(b"\0")
                require(0 < separator < 80 and len(payload) > separator + 2 and payload[separator + 1] == 0, "Invalid PNG colour profile")
                inflater = zlib.decompressobj()
                profile = inflater.decompress(payload[separator + 2:], MAX_PROFILE + 1)
                require(len(profile) <= MAX_PROFILE and inflater.eof and not inflater.unused_data and not inflater.unconsumed_tail, "PNG profile exceeds its bound or is incomplete")
                profiles.append(profile)
            offset = end
    elif data.startswith(b"RIFF") and data[8:12] == b"WEBP":
        require(len(data) >= 12 and struct.unpack_from("<I", data, 4)[0] + 8 == len(data), "Incomplete WebP container")
        offset = 12
        while offset < len(data):
            require(offset + 8 <= len(data), "Truncated WebP metadata")
            length = struct.unpack_from("<I", data, offset + 4)[0]
            end = offset + 8 + length
            require(end + length % 2 <= len(data), "Truncated WebP chunk")
            if data[offset:offset + 4] == b"ICCP": profiles.append(data[offset + 8:end])
            offset = end + length % 2
    elif data.startswith(b"\xff\xd8"):
        offset = 2; fragments = {}; expected = None
        while offset < len(data):
            require(offset + 2 <= len(data) and data[offset] == 255, "Invalid JPEG marker")
            marker = data[offset + 1]
            if marker in (0xd9, 0xda): break
            require(offset + 4 <= len(data), "Truncated JPEG marker")
            length = struct.unpack_from(">H", data, offset + 2)[0]
            end = offset + 2 + length
            require(length >= 2 and end <= len(data), "Truncated JPEG metadata")
            payload = data[offset + 4:end]
            if marker == 0xe2 and payload.startswith(b"ICC_PROFILE\0"):
                require(len(payload) >= 14, "Incomplete JPEG colour profile")
                index, count = payload[12:14]
                require(1 <= index <= count and index not in fragments and (expected is None or expected == count), "Conflicting JPEG profile fragments")
                expected = count; fragments[index] = payload[14:]
                require(sum(map(len, fragments.values())) <= MAX_PROFILE, "JPEG profile exceeds its bound")
            offset = end
        if expected is not None:
            require(len(fragments) == expected, "Incomplete JPEG colour profile fragments")
            profiles.append(b"".join(fragments[index] for index in range(1, expected + 1)))
    else: raise AssertionError("Unsupported metadata fixture format")
    require(len(profiles) <= 1, "Multiple colour profiles cannot prove preservation")
    if not profiles: return None
    profile = profiles[0]
    require(128 <= len(profile) <= MAX_PROFILE and profile[36:40] == b"acsp", "Invalid complete colour profile")
    return profile

def add_synthetic_image_metadata(path, profile_fixture):
    path = Path(path); data = path.read_bytes()
    require(data.startswith(PNG) and data[12:16] == b"IHDR", "Metadata fixture must be a generated PNG")
    require(read_icc_profile(path) is None, "Fixture already carries a colour profile")
    profile = read_icc_profile(profile_fixture)
    require(profile is not None, "Public synthetic fixture has no profile")
    artist = b"Optimisarr acceptance\0"
    # Little-endian TIFF with one ASCII Artist entry; no private camera data or orientation.
    exif = b"II\x2a\x00\x08\x00\x00\x00" + struct.pack("<HHHII", 1, 0x013b, 2, len(artist), 26) + b"\0" * 4 + artist
    prepared = data[:33] + _chunk(b"iCCP", b"sRGB\0\0" + zlib.compress(profile)) + _chunk(b"eXIf", exif) + data[33:]
    # Docker's encoder may own the original inode. The harness owns this fixture directory,
    # so prepare a new public synthetic inode with shared read permissions.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + ".", suffix=".metadata", delete=False) as output:
            temporary = Path(output.name)
            output.write(prepared)
        temporary.chmod(0o644)
        temporary.replace(path)
    finally:
        if temporary is not None: temporary.unlink(missing_ok=True)
