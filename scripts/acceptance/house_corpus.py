"""Private, operator-owned regression clips; never acquire or publish library media."""
import math
from pathlib import Path
import shutil

from .core import require, save, sha256

HOUSE_PROPERTIES = {"soundtrack", "lossy-aac-window", "duration-eng-statistics", "stale-statistics",
    "mp4-track-title", "decoded-overs", "negative-opus-start", "missing-track-duration", "subtitle-heavy-claim",
    "eac3-surround", "opus-surround", "stereo-surround-mix"}


def validate_expected(expected):
    value = {"outcome": expected} if isinstance(expected, str) else expected
    require(isinstance(value, dict) and value.get("outcome") in ("passes", "blocked", "search-fallback"),
            "Expected outcome must be passes, blocked or search-fallback")
    if value["outcome"] == "blocked":
        require(isinstance(value.get("reason"), str) and value["reason"].strip(), "Blocked outcome needs a reason substring")
    return value


def assert_outcome(expected, status, error, verification):
    expected = validate_expected(expected)
    if expected["outcome"] == "blocked":
        quality = verification.get("soundtrackQuality") or {}
        # The job error names only the failed check; each track carries the specific refusal.
        track_reasons = [(track.get("report") or {}).get("unavailableReason") or "" for track in quality.get("tracks") or []]
        reasons = " ".join(str(value) for value in (error or "", quality.get("unavailableReason") or "", *track_reasons))
        require(status == "Failed" and expected["reason"].casefold() in reasons.casefold(),
                f"Expected blocked reason {expected['reason']!r}; got {status}: {reasons}")
        return
    require(status == "ReadyToReplace", f"Expected successful assessment; got {status}: {error}")
    require((verification.get("soundtrackQuality") or {}).get("gatePassed") is True,
            "House soundtrack gate did not pass")
    search = verification.get("soundtrackSearch") or {}
    tracks = search.get("tracks") or []
    decisions = [(track.get("result") or {}).get("decision") or {} for track in tracks]
    fallback = bool(search.get("unavailable")) or any(d.get("fellBack") is True for d in decisions)
    require(fallback == (expected["outcome"] == "search-fallback"), "Unexpected soundtrack search fallback outcome")
    if not fallback:
        require(tracks and all((track.get("result") or {}).get("probes") for track in tracks)
                and all(d.get("fellBack") is False for d in decisions), "Soundtrack search has no measured decisions")


def prepare_house(tools, root, entries):
    require(isinstance(entries, list) and entries, "House input must be a nonempty JSON list")
    root = Path(root).resolve()
    root.mkdir(parents=True, exist_ok=False, mode=0o700)
    clips = []
    for index, entry in enumerate(entries, 1):
        start, seconds = entry.get("start"), entry.get("seconds")
        require(all(isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)
                    for value in (start, seconds)) and start >= 0 and 0 < seconds <= 600,
                "House windows need finite start >= 0 and 0 < seconds <= 600")
        require(isinstance(entry.get("reason"), str) and entry["reason"].strip(), "Every clip needs its regression reason")
        expected = validate_expected(entry.get("expected"))
        property_name = entry.get("property", "soundtrack")
        require(property_name in HOUSE_PROPERTIES, "Choose a documented house corpus property without private identifiers")
        container = entry.get("targetContainer", "mkv")
        require(container in ("mkv", "mp4"), "House targetContainer must be mkv or mp4")
        source = Path(entry["source"]).expanduser().resolve(strict=True)
        require(source.is_file(), "House source must be a file")
        identifier = f"house-{index:03}"
        target = root / (identifier + source.suffix.lower())
        source_probe = tools.probe(source)
        cut = entry.get("cut", "stream-copy")
        require(cut in ("stream-copy", "whole-file"), "House cuts must use stream-copy or whole-file")
        if cut == "whole-file":
            duration = float(source_probe.get("format", {}).get("duration", "nan"))
            require(start == 0 and math.isfinite(duration) and 0 < duration <= seconds + .25,
                    "Whole-file import requires an already-short clip within the requested duration")
            shutil.copyfile(source, target)
        else:
            # Preserve streams and tags; do not silently transcode a failed cut. Keyframe pre-roll
            # and muxer rewrites remain visible in the retained probes for operator qualification.
            tools.encode(["-ss", str(start), "-i", tools.path(source), "-t", str(seconds),
                "-map", "0", "-map_metadata", "0", "-c", "copy", "-avoid_negative_ts", "disabled", tools.path(target)])
        probe = tools.probe(target)
        clips.append({"id": identifier, "path": target.name, "sha256": sha256(target),
            "reason": entry["reason"], "property": property_name, "expected": expected, "targetContainer": container,
            "source": {"private": True, "start": start, "seconds": seconds, "cut": cut},
            "sourceProbe": source_probe, "probe": probe})
    manifest = root / "corpus.json"
    save(manifest, {"version": 1, "kind": "house", "tools": tools.versions(), "clips": clips})
    return manifest
