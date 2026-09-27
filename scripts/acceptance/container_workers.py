"""Final-image workers for disposable Linux CI daemons, never managed deployment hosts."""
import json
import os
import secrets
import re
import subprocess
import shutil

from .core import Blocked, command, require
from .monitor import LinuxObserver
from .workers import Workers, free_port


class ContainerWorkers(Workers):
    def __init__(self, api, root, server, image, *, devices=(), gpus=None):
        super().__init__(api, root, server, "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                         "/usr/lib/jellyfin-ffmpeg/ffprobe", require_ram=True)
        self.image = image
        self.devices, self.gpus = devices, gpus
        self.containers = []

    def docker_options(self):
        args = []
        for device in self.devices:
            args += ["--device", device]
        if self.gpus:
            args += ["--gpus", self.gpus]
        return args

    def discover(self, argv):
        if not shutil.which("docker"):
            raise Blocked("Docker CLI is unavailable for the requested worker image")
        try:
            platform = command(["docker", "info", "--format", "{{.OSType}}"], timeout=10).strip()
        except (RuntimeError, subprocess.TimeoutExpired) as exc:
            raise Blocked("Docker daemon is unavailable for the requested worker image") from exc
        if platform != "linux":
            raise Blocked("The worker image needs a Linux Docker daemon")
        name = "optimisarr-acceptance-discovery-" + secrets.token_hex(5)
        self.containers.append(name)
        try:
            capabilities = json.loads(command(["docker", "run", "--name", name, *self.docker_options(),
                "-e", "OPTIMISARR_WEB_ENABLED=false", self.image, "--discover"], timeout=120))
            require(capabilities["videoEncoders"], "Container proved no encoders")
            capabilities["acceptanceImageId"] = command(["docker", "inspect", "-f", "{{.Image}}", name]).strip()
            return capabilities
        finally:
            self.remove(name)

    def launch(self, argv, capabilities, encoder):
        require(re.fullmatch(r"[a-zA-Z0-9_]+", encoder), "Unsafe encoder name")
        name = f"acceptance-linux-{encoder}"
        container = "optimisarr-acceptance-worker-" + secrets.token_hex(5)
        port = free_port()
        config = self.root / (name + "-config")
        config.mkdir(mode=0o700)
        settings = self.api.request("/api/settings")
        settings["remoteWorkersEnabled"] = True
        self.api.request("/api/settings", "PUT", settings)
        env = {**os.environ, "OPTIMISARR_PAIRING_CODE": self.api.post("/api/workers/pairing-code")["code"]}
        args = ["docker", "run", "-d", "--name", container, "--network", "host",
                "--memory", "2g", "--memory-swap", "2g", "--tmpfs", "/work:size=1g,mode=0700,uid=1000,gid=1000",
                "-v", f"{config}:/config", "-e", "OPTIMISARR_PAIRING_CODE",
                "-e", f"OPTIMISARR_SERVER={self.server}", "-e", f"OPTIMISARR_WORKER_NAME={name}",
                "-e", f"OPTIMISARR_ENCODER={encoder}", "-e", "OPTIMISARR_WEB_ENABLED=true",
                "-e", f"ASPNETCORE_URLS=http://127.0.0.1:{port}", *self.docker_options(), self.image]
        self.containers.append(container)
        try:
            subprocess.run(args, env=env, check=True, capture_output=True, text=True, timeout=120)
            self.wait_online(name, lambda: command(["docker", "inspect", "-f", "{{.State.Running}}", container]).strip() == "true")
        except Exception:
            self.remove(container)
            raise
        def files(job_id):
            # find /work succeeds even when completion removed the requested job directory.
            output = command(["docker", "exec", container, "find", "/work", "-ignore_readdir_race", "-maxdepth", "2", "-type", "f",
                              "-path", f"/work/job-{job_id}/*", "-printf", "%f %s\n"])
            return [{"name": row.rsplit(" ", 1)[0], "bytes": int(row.rsplit(" ", 1)[1])}
                    for row in output.splitlines()]
        self.observers[name] = LinuxObserver(f"http://127.0.0.1:{port}", files,
            lambda: command(["docker", "exec", container, "stat", "-f", "-c", "%T", "/work"]).strip(), require_ram=True)
        return {"name": name, "encoder": encoder, "image": self.image, "online": True, "ramLimitBytes": 1073741824}

    def remove(self, name):
        logs = subprocess.run(["docker", "logs", name], capture_output=True, text=True, timeout=30)
        (self.root / (name + ".log")).write_text(logs.stdout + logs.stderr)
        result = subprocess.run(["docker", "rm", "-f", name], capture_output=True, text=True, timeout=30)
        require(result.returncode == 0 or "No such container" in result.stderr, "Could not remove owned test worker")
        self.containers.remove(name)

    def stop(self):
        failures = []
        for name in list(self.containers):
            try:
                self.remove(name)
            except Exception as exc:
                failures.append(str(exc))
        require(not failures, "Container cleanup failed: " + "; ".join(failures))
