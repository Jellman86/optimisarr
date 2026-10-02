#!/usr/bin/env python3
"""Backport FFmpeg 618fc15e65: do not parse an empty Opus parser flush as a packet."""
from pathlib import Path
import sys

BEFORE = "if (set_frame_duration(ctx, avctx, buf, buf_size) < 0)"
AFTER = "if (buf_size && set_frame_duration(ctx, avctx, buf, buf_size) < 0)"

def backport(source):
    if source.count(AFTER) == 1 and BEFORE not in source:
        return source
    if source.count(BEFORE) != 1:
        raise ValueError("Unsupported FFmpeg Opus parser source; review the upstream fix before building.")
    return source.replace(BEFORE, AFTER, 1)

if __name__ == "__main__":
    path = Path(sys.argv[1]) / "libavcodec/opus/parser.c"
    source = path.read_text()
    updated = backport(source)
    if updated != source:
        path.write_text(updated)
