"""Small generated VFW Matroska fixture with DTS-only pictures and a later audio start.

Matroska's V_MS/VFW/FOURCC stores decode timestamps. This exercises the same demux
and MP4 edit-list boundary as DTS-only VC-1, using FFmpeg's free MPEG-4 encoder.
"""
import struct


def element(identifier, payload):
    width = next(n for n in range(1, 9) if len(payload) < (1 << (7 * n)) - 1)
    return bytes.fromhex(identifier) + ((1 << (7 * width)) | len(payload)).to_bytes(width, "big") + payload


def integer(identifier, value):
    return element(identifier, value.to_bytes(max(1, (value.bit_length() + 7) // 8), "big"))


def packet_bytes(data, packet):
    start, length = int(packet["pos"]), int(packet["size"])
    if start < 0 or length <= 0 or start + length > len(data):
        raise ValueError("Generated packet is outside its owned fixture")
    return data[start:start + length]


def vfw_matroska(avi, video_packets, flac, audio_packets, seconds, audio_start_ms=81, *, subtitles=True):
    marker = avi.find(b"strf")
    if marker < 0 or marker + 8 > len(avi):
        raise ValueError("Generated AVI has no video codec header")
    length = struct.unpack_from("<I", avi, marker + 4)[0]
    private = avi[marker + 8:marker + 8 + length]
    if len(private) != length or length < 40 or flac[:4] != b"fLaC" or len(flac) < 42:
        raise ValueError("Generated codec headers are incomplete")
    if int.from_bytes(flac[5:8], "big") != 34:
        raise ValueError("Generated FLAC must begin with STREAMINFO")
    width, height = struct.unpack_from("<ii", private, 4)
    header = element("1a45dfa3", integer("4286", 1) + integer("42f7", 1) + integer("42f2", 4)
                     + integer("42f3", 8) + element("4282", b"matroska") + integer("4287", 4) + integer("4285", 2))
    info = element("1549a966", integer("2ad7b1", 1000000)
                   + element("4489", struct.pack(">d", seconds * 1000 + audio_start_ms))
                   + element("4d80", b"Optimisarr generated regression") + element("5741", b"Optimisarr"))
    video = element("ae", integer("d7", 1) + integer("73c5", 1) + integer("83", 1)
                    + element("86", b"V_MS/VFW/FOURCC") + element("63a2", private) + integer("23e383", 40000000)
                    + element("e0", integer("b0", width) + integer("ba", height)))
    audio = element("ae", integer("d7", 2) + integer("73c5", 2) + integer("83", 2)
                    + element("86", b"A_FLAC") + element("63a2", b"fLaC\x80\x00\x00\x22" + flac[8:42])
                    + element("22b59c", b"eng")
                    + element("e1", element("b5", struct.pack(">d", 48000)) + integer("9f", 1) + integer("6264", 16)))
    subtitle = element("ae", integer("d7", 3) + integer("73c5", 3) + integer("83", 17)
                       + element("86", b"S_TEXT/UTF8") + element("22b59c", b"eng"))
    packets = [(round(float(p["dts_time"]) * 1000), 1, "K" in p.get("flags", ""), packet_bytes(avi, p), None)
               for p in video_packets]
    packets += [(audio_start_ms + round(float(p["pts_time"]) * 1000), 2, True, packet_bytes(flac, p), None)
                for p in audio_packets]
    if subtitles:
        packets += [(1081, 3, True, b"Generated timestamp regression", 1000)]
    clusters, blocks, origin = [], [], None
    # Equal decode timestamps must retain the bitstream's packet order.
    for timestamp, track, keyframe, payload, duration in sorted(packets, key=lambda packet: packet[0]):
        if origin is None or timestamp - origin >= 2000:
            if origin is not None:
                clusters.append(element("1f43b675", integer("e7", origin) + b"".join(blocks)))
            origin, blocks = timestamp, []
        block = bytes([128 + track]) + (timestamp - origin).to_bytes(2, "big", signed=True) + bytes([128 if keyframe and duration is None else 0]) + payload
        blocks.append(element("a3", block) if duration is None
                      else element("a0", element("a1", block) + integer("9b", duration)))
    if origin is not None:
        clusters.append(element("1f43b675", integer("e7", origin) + b"".join(blocks)))
    return header + element("18538067", info + element("1654ae6b", video + audio + (subtitle if subtitles else b"")) + b"".join(clusters))
