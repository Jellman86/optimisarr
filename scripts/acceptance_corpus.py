#!/usr/bin/env python3
"""Download pinned CC-BY films and create attributed, checksum-locked excerpts."""
import argparse
from pathlib import Path

from acceptance.corpus import prepare
from acceptance.media import Tools

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--root", required=True, type=Path)
parser.add_argument("--source", action="append", choices=("bunny", "tears"))
parser.add_argument("--seconds", type=int, default=12)
parser.add_argument("--ffmpeg", default="ffmpeg")
parser.add_argument("--ffprobe", default="ffprobe")

if __name__ == "__main__":
    args = parser.parse_args()
    if not 2 <= args.seconds <= 600:
        parser.error("--seconds must be between 2 and 600")
    print(prepare(Tools(args.ffmpeg, args.ffprobe), args.root, args.source or ["bunny", "tears"], args.seconds))
