#!/usr/bin/env python3
"""Generate Optimisarr's runtime OpenAPI document and fail on drift."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
from urllib.error import URLError
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
SPEC = ROOT / "docs" / "openapi.json"


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def fetch_json(url: str, timeout_seconds: int = 90) -> dict:
    deadline = time.monotonic() + timeout_seconds
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            with urlopen(url, timeout=2) as response:
                return json.loads(response.read().decode("utf-8"))
        except (ConnectionError, TimeoutError, URLError, json.JSONDecodeError) as exc:
            last_error = exc
            time.sleep(0.25)
    raise RuntimeError(f"Timed out waiting for {url}: {last_error}")


def generate(configuration: str, no_build: bool) -> dict:
    port = free_port()
    with tempfile.TemporaryDirectory(prefix="optimisarr-openapi-") as temp:
        temp_path = Path(temp)
        for name in ("config", "work", "trash"):
            (temp_path / name).mkdir()

        command = [
            "dotnet",
            "run",
            "--no-launch-profile",
            "--project",
            str(ROOT / "src" / "Optimisarr.Api" / "Optimisarr.Api.csproj"),
            "--configuration",
            configuration,
        ]
        if no_build:
            command.append("--no-build")

        env = os.environ.copy()
        env.update(
            {
                "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
                "OPTIMISARR_CONFIG_DIR": str(temp_path / "config"),
            }
        )

        process = subprocess.Popen(
            command,
            cwd=ROOT,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )

        error: Exception | None = None
        document: dict | None = None
        try:
            document = fetch_json(f"http://127.0.0.1:{port}/openapi/v1.json")
        except Exception as exc:
            error = exc
        finally:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)

        output = ""
        if process.stdout is not None:
            output = process.stdout.read()
        if error is not None:
            raise RuntimeError(f"OpenAPI generation failed: {error}\n{output}") from None
        if document is None:
            raise RuntimeError(f"OpenAPI generation failed without a document.\n{output}")

    return document


def normalized(document: dict) -> str:
    document.pop("servers", None)
    return json.dumps(document, indent=2, sort_keys=True) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--update", action="store_true", help="write docs/openapi.json")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()

    try:
        rendered = normalized(generate(args.configuration, args.no_build))
    except Exception as exc:
        print(exc, file=sys.stderr)
        return 1

    if args.update:
        SPEC.write_text(rendered, encoding="utf-8")
        print(f"Updated {SPEC.relative_to(ROOT)}")
        return 0

    if not SPEC.exists():
        print(f"Missing {SPEC.relative_to(ROOT)}. Run scripts/check_openapi.py --update.", file=sys.stderr)
        return 1

    existing = SPEC.read_text(encoding="utf-8")
    if existing != rendered:
        print(
            f"{SPEC.relative_to(ROOT)} is out of date. Run scripts/check_openapi.py --update.",
            file=sys.stderr,
        )
        return 1

    print("OpenAPI document is up to date.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
