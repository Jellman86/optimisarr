#!/usr/bin/env python3
"""Fail CI when a local Markdown link points to a missing file."""

from pathlib import Path
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
LINK = re.compile(r"\[[^]]*\]\(([^)#]+)(?:#[^)]*)?\)")
API_ROW = re.compile(r"^\|\s*`(GET|POST|PUT|DELETE)`\s*\|\s*`([^`]+)`\s*\|", re.MULTILINE)
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

openapi = json.loads((ROOT / "docs" / "openapi.json").read_text(encoding="utf-8"))
paths = openapi.get("paths", {})
missing_api: list[str] = []
api_doc = (ROOT / "docs" / "api.md").read_text(encoding="utf-8")
for method, endpoint in API_ROW.findall(api_doc):
    path = endpoint.split("?", 1)[0]
    operations = paths.get(path, {})
    if method.lower() not in operations:
        missing_api.append(f"{method} {endpoint}")

if missing_api:
    print("API reference entries missing from docs/openapi.json:", *missing_api, sep="\n  ")
    sys.exit(1)

print("Local documentation links are valid.")
print("API reference endpoints exist in docs/openapi.json.")
