"""Observe the real Linux worker, its working files and cleanup during owned jobs."""
import math
import time
from pathlib import Path

from .core import Api, Blocked, require


class LinuxObserver:
    def __init__(self, url, files, filesystem, *, require_ram=False):
        self.api = Api(url, timeout=5)
        self.files = files
        self.filesystem = filesystem
        self.require_ram = require_ram
        self.samples = {}

    def sample(self, job_id):
        status = self.api.request("/api/sidecar/status")
        job = next((j for j in status["jobs"] if j["jobId"] == job_id), None)
        files = self.files(job_id)
        sample = {"state": status["state"], "storage": status["storage"],
                  "metrics": status.get("metrics"), "job": job, "files": files}
        history = self.samples.setdefault(job_id, [])
        # Keep early transfer evidence and the most recent progress without unbounded reports.
        if len(history) >= 240:
            del history[120]
        history.append(sample)
        return sample

    def finish(self, job_id, *, completed=True):
        samples = self.samples.pop(job_id, [])
        filesystem = self.filesystem()
        if self.require_ram:
            require(filesystem in ("tmpfs", "ramfs"), f"Worker scratch is {filesystem}, not RAM")
            require(samples and all(s["storage"]["kind"] == "RAM" for s in samples),
                    "Dashboard did not report RAM working storage")
        if completed:
            jobs = [s["job"] for s in samples if s["job"]]
            if not jobs:
                raise Blocked("No active worker job was sampled; use a longer fixture")
            require(any(j.get("sourceMedia", {}).get("width", 0) > 0 for j in jobs if j.get("sourceMedia")),
                    "Worker monitor omitted source media details")
            observed = [f for s in samples for f in s["files"] if f["bytes"] > 0]
            if not any(f["name"] == "source" for f in observed) or not any(f["name"].startswith("candidate.") for f in observed):
                raise Blocked("Source and candidate working files were not both observed; use a longer fixture")
            metrics = [s["metrics"] for s in samples if s["metrics"]]
            require(any(valid_percent(m.get("cpuPercent")) for m in metrics), "No valid CPU sample")
            require(all(m.get("gpuPercent") is None or valid_percent(m["gpuPercent"]) for m in metrics),
                    "Invalid GPU utilization sample")
        deadline = time.monotonic() + 30
        while True:
            status = self.api.request("/api/sidecar/status")
            if not self.files(job_id) and all(j["jobId"] != job_id for j in status["jobs"]):
                break
            require(time.monotonic() < deadline, "Worker retained working files or a finished job in its monitor")
            time.sleep(.2)
        return {"filesystem": filesystem, "cleanupConfirmed": True, "samples": samples}


def valid_percent(value):
    return isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value) and 0 <= value <= 100


def local_files(scratch, job_id):
    directory = Path(scratch) / f"job-{job_id}"
    result = []
    for path in directory.glob("*"):
        try:
            if path.is_file():
                result.append({"name": path.name, "bytes": path.stat().st_size})
        except FileNotFoundError:
            pass  # Normal completion can remove a sampled file between glob and stat.
    return result
