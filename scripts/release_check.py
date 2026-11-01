#!/usr/bin/env python3
"""Qualify an isolated release and export an allowlisted, redacted JSON/Markdown summary."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import plistlib
import re
import signal
import subprocess
import sys
import uuid
import zipfile

from acceptance.core import Blocked, require, save, sha256
from acceptance.house_corpus import HOUSE_PROPERTIES

REPOSITORY = Path(__file__).resolve().parents[1]
REGRESSIONS = ("interactive-samples", "interactive-lifecycle", "subtitle-mux", "fractional-timing",
    "uneven-timing", "initial-pictures", "subtitle-overlap", "alac-copy", "audio", "soundtrack-quality",
    "image-quality", "quality-first", "quality-first-workers", "quality-first-source", "quality-first-webp",
    "quality-first-metadata", "quality-first-retry", "quality-first-ownership")
WORKER_REGRESSIONS = {"interactive-lifecycle", "quality-first-workers"}
PROPERTIES = set(REGRESSIONS) | HOUSE_PROPERTIES | {"smoke", "fleet", "house-corpus", "image", "macos-package", "windows-package"}


def token(value, pattern):
    return value if isinstance(value, str) and re.fullmatch(pattern, value) else None


def same_version(left, right):
    def parts(value):
        if not token(value, r"\d+\.\d+\.\d+(?:\.\d+)?"):
            return None
        numbers = tuple(int(part) for part in value.split("."))
        return numbers + (0,) * (4 - len(numbers))
    return parts(left) is not None and parts(left) == parts(right)


def safe_receipt(value):
    """Only typed release identifiers can leave the host; never copy arbitrary log strings."""
    patterns = {"sha256": r"[a-f0-9]{64}", "sourceCommit": r"[a-f0-9]{40}",
        "imageDigest": r"sha256:[a-f0-9]{64}", "version": r"\d+\.\d+\.\d+(?:\.\d+)?",
        "ffmpegVersion": r"\d+\.\d+(?:\.\d+)?",
        "ffmpegRevision": r"[a-f0-9]{40}", "nativeHelperRevision": r"[a-f0-9]{40}"}
    return {key: valid for key, pattern in patterns.items() if (valid := token(value.get(key), pattern)) is not None}


def public_summary(private):
    steps = []
    for index, step in enumerate(private.get("steps", []), 1):
        row = {"id": f"step-{index:03}", "property": step["property"] if step.get("property") in PROPERTIES else "soundtrack",
               "status": step["status"] if step.get("status") in ("passed", "failed", "blocked") else "blocked"}
        if "corpus" in step:
            row["corpus"] = [{"id": f"clip-{i:03}", "property": clip.get("property") if clip.get("property") in HOUSE_PROPERTIES else "soundtrack",
                              "status": clip.get("status") if clip.get("status") in ("passed", "failed", "blocked") else "blocked"}
                             for i, clip in enumerate(step["corpus"], 1)]
        steps.append(row)
    artifacts = safe_receipt(private.get("artifacts", {}))
    for platform in ("macos", "windows"):
        if platform in private.get("artifacts", {}):
            artifacts[platform] = safe_receipt(private["artifacts"][platform])
            artifacts[platform]["metadataSource"] = "operator-receipt"
    workers = []
    for index, worker in enumerate(private.get("workers", []), 1):
        row = {"id": f"worker-{index:03}", "toolMetadataSource": "operator-receipt", **safe_receipt(worker)}
        protocol = worker.get("protocolVersion")
        if isinstance(protocol, int) and not isinstance(protocol, bool) and 0 < protocol < 10000:
            row["protocolVersion"] = protocol
        version = token(worker.get("sidecarVersion"), r"\d+\.\d+\.\d+(?:\.\d+)?")
        if version:
            row["version"] = version
        workers.append(row)
    completed = private.get("completed") is True
    status = "failed" if any(s["status"] == "failed" for s in steps) else (
        "blocked" if not completed or not steps or any(s["status"] == "blocked" for s in steps) else "passed")
    return {"schemaVersion": 1, "completed": completed, "status": status, "artifacts": artifacts, "workers": workers, "steps": steps}


def package_receipt(spec, commit):
    if not spec:
        raise Blocked("Required packaged artifact is absent")
    receipt = safe_receipt(spec)
    if spec.get("path"):
        path = Path(spec["path"]).expanduser()
        if not path.is_file():
            raise Blocked("Packaged artifact file is unavailable; supply a package archive or MSI")
        actual = sha256(path)
        require(not spec.get("sha256") or actual == spec["sha256"], "Packaged artifact checksum mismatch")
        receipt["sha256"] = actual
        if path.suffix.lower() == ".zip":
            with zipfile.ZipFile(path) as archive:
                names = [name for name in archive.namelist() if name.endswith(".app/Contents/Info.plist")]
                require(len(names) == 1, "Mac archive must contain one application Info.plist")
                version = plistlib.loads(archive.read(names[0]))["CFBundleShortVersionString"]
                require(version == receipt.get("version"), "Mac package version differs from receipt")
    for key in ("sha256", "version", "sourceCommit", "ffmpegRevision", "nativeHelperRevision"):
        if key not in receipt:
            raise Blocked("Required package hash, version, commit or tool revision is missing")
    require(receipt["sourceCommit"] == commit, "Packaged artifact belongs to another source commit")
    return receipt


def owned_command(argv, *, timeout=600, check=False, capture_output=False, text=False, **kwargs):
    if capture_output:
        kwargs.update(stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    process = subprocess.Popen(argv, start_new_session=os.name != "nt", text=text, **kwargs)
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except BaseException:
        # Let the harness execute its finally block and remove only its own containers.
        if process.poll() is None:
            if os.name == "nt":
                process.send_signal(signal.CTRL_BREAK_EVENT)
            else:
                os.killpg(process.pid, signal.SIGINT)
            try:
                process.communicate(timeout=60)
            except subprocess.TimeoutExpired:
                process.kill()
                process.communicate(timeout=10)
        raise
    result = subprocess.CompletedProcess(argv, process.returncode, stdout, stderr)
    if check:
        result.check_returncode()
    return result


class ReleaseCheck:
    def __init__(self, root, image, config, *, corpus=None, run=owned_command, config_path=None):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=False, mode=0o700)
        self.image, self.config, self.corpus, self.execute = image, config, corpus, run
        self.config_path = Path(config_path).resolve() if config_path else None
        self.steps, self.artifacts, self.workers = [], {}, []
        self.completed = False
        self.write()

    def record(self, prop, status, **details):
        self.steps.append({"property": prop, "status": status, **details})
        self.write()

    def write(self):
        private = {"completed": self.completed, "steps": self.steps, "artifacts": self.artifacts, "workers": self.workers}
        save(self.root / "private-report.json", private)
        summary = public_summary(private)
        save(self.root / "release-check.json", summary)
        rows = ["# Release regression check", "", f"Result: **{summary['status']}**", "",
                "Only this summary and release-check.json may leave the host. Full evidence remains private.", "",
                "Package versions and tool revisions are operator receipts; hashes are recomputed when files are supplied.", "",
                "```json", json.dumps(summary["artifacts"], indent=2), "```", "",
                "| Step | Property | Outcome |", "| --- | --- | --- |"]
        rows += [f"| {step['id']} | {step['property']} | {step['status']} |" for step in summary["steps"]]
        rows += ["", "Worker evidence:", "", "```json", json.dumps(summary["workers"], indent=2), "```"]
        for step in summary["steps"]:
            for clip in step.get("corpus", []):
                rows.append(f"\n{step['id']}: {clip['id']} — {clip['property']} — {clip['status']}")
        (self.root / "release-check.md").write_text("\n".join(rows) + "\n")

    def artifacts_check(self, commit):
        self.artifacts["sourceCommit"] = commit
        try:
            if not self.image:
                tag = "optimisarr:release-check-" + uuid.uuid4().hex[:12]
                built = self.process(["docker", "build", "--build-arg", "GIT_HASH=" + commit, "-t", tag, "."], "image-build", cwd=REPOSITORY)
                require(built.returncode == 0, "Release image build failed")
                self.image = tag
            else:
                require(re.fullmatch(r"(?:[^\s]+@)?sha256:[a-f0-9]{64}", self.image), "Accepted release image must use an exact digest")
            try:
                result = self.execute(["docker", "image", "inspect", self.image, "--format", "{{json .}}"],
                                      capture_output=True, text=True, timeout=120, check=True)
            except subprocess.CalledProcessError:
                raise Blocked("Pinned image is unavailable to the local Docker daemon") from None
            image = json.loads(result.stdout)
            digest = self.image.rsplit("@", 1)[-1] if "@" in self.image else image["Id"]
            require(token(digest, r"sha256:[a-f0-9]{64}"), "Image inspect did not return an immutable digest")
            self.artifacts["imageDigest"] = digest
            self.image = image["Id"]
            receipt = safe_receipt(self.config.get("artifacts", {}).get("image", {}))
            if not all(key in receipt for key in ("sourceCommit", "version", "ffmpegRevision", "nativeHelperRevision")):
                raise Blocked("Image source/version/tool receipt is missing")
            require(receipt["sourceCommit"] == commit, "Image receipt belongs to another source commit")
            helper = self.execute(["docker", "run", "--rm", "--network", "none", "--entrypoint", "/app/optimisarr-audio-quality",
                                   self.image, "--version"], capture_output=True, text=True, timeout=120, check=True)
            require(json.loads(helper.stdout).get("revision") == receipt["nativeHelperRevision"], "Image native-helper revision differs from receipt")
            ffmpeg = self.execute(["docker", "run", "--rm", "--network", "none", "--entrypoint", "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                                   self.image, "-version"], capture_output=True, text=True, timeout=120, check=True)
            version = re.search(r"ffmpeg version (?:n)?(\d+\.\d+(?:\.\d+)?)", ffmpeg.stdout)
            require(version, "Image FFmpeg did not report a version")
            self.artifacts.update(receipt, ffmpegVersion=version[1])
            self.record("image", "passed")
        except Exception as exc:
            self.image = None
            self.record("image", "blocked" if isinstance(exc, (Blocked, FileNotFoundError)) else "failed", detail=str(exc))
        for platform in ("macos", "windows"):
            try:
                self.artifacts[platform] = package_receipt(self.config.get("artifacts", {}).get(platform), commit)
                self.record(platform + "-package", "passed")
            except Exception as exc:
                self.record(platform + "-package", "blocked" if isinstance(exc, Blocked) else "failed", detail=str(exc))

    def process(self, argv, name, **kwargs):
        with (self.root / (name + ".log")).open("w") as log:
            return self.execute(argv, stdout=log, stderr=subprocess.STDOUT, timeout=self.config.get("stepTimeout", 14400), **kwargs)

    def acceptance(self, prop, extra, *, native=False, worker=None):
        if not self.image and not native:
            self.record(prop, "blocked", detail="Release image unavailable")
            return
        step = self.root / f"run-{len(self.steps) + 1:03}-{uuid.uuid4().hex[:8]}"
        argv = [sys.executable, str(REPOSITORY / "scripts/media_acceptance.py"), "--root", str(step)]
        if native:
            spec = self.config["native"]
            argv += ["--native", spec["path"]]
            for option in ("ffmpeg", "ffprobe", "vmaf"):
                if spec.get(option):
                    argv += ["--" + option, spec[option]]
        else:
            argv += ["--image", self.image]
        argv += ["--fixture-seconds", "16", "--local-encoder", "libx265", *extra]
        try:
            result = self.process(argv, step.name)
            report_path = step / "report/report.json"
            report = json.loads(report_path.read_text()) if report_path.is_file() else {}
            summary, results = report.get("summary", {}), report.get("results", [])
            status = "failed" if result.returncode not in (0, 2) or summary.get("status") == "failed" else (
                "passed" if result.returncode == 0 and summary.get("completed") is True and summary.get("status") == "passed"
                and results and all(r.get("status") == "passed" for r in results) else "blocked")
            for worker_name in ([worker] if isinstance(worker, str) else worker or []):
                observed = [w for w in report.get("environment", {}).get("workers", []) if w.get("name") == worker_name]
                spec = self.config.get("workers", {}).get(worker_name, {})
                if not observed or not observed[0].get("online") or not observed[0].get("videoEncoders"):
                    status = "blocked" if status != "failed" else status
                elif not observed[0].get("protocolVersion") or not observed[0].get("sidecarVersion"):
                    status = "blocked" if status != "failed" else status
                else:
                    bound = {**spec, **observed[0]}
                    if spec.get("image"):
                        bound["imageDigest"] = spec["image"].rsplit("@", 1)[-1]
                    self.workers.append(bound)
                    has_tools = all(key in safe_receipt(bound) for key in ("ffmpegRevision", "nativeHelperRevision"))
                    if not has_tools:
                        status = "blocked" if status != "failed" else status
                    package = self.artifacts.get(observed[0].get("operatingSystem"), {})
                    expected_version = package.get("version") or self.artifacts.get("version")
                    if expected_version and not same_version(observed[0]["sidecarVersion"], expected_version):
                        status = "failed"
                    tool_receipt = package or self.artifacts
                    if has_tools and tool_receipt and any(bound.get(key) != tool_receipt.get(key) for key in ("ffmpegRevision", "nativeHelperRevision")):
                        status = "failed"
            actual_version = report.get("environment", {}).get("health", {}).get("version")
            if self.artifacts.get("version") and not same_version(actual_version, self.artifacts["version"]):
                status = "failed"
            details = {"evidenceRoot": str(step), "worker": worker}
            if prop == "house-corpus":
                details["corpus"] = []
                for clip in json.loads(Path(self.corpus).read_text())["clips"]:
                    outcomes = [r.get("status") for r in results if r.get("name", "").endswith("-" + clip["id"])]
                    clip_status = "failed" if "failed" in outcomes else (
                        "passed" if outcomes and all(outcome == "passed" for outcome in outcomes) else "blocked")
                    details["corpus"].append({**clip, "status": clip_status})
            self.record(prop, status, **details)
        except (FileNotFoundError, subprocess.TimeoutExpired) as exc:
            self.record(prop, "blocked", detail=str(exc))
        except Exception as exc:
            self.record(prop, "failed", detail=str(exc))

    def worker_spec(self, name):
        spec = self.config.get("workers", {}).get(name)
        if not spec:
            raise Blocked("Required worker has no launcher configuration")
        encoder = spec.get("encoder")
        require(token(encoder, r"[a-zA-Z0-9_]+"), "Required worker needs one encoder")
        launcher = {"name": name, "encoder": encoder}
        if spec.get("ssh"):
            require(self.config_path, "SSH needs an operator config file")
            command = [sys.executable, str(REPOSITORY / "scripts/release_worker_ssh.py"), "--config", str(self.config_path), "--worker", name]
            launcher.update(command=command, remote=True)
        elif spec.get("image"):
            require(re.fullmatch(r"(?:[^\s]+@)?sha256:[a-f0-9]{64}", spec["image"]), "Release worker image must use an exact digest")
            launcher["image"] = spec["image"]
        elif spec.get("command"):
            launcher["command"] = spec["command"]
        else:
            raise Blocked("Required worker has no launcher")
        launcher.update(devices=spec.get("devices", []), gpus=spec.get("gpus"), memory=spec.get("memory", "6g"))
        return launcher

    def run_matrices(self):
        self.acceptance("smoke", ["--tier", "smoke", "--vmaf-shadow", "--diagnostics"])
        for regression in REGRESSIONS:
            if regression in WORKER_REGRESSIONS:
                continue
            if regression == "quality-first-ownership" and (not self.config.get("native") or os.name == "nt"):
                self.record(regression, "blocked", detail="Native POSIX runtime not configured")
            else:
                self.acceptance(regression, ["--regression", regression], native=regression == "quality-first-ownership")
        if self.corpus:
            self.acceptance("house-corpus", ["--regression", "house-corpus", "--corpus", str(self.corpus)])
        required = self.config.get("requiredWorkers", [])
        if not required:
            for prop in ("fleet", *sorted(WORKER_REGRESSIONS)):
                self.record(prop, "blocked", detail="No required workers configured")
            return
        launchers = []
        for name in required:
            try:
                launchers.append(self.worker_spec(name))
            except Exception as exc:
                self.record("fleet", "blocked", detail=str(exc), worker=name)
        if not launchers:
            for prop in ("fleet", *sorted(WORKER_REGRESSIONS)):
                self.record(prop, "blocked", detail="No usable worker launchers")
            return
        worker_config = self.root / "worker-launchers.json"
        save(worker_config, launchers)
        args = ["--tier", "fleet", "--worker-config", str(worker_config), "--listen", self.config.get("listen", "127.0.0.1")]
        for name in required:
            args += ["--expected-worker", name]
        self.acceptance("fleet", args, worker=required)
        for regression in sorted(WORKER_REGRESSIONS):
            self.acceptance(regression, [*args, "--regression", regression], worker=required)
        self.acceptance("soundtrack-quality", [*args, "--regression", "soundtrack-quality"], worker=required)
        if self.corpus:
            self.acceptance("house-corpus", [*args, "--regression", "house-corpus", "--corpus", str(self.corpus)], worker=required)

    def finish(self):
        self.completed = True
        self.write()
        status = public_summary({"completed": True, "steps": self.steps})["status"]
        return {"passed": 0, "failed": 1, "blocked": 2}[status]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, type=Path, help="New private run root")
    parser.add_argument("--config", required=True, type=Path, help="Private artifact receipts and worker launcher configuration")
    parser.add_argument("--image", help="Exact published name@sha256:digest; omit to build this checkout")
    parser.add_argument("--corpus", type=Path, help="Private house corpus manifest; never attached to a release")
    args = parser.parse_args()
    config = json.loads(args.config.read_text())
    check = ReleaseCheck(args.root, args.image, config, corpus=args.corpus, config_path=args.config)
    try:
        commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=REPOSITORY, capture_output=True, text=True, check=True, timeout=30).stdout.strip()
        check.artifacts_check(commit)
        check.run_matrices()
    except KeyboardInterrupt:
        check.record("smoke", "blocked", detail="Interrupted; release qualification incomplete")
    except Exception as exc:
        check.record("smoke", "failed", detail=str(exc))
    finally:
        code = check.finish()
    print(f"Redacted summary: {check.root / 'release-check.md'}")
    return code


if __name__ == "__main__":
    sys.exit(main())
