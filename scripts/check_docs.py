#!/usr/bin/env python3
"""Fail CI when a local Markdown link points to a missing file."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
LINK = re.compile(r"\[[^]]*\]\(([^)#]+)(?:#[^)]*)?\)")
missing: list[str] = []

for source in [ROOT / "README.md", *ROOT.glob("docs/**/*.md")]:
    for target in LINK.findall(source.read_text(encoding="utf-8-sig")):
        if "://" in target or target.startswith("mailto:"):
            continue
        if not (source.parent / target).resolve().exists():
            missing.append(f"{source.relative_to(ROOT)} -> {target}")

if missing:
    print("Broken local documentation links:", *missing, sep="\n  ")
    sys.exit(1)

print("Local documentation links are valid.")
