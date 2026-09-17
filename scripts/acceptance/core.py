"""Fail-closed measurement, process execution, filesystem boundaries and reports."""
from __future__ import annotations

import hashlib
import html
import json
import math
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET


class Blocked(RuntimeError):
    """A required facility was unavailable; never a passing test."""


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inside(root, path):
    root, path = Path(root).resolve(), Path(path).resolve()
    require(path != root and path.is_relative_to(root), f"Path escapes test root: {path}")
    return path


def save(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")
    temporary.replace(path)


def command(argv, *, cwd=None, timeout=600, include_stderr=False):
    # Argument arrays only. subprocess.run kills and waits for the child on timeout.
    result = subprocess.run([str(arg) for arg in argv], cwd=cwd, capture_output=True,
                            text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f"{Path(argv[0]).name} exited {result.returncode}: {result.stderr[-8000:]}")
    return result.stdout + (result.stderr if include_stderr else "")


def statistics(document):
    """Recompute every aggregate from raw frames; do not trust pooled summaries.

    libvmaf uses an offset harmonic mean: N / sum(1 / (score + 1)) - 1.
    The fifth percentile uses linear interpolation, matching the application's contract.
    """
    frames = document.get("frames", [])
    require(bool(frames), "VMAF returned no frames")
    scores = [frame["metrics"]["vmaf"] for frame in frames]
    require(all(isinstance(x, (int, float)) and not isinstance(x, bool)
                and math.isfinite(x) and 0 <= x <= 100 for x in scores),
            "VMAF contains missing, non-finite or out-of-range scores")
    require([frame["frameNum"] for frame in frames] == list(range(len(frames))),
            "VMAF frame sequence is incomplete or duplicated")
    ordered = sorted(scores)
    rank = (len(scores) - 1) * .05
    low, high = math.floor(rank), math.ceil(rank)
    return {"mean": sum(scores) / len(scores),
            "harmonic": len(scores) / sum(1 / (x + 1) for x in scores) - 1,
            "p5": ordered[low] + (ordered[high] - ordered[low]) * (rank - low),
            "minimum": ordered[0], "frames": len(scores)}


def quality_failures(scores, thresholds):
    return [key for key in ("harmonic", "p5", "minimum")
            if not math.isfinite(scores[key]) or scores[key] < thresholds[key]]


class Api:
    def __init__(self, url, token="", timeout=120):
        self.url, self.token, self.timeout = url.rstrip("/"), token, timeout

    def request(self, path, method="GET", body=None):
        data = None if body is None else json.dumps(body).encode()
        headers = {"Content-Type": "application/json"}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        request = urllib.request.Request(self.url + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                raw = response.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as exc:
            # Never retain headers or credentials in artifacts.
            raise RuntimeError(f"{method} {path}: HTTP {exc.code}: {exc.read().decode()[:1500]}") from None

    def post(self, path, body=None):
        return self.request(path, "POST", body)


class Report:
    def __init__(self, root):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=False)
        self.results = []
        self.environment = {}

    def case(self, name, action):
        start = time.monotonic()
        row = {"name": name, "status": "passed"}
        try:
            row["evidence"] = action()
        except Blocked as exc:
            row.update(status="blocked", detail=str(exc))
        except Exception as exc:
            row.update(status="failed", detail=f"{type(exc).__name__}: {exc}")
        row["seconds"] = round(time.monotonic() - start, 3)
        self.results.append(row)
        self.write()
        print(f"{row['status'].upper():7} {name}: {row.get('detail', '')}", flush=True)
        return row

    def write(self):
        save(self.root / "report.json", {"environment": self.environment, "results": self.results})
        failures = sum(r["status"] == "failed" for r in self.results)
        blocked = sum(r["status"] == "blocked" for r in self.results)
        suite = ET.Element("testsuite", name="Optimisarr media acceptance", tests=str(len(self.results)),
                           failures=str(failures), errors=str(blocked))
        for row in self.results:
            case = ET.SubElement(suite, "testcase", name=row["name"], time=str(row["seconds"]))
            if row["status"] != "passed":
                ET.SubElement(case, "error" if row["status"] == "blocked" else "failure",
                              message=row.get("detail", "")).text = row.get("detail", "")
        ET.ElementTree(suite).write(self.root / "junit.xml", encoding="unicode", xml_declaration=True)
        rows = "".join(f"<tr><td>{html.escape(r['name'])}</td><td>{r['status']}</td>"
                       f"<td>{r['seconds']}</td><td><pre>{html.escape(json.dumps(r.get('evidence', r.get('detail')), indent=2))}</pre></td></tr>"
                       for r in self.results)
        (self.root / "index.html").write_text("<!doctype html><meta charset=utf-8><title>Optimisarr acceptance</title>"
            "<style>body{font:15px system-ui;background:#101827;color:#dee9f2;padding:2rem}"
            "table{border-collapse:collapse;width:100%}td,th{padding:1rem;text-align:left;border-bottom:1px solid #405066}"
            "pre{white-space:pre-wrap;overflow-wrap:anywhere}a{color:#60d5ee}</style>"
            f"<h1>Media acceptance</h1><p>{len(self.results)} cases · {failures} failures · {blocked} blocked</p>"
            "<p><a href=report.json>Full evidence</a> · <a href=junit.xml>JUnit</a></p>"
            "<table><tr><th>Case</th><th>Result</th><th>Seconds</th><th>Evidence</th></tr>" + rows + "</table>")

    @property
    def exit_code(self):
        return 1 if any(r["status"] == "failed" for r in self.results) else (
            2 if any(r["status"] == "blocked" for r in self.results) else 0)
