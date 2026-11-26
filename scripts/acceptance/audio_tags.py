"""Write and read music tags without FFmpeg, so acceptance can build tags FFmpeg cannot write and
check the server's tag gate independently of it."""
from __future__ import annotations

import hashlib
import os
import struct
from pathlib import Path


def _replace(path, data):
    # A tool container may have written the file as another user; the directory is the run's own,
    # so the new bytes go in beside it and take its name.
    staged = path.with_name(path.name + ".tagged")
    staged.write_bytes(data)
    os.replace(staged, path)


def _syncsafe(value):
    return bytes(((value >> 21) & 0x7F, (value >> 14) & 0x7F, (value >> 7) & 0x7F, value & 0x7F))


def _unsyncsafe(data):
    return (data[0] << 21) | (data[1] << 14) | (data[2] << 7) | data[3]


def id3_text(frame_id, *values):
    """A text frame in UTF-8 (ID3v2.4) or, for v2.3, rewritten by id3_tag as UTF-16."""
    return frame_id, ("text", list(values))


def id3_frame(frame_id, payload):
    return frame_id, ("raw", payload)


def popm(email, rating, count):
    return id3_frame("POPM", email.encode("latin-1") + b"\0" + bytes([rating]) + struct.pack(">I", count))


def ufid(owner, identifier):
    return id3_frame("UFID", owner.encode("latin-1") + b"\0" + identifier.encode("ascii"))


def uslt(language, text, description=""):
    return id3_frame("USLT", b"\x03" + language.encode("ascii") + description.encode("utf-8") + b"\0" + text.encode("utf-8"))


def id3_tag(frames, version=4):
    body = bytearray()
    for frame_id, (kind, content) in frames:
        if kind == "text":
            if version == 4:
                payload = b"\x03" + "\0".join(content).encode("utf-8")
            else:
                payload = b"\x01" + "/".join(content).encode("utf-16")
        else:
            payload = content
        size = _syncsafe(len(payload)) if version == 4 else struct.pack(">I", len(payload))
        body += frame_id.encode("ascii") + size + b"\0\0" + payload
    return b"ID3" + bytes((version, 0, 0)) + _syncsafe(len(body)) + bytes(body)


def strip_id3(data):
    if data[:3] == b"ID3":
        return data[10 + _unsyncsafe(data[6:10]):]
    return data


def write_id3(path, frames, version=4):
    path = Path(path)
    _replace(path, id3_tag(frames, version) + strip_id3(path.read_bytes()))


def read_id3(path):
    """Every frame of an ID3v2.3/2.4 tag as (id, sha256 of its payload), in order."""
    data = Path(path).read_bytes()
    if data[:3] != b"ID3":
        return []
    version, flags = data[3], data[5]
    end = 10 + _unsyncsafe(data[6:10])
    if flags & 0x80 and version == 3:
        data = data[:10] + data[10:end].replace(b"\xff\x00", b"\xff")
        end = len(data)
    position = 10
    frames = []
    while position + 10 <= end and data[position] != 0:
        frame_id = data[position:position + 4].decode("latin-1")
        raw = data[position + 4:position + 8]
        size = _unsyncsafe(raw) if version == 4 else struct.unpack(">I", raw)[0]
        payload = data[position + 10:position + 10 + size]
        frames.append((frame_id, payload))
        position += 10 + size
    return frames


def frame_ids(path):
    return [frame_id for frame_id, _ in read_id3(path)]


FLAC_BLOCK_NAMES = {0: "STREAMINFO", 1: "PADDING", 2: "APPLICATION", 3: "SEEKTABLE", 4: "VORBIS_COMMENT", 5: "CUESHEET", 6: "PICTURE"}


def _flac_blocks(data):
    if data[:4] != b"fLaC":
        raise ValueError("Not a FLAC file")
    position, blocks = 4, []
    while True:
        header = data[position]
        size = int.from_bytes(data[position + 1:position + 4], "big")
        blocks.append((header & 0x7F, data[position + 4:position + 4 + size]))
        position += 4 + size
        if header & 0x80:
            return blocks, data[position:]


def _vorbis_comment(vendor, comments):
    encoded = [f"{key}={value}".encode("utf-8") for key, value in comments]
    body = struct.pack("<I", len(vendor)) + vendor.encode("utf-8") + struct.pack("<I", len(encoded))
    for comment in encoded:
        body += struct.pack("<I", len(comment)) + comment
    return body


def _read_vorbis_comment(body):
    vendor_length = struct.unpack_from("<I", body, 0)[0]
    position = 4 + vendor_length
    count = struct.unpack_from("<I", body, position)[0]
    position += 4
    comments = []
    for _ in range(count):
        length = struct.unpack_from("<I", body, position)[0]
        key, _, value = body[position + 4:position + 4 + length].decode("utf-8").partition("=")
        comments.append((key.upper(), value))
        position += 4 + length
    return comments


def write_flac(path, comments, application=None):
    """Replaces a FLAC file's comments, keeping STREAMINFO and SEEKTABLE, and adds an APPLICATION block."""
    path = Path(path)
    blocks, audio = _flac_blocks(path.read_bytes())
    kept = [(kind, body) for kind, body in blocks if kind in (0, 3, 6)]
    kept.append((4, _vorbis_comment("acceptance", comments)))
    if application is not None:
        kept.append((2, application))
    out = bytearray(b"fLaC")
    for index, (kind, body) in enumerate(kept):
        out += bytes([kind | (0x80 if index == len(kept) - 1 else 0)]) + len(body).to_bytes(3, "big") + body
    _replace(path, bytes(out) + audio)


def read_flac(path):
    """The block names in order, and the comments as (FIELD, value) in order."""
    blocks, _ = _flac_blocks(Path(path).read_bytes())
    names = [FLAC_BLOCK_NAMES.get(kind, str(kind)) for kind, _ in blocks]
    comments = next((_read_vorbis_comment(body) for kind, body in blocks if kind == 4), [])
    return names, comments


def _text(encoding, data):
    codec = {0: "latin-1", 1: "utf-16", 2: "utf-16-be", 3: "utf-8"}[encoding]
    return data.decode(codec)


def _values(encoding, data):
    values = [value.lstrip("\ufeff") for value in _text(encoding, data).split("\0")]
    while len(values) > 1 and values[-1] == "":
        values.pop()
    return tuple(values)


def _split(encoding, data):
    width = 2 if encoding in (1, 2) else 1
    for index in range(0, len(data) - width + 1, width):
        if data[index:index + width] == b"\0" * width:
            return _text(encoding, data[:index]).lstrip("\ufeff"), data[index + width:]
    raise ValueError("Unterminated ID3 string")


def id3_entries(path):
    """Every ID3v2 frame as (key, values), text decoded, so encodings and versions compare equal."""
    entries = []
    for frame_id, payload in read_id3(path):
        if frame_id in ("TXXX", "COMM", "USLT"):
            offset = 1 if frame_id == "TXXX" else 4
            description, rest = _split(payload[0], payload[offset:])
            key = f"{frame_id}:{description}" if frame_id == "TXXX" else f"{frame_id}:{payload[1:4].decode('latin-1')}:{description}"
            entries.append((key, _values(payload[0], rest)))
        elif frame_id == "APIC":
            mime_end = payload.index(b"\0", 1)
            description, data = _split(payload[0], payload[mime_end + 2:])
            entries.append((f"APIC:{payload[mime_end + 1]}:{description}", (hashlib.sha256(data).hexdigest(),)))
        elif frame_id.startswith("T"):
            entries.append((frame_id, _values(payload[0], payload[1:])))
        else:
            entries.append((frame_id, (hashlib.sha256(payload).hexdigest(),)))
    return entries


def _ogg_packets(data, count):
    position, packets, packet = 0, [], b""
    while len(packets) < count:
        if data[position:position + 4] != b"OggS":
            raise ValueError("Not an Ogg page")
        segments = data[position + 26]
        table = data[position + 27:position + 27 + segments]
        body = position + 27 + segments
        for lacing in table:
            packet += data[body:body + lacing]
            body += lacing
            if lacing < 255:
                packets.append(packet)
                packet = b""
        position = body
    return packets[:count]


def comment_entries(path):
    """A FLAC or Opus file's comments as (FIELD, values in order), and FLAC's other kept blocks."""
    data = Path(path).read_bytes()
    if data[:4] == b"fLaC":
        blocks, _ = _flac_blocks(data)
        comments = next((_read_vorbis_comment(body) for kind, body in blocks if kind == 4), [])
        extra = [(FLAC_BLOCK_NAMES.get(kind, str(kind)), (hashlib.sha256(body).hexdigest(),))
                 for kind, body in blocks if kind not in (0, 1, 3, 4)]
    else:
        tags = _ogg_packets(data, 2)[1]
        if tags[:8] != b"OpusTags":
            raise ValueError("No Opus comment header")
        comments, extra = _read_vorbis_comment(tags[8:]), []
    fields = {}
    for key, value in comments:
        fields.setdefault(key, []).append(value)
    return [(key, tuple(values)) for key, values in fields.items()] + extra


# Written again for the trimmed file on purpose: the encoder, the length and gapless figures of the
# untrimmed file, and Optimisarr's marker.
REGENERATED = {"TSSE", "TLEN", "TXXX:iTunSMPB", "TXXX:optimisarr", "ENCODER", "OPTIMISARR"}


def lost_or_changed(source_entries, output_entries):
    """The keys of source tags the output does not hold with exactly the same values, as often."""
    def grouped(entries):
        result = {}
        for key, values in entries:
            result.setdefault(key, []).append(values)
        return {key: sorted(values) for key, values in result.items()}
    source, output = grouped(source_entries), grouped(output_entries)
    return sorted(key for key, values in source.items() if key not in REGENERATED and output.get(key) != values)


__all__ = ["id3_text", "id3_frame", "popm", "ufid", "uslt", "write_id3", "read_id3", "frame_ids",
           "write_flac", "read_flac", "id3_entries", "comment_entries", "lost_or_changed"]
