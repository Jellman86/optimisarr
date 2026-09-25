#!/usr/bin/env python3
"""Launch isolated Optimisarr acceptance runs and retain reproducible evidence."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import time

from acceptance.core import Api, Blocked, Report, command, require, save
from acceptance.media import Tools
from acceptance.runner import Harness
from acceptance.workers import Workers

def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def strict_worker_verification_for_run(tier: str, server_verification: bool) -> bool:
    return tier == "fleet" and not server_verification


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="New, empty run directory (never an existing library)")
    runtime = parser.add_mutually_exclusive_group(required=True)
    runtime.add_argument("--image", help="Exact container image tag/digest to test")
    runtime.add_argument("--native", type=Path, help="Built Optimisarr.Api.dll for local acceptance")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    parser.add_argument("--vmaf", help="Independent reference FFmpeg with libvmaf (native mode)")
    verification = parser.add_mutually_exclusive_group()
    verification.add_argument("--sidecar-verification", action="store_true", help="Require complete sidecar verification (already the fleet default)")
    verification.add_argument("--server-verification", action="store_true", help="Explicitly test the legacy server-verification mode")
    parser.add_argument("--tier", choices=("smoke", "fleet"), default="smoke")
    parser.add_argument("--corpus", type=Path, help="Checksum-locked corpus.json produced by acceptance_corpus.py")
    parser.add_argument("--expected-worker", action="append", default=[])
    parser.add_argument("--worker-command", help='JSON argument array for a local disposable worker, e.g. ["/path/AcceptanceWorker"]')
    parser.add_argument("--worker-encoder", action="append", default=[], help="Limit disposable worker discovery to these encoders")
    parser.add_argument("--local-encoder", action="append", default=[], help="Limit local encoding matrix (otherwise every available encoder)")
    parser.add_argument("--fixture-variant", action="append", choices=("sdr", "vfr", "offset", "ten-bit"))
    parser.add_argument("--soak-cycles", type=int, default=0, help="Repeat encode/verify/rollback on every selected worker on the same server")
    parser.add_argument("--fixture-seconds", type=int, default=8, help="Generated source duration, 8 to 600 seconds")
    parser.add_argument("--pairing-wait", type=int, default=0, help="Seconds to allow TEST sidecars to pair before starting")
    parser.add_argument("--port", type=int, help="Fixed port for test workers; defaults to a free loopback port")
    parser.add_argument("--listen", default="127.0.0.1", help="Use a reachable host interface only for isolated worker acceptance")
    parser.add_argument("--device", action="append", default=[], help="Docker device mapping, e.g. /dev/dri:/dev/dri")
    parser.add_argument("--gpus", help="Docker GPU request, e.g. all")
    parser.add_argument("--timeout", type=int, default=900, help="Per-job deadline in seconds")
    args = parser.parse_args()
    if not 8 <= args.fixture_seconds <= 600 or not 0 <= args.soak_cycles <= 1000:
        parser.error("Fixture duration must be 8–600 seconds and soak cycles 0–1000")
    if args.sidecar_verification and args.tier != "fleet":
        parser.error("--sidecar-verification requires --tier fleet")
    if args.server_verification and args.tier != "fleet":
        parser.error("--server-verification requires --tier fleet")
    if args.expected_worker and args.tier != "fleet":
        parser.error("--expected-worker requires --tier fleet")
    if args.worker_command and args.tier != "fleet":
        parser.error("--worker-command requires --tier fleet")
    if args.corpus and not args.corpus.resolve().is_relative_to(args.root.resolve()):
        # Corpus imports are copied below before starting the server, never mounted from arbitrary paths.
        require(args.corpus.is_file(), "Corpus manifest does not exist")
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=False)
    for name in ("config", "data", "work", "trash", "fixtures"):
        (root / name).mkdir()
    report = Report(root / "report")
    token = secrets.token_hex(32)
    port = args.port or free_port()
    url = f"http://127.0.0.1:{port}"
    api = Api(url, token)
    process, container, log, workers = None, None, None, None
    container_started = False
    try:
        if not shutil.which("docker" if args.image else "dotnet"):
            raise Blocked("Docker CLI is unavailable" if args.image else "The .NET runtime is unavailable")
        env = {**os.environ, "ASPNETCORE_URLS": f"http://{args.listen}:{port}",
               "OPTIMISARR_ADMIN_TOKEN": token, "OPTIMISARR_CONFIG_DIR": str(root / "config"),
               "OPTIMISARR_WORK_DIR": str(root / "work"), "OPTIMISARR_TRASH_DIR": str(root / "trash"),
               "OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS": "true",
               "OPTIMISARR_FFMPEG": args.ffmpeg, "OPTIMISARR_FFPROBE": args.ffprobe,
               "OPTIMISARR_FFMPEG_VMAF": args.vmaf or args.ffmpeg}
        if args.image:
            container = "optimisarr-acceptance-" + secrets.token_hex(5)
            argv = ["docker", "run", "-d", "--name", container, "-p", f"{args.listen}:{port}:8787",
                    "-v", f"{root}:/acceptance", "-e", "OPTIMISARR_ADMIN_TOKEN",
                    "-e", "OPTIMISARR_CONFIG_DIR=/acceptance/config",
                    "-e", "OPTIMISARR_WORK_DIR=/acceptance/work", "-e", "OPTIMISARR_TRASH_DIR=/acceptance/trash",
                    "-e", "OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS=true",
                    "-e", f"PUID={os.getuid()}", "-e", f"PGID={os.getgid()}"]
            for device in args.device:
                argv += ["--device", device]
            if args.gpus:
                argv += ["--gpus", args.gpus]
            # Pass secrets through environment, never command arguments or report JSON.
            subprocess.run(argv + [args.image], env=env, check=True, capture_output=True, timeout=120)
            container_started = True
            tools = Tools("/usr/lib/jellyfin-ffmpeg/ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffprobe",
                          container=container, root=root, vmaf="/usr/local/lib/optimisarr/ffmpeg-vmaf")
            save(root / "image.json", json.loads(command(["docker", "image", "inspect", args.image,
                "--format", "{{json .Id}}"])))
        else:
            log = (root / "server.log").open("w")
            process = subprocess.Popen(["dotnet", str(args.native.resolve())], env=env,
                                       stdout=log, stderr=subprocess.STDOUT, cwd=root, start_new_session=True)
            tools = Tools(args.ffmpeg, args.ffprobe, vmaf=args.vmaf)
        deadline = time.monotonic() + 120
        while True:
            try:
                api.request("/api/ready")
                break
            except (OSError, RuntimeError):
                if time.monotonic() > deadline or (process and process.poll() is not None):
                    raise Blocked("Test server did not become ready; inspect server.log")
                time.sleep(.5)
        if args.pairing_wait:
            settings = api.request("/api/settings")
            settings["remoteWorkersEnabled"] = True
            api.request("/api/settings", "PUT", settings)
            print(f"Isolated test server port: {port}. Pair test sidecars only. Waiting {args.pairing_wait}s.", flush=True)
            # PIN expires; reissue periodically during the bounded pairing window.
            deadline = time.monotonic() + args.pairing_wait
            while time.monotonic() < deadline:
                pin = api.post("/api/workers/pairing-code")
                print(f"Test pairing PIN: {pin['code']} (do not put production sidecars on this instance)", flush=True)
                for _ in range(min(60, max(1, int(deadline - time.monotonic())))):
                    time.sleep(1)
        if args.worker_command:
            argv = json.loads(args.worker_command)
            require(isinstance(argv, list) and argv and all(isinstance(x, str) for x in argv), "Worker command must be a JSON argument array")
            workers = Workers(api, root, url, args.ffmpeg, args.ffprobe)
            workers.start(argv, args.worker_encoder)
        corpus = args.corpus
        if corpus:
            from acceptance.corpus import import_corpus
            corpus = import_corpus(corpus, root / "corpus")
        def restart():
            nonlocal process
            if container:
                command(["docker", "restart", "--time", "0", container])
            else:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=15)
                process = subprocess.Popen(["dotnet", str(args.native.resolve())], env=env,
                    stdout=log, stderr=subprocess.STDOUT, cwd=root, start_new_session=True)
            ready_by = time.monotonic() + 120
            while time.monotonic() < ready_by:
                try:
                    api.request("/api/ready")
                    return
                except (OSError, RuntimeError):
                    time.sleep(.5)
            raise Blocked("Owned test server did not recover after abrupt restart")

        harness = Harness(api, tools, root, report, timeout=args.timeout, restart=restart)
        return harness.run(tier=args.tier,
                           strict_worker_verification=strict_worker_verification_for_run(args.tier, args.server_verification),
                           corpus=corpus, expected_workers=args.expected_worker,
                           local_encoders=args.local_encoder, variants=args.fixture_variant,
                           soak_cycles=args.soak_cycles, fixture_seconds=args.fixture_seconds)
    except KeyboardInterrupt:
        report.case("interrupted", lambda: (_ for _ in ()).throw(Blocked("Run interrupted; isolated server stopped")))
        return 130
    except Exception as exc:
        report.case("harness", lambda: (_ for _ in ()).throw(exc))
        return report.exit_code
    finally:
        if workers:
            workers.stop()
        if process:
            os.killpg(process.pid, signal.SIGTERM) if process.poll() is None else None
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
        if log:
            log.close()
        if container_started:
            try:
                (root / "server.log").write_text(command(["docker", "logs", container], include_stderr=True))
            finally:
                subprocess.run(["docker", "rm", "-f", container], capture_output=True, timeout=30)
        report.write()
        print(f"Report: {report.root / 'index.html'}", flush=True)


if __name__ == "__main__":
    sys.exit(main())
