"""Execute real application workflows exclusively against an owned test instance."""
from __future__ import annotations

import json
import os
import re
from pathlib import Path
import shutil
import time

from .core import Blocked, inside, quality_failures, require, save, sha256
from .media import compare_report, validate_shadow_report

TERMINAL = {"ReadyToReplace", "Completed", "Failed", "Cancelled"}
MODES = {"libx264": "Cpu", "libx265": "Cpu", "libsvtav1": "Cpu",
         "h264_nvenc": "NvidiaNvenc", "hevc_nvenc": "NvidiaNvenc", "av1_nvenc": "NvidiaNvenc",
         "h264_qsv": "IntelQsv", "hevc_qsv": "IntelQsv", "av1_qsv": "IntelQsv",
         "h264_vaapi": "Vaapi", "hevc_vaapi": "Vaapi", "av1_vaapi": "Vaapi",
         "h264_videotoolbox": "Auto", "hevc_videotoolbox": "Auto"}
CODECS = {"libx264": "h264", "libx265": "hevc", "libsvtav1": "av1"}
DEFAULT_GATES = {"harmonic": 93, "p5": 80, "minimum": 50}


def missing_workers(workers, required):
    usable = {worker["name"] for worker in workers
              if worker.get("online") and not worker.get("revokedAt") and worker.get("videoEncoders")}
    return [name for name in required if name not in usable]


class Harness:
    def __init__(self, api, tools, root, report, *, timeout=900, restart=None):
        self.api, self.tools, self.root, self.report = api, tools, Path(root), report
        self.timeout = timeout
        self.restart = restart
        self.prefix = "acceptance-" + self.root.name + "-"
        self.workers = []
        self.job_ids = set()
        self.observers = {}

    def preflight(self):
        # Fresh instances only: names, empty databases and actual work-root correspondence all
        # matter. Merely naming a library 'test' does not make a production server disposable.
        require(not self.api.request("/api/libraries"), "Acceptance needs a fresh instance without libraries")
        require(not self.api.request("/api/jobs"), "Acceptance needs an empty queue and history")
        require(not self.api.request("/api/replacements"), "Acceptance needs empty replacement history")
        status = self.api.request("/api/queue/status")
        require(status["workRoot"].rstrip("/") == self.tools.path(self.root / "work").rstrip("/"),
                "Server work root does not match the owned acceptance directory")
        self.settings = self.api.request("/api/settings")
        self.workers = self.api.request("/api/workers")
        require(not any(w["heldLeases"] for w in self.workers), "Workers still hold existing leases")
        names = [w["name"] for w in self.workers if not w["revokedAt"]]
        require(len(names) == len(set(names)), "Worker names must be unique to verify attribution")
        self.report.environment = {"health": self.api.request("/api/health"),
            "tools": self.tools.versions(), "hardware": self.api.request("/api/system/hardware"),
            "workers": self.workers, "policy": DEFAULT_GATES,
            "scope": "Fresh isolated instance; raw-frame independent SDR quality and lossless audio checks"}
        self.report.write()
        self.configure()
        self.api.post("/api/queue/resume")
        return self.report.environment

    def configure(self, **changes):
        settings = self.api.request("/api/settings")
        settings.update(maxConcurrentJobs=1, minFreeDiskBytes=0, cpuThreadLimit=2,
            libraryScanIntervalHours=24, encoderMode="Cpu", hardwareDecode=False,
            dryRunMode=False, replacementQuarantineRetentionDays=0)
        settings.update(changes)
        return self.api.request("/api/settings", "PUT", settings)

    def select_worker(self, worker):
        current_workers = {w["id"]: w for w in self.api.request("/api/workers")}
        if worker:
            current = current_workers.get(worker["id"])
            if not current or not current["online"] or current.get("revokedAt"):
                raise Blocked(f"Required worker {worker['name']} is absent, offline or revoked")
        # Only on the fresh, isolated instance checked above. Never drain the production fleet.
        for known in self.workers:
            current = current_workers.get(known["id"])
            if not current or current.get("revokedAt"):
                continue
            method = "DELETE" if worker and known["id"] == worker["id"] else "POST"
            self.api.request(f"/api/workers/{known['id']}/drain", method)
        if worker:
            require(self.settings["remoteWorkersAvailable"], "Remote worker feature is disabled on test server")
        self.configure(remoteWorkersEnabled=bool(self.workers))

    def create_job(self, name, fixture, *, encoder="libx265", worker=None, strategy="Fixed", gates=None,
                   overrides=None, expected_ineligible=None):
        codec = CODECS.get(encoder, encoder.split("_")[0])
        case_root = inside(self.root / "data", self.root / "data" / name)
        case_root.mkdir(parents=True, exist_ok=False)
        source = case_root / ("source ' [字幕]" + Path(fixture).suffix)
        shutil.copyfile(fixture, source)
        os.utime(source, (time.time() - 600, time.time() - 600))
        thresholds = gates or DEFAULT_GATES
        body = {"name": self.prefix + name, "path": self.tools.path(case_root), "mediaType": "Film",
            "ruleProfile": "ConservativeHevc", "enabled": True, "minFileSizeBytes": 0,
            "targetVideoCodec": codec, "targetContainer": "mkv", "skipEfficientSources": False,
            "videoAudioCodec": "copy",
            "reencodeSameCodecAboveBytes": 1, "qualityCrf": 18,
            "encoderPreset": "fast" if encoder in ("libx264", "libx265") else None,
            "videoQualityStrategy": strategy, "workPlacement": "WorkerOnly" if worker else "LocalOnly",
            "vmafQualityGateEnabled": True, "minVmafHarmonicMean": thresholds["harmonic"],
            "minVmafMin": thresholds["p5"], "minVmafCatastrophicMin": thresholds["minimum"],
            "clipVmafEnabled": False, "vmafFrameSubsample": 1, "autoEnqueueEnabled": False,
            "autoReplace": False, "requireSizeReduction": True,
            "requireAudioRetained": True, "requireSubtitlesRetained": True}
        body.update(overrides or {})
        library = self.api.post("/api/libraries", body)
        library_id = library["id"]
        self.api.post(f"/api/libraries/{library_id}/scan")
        files = self.api.request(f"/api/media?libraryId={library_id}")
        require(len(files) == 1, f"Scan did not discover exactly one fixture: {files}")
        self.api.post(f"/api/media/{files[0]['id']}/probe")
        candidates = self.api.request(f"/api/candidates?libraryId={library_id}")
        if expected_ineligible:
            require(len(candidates) == 1 and not candidates[0]["eligible"]
                    and expected_ineligible in candidates[0]["reason"], f"Unsupported media was not safely excluded: {candidates}")
            require(sha256(source) == sha256(fixture), "Eligibility refusal changed the source")
            return {"eligibility": candidates[0], "sourceUnchanged": True}
        require(len(candidates) == 1 and candidates[0]["eligible"], f"Fixture ineligible: {candidates}")
        self.api.post(f"/api/libraries/{library_id}/enqueue")
        jobs = self.api.request(f"/api/jobs?libraryId={library_id}")
        require(len(jobs) == 1, f"Expected one enqueued job, got {jobs}")
        self.job_ids.add(jobs[0]["id"])
        return {"libraryId": library_id, "jobId": jobs[0]["id"], "mediaId": files[0]["id"],
                "source": source, "sourceSha256": sha256(source), "settings": body,
                "workerId": worker["id"] if worker and "id" in worker else None}

    def wait_job(self, case):
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            job = next(j for j in self.api.request(f"/api/jobs?libraryId={case['libraryId']}") if j["id"] == case["jobId"])
            if case.get("workerId"):
                worker_name = next((w["name"] for w in self.workers if w["id"] == case["workerId"]), None)
                observer = self.observers.get(worker_name)
                if observer:
                    try:
                        observer.sample(case["jobId"])
                    except Exception:
                        self.api.post(f"/api/jobs/{case['jobId']}/cancel")
                        raise
            if job["status"] in TERMINAL:
                return job
            if case.get("workerId"):
                worker = next((w for w in self.api.request("/api/workers") if w["id"] == case["workerId"]), None)
                if not worker or not worker["online"] or worker.get("revokedAt"):
                    self.api.post(f"/api/jobs/{case['jobId']}/cancel")
                    raise Blocked(f"Worker for job {case['jobId']} is absent, offline or revoked; cancellation requested")
            time.sleep(.1 if case.get("workerId") else .5)
        self.api.post(f"/api/jobs/{case['jobId']}/cancel")
        raise Blocked(f"Job {case['jobId']} exceeded {self.timeout}s; cancellation requested")

    def output(self, case):
        directory = inside(self.root / "work", self.root / "work" / str(case["mediaId"]))
        files = [p for p in directory.rglob("*") if p.is_file() and p.suffix.lower() in (".mkv", ".mp4", ".webm", ".m4a", ".mp3", ".webp", ".jpg", ".avif")]
        require(len(files) == 1, f"Expected exactly one delivered candidate, found {files}")
        return inside(self.root / "work", files[0])

    def video(self, name, fixture, encoder="libx265", worker=None, strategy="Fixed", reject=False, hardware_decode=False, audio_gates=False):
        self.select_worker(worker)
        self.configure(encoderMode=MODES[encoder] if not worker else "Cpu", hardwareDecode=hardware_decode)
        gates = {"harmonic": 100, "p5": 100, "minimum": 100} if reject else DEFAULT_GATES
        if (encoder.startswith("h264") or encoder == "libx264") and "ten-bit" in str(fixture):
            return self.create_job(name, fixture, encoder=encoder, worker=worker,
                                   expected_ineligible="limited to 8-bit sources")
        overrides = {"audioLoudnessGateEnabled": True, "maxLoudnessDriftLufs": 1,
                     "audioClippingGateEnabled": True, "maxTruePeakDbtp": 0} if audio_gates else None
        case = self.create_job(name, fixture, encoder=encoder, worker=worker, strategy=strategy, gates=gates,
                               overrides=overrides)
        directory = self.report.root / name
        directory.mkdir()
        save(directory / "case.json", {**case, "source": str(case["source"])})
        job = self.wait_job(case)
        save(directory / "job.json", job)
        observer = self.observers.get(worker["name"]) if worker else None
        if observer:
            save(directory / "worker-samples.json", observer.samples.get(case["jobId"], []))
            save(directory / "worker-monitor.json", observer.finish(case["jobId"], completed=job["status"] in ("ReadyToReplace", "Failed")))
        if hardware_decode and not reject:
            require(re.search(r"(?:^| )-hwaccel (qsv|vaapi|cuda|videotoolbox)(?: |$)", job["ffmpegArguments"] or ""),
                    "GPU decode was requested but the delivered encode used software decoding")
        require(sha256(case["source"]) == case["sourceSha256"], "Original changed before replacement")
        require(job["workerName"] == (worker["name"] if worker else None), "Wrong worker executed job")
        if job["videoEncoder"] != encoder:
            raise Blocked(f"Requested coverage for {encoder}, scheduler selected {job['videoEncoder']}; not covered")
        verification = json.loads(job["verificationReportJson"] or "{}")
        if getattr(self, "vmaf_shadow", False):
            save(directory / "shadow-check.json", validate_shadow_report(verification))
        if worker and getattr(self, "strict_worker_verification", False):
            require(verification.get("context", {}).get("verificationLocation") == "Worker",
                    "Strict sidecar verification was not recorded; server fallback cannot satisfy this run")
        if reject:
            require(job["status"] == "Failed" and job["verificationPassed"] is False,
                    f"Expected a recorded VMAF rejection, got {job['status']}, verificationPassed={job['verificationPassed']}: {job['errorMessage']}")
            checks = verification.get("checks", [])
            require(any(c["outcome"] in (1, "Failed") and "vmaf" in (c["name"] + c["detail"]).lower() for c in checks),
                    f"Candidate failed for a different reason: {checks}")
            require(not any(r["jobId"] == case["jobId"] for r in self.api.request("/api/replacements")),
                    "Rejected candidate acquired replacement history")
            if hardware_decode:
                require(verification.get("context", {}).get("decodeRetry"),
                        "Software-decode retry was not verified; fallback coverage is missing")
            return {"jobId": case["jobId"], "expectedRejection": True, "originalUnchanged": True,
                    "decodeRetry": verification.get("context", {}).get("decodeRetry")}
        require(job["status"] == "ReadyToReplace" and job["verificationPassed"] is True,
                f"Output failed application verification: {job['status']}: {job['errorMessage']}")
        candidate = self.output(case)
        require(candidate.stat().st_size < case["source"].stat().st_size, "No size saving")
        expected_codec = CODECS.get(encoder, encoder.split("_")[0])
        streams = self.tools.probe(candidate)["streams"]
        require(next(s["codec_name"] for s in streams if s["codec_type"] == "video") == expected_codec,
                "Delivered codec differs from requested codec")
        source_bytes, candidate_bytes = case["source"].stat().st_size, candidate.stat().st_size
        scores = self.tools.measure(case["source"], candidate, directory)
        require(not quality_failures(scores, gates), f"Independent quality gate failed: {scores}")
        compare_report(verification, scores)
        if audio_gates:
            before, after = self.tools.loudness(case["source"]), self.tools.loudness(candidate)
            require(abs(before["lufs"] - after["lufs"]) <= 1, "Worker output loudness drift exceeds 1 LUFS")
            require(after["truePeak"] <= 0, "Worker output introduced clipping")
        self.replace_restore(case, candidate)
        return {"jobId": case["jobId"], "workerId": worker["id"] if worker else None,
                "encoder": encoder, "strategy": strategy, "scores": scores, "restoredOriginal": True,
                "sourceBytes": source_bytes, "candidateBytes": candidate_bytes,
                "savingPercent": 100 * (1 - candidate_bytes / source_bytes)}

    def replace_restore(self, case, candidate):
        candidate_hash = sha256(candidate)
        self.api.post(f"/api/jobs/{case['jobId']}/replace")
        # Retrying a successful request must not create a second filesystem operation.
        self.api.post(f"/api/jobs/{case['jobId']}/replace")
        replacements = [r for r in self.api.request("/api/replacements") if r["jobId"] == case["jobId"]]
        require(len(replacements) == 1, "Repeated replace created duplicate history")
        replacement = replacements[0]
        def local(server_path):
            if self.tools.container:
                require(server_path.startswith("/acceptance/"), "Unexpected replacement path")
                server_path = self.root / server_path.removeprefix("/acceptance/")
            return inside(self.root, server_path)
        quarantine, final = local(replacement["quarantinePath"]), local(replacement["finalPath"])
        require(sha256(quarantine) == case["sourceSha256"], "Quarantine original hash mismatch")
        require(sha256(final) == candidate_hash, "Installed candidate hash mismatch")
        self.api.post(f"/api/replacements/{replacement['id']}/rollback")
        require(sha256(case["source"]) == case["sourceSha256"], "Rollback did not restore original bytes")

    def cancel(self, fixture):
        self.select_worker(None)
        self.api.post("/api/queue/pause")
        try:
            case = self.create_job("cancel", fixture)
            self.api.post(f"/api/jobs/{case['jobId']}/cancel")
            job = self.wait_job(case)
            require(job["status"] == "Cancelled", "Queued cancellation did not reach Cancelled")
            require(sha256(case["source"]) == case["sourceSha256"], "Cancelled job changed source")
            return {"jobId": case["jobId"], "originalUnchanged": True}
        finally:
            self.api.post("/api/queue/resume")

    def audio(self):
        self.select_worker(None)
        fixture = self.root / "fixtures" / "audio.flac"
        self.tools.encode(["-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.1:duration=45:sample_rate=48000:seed=42",
            "-c:a", "flac", "-metadata", "artist=Optimisarr acceptance", self.tools.path(fixture)])
        case = self.create_job("audio-aac", fixture, overrides={"mediaType": "Music", "audioTargetCodec": "aac",
            "audioBitrateKbps": 128, "vmafQualityGateEnabled": False, "audioLoudnessGateEnabled": True,
            "maxLoudnessDriftLufs": 1, "audioClippingGateEnabled": True, "maxTruePeakDbtp": 0})
        job = self.wait_job(case)
        save(self.report.root / "audio-aac" / "job.json", job)
        require(job["status"] == "ReadyToReplace", f"Audio failed: {job['errorMessage']}")
        candidate = self.output(case)
        before, after = self.tools.loudness(case["source"]), self.tools.loudness(candidate)
        require(abs(before["lufs"] - after["lufs"]) <= 1, "Audio loudness drift exceeds 1 LUFS")
        require(after["truePeak"] <= 0, "Audio re-encode introduced clipping")
        probe = self.tools.probe(candidate)
        require(probe["streams"][0]["codec_name"] == "aac", "Wrong audio encoder output")
        require(probe["format"].get("tags", {}).get("artist") == "Optimisarr acceptance", "Lost audio metadata")
        self.replace_restore(case, candidate)
        return {"original": before, "encoded": after, "restoredOriginal": True}

    def preview(self, fixture):
        self.select_worker(None)
        # Keep the short-preview regression even when the main corpus is lengthened for soak.
        fixture = self.root / "fixtures" / "short-preview.mkv"
        self.tools.fixture(fixture, seconds=8)
        self.api.post("/api/queue/pause")
        try:
            case = self.create_job("preview-source", fixture)
            self.api.post(f"/api/jobs/{case['jobId']}/cancel")
        finally:
            self.api.post("/api/queue/resume")
        job_id = self.api.post(f"/api/media/{case['mediaId']}/preview")["jobId"]
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            preview = self.api.request(f"/api/preview/{job_id}")
            if preview["status"] in TERMINAL:
                break
            time.sleep(.5)
        else:
            raise Blocked("Preview timed out")
        save(self.report.root / "preview" / "comparison.json", preview)
        require(preview["verificationPassed"] is True, f"Preview failed: {preview['errorMessage']}")
        directory = self.root / "work" / "preview" / str(job_id)
        outputs = list(directory.rglob("*.mkv"))
        require(len(outputs) == 1, "Preview output missing or ambiguous")
        scores = self.tools.measure(case["source"], outputs[0], self.report.root / "preview")
        require(not quality_failures(scores, DEFAULT_GATES), "Preview independently failed quality")
        require(sha256(case["source"]) == case["sourceSha256"], "Preview changed source")
        self.api.request(f"/api/preview/{job_id}", "DELETE")
        require(not outputs[0].exists(), "Preview cleanup left output behind")
        return {"scores": scores, "originalUnchanged": True, "cleaned": True}

    def image(self):
        self.select_worker(None)
        fixture = self.root / "fixtures" / "picture.bmp"
        self.tools.encode(["-f", "lavfi", "-i", "testsrc2=size=512x512:rate=1:duration=1",
                           "-vf", "format=gray", "-pix_fmt", "bgr24", "-frames:v", "1", self.tools.path(fixture)])
        case = self.create_job("image-jpeg", fixture, overrides={"mediaType": "Photo", "targetImageFormat": "jpeg",
            "imageQuality": 95, "reencodeLossyImages": True, "vmafQualityGateEnabled": False, "imageQualityGateEnabled": True,
            "minimumImageSsim": .95, "imageMetadataGateEnabled": True, "requireSizeReduction": False})
        job = self.wait_job(case)
        save(self.report.root / "image-jpeg" / "job.json", job)
        require(job["status"] == "ReadyToReplace", f"Image failed: {job['errorMessage']}")
        candidate = self.output(case)
        probe = self.tools.probe(candidate)["streams"][0]
        require((probe["width"], probe["height"], probe["codec_name"]) == (512, 512, "mjpeg"), "Changed image structure")
        log = self.tools.run(self.tools.ffmpeg, ["-hide_banner", "-i", self.tools.path(case["source"]),
            "-i", self.tools.path(candidate), "-lavfi", "[0:v]format=gbrp[a];[1:v]format=gbrp[b];[a][b]ssim",
            "-f", "null", "-"], include_stderr=True)
        match = re.search(r"All:([\d.]+)", log)
        require(match is not None and float(match[1]) >= .95, "Image independently failed SSIM")
        self.replace_restore(case, candidate)
        return {"ssim": float(match[1]), "restoredOriginal": True,
                "scope": "Opaque BMP to JPEG; metadata-free generated source"}

    def calibration(self):
        self.select_worker(None)
        fixture = self.root / "fixtures" / "calibration.mkv"
        self.tools.fixture(fixture, seconds=60)
        self.api.post("/api/queue/pause")
        try:
            case = self.create_job("calibration-source", fixture)
            self.api.post(f"/api/jobs/{case['jobId']}/cancel")
        finally:
            self.api.post("/api/queue/resume")
        session = self.api.post(f"/api/libraries/{case['libraryId']}/calibration", {"mediaFileId": case["mediaId"], "diagnosticsEnabled": True})
        deadline = time.monotonic() + self.timeout
        while session["status"] not in ("Comparing", "Failed") and time.monotonic() < deadline:
            time.sleep(.5)
            session = self.api.request(f"/api/calibration/{session['id']}")
        save(self.report.root / "calibration" / "session.json", session)
        require(session["status"] == "Comparing", f"Calibration did not become playable: {session['error']}")
        require(len(session["variants"]) >= 2 and all(v["samples"] for v in session["variants"]), "Calibration has missing samples")
        require(sha256(case["source"]) == case["sourceSha256"], "Calibration changed original")
        require(not any(r["mediaFileId"] == case["mediaId"] for r in self.api.request("/api/replacements")), "Calibration allowed replacement")
        return {"sessionId": session["id"], "variants": len(session["variants"]), "originalUnchanged": True}

    def disk_guard(self, fixture):
        self.select_worker(None)
        self.configure(minFreeDiskBytes=9_000_000_000_000_000)
        try:
            case = self.create_job("low-space", fixture)
            time.sleep(2)
            status = self.api.request("/api/queue/status")
            job = self.api.request(f"/api/jobs?libraryId={case['libraryId']}")[0]
            require(not status["canStart"] and job["status"] == "Queued", "Low-space guard dispatched a job")
            self.api.post(f"/api/jobs/{case['jobId']}/cancel")
            require(sha256(case["source"]) == case["sourceSha256"], "Low-space refusal changed source")
            return {"blockedReason": status["blockedReason"], "originalUnchanged": True}
        finally:
            self.configure()

    def restart_queue(self, fixture):
        self.select_worker(None)
        self.api.post("/api/queue/pause")
        case = self.create_job("restart-queued", fixture)
        if not self.restart:
            raise Blocked("This runner cannot restart its test server")
        try:
            self.restart()
            require(self.api.request("/api/queue/status")["manuallyPaused"], "Restart forgot manual pause")
            jobs = self.api.request(f"/api/jobs?libraryId={case['libraryId']}")
            require(len(jobs) == 1 and jobs[0]["id"] == case["jobId"] and jobs[0]["status"] == "Queued",
                    "Restart lost or duplicated queued work")
        finally:
            self.api.post("/api/queue/resume")
        job = self.wait_job(case)
        require(job["status"] == "ReadyToReplace", f"Recovered job failed: {job['errorMessage']}")
        candidate = self.output(case)
        scores = self.tools.measure(case["source"], candidate, self.report.root / "restart")
        require(not quality_failures(scores, DEFAULT_GATES), "Recovered job failed independent quality")
        self.replace_restore(case, candidate)
        return {"jobId": case["jobId"], "queueSurvivedAbruptRestart": True, "scores": scores, "restoredOriginal": True}

    def cancel_running(self, worker=None, encoder="libx265"):
        self.select_worker(worker)
        name = f"cancel-running-worker-{worker['id']}-{encoder}" if worker else "cancel-running"
        fixture = self.root / "fixtures" / (name + ".mkv")
        self.tools.fixture(fixture, seconds=120)
        case = self.create_job(name, fixture, worker=worker, encoder=encoder, overrides={"encoderPreset": "slow" if encoder in ("libx264", "libx265") else None})
        deadline = time.monotonic() + min(self.timeout, 120)
        while time.monotonic() < deadline:
            job = self.api.request(f"/api/jobs?libraryId={case['libraryId']}")[0]
            observer = self.observers.get(worker["name"]) if worker else None
            observed = observer.sample(case["jobId"]) if observer else None
            encoding_observed = bool(observed and observed.get("job") and observed["job"]["stage"] == "Encoding")
            if job["status"] == "Transcoding" or encoding_observed or (worker and not observer and job["status"] == "Leased"):
                break
            if job["status"] in TERMINAL:
                raise Blocked("Encode finished before active cancellation could be exercised")
            time.sleep(.02)
        else:
            self.api.post(f"/api/jobs/{case['jobId']}/cancel")
            raise Blocked("No running encode observed for cancellation; cancellation requested")
        self.api.post(f"/api/jobs/{case['jobId']}/cancel")
        require(self.wait_job(case)["status"] == "Cancelled", "Running cancellation failed")
        deadline = time.monotonic() + 15
        while self.api.request("/api/queue/status")["runningJobs"] and time.monotonic() < deadline:
            time.sleep(.1)
        require(self.api.request("/api/queue/status")["runningJobs"] == 0, "Cancelled encode still occupies a slot")
        if worker:
            require(job["workerName"] == worker["name"], "Cancellation targeted a different worker")
            observer = self.observers.get(worker["name"])
            if observer:
                save(self.report.root / (name + ".json"), observer.finish(case["jobId"], completed=False))
            deadline = time.monotonic() + 20
            while next(w for w in self.api.request("/api/workers") if w["id"] == worker["id"])["heldLeases"]:
                require(time.monotonic() < deadline, "Cancelled worker retained its lease")
                time.sleep(.2)
        require(sha256(case["source"]) == case["sourceSha256"], "Cancelled encode altered its original")
        return {"jobId": case["jobId"], "cancelledWhileTranscoding": not worker or encoding_observed,
                "remoteStage": job.get("remoteStage"), "slotReleased": True, "originalUnchanged": True}

    def concurrent(self):
        self.select_worker(None)
        self.configure(maxConcurrentJobs=2)
        # Short clips can finish before the dispatch loop starts the second job on a fast host.
        # Both jobs must remain active long enough to exercise isolation, not merely enqueueing.
        first = self.root / "fixtures" / "concurrent-first.mkv"
        other = self.root / "fixtures" / "concurrent-distinct.mkv"
        self.tools.fixture(first, seconds=120)
        self.tools.fixture(other, seconds=121)
        self.api.post("/api/queue/pause")
        try:
            cases = [self.create_job(f"concurrent-{i}", f, overrides={"encoderPreset": "slow"})
                     for i, f in enumerate((first, other))]
        finally:
            self.api.post("/api/queue/resume")
        jobs = []
        for case in cases:
            job = self.wait_job(case)
            jobs.append(job)
            require(job["status"] == "ReadyToReplace", f"Concurrent job failed: {job['errorMessage']}")
            candidate = self.output(case)
            scores = self.tools.measure(case["source"], candidate, self.report.root / f"concurrent-{case['jobId']}")
            require(not quality_failures(scores, DEFAULT_GATES), "Concurrent output failed quality")
            self.replace_restore(case, candidate)
        require(max(j["startedAt"] for j in jobs) < min(j["finishedAt"] for j in jobs),
                "Jobs did not actually overlap; concurrency was not exercised")
        return {"jobs": [c["jobId"] for c in cases], "isolatedOutputs": True, "overlapConfirmed": True}

    def hardware_decode_rejection(self, encoder, fixture):
        name = f"local-{encoder}-decode-retry-rejection"
        compressed = self.root / "fixtures" / f"{encoder}-decode-source.mkv"
        # Leave enough size headroom to reach VMAF rejection and its software-decode retry.
        # A CRF 10 source let Intel outputs hit the size guard first, testing the wrong gate.
        self.tools.encode(["-i", self.tools.path(fixture), "-c:v", "libx264", "-crf", "1",
                           "-profile:v", "high", "-preset", "fast", "-c:a", "copy", self.tools.path(compressed)])
        return self.video(name, compressed, encoder, reject=True, hardware_decode=True)

    def worker_gpu_decode(self, worker, encoder, fixture):
        decoder = {"qsv": "qsv", "vaapi": "vaapi", "nvenc": "cuda", "videotoolbox": "videotoolbox"}.get(encoder.rsplit("_", 1)[-1])
        if not decoder or decoder not in worker.get("hardwareDecoders", []):
            raise Blocked(f"{worker['name']} did not prove {decoder or 'a GPU'} decoding")
        name = f"worker-{worker['id']}-{encoder}-gpu-decode"
        source = self.root / "fixtures" / (name + ".mkv")
        self.tools.encode(["-i", self.tools.path(fixture), "-c:v", "libx264", "-crf", "1",
                           "-pix_fmt", "yuv420p", "-c:a", "copy", self.tools.path(source)])
        return self.video(name, source, encoder, worker, hardware_decode=True)

    def reconnect_workers(self):
        if not self.restart:
            raise Blocked("This runner cannot restart its owned server")
        active = [w for w in self.api.request("/api/workers") if w["online"] and not w["revokedAt"]]
        if not active:
            raise Blocked("No online workers to exercise reconnect")
        self.restart()
        deadline = time.monotonic() + 120
        while True:
            current = self.api.request("/api/workers")
            missing = [w["name"] for w in active if not any(x["id"] == w["id"] and x["online"]
                       and x["lastSeenAt"] > w["lastSeenAt"] for x in current)]
            if not missing:
                return {"reconnectedWorkers": [w["name"] for w in active], "identitiesPreserved": True}
            require(time.monotonic() < deadline, "Workers failed to reconnect: " + ", ".join(missing))
            time.sleep(.5)

    def unavailable_worker(self, worker, fixture):
        self.select_worker(None)  # All real workers drained; worker-only jobs must stay queued.
        case = self.create_job(f"unavailable-worker-{worker['id']}", fixture, worker=worker)
        try:
            time.sleep(2)
            job = self.api.request(f"/api/jobs?libraryId={case['libraryId']}")[0]
            require(job["status"] == "Queued" and job["waitingForWorker"], "Worker-only job fell back to local encoding")
            require(sha256(case["source"]) == case["sourceSha256"], "Unavailable-worker wait changed source")
            return {"remainedQueued": True, "originalUnchanged": True}
        finally:
            self.api.post(f"/api/jobs/{case['jobId']}/cancel")

    def restore(self):
        errors = []
        def attempt(action):
            try:
                return action()
            except Exception as error:
                errors.append(str(error))
                return None

        # Attempt every independent cleanup even if a previous request failed.
        jobs = attempt(lambda: self.api.request("/api/jobs")) or []
        for job in jobs:
            if job["id"] in self.job_ids and job["status"] not in TERMINAL:
                attempt(lambda: self.api.post(f"/api/jobs/{job['id']}/cancel"))
        current = {w["id"]: w for w in (attempt(lambda: self.api.request("/api/workers")) or [])}
        for worker in self.workers:
            live = current.get(worker["id"])
            if live and not live.get("revokedAt"):
                attempt(lambda: self.api.request(f"/api/workers/{worker['id']}/drain",
                        "POST" if worker["drainRequestedAt"] else "DELETE"))
        attempt(lambda: self.api.request("/api/settings", "PUT", self.settings))
        require(not errors, "Cleanup failed: " + "; ".join(errors))

    def run(self, *, tier="smoke", corpus=None, expected_workers=(), local_encoders=(), variants=None,
            soak_cycles=0, fixture_seconds=8, strict_worker_verification=False):
        if self.report.case("preflight", self.preflight)["status"] != "passed":
            return self.report.exit_code
        self.strict_worker_verification = strict_worker_verification
        # Smoke deliberately exercises the server-verification protocol path; fleet defaults to
        # strict worker evidence. Set either mode explicitly on the fresh test instance.
        self.configure(workerVerificationRequired=strict_worker_verification)
        self.report.environment["strictWorkerVerification"] = strict_worker_verification
        try:
            fixture_dir = self.root / "fixtures"
            variants = variants or (["sdr"] if tier == "smoke" else ["sdr", "vfr", "offset", "ten-bit"])
            if "sdr" not in variants:
                variants = ["sdr", *variants]
            fixtures = {}
            for variant in variants:
                path = fixture_dir / f"{variant}.mkv"
                result = self.report.case("fixture-" + variant, lambda p=path, v=variant: self.tools.fixture(p, v, seconds=fixture_seconds))
                if result["status"] == "passed":
                    fixtures[variant] = path
            if corpus:
                manifest = json.loads(Path(corpus).read_text())
                for item in manifest["clips"]:
                    path = Path(corpus).parent / item["path"]
                    require(sha256(path) == item["sha256"], "Corpus checksum mismatch")
                    fixtures[item["id"]] = path
                save(self.report.root / "corpus.json", manifest)
            primary = fixtures.get("sdr")
            if not primary:
                raise Blocked("Could not generate primary video fixture")
            hardware = self.report.environment["hardware"]["hardware"]["encoders"]
            available = {e["name"] for e in hardware if e["available"]}
            encoders = local_encoders or (["libx265"] if tier == "smoke" else [e for e in MODES if e in available])
            self.report.environment["matrix"] = {"localEncoders": encoders, "fixtures": list(fixtures),
                                                "expectedWorkers": list(expected_workers), "tier": tier,
                                                "soakCycles": soak_cycles, "fixtureSeconds": fixture_seconds}
            for encoder in encoders:
                if encoder not in available:
                    self.report.case("local-" + encoder, lambda e=encoder: (_ for _ in ()).throw(Blocked(f"Required local encoder {e} is unavailable")))
                    continue
                for variant, fixture in fixtures.items():
                    name = f"local-{encoder}-{variant}"
                    self.report.case(name, lambda n=name, f=fixture, e=encoder: self.video(n, f, e))
                if encoder.endswith(("_qsv", "_nvenc", "_vaapi", "_videotoolbox")):
                    self.report.case(f"local-{encoder}-decode-retry-rejection",
                                     lambda e=encoder: self.hardware_decode_rejection(e, primary))
            self.report.case("local-vmaf-rejection", lambda: self.video("local-vmaf-rejection", primary, reject=True))
            self.report.case("queued-cancellation", lambda: self.cancel(primary))
            self.report.case("running-cancellation", self.cancel_running)
            self.report.case("low-disk-space", lambda: self.disk_guard(primary))
            self.report.case("audio-quality-and-rollback", self.audio)
            self.report.case("image-quality-and-rollback", self.image)
            self.report.case("preview-quality-and-cleanup", lambda: self.preview(primary))
            self.report.case("concurrent-isolation", self.concurrent)
            from .faults import worker_faults
            self.report.case("worker-protocol-faults", lambda: worker_faults(self, primary))
            # Real sidecars must tolerate temporary control-plane unavailability before this
            # scenario is added to fleet runs; the container/native server case stands alone.
            if tier == "smoke":
                self.report.case("abrupt-restart-preserves-queue", lambda: self.restart_queue(primary))
            if tier != "smoke":
                self.report.case("local-adaptive", lambda: self.video("local-adaptive", primary, strategy="AdaptiveVmaf"))
                self.report.case("calibration-playable-samples", self.calibration)
                if not any(not w["revokedAt"] for w in self.workers):
                    self.report.case("fleet-workers", lambda: (_ for _ in ()).throw(
                        Blocked("No workers paired; a local-only run cannot certify the fleet")))
                self.report.case("workers-reconnect-after-server-restart", self.reconnect_workers)
                for worker in self.workers:
                    if worker["revokedAt"]:
                        continue
                    self.report.case(f"worker-{worker['id']}-unavailable-placement", lambda w=worker: self.unavailable_worker(w, primary))
                    if not worker["videoEncoders"]:
                        self.report.case(f"worker-{worker['id']}-capabilities", lambda w=worker: (_ for _ in ()).throw(Blocked(f"{w['name']} proved no video encoders")))
                    for encoder in worker["videoEncoders"]:
                        if encoder not in MODES:
                            self.report.case(f"worker-{worker['id']}-{encoder}", lambda e=encoder: (_ for _ in ()).throw(Blocked(f"No acceptance scenario for advertised encoder {e}")))
                            continue
                        self.report.case(f"worker-{worker['id']}-{encoder}-running-cancel", lambda w=worker, e=encoder: self.cancel_running(w, e))
                        if encoder.endswith(("_qsv", "_vaapi", "_nvenc", "_videotoolbox")):
                            self.report.case(f"worker-{worker['id']}-{encoder}-gpu-decode", lambda w=worker, e=encoder: self.worker_gpu_decode(w, e, primary))
                        for variant, fixture in fixtures.items():
                            name = f"worker-{worker['id']}-{encoder}-{variant}"
                            self.report.case(name, lambda n=name, f=fixture, e=encoder, w=worker: self.video(n, f, e, w))
                        name = f"worker-{worker['id']}-{encoder}-adaptive"
                        self.report.case(name, lambda n=name, e=encoder, w=worker: self.video(n, primary, e, w, strategy="AdaptiveVmaf"))
                        name = f"worker-{worker['id']}-{encoder}-vmaf-rejection"
                        self.report.case(name, lambda n=name, e=encoder, w=worker: self.video(n, primary, e, w, reject=True))
                        name = f"worker-{worker['id']}-{encoder}-audio-gates"
                        self.report.case(name, lambda n=name, e=encoder, w=worker: self.video(n, primary, e, w, audio_gates=True))
                for name in missing_workers(self.api.request("/api/workers"), expected_workers):
                    self.report.case(f"required-worker-{name}", lambda n=name: (_ for _ in ()).throw(Blocked(f"{n} is absent, offline, revoked or has no proved encoder")))
            for cycle in range(soak_cycles):
                for encoder in encoders:
                    name = f"soak-{cycle}-local-{encoder}"
                    self.report.case(name, lambda n=name, e=encoder: self.video(n, primary, e))
                if tier == "fleet":
                    for worker in self.workers:
                        if worker["revokedAt"]:
                            continue
                        for encoder in worker["videoEncoders"]:
                            if encoder not in MODES:
                                continue
                            name = f"soak-{cycle}-worker-{worker['id']}-{encoder}"
                            self.report.case(name, lambda n=name, w=worker, e=encoder: self.video(n, primary, e, w))
                save(self.report.root / f"soak-{cycle}.json", {
                    "queue": self.api.request("/api/queue/status"), "workers": self.api.request("/api/workers"),
                    "freeBytes": shutil.disk_usage(self.root).free})
            for damage in ("black", "drop", "truncate"):
                def negative(kind=damage):
                    output = fixture_dir / f"damaged-{kind}.mkv"
                    self.tools.damaged(primary, output, kind)
                    try:
                        scores = self.tools.measure(primary, output, self.report.root / f"oracle-{kind}")
                    except AssertionError as exc:
                        # Structural failures must be specific, never arbitrary process errors.
                        require(kind in ("drop", "truncate") and "Frame loss" in str(exc), str(exc))
                        return {"expectedFailure": str(exc)}
                    failures = quality_failures(scores, DEFAULT_GATES)
                    require(kind == "black" and failures, "Independent verifier accepted deliberate damage")
                    return {"expectedFailure": failures, "scores": scores}
                self.report.case("oracle-rejects-" + damage, negative)
        finally:
            self.report.case("restore-test-settings", self.restore)
        return self.report.exit_code
