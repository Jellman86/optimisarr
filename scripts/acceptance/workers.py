"""Launch disposable sidecars, one proved encoder per identity, without touching installed apps."""
import json
import os
from pathlib import Path
import signal
import socket
import re
import subprocess
import time

from .core import Blocked, command, require
from .monitor import LinuxObserver, local_files


class Workers:
    def __init__(self, api, root, server, ffmpeg, ffprobe, *, vmaf=None, require_ram=False, scratch_root=None, observe_local=True):
        self.api, self.root, self.server = api, Path(root), server
        self.scratch_root = Path(scratch_root) if scratch_root else self.root
        self.env = {**{k: v for k, v in os.environ.items() if not k.startswith("OPTIMISARR_")}, "OPTIMISARR_FFMPEG": ffmpeg, "OPTIMISARR_FFPROBE": ffprobe,
                    "OPTIMISARR_WEB_ENABLED": "false",
                    "OPTIMISARR_ACCEPTANCE_SERVER": server,
                    "OPTIMISARR_DIAGNOSTIC_DIR": str(self.root / "diagnostics"),
                    "OPTIMISARR_SERVER": server,
                    "OPTIMISARR_CONFIG_DIR": str(self.root / "discovery-config"),
                    "OPTIMISARR_SIDECAR_WORK": str(self.scratch_root / "discovery"),
                    "OPTIMISARR_ACCEPTANCE_SCRATCH": str(self.scratch_root / "discovery")}
        if vmaf:
            self.env["OPTIMISARR_FFMPEG_VMAF"] = vmaf
        self.processes = []
        self.observers = {}
        self.require_ram = require_ram
        self.observe_local = observe_local

    def discover(self, argv):
        try:
            discovery = subprocess.run(argv + ["--discover"], env=self.env, capture_output=True,
                                       text=True, timeout=120, check=True)
        except FileNotFoundError as exc:
            raise Blocked("Worker executable is unavailable") from exc
        capabilities = json.loads(discovery.stdout)
        if not capabilities["videoEncoders"]:
            raise Blocked("Worker proved no encoders")
        return capabilities

    def start(self, argv, encoders=(), *, report):
        result = report.case("worker-discovery", lambda: self.discover(argv))
        if result["status"] != "passed":
            return None
        capabilities = result["evidence"]
        proved = capabilities["videoEncoders"]
        for encoder in dict.fromkeys(encoders or proved):
            def start_encoder(encoder=encoder):
                if encoder not in proved:
                    raise Blocked(f"Requested worker encoder {encoder} did not pass its capability probe")
                return self.launch(argv, capabilities, encoder)
            report.case("worker-start-" + encoder, start_encoder)
        return capabilities

    def launch(self, argv, capabilities, encoder):
        settings = self.api.request("/api/settings")
        settings["remoteWorkersEnabled"] = True
        self.api.request("/api/settings", "PUT", settings)
        pin = self.api.post("/api/workers/pairing-code")["code"]
        name = f"acceptance-{capabilities['operatingSystem']}-{encoder}"
        require(re.fullmatch(r"[a-zA-Z0-9_-]+", name), "Unsafe worker capability name")
        port = free_port() if capabilities["operatingSystem"] == "linux" and self.observe_local else None
        if self.require_ram and port is None:
            raise Blocked("RAM file observations currently require the Linux worker; use native platform RAM tests too")
        log = (self.root / (name + ".log")).open("w")
        env = {**self.env, "OPTIMISARR_ACCEPTANCE_PIN": pin, "OPTIMISARR_ACCEPTANCE_NAME": name,
               "OPTIMISARR_PAIRING_CODE": pin, "OPTIMISARR_WORKER_NAME": name,
               "OPTIMISARR_ENCODER": encoder,
               "OPTIMISARR_CONFIG_DIR": str(self.root / (name + "-config")),
               "OPTIMISARR_SIDECAR_WORK": str(self.scratch_root / name),
               "OPTIMISARR_DIAGNOSTIC_DIR": str(self.root / (name + "-diagnostics")),
               "OPTIMISARR_ACCEPTANCE_ENCODER": encoder,
               "OPTIMISARR_ACCEPTANCE_SCRATCH": str(self.scratch_root / name)}
        if port:
            env.update(OPTIMISARR_WEB_ENABLED="true", ASPNETCORE_URLS=f"http://127.0.0.1:{port}")
        try:
            process = subprocess.Popen(argv, env=env, stdout=log, stderr=subprocess.STDOUT,
                                       start_new_session=os.name != "nt")
        except Exception:
            log.close()
            raise
        self.processes.append((process, log))
        try:
            self.wait_online(name, lambda: process.poll() is None)
        except Exception:
            stop_process(process, log)
            raise
        if port:
            scratch = self.scratch_root / name
            self.observers[name] = LinuxObserver(f"http://127.0.0.1:{port}",
                lambda job_id: local_files(scratch, job_id),
                lambda: command(["stat", "-f", "-c", "%T", str(scratch)]).strip(), require_ram=self.require_ram)
        return {"name": name, "encoder": encoder, "online": True}

    def wait_online(self, name, running):
        deadline = time.monotonic() + 120
        while True:
            workers = self.api.request("/api/workers")
            if any(w["name"] == name and w["online"] for w in workers):
                return
            if not running() or time.monotonic() >= deadline:
                raise RuntimeError(f"Disposable worker {name} failed to pair; inspect its log")
            time.sleep(.5)

    def stop(self):
        failures = []
        for process, log in self.processes:
            try:
                stop_process(process, log)
            except Exception as exc:
                failures.append(str(exc))
        require(not failures, "Worker cleanup failed: " + "; ".join(failures))


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def stop_process(process, log):
    try:
        if process.poll() is None:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=30)
            else:
                os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                if os.name != "nt":
                    os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
    finally:
        log.close()
