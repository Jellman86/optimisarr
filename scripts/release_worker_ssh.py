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
import zlib
from urllib.parse import urlsplit

from acceptance.core import require

TOOL_KEYS = {"OPTIMISARR_FFMPEG", "OPTIMISARR_FFPROBE", "OPTIMISARR_FFMPEG_VMAF",
             "OPTIMISARR_NATIVE_HOST", "OPTIMISARR_AUDIO_QUALITY", "OPTIMISARR_IMAGE_QUALITY", "OPTIMISARR_EXIFTOOL"}

# Windows OpenSSH runs every session inside a job object, and the sidecar rightly refuses native
# memory inside a job whose limits it cannot prove. The supervisor therefore starts this guardian
# outside the job. Its Windows debug lifetime owns every worker descendant, including when
# the guardian itself crashes. This never changes the installed service or memory policy.
WINDOWS_GUARDIAN = r'''
import ctypes, os, signal, subprocess, sys, threading

def windows_worker():
    from ctypes import wintypes as w
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    class ExceptionRecord(ctypes.Structure):
        _fields_ = [("code", w.DWORD), ("flags", w.DWORD), ("record", w.LPVOID),
                    ("address", w.LPVOID), ("count", w.DWORD), ("parameters", ctypes.c_size_t * 15)]
    class ExceptionInfo(ctypes.Structure):
        _fields_ = [("record", ExceptionRecord), ("first_chance", w.DWORD)]
    class ProcessInfo(ctypes.Structure):
        _fields_ = [("file", w.HANDLE), ("process", w.HANDLE), ("thread", w.HANDLE),
                    ("base", w.LPVOID), ("debug_offset", w.DWORD), ("debug_size", w.DWORD),
                    ("tls", w.LPVOID), ("start", w.LPVOID), ("image", w.LPVOID), ("unicode", w.WORD)]
    class DebugInfo(ctypes.Union):
        _fields_ = [("exception", ExceptionInfo), ("process", ProcessInfo),
                    ("file", w.HANDLE), ("exit_code", w.DWORD)]
    class DebugEvent(ctypes.Structure):
        _fields_ = [("code", w.DWORD), ("pid", w.DWORD), ("tid", w.DWORD), ("info", DebugInfo)]
    kernel.WaitForDebugEvent.argtypes = [ctypes.POINTER(DebugEvent), w.DWORD]
    kernel.WaitForDebugEvent.restype = w.BOOL
    kernel.ContinueDebugEvent.argtypes = [w.DWORD, w.DWORD, w.DWORD]
    kernel.ContinueDebugEvent.restype = w.BOOL
    kernel.DebugSetProcessKillOnExit.argtypes = [w.BOOL]
    kernel.DebugSetProcessKillOnExit.restype = w.BOOL
    kernel.CloseHandle.argtypes = [w.HANDLE]
    kernel.CloseHandle.restype = w.BOOL
    # DEBUG_PROCESS includes descendants without putting them in an unmeasurable job.
    # Windows kills every debuggee if this guardian exits, including a crash or task kill.
    child = subprocess.Popen(sys.argv[1:], stdin=subprocess.DEVNULL, creationflags=0x00000001)
    if not kernel.DebugSetProcessKillOnExit(True):
        os._exit(1)
    def stop_when_released():
        try:
            sys.stdin.buffer.read()
        finally:
            os._exit(1)
    threading.Thread(target=stop_when_released, daemon=True).start()
    initial_breakpoints = set()
    while True:
        event = DebugEvent()
        if not kernel.WaitForDebugEvent(ctypes.byref(event), 1000):
            if ctypes.get_last_error() in (121, 258):
                continue
            os._exit(1)
        status = 0x00010002  # DBG_CONTINUE
        if event.code == 1:
            # Handle only the loader's first breakpoint; pass application exceptions back
            # to their normal handlers, including the CLR's managed exception machinery.
            if event.info.exception.record.code == 0x80000003 and event.pid not in initial_breakpoints:
                initial_breakpoints.add(event.pid)
            else:
                status = 0x80010001  # DBG_EXCEPTION_NOT_HANDLED
        elif event.code in (3, 6):
            # The debugging subsystem closes process/thread handles at exit. Image/DLL
            # file handles belong to the debugger and must be closed explicitly.
            handle = event.info.process.file if event.code == 3 else event.info.file
            if handle:
                kernel.CloseHandle(handle)
        if event.code == 5:
            initial_breakpoints.discard(event.pid)
        root_exited = event.code == 5 and event.pid == child.pid
        code = event.info.exit_code if root_exited else None
        if not kernel.ContinueDebugEvent(event.pid, event.tid, status):
            os._exit(1)
        if root_exited:
            # Even a successful root may have left a tool running. Kernel-owned debug
            # membership retires those descendants when this guardian exits.
            return ctypes.c_int32(code).value

def posix_worker():
    child = subprocess.Popen(sys.argv[1:], stdin=subprocess.DEVNULL, start_new_session=True)
    def stop():
        try:
            os.killpg(child.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    def stop_when_released():
        sys.stdin.buffer.read()
        stop()
    threading.Thread(target=stop_when_released, daemon=True).start()
    try:
        return child.wait()
    finally:
        stop()

try:
    code = windows_worker() if os.name == "nt" else posix_worker()
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(code if code >= 0 else 1)
except BaseException:
    # A broken event loop must terminate its debuggees, never detach them.
    os._exit(1)
'''

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
process = None
if os.name == "nt":
    try:
        process = subprocess.Popen([sys.executable, "-c", WINDOWS_GUARDIAN, *request["command"]], env=env,
            stdin=subprocess.PIPE, creationflags=0x01000000)
    except OSError:
        sys.stderr.write("Windows acceptance cannot leave the SSH job; no worker was started.\n")
        sys.stderr.flush()
        os._exit(2)
if process is None:
    process = subprocess.Popen(request["command"], env=env, stdin=subprocess.DEVNULL,
        start_new_session=os.name != "nt")
started = time.monotonic()
try:
    while process.poll() is None:
        if stop.wait(.5) or time.monotonic() - last[0] > 45 or time.monotonic() - started > 14400:
            break
finally:
    if os.name == "nt":
        # EOF asks the kernel-backed guardian to retire every debuggee. Killing only
        # this retained guardian handle is also safe if its control thread is stuck.
        if process.stdin is not None:
            try:
                process.stdin.close()
            except OSError:
                pass
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    elif process.poll() is None:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
# The control thread is still blocked reading stdin; normal interpreter shutdown can abort on
# that lock and replace the child's exit code. Flush and leave without finalization.
sys.stdout.flush()
sys.stderr.flush()
os._exit(process.returncode if process.returncode >= 0 else 1)
'''.replace("WINDOWS_GUARDIAN, *", repr(WINDOWS_GUARDIAN) + ", *", 1)


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
    encoded = base64.b64encode(zlib.compress(REMOTE_SUPERVISOR.encode())).decode()
    remote = python + ' -u -c "exec(__import__(\'zlib\').decompress(__import__(\'base64\').b64decode(\'' + encoded + '\')))"'
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
        def send(line):
            # A remote that already finished closes its end; that is its exit, not a lost launcher.
            try:
                process.stdin.write(line)
                process.stdin.flush()
                return True
            except (BrokenPipeError, OSError):
                return False
        send(json.dumps(payload) + "\n")
        # A harness killed outright never runs its cleanup. Re-parenting means it is gone, so stop
        # sending keepalives and close the session; the remote supervisor then stops its worker.
        parent = os.getppid()
        while process.poll() is None and not stopped.wait(interval):
            if os.getppid() != parent or not send("alive\n"):
                break
        try:
            process.stdin.close()
        except (BrokenPipeError, OSError):
            pass
        code = process.wait(timeout=7)
        for thread in threads:
            thread.join(timeout=1)
        return code
    finally:
        if process and process.poll() is None:
            try:
                process.stdin.close()
            except (BrokenPipeError, OSError):
                pass
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
