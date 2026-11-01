#!/usr/bin/env python3
"""SSH transport for disposable acceptance launchers; secrets use stdin, never arguments."""
import argparse
import base64
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import threading
import uuid
from urllib.parse import urlsplit

from acceptance.core import require

TOOL_KEYS = {"OPTIMISARR_FFMPEG", "OPTIMISARR_FFPROBE", "OPTIMISARR_FFMPEG_VMAF",
             "OPTIMISARR_NATIVE_HOST", "OPTIMISARR_AUDIO_QUALITY", "OPTIMISARR_IMAGE_QUALITY", "OPTIMISARR_EXIFTOOL"}

# A remote Python supervisor owns only its child tree. EOF, missing keepalives or the
# lifetime bound stop that tree even if the local harness or SSH connection disappears.
REMOTE_SUPERVISOR = r'''
import json, os, signal, subprocess, sys, threading, time
request = json.loads(sys.stdin.buffer.readline(262145))
stop = threading.Event()
last = [time.monotonic()]
def control():
    for line in sys.stdin.buffer:
        if line.strip() == b"stop":
            break
        last[0] = time.monotonic()
    stop.set()
threading.Thread(target=control, daemon=True).start()
env = {k: v for k, v in os.environ.items() if not k.startswith("OPTIMISARR_")}
env.update(request["env"])
process = subprocess.Popen(request["command"], env=env, stdin=subprocess.DEVNULL,
    start_new_session=os.name != "nt")
started = time.monotonic()
try:
    while process.poll() is None:
        if stop.wait(.5) or time.monotonic() - last[0] > 45 or time.monotonic() - started > 14400:
            break
finally:
    if process.poll() is None:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=10)
        else:
            os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            if os.name != "nt":
                os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
sys.exit(process.returncode)
'''


def ssh_request(worker, env, *, discover):
    spec = worker["ssh"]
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", spec["host"]), "Use an SSH host alias without shell syntax")
    python = spec.get("python", "python3")
    require(re.fullmatch(r"[A-Za-z0-9_./:\\-]+", python), "Remote Python must be a simple executable path without spaces")
    command = spec.get("command")
    require(isinstance(command, list) and command and all(isinstance(x, str) and x for x in command), "Remote command must be an argument array")
    tools = spec.get("tools", {})
    require(isinstance(tools, dict) and set(tools) <= TOOL_KEYS and all(isinstance(v, str) for v in tools.values()), "Only tool paths may be configured in remote environment")
    server = urlsplit(env["OPTIMISARR_ACCEPTANCE_SERVER"])
    template = spec["serverUrl"]
    require("{port}" in template and not urlsplit(template.replace("{port}", "12345")).username,
            "Remote serverUrl needs {port} and must not contain credentials")
    remote_server = template.replace("{port}", str(server.port))
    require(urlsplit(remote_server).scheme in ("http", "https") and urlsplit(remote_server).port == server.port,
            "Remote serverUrl must point at the owned server port")
    scratch = spec["scratchRoot"].rstrip("/\\") + "/release-" + uuid.uuid4().hex
    forwarded = {key: env[key] for key in ("OPTIMISARR_ACCEPTANCE_NAME", "OPTIMISARR_ACCEPTANCE_ENCODER") if key in env}
    if not discover:
        forwarded["OPTIMISARR_ACCEPTANCE_PIN"] = env["OPTIMISARR_ACCEPTANCE_PIN"]
    forwarded.update(tools)
    forwarded.update(OPTIMISARR_ACCEPTANCE_SERVER=remote_server, OPTIMISARR_ACCEPTANCE_SCRATCH=scratch,
        OPTIMISARR_NATIVE_ACCOUNTING_ROOT=scratch + "-accounting",
        OPTIMISARR_NATIVE_ACCOUNTING_SERVICE="uk.optimisarr.acceptance." + uuid.uuid4().hex)
    encoded = base64.b64encode(REMOTE_SUPERVISOR.encode()).decode()
    remote = python + ' -u -c "exec(__import__(\'base64\').b64decode(\'' + encoded + '\'))"'
    argv = ["ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=10",
            "-o", "ServerAliveCountMax=3", "--", spec["host"], remote]
    return argv, {"command": command + (["--discover"] if discover else []), "env": forwarded}


def relay(source, destination, secrets):
    for line in source:
        for secret in secrets:
            if secret:
                line = line.replace(secret, "[redacted]")
        destination.write(line)
        destination.flush()


def run_ssh(argv, payload, *, interval=10):
    stopped = threading.Event()
    previous = {}
    for sig in (signal.SIGTERM, signal.SIGINT):
        previous[sig] = signal.signal(sig, lambda *_: stopped.set())
    process = None
    try:
        process = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, start_new_session=os.name != "nt")
        secrets = [payload["env"].get("OPTIMISARR_ACCEPTANCE_PIN", "")]
        threads = [threading.Thread(target=relay, args=(source, destination, secrets), daemon=True)
                   for source, destination in ((process.stdout, sys.stdout), (process.stderr, sys.stderr))]
        for thread in threads:
            thread.start()
        process.stdin.write(json.dumps(payload) + "\n")
        process.stdin.flush()
        while process.poll() is None and not stopped.wait(interval):
            process.stdin.write("alive\n")
            process.stdin.flush()
        process.stdin.close()
        code = process.wait(timeout=7)
        for thread in threads:
            thread.join(timeout=1)
        return code
    finally:
        if process and process.poll() is None:
            process.stdin.close()
            try:
                process.wait(timeout=7)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)
        for sig, handler in previous.items():
            signal.signal(sig, handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--worker", required=True)
    parser.add_argument("--discover", action="store_true")
    args = parser.parse_args()
    try:
        worker = json.loads(args.config.read_text())["workers"][args.worker]
        argv, payload = ssh_request(worker, os.environ, discover=args.discover)
        return run_ssh(argv, payload)
    except Exception as exc:
        # Config, payloads and process exceptions can contain private data. Full worker stderr
        # is already relayed with the pairing PIN removed; never dump the request itself.
        print(f"Disposable SSH launcher unavailable ({type(exc).__name__})", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
