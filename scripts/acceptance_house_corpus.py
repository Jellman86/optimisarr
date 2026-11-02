#!/usr/bin/env python3
"""Cut checksum-locked private regression clips beneath a new operator-owned root."""
import argparse
import json
from pathlib import Path

from acceptance.house_corpus import prepare_house
from acceptance.media import Tools


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--entries", type=Path, required=True, help="Private JSON list of source/start/seconds/reason/expected entries")
    parser.add_argument("--root", type=Path, required=True, help="New private directory outside the repository")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    args = parser.parse_args()
    print(prepare_house(Tools(args.ffmpeg, args.ffprobe), args.root, json.loads(args.entries.read_text())))


if __name__ == "__main__":
    main()
