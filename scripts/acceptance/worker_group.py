"""Own mixed disposable launchers beside one isolated server, with independent cleanup."""
import re

from .container_workers import ContainerWorkers
from .workers import Workers
from .core import require


class WorkerGroup:
    def __init__(self, api, root, server, ffmpeg, ffprobe):
        self.api, self.root, self.server = api, root, server
        self.ffmpeg, self.ffprobe = ffmpeg, ffprobe
        self.workers, self.observers = [], {}

    def start(self, specs, report):
        names = [spec.get("name", "") for spec in specs]
        require(names and len(names) == len(set(names)) and all(re.fullmatch(r"[a-zA-Z0-9_-]+", n) for n in names),
                "Worker group requires safe unique names")
        for spec in specs:
            def launch(spec=spec):
                root = self.root / ("worker-" + spec["name"])
                root.mkdir(mode=0o700)
                require(bool(spec.get("image")) != bool(spec.get("command")), "Worker needs exactly one launcher")
                if spec.get("image"):
                    worker = ContainerWorkers(self.api, root, self.server, spec["image"], name=spec["name"],
                        devices=spec.get("devices", []), gpus=spec.get("gpus"), memory=spec.get("memory", "6g"))
                else:
                    worker = Workers(self.api, root, self.server, self.ffmpeg, self.ffprobe,
                                     name=spec["name"], observe_local=not spec.get("remote", False))
                self.workers.append(worker)
                worker.start(spec.get("command", []), [spec["encoder"]], report=report)
                self.observers.update(worker.observers)
            report.case("launcher-" + spec["name"], launch)

    def stop(self):
        errors = []
        for worker in self.workers:
            try:
                worker.stop()
            except Exception as exc:
                errors.append(str(exc))
        require(not errors, "Worker group cleanup failed: " + "; ".join(errors))
