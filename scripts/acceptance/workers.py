"""Launch disposable sidecars, one proved encoder per identity, without touching installed apps."""
import json
import os
from pathlib import Path
import signal
import subprocess
import time

from .core import Blocked, require


class Workers:
    def __init__(self, api, root, server, ffmpeg, ffprobe):
        self.api, self.root, self.server = api, Path(root), server
        self.env = {**os.environ, "OPTIMISARR_FFMPEG": ffmpeg, "OPTIMISARR_FFPROBE": ffprobe,
                    "OPTIMISARR_ACCEPTANCE_SERVER": server,
                    "OPTIMISARR_ACCEPTANCE_SCRATCH": str(self.root / "discovery")}
        self.processes = []

    def start(self, argv, encoders=()):
        discovery = subprocess.run(argv + ["--discover"], env=self.env, capture_output=True,
                                   text=True, timeout=120, check=True)
        capabilities = json.loads(discovery.stdout)
        proved = capabilities["videoEncoders"]
        require(bool(proved), "Worker proved no encoders")
        for encoder in encoders or proved:
            if encoder not in proved:
                raise Blocked(f"Requested worker encoder {encoder} did not pass its capability probe")
            settings = self.api.request("/api/settings")
            settings["remoteWorkersEnabled"] = True
            self.api.request("/api/settings", "PUT", settings)
            pin = self.api.post("/api/workers/pairing-code")["code"]
            name = f"acceptance-{capabilities['operatingSystem']}-{encoder}"
            log = (self.root / (name + ".log")).open("w")
            env = {**self.env, "OPTIMISARR_ACCEPTANCE_PIN": pin, "OPTIMISARR_ACCEPTANCE_NAME": name,
                   "OPTIMISARR_ACCEPTANCE_ENCODER": encoder,
                   "OPTIMISARR_ACCEPTANCE_SCRATCH": str(self.root / name)}
            process = subprocess.Popen(argv, env=env, stdout=log, stderr=subprocess.STDOUT,
                                       start_new_session=os.name != "nt")
            self.processes.append((process, log))
            deadline = time.monotonic() + 120
            while True:
                workers = self.api.request("/api/workers")
                if any(w["name"] == name and w["online"] for w in workers):
                    break
                if process.poll() is not None or time.monotonic() >= deadline:
                    raise Blocked(f"Disposable worker {name} failed to pair; inspect its log")
                time.sleep(.5)
        return capabilities

    def stop(self):
        for process, log in self.processes:
            if process.poll() is None:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=30)
                else:
                    os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
            log.close()
