"""Execute real application workflows exclusively against an owned test instance."""
from __future__ import annotations

import json
import os
import re
from pathlib import Path
import shutil
import time

from .core import Blocked, inside, quality_failures, require, save, sha256
from .media import compare_report, validate_shadow_report, validate_soundtrack_report

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
            return {"libraryId": library_id, "mediaId": files[0]["id"],
                    "eligibility": candidates[0], "sourceUnchanged": True}
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
        files = [p for p in directory.rglob("*") if p.is_file() and p.suffix.lower() in (".mkv", ".mp4", ".webm", ".m4a", ".mp3", ".opus", ".webp", ".jpg", ".avif")]
        require(len(files) == 1, f"Expected exactly one delivered candidate, found {files}")
        return inside(self.root / "work", files[0])

    def subtitle_mux(self, name, source, encoder="libx265", worker=None):
        fixture = self.root / "fixtures" / (name + ".mp4")
        self.tools.subtitle_fixture(fixture, source)
        return self.video(name, fixture, encoder, worker, check_subtitles=True)

    def subtitle_overlap(self, name, source, encoder="libx265", worker=None, filtered=False):
        fixture = self.root / "fixtures" / (name + ".mkv")
        self.tools.overlapping_subtitle_fixture(fixture, source)
        return self.video(name, fixture, encoder, worker, container="mp4",
            rule_overrides={"keepSubtitleLanguages": "fra"} if filtered else None,
            subtitle_expectations=(["mov_text"], ["fra"], [1]) if filtered
                else (["subrip"] * 3, ["eng", "fra", "jpn"], [0, 1, 2]),
            expected_container="mp4" if filtered else "mkv")

    def alac_copy(self, name, source, encoder="libx265", worker=None, mode="matroska"):
        fixture = self.root / "fixtures" / (name + (".mp4" if mode == "native-mp4" else ".mkv"))
        self.tools.alac_fixture(fixture, source, mixed=mode == "filtered")
        if mode == "remux-noop":
            self.select_worker(worker)
            case = self.create_job(name, fixture, encoder=encoder, worker=worker,
                overrides={"ruleProfile": "RemuxCleanup", "targetVideoCodec": None, "targetContainer": "mp4"},
                expected_ineligible="ALAC")
            self.api.post(f"/api/libraries/{case['libraryId']}/enqueue")
            jobs = self.api.request(f"/api/jobs?libraryId={case['libraryId']}")
            require(not jobs, "Unchanged ALAC remux was queued despite its ineligible reason")
            return {**case, "excludedBeforeQueueing": True}
        return self.video(name, fixture, encoder, worker, container="mp4",
            rule_overrides={"keepAudioLanguages": "eng"} if mode == "filtered" else None,
            audio_expectations=(["flac"], ["eng"], [0]) if mode == "filtered" else (["alac"], ["eng"], [0]),
            expected_container="mkv" if mode == "matroska" else "mp4")

    def video(self, name, fixture, encoder="libx265", worker=None, strategy="Fixed", reject=False, hardware_decode=False, audio_gates=False, check_subtitles=False, container="mkv", rule_overrides=None, subtitle_expectations=None, expected_container=None, audio_expectations=None, check_picture_origin=False, numbered_repeated_fixture=False, check_regular_cadence=False, reference_decoder_timing=False):
        self.select_worker(worker)
        self.configure(encoderMode=MODES[encoder] if not worker else "Cpu", hardwareDecode=hardware_decode)
        gates = {"harmonic": 100, "p5": 100, "minimum": 100} if reject else DEFAULT_GATES
        if (encoder.startswith("h264") or encoder == "libx264") and "ten-bit" in str(fixture):
            return self.create_job(name, fixture, encoder=encoder, worker=worker,
                                   expected_ineligible="limited to 8-bit sources")
        overrides = {"audioLoudnessGateEnabled": True, "maxLoudnessDriftLufs": 1,
                     "audioClippingGateEnabled": True, "maxTruePeakDbtp": 0} if audio_gates else None
        # A floor of 100 is attainable by v1; add measurable compression damage rather than
        # assuming the model can never return its upper bound for a clean tiny fixture.
        overrides = {**(overrides or {}), **({"qualityCrf": 45} if reject else {}),
                     **(rule_overrides or {}), "targetContainer": container}
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
        if not reject:
            require(job["status"] == "ReadyToReplace" and job["verificationPassed"] is True,
                    f"Output failed application verification: {job['status']}: {job['errorMessage']}")
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
        candidate = self.output(case)
        if expected_container:
            require(candidate.suffix.lower() == "." + expected_container, "Unexpected planned output container")
        require(candidate.stat().st_size < case["source"].stat().st_size, "No size saving")
        expected_codec = CODECS.get(encoder, encoder.split("_")[0])
        streams = self.tools.probe(candidate)["streams"]
        require(next(s["codec_name"] for s in streams if s["codec_type"] == "video") == expected_codec,
                "Delivered codec differs from requested codec")
        source_bytes, candidate_bytes = case["source"].stat().st_size, candidate.stat().st_size
        scores = self.tools.measure(case["source"], candidate, directory,
            kept_subtitle_indexes=subtitle_expectations[2] if subtitle_expectations else None,
            kept_audio_indexes=audio_expectations[2] if audio_expectations else None,
            numbered_repeated_fixture=numbered_repeated_fixture, reference_decoder_timing=reference_decoder_timing)
        require(not quality_failures(scores, gates), f"Independent quality gate failed: {scores}")
        compare_report(verification, scores)
        if audio_gates:
            before, after = self.tools.loudness(case["source"]), self.tools.loudness(candidate)
            require(abs(before["lufs"] - after["lufs"]) <= 1, "Worker output loudness drift exceeds 1 LUFS")
            require(after["truePeak"] <= 0, "Worker output introduced clipping")
        if audio_expectations:
            codecs, languages, _ = audio_expectations
            audio = [stream for stream in streams if stream["codec_type"] == "audio"]
            require([stream["codec_name"] for stream in audio] == codecs, "Copied audio codec changed")
            require([stream.get("tags", {}).get("language") for stream in audio] == languages,
                    "Copied audio language or order changed")
        if check_picture_origin:
            self.tools.check_av_start_offset(case["source"], candidate, directory, decoder_timing=reference_decoder_timing)
        if check_regular_cadence:
            self.tools.check_regular_stored_cadence(candidate)
        if check_subtitles or subtitle_expectations:
            codecs, languages, source_indexes = subtitle_expectations or (["ass", "ass"], ["eng", "fra"], [0, 1])
            subtitles = [stream for stream in streams if stream["codec_type"] == "subtitle"]
            require([stream["codec_name"] for stream in subtitles] == codecs,
                    "Subtitle output codecs differ from the preserving plan")
            require([stream.get("tags", {}).get("language") for stream in subtitles] == languages,
                    "Subtitle language or order changed")
            for index, source_index in enumerate(source_indexes):
                before = self.tools.subtitle_cues(case["source"], source_index, picture_origin=check_picture_origin,
                    generate_pts=check_picture_origin and not reference_decoder_timing)
                after = self.tools.subtitle_cues(candidate, index, picture_origin=check_picture_origin)
                require(bool(before) and before == after, f"Subtitle {index} text or timing changed")
                (directory / f"subtitle-{index}.srt").write_text(after, encoding="utf-8")
        self.replace_restore(case, candidate)
        return {"jobId": case["jobId"], "workerId": worker["id"] if worker else None,
                "encoder": encoder, "strategy": strategy, "scores": scores, "restoredOriginal": True,
                "sourceBytes": source_bytes, "candidateBytes": candidate_bytes,
                "savingPercent": 100 * (1 - candidate_bytes / source_bytes)}

    def numbered_picture_quality_control(self, fixture):
        directory = self.report.root / "numbered-picture-quality-control"
        directory.mkdir(parents=True, exist_ok=True)
        candidate = directory / "damaged.mp4"
        original_hash = sha256(fixture)
        probe = self.tools.probe(fixture)
        first = self.tools.first_av_timestamps(fixture, generate_pts=True)["video"]
        offset = max(0, float(probe["format"]["start_time"]) - first)
        # The ordinary cadence filter discards picture 4 at the repeated time. Damage
        # only its body: identity markers must survive and sequential quality must see it.
        self.tools.encode(["-fflags", "+genpts", "-itsoffset", str(offset), "-i", self.tools.path(fixture),
            "-map", "0:V:0", "-map", "0:a:0", "-map", "0:s:0",
            "-vf", "drawbox=y=48:h=132:color=black:t=fill:enable='eq(n,4)'",
            "-c:v", "libx264", "-crf", "0", "-preset", "fast", "-fps_mode", "passthrough",
            "-enc_time_base:v:0", "demux", "-c:a", "alac", "-c:s", "mov_text", self.tools.path(candidate)])
        scores = self.tools.measure(fixture, candidate, directory, numbered_repeated_fixture=True)
        frames = json.loads((directory / "vmaf.json").read_text())["frames"]
        damaged_score = frames[4]["metrics"]["vmaf"]
        require(damaged_score < 75, "Quality oracle omitted the damaged repeated-time picture")
        require(sha256(fixture) == original_hash, "Negative quality control changed its source")
        return {"damagedPictureIndex": 4, "damagedPictureVmaf": damaged_score,
                "numberedPictures": scores["frames"], "originalUnchanged": True}

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


    def soundtrack(self, codec="aac", worker=None, *, filtered=False, reject=False, copied=False, long=False, short=False):
        self.select_worker(worker)
        name = (f"worker-{worker['id']}" if worker else "local") + "-soundtrack-" + codec
        name += "-filtered" if filtered else "-rejected" if reject else "-copied" if copied else "-passed"
        name += "-long" if long else "-short-mp4" if short else ""
        seconds = 120 if long else 12 if short else 8
        audio_seconds = seconds - 0.5 if short else seconds
        commentary_seconds = seconds - 2.5 if long else audio_seconds
        fixture = self.root / "fixtures" / (name + ".mkv")
        self.tools.encode(["-f", "lavfi", "-i", f"testsrc2=size=64x64:rate=10:duration={seconds}",
            "-f", "lavfi", "-i", f"anoisesrc=color=pink:amplitude=0.1:duration={audio_seconds}:sample_rate=48000:seed=42",
            "-f", "lavfi", "-i", f"sine=frequency=700:sample_rate=48000:duration={commentary_seconds}",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-c:v", "libx264", "-c:a", "pcm_s16le", "-ac", "2",
            "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=" if short else "title=Main",
            "-metadata:s:a:1", "language=fra", "-metadata:s:a:1", "title=" if short else "title=Commentary", self.tools.path(fixture)])
        # Limits 1 and 0 are deterministic acceptance controls, not calibrated listening presets.
        limit = 0 if reject else 1
        case = self.create_job(name, fixture, encoder="libx264", worker=worker, overrides={
            "targetVideoCodec": None, "targetContainer": "mp4" if short else "mkv", "videoAudioCodec": "copy" if copied else codec,
            "videoAudioBitrateKbps": 128, "vmafQualityGateEnabled": False, "requireSizeReduction": False,
            "soundtrackQualityGateEnabled": True, "maximumSoundtrackQualityDistance": limit,
            "keepAudioLanguages": "fra" if filtered else None})
        job = self.wait_job(case)
        save(self.report.root / name / "job.json", job)
        verification = json.loads(job["verificationReportJson"] or "{}")
        require(job["workerName"] == (worker["name"] if worker else None), "Soundtrack ran on the wrong host")
        require(sha256(case["source"]) == case["sourceSha256"], "Assessment changed the source")
        require(job["status"] == ("Failed" if reject else "ReadyToReplace"), f"Unexpected soundtrack job verdict: {job['errorMessage']}")
        if copied:
            require(not verification.get("soundtrackQuality"), "Copied audio launched perceptual assessment")
        else:
            location = "Worker" if worker and self.strict_worker_verification else "Server"
            validate_soundtrack_report(verification.get("soundtrackQuality"), [1] if filtered else [0, 1], location, limit, passes=not reject)
            if long:
                for track in verification["soundtrackQuality"]["tracks"]:
                    assessment = track["report"]["evidence"]["assessment"]
                    require(len(assessment["windows"]) == 3, "Long soundtrack did not sample beginning, middle and end")
                    require(abs(sum(w["window"]["durationSeconds"] for w in assessment["windows"]) - 90) < 1e-6,
                            "Long soundtrack exceeds the assigned 90-second budget")
        if reject:
            require(not any(r["jobId"] == case["jobId"] for r in self.api.request("/api/replacements")), "Failed gate replaced media")
            return {"originalUnchanged": True, "gateBlocked": True}
        candidate = self.output(case)
        probe = self.tools.probe(candidate)
        tracks = [stream for stream in probe["streams"] if stream["codec_type"] == "audio"]
        require([track.get("tags", {}).get("language") for track in tracks] == (["fra"] if filtered else ["eng", "fra"]),
                "Retained soundtrack language changed")
        self.tools.run(self.tools.ffmpeg, ["-v", "error", "-xerror", "-i", self.tools.path(candidate), "-f", "null", "-"])
        self.replace_restore(case, candidate)
        return {"originalUnchanged": True, "restoredOriginal": True, "tracks": len(tracks), "copied": copied}

    def audio(self, codec="aac", worker=None, *, artwork=False, downmix=False):
        self.select_worker(worker)
        name = f"worker-{worker['id']}-audio-{codec}" if worker else f"audio-{codec}"
        if artwork: name += "-artwork"
        if downmix: name += "-downmix"
        fixture = self.root / "fixtures" / f"{name}.flac"
        inputs = ["-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.1:duration=45:sample_rate=48000:seed=42"]
        cover = []
        if artwork:
            image = self.root / "fixtures" / "audio-cover.jpg"
            self.tools.encode(["-f", "lavfi", "-i", "testsrc2=size=128x128:rate=1", "-frames:v", "1", self.tools.path(image)])
            inputs += ["-i", self.tools.path(image)]
            cover = ["-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v", "attached_pic"]
        self.tools.encode(inputs + cover + ["-ac", "6" if downmix else "2", "-c:a", "flac",
            "-metadata", "artist=Optimisarr acceptance", "-metadata", "title=Audio fixture", self.tools.path(fixture)])
        case = self.create_job(name, fixture, worker=worker, overrides={"mediaType": "Music", "audioTargetCodec": codec,
            "downmixToStereo": downmix,
            "audioBitrateKbps": 128, "vmafQualityGateEnabled": False, "audioLoudnessGateEnabled": True,
            "maxLoudnessDriftLufs": 1, "audioClippingGateEnabled": True, "maxTruePeakDbtp": 0})
        job = self.wait_job(case)
        save(self.report.root / name / "job.json", job)
        require(job["status"] == "ReadyToReplace", f"Audio failed: {job['errorMessage']}")
        candidate = self.output(case)
        before, after = self.tools.loudness(case["source"]), self.tools.loudness(candidate)
        require(abs(before["lufs"] - after["lufs"]) <= 1, "Audio loudness drift exceeds 1 LUFS")
        require(after["truePeak"] <= 0, "Audio re-encode introduced clipping")
        probe = self.tools.probe(candidate)
        audio = [stream for stream in probe["streams"] if stream["codec_type"] == "audio"]
        require(len(audio) == 1 and audio[0]["codec_name"] == codec, "Wrong audio encoder output")
        require(audio[0]["channels"] == 2, "Unexpected channel layout")
        tags = {**probe["format"].get("tags", {}), **audio[0].get("tags", {})}
        tags = {key.lower(): value for key, value in tags.items()}
        require(tags.get("artist") == "Optimisarr acceptance" and tags.get("title") == "Audio fixture", "Lost audio metadata")
        if artwork:
            require(any(stream.get("disposition", {}).get("attached_pic") == 1 for stream in probe["streams"]), "Lost embedded cover art")
        require(job["workerName"] == (worker["name"] if worker else None), "Audio ran on the wrong host")
        verification = json.loads(job["verificationReportJson"] or "{}")
        if worker and getattr(self, "strict_worker_verification", False):
            require(verification.get("context", {}).get("verificationLocation") == "Worker", "Audio verification fell back to the server")
        self.tools.run(self.tools.ffmpeg, ["-v", "error", "-xerror", "-i", self.tools.path(candidate), "-f", "null", "-"])
        save(self.report.root / name / "independent-audio.json", {"source": before, "candidate": after, "probe": probe})
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

    def image_perceptual(self, target="jpeg", *, reject=False, reporting=True, gate=True, unsupported=False):
        self.select_worker(None)
        name = f"image-perceptual-{target}-{'unsupported' if unsupported else 'reject' if reject else 'gate' if gate else 'report' if reporting else 'off'}"
        fixture = self.root / "fixtures" / (name + (".bmp" if unsupported else ".png"))
        self.tools.encode(["-f", "lavfi", "-i", "testsrc2=size=512x512:rate=1:duration=1",
            "-vf", "noise=alls=40:allf=t", "-pix_fmt", "bgr24" if unsupported else "rgb24", "-frames:v", "1", self.tools.path(fixture)])
        case = self.create_job(name, fixture, overrides={"mediaType": "Photo", "targetImageFormat": target,
            "imageQuality": 10 if reject else 95, "reencodeLossyImages": True, "vmafQualityGateEnabled": False,
            "imageQualityGateEnabled": True, "minimumImageSsim": 0, "imageMetadataGateEnabled": True,
            "requireSizeReduction": False, "imagePerceptualReportingEnabled": reporting,
            "imagePerceptualGateEnabled": gate, "minimumImagePerceptualScore": 100 if reject else 0 if gate else None})
        job = self.wait_job(case)
        save(self.report.root / name / "job.json", job)
        require(sha256(case["source"]) == case["sourceSha256"], "Image assessment changed the source")
        report = json.loads(job["verificationReportJson"])
        image = report.get("imagePerceptualQuality")
        failed = reject or unsupported
        require(job["status"] == ("Failed" if failed else "ReadyToReplace"), f"Unexpected image verdict: {job['errorMessage']}")
        if reporting or gate:
            require(image is not None and image["measurementLocation"] == "Server", "Missing server image evidence")
            if unsupported:
                require(image["measurement"] is None and image["error"], "Unsupported image received a score")
            else:
                measurement = image["measurement"]
                require(measurement and measurement["width"] == 512 and measurement["height"] == 512, "Incomplete image coverage")
                require(image["revision"] == "a7a9c787341cf703dede03c2009fa460cae5e5df", "Wrong metric revision")
                require(image["sourceSha256"].lower() == case["sourceSha256"].lower(), "Evidence source hash differs")
                require(image["candidateSha256"].lower() == sha256(self.output(case)).lower(), "Evidence candidate hash differs")
            require(image["gateEnabled"] == gate and (not gate or image["gatePassed"] == (not failed)), "Incorrect image gate attribution")
        else:
            require(image is None, "Disabled image reporting still ran")
        if failed:
            require(not any(r["jobId"] == case["jobId"] for r in self.api.request("/api/replacements")), "Rejected image acquired replacement history")
        else:
            self.replace_restore(case, self.output(case))
        return {"originalUnchanged": True, "gateBlocked": failed, "restoredOriginal": not failed,
                "report": image, "scope": "Synthetic stills; CPU image encoding and server-local assessment"}

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
            soak_cycles=0, fixture_seconds=8, strict_worker_verification=False, regression=None):
        if self.report.case("preflight", self.preflight)["status"] != "passed":
            return self.report.exit_code
        self.strict_worker_verification = strict_worker_verification
        # Smoke deliberately exercises the server-verification protocol path; fleet defaults to
        # strict worker evidence. Set either mode explicitly on the fresh test instance.
        self.configure(workerVerificationRequired=strict_worker_verification)
        self.report.environment["strictWorkerVerification"] = strict_worker_verification
        try:
            if regression == "image-quality":
                self.report.case("image-perceptual-jpeg", lambda: self.image_perceptual("jpeg"))
                self.report.case("image-perceptual-webp", lambda: self.image_perceptual("webp"))
                self.report.case("image-perceptual-reject", lambda: self.image_perceptual(reject=True))
                self.report.case("image-perceptual-report", lambda: self.image_perceptual(gate=False))
                self.report.case("image-perceptual-off", lambda: self.image_perceptual(reporting=False, gate=False))
                self.report.case("image-perceptual-unsupported", lambda: self.image_perceptual(unsupported=True))
                return self.report.exit_code
            fixture_dir = self.root / "fixtures"
            variants = variants or (["sdr"] if tier == "smoke" else ["sdr", "vfr", "offset", "ten-bit"])
            if regression == "fractional-timing" and "fractional" not in variants:
                variants = [*variants, "fractional"]
            if regression == "uneven-timing" and "uneven" not in variants:
                variants = [*variants, "uneven"]
            if regression == "initial-pictures":
                variants = list(dict.fromkeys([*variants, "dts-only", "dts-only-no-subtitles", "dts-repeated", "dts-bframes", "dts-h264"]))
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

            if regression == "soundtrack-quality":
                for worker in [None, *[w for w in self.workers if w["online"] and not w["revokedAt"]]]:
                    label = f"worker-{worker['id']}" if worker else "local"
                    for codec in ("aac", "opus", "mp3"):
                        self.report.case(label + "-soundtrack-" + codec, lambda c=codec, w=worker: self.soundtrack(c, w))
                    self.report.case(label + "-soundtrack-filtered", lambda w=worker: self.soundtrack("aac", w, filtered=True))
                    self.report.case(label + "-soundtrack-rejected", lambda w=worker: self.soundtrack("aac", w, reject=True))
                    self.report.case(label + "-soundtrack-copied", lambda w=worker: self.soundtrack("aac", w, copied=True))
                    self.report.case(label + "-soundtrack-short", lambda w=worker: self.soundtrack("aac", w, short=True))
                    self.report.case(label + "-soundtrack-long", lambda w=worker: self.soundtrack("aac", w, long=True))
                if tier == "fleet" and not any(w["online"] and not w["revokedAt"] for w in self.workers):
                    self.report.case("soundtrack-workers", lambda: (_ for _ in ()).throw(Blocked("No soundtrack workers paired")))
                return self.report.exit_code
            if regression == "audio":
                for codec in ("aac", "opus", "mp3"):
                    self.report.case(f"local-audio-{codec}", lambda c=codec: self.audio(c))
                self.report.case("local-audio-artwork", lambda: self.audio("mp3", artwork=True))
                usable = [worker for worker in self.workers if worker["online"] and not worker["revokedAt"]]
                if tier == "fleet" and not usable:
                    self.report.case("audio-workers", lambda: (_ for _ in ()).throw(Blocked("No audio workers paired")))
                for worker in usable:
                    for codec, encoder in (("aac", "aac"), ("opus", "libopus"), ("mp3", "libmp3lame")):
                        name = f"worker-{worker['id']}-audio-{codec}"
                        if encoder not in worker["audioEncoders"]:
                            self.report.case(name, lambda e=encoder: (_ for _ in ()).throw(Blocked(f"Audio encoder {e} was not proved")))
                            continue
                        self.report.case(name, lambda c=codec, w=worker: self.audio(c, w))
                    self.report.case(f"worker-{worker['id']}-audio-downmix", lambda w=worker: self.audio("aac", w, downmix=True))
                    self.report.case(f"worker-{worker['id']}-audio-artwork", lambda w=worker: self.audio("mp3", w, artwork=True))
                return self.report.exit_code
            if regression in ("subtitle-mux", "fractional-timing", "uneven-timing", "initial-pictures", "subtitle-overlap", "alac-copy"):
                if regression == "initial-pictures":
                    require("dts-repeated" in fixtures, "Numbered repeated-timestamp fixture could not be generated")
                    self.report.case("numbered-picture-quality-control",
                                     lambda: self.numbered_picture_quality_control(fixtures["dts-repeated"]))
                def regression_case(name, encoder, worker=None):
                    if regression == "subtitle-mux":
                        return self.subtitle_mux(name, primary, encoder, worker)
                    if regression == "subtitle-overlap":
                        return self.subtitle_overlap(name, primary, encoder, worker)
                    if regression == "alac-copy":
                        return self.alac_copy(name, primary, encoder, worker)
                    if regression == "initial-pictures":
                        require("dts-only" in fixtures, "DTS-only fixture could not be generated")
                        with_subtitles = self.video(name, fixtures["dts-only"], encoder, worker, container="mp4",
                            subtitle_expectations=(["mov_text"], ["eng"], [0]), check_picture_origin=True)
                        require("dts-only-no-subtitles" in fixtures, "Subtitle-free DTS-only fixture could not be generated")
                        without_subtitles = self.video(name + "-no-subtitles", fixtures["dts-only-no-subtitles"], encoder, worker,
                            container="mp4", check_picture_origin=True)
                        require("dts-repeated" in fixtures, "Numbered repeated-timestamp fixture could not be generated")
                        repeated = self.video(name + "-repeated", fixtures["dts-repeated"], encoder, worker, container="mp4",
                            subtitle_expectations=(["mov_text"], ["eng"], [0]), check_picture_origin=True,
                            numbered_repeated_fixture=True)
                        require("dts-bframes" in fixtures, "Decode-only B-frame fixture could not be generated")
                        reordered = self.video(name + "-bframes", fixtures["dts-bframes"], encoder, worker, container="mp4",
                            subtitle_expectations=(["mov_text"], ["eng"], [0]), check_picture_origin=True,
                            check_regular_cadence=True, reference_decoder_timing=True)
                        require("dts-h264" in fixtures, "Decode-only H.264 fixture could not be generated")
                        in_order = self.video(name + "-h264", fixtures["dts-h264"], encoder, worker, container="mp4",
                            subtitle_expectations=(["mov_text"], ["eng"], [0]), check_picture_origin=True,
                            check_regular_cadence=True, reference_decoder_timing=True)
                        return {"withSubtitles": with_subtitles, "withoutSubtitles": without_subtitles,
                                "repeatedTimestamps": repeated, "reorderedDecodeOnly": reordered,
                                "inOrderDecodeOnly": in_order}
                    if regression == "uneven-timing":
                        require("uneven" in fixtures, "Uneven timestamp fixture could not be generated")
                        return self.video(name, fixtures["uneven"], encoder, worker, container="mp4")
                    require("fractional" in fixtures, "Fractional timestamp fixture could not be generated")
                    return self.video(name, fixtures["fractional"], encoder, worker, container="mp4")
                suffix = {"subtitle-mux": "mov-text-to-mkv", "fractional-timing": "fractional-to-mp4",
                          "uneven-timing": "uneven-to-mp4", "initial-pictures": "dts-only-to-mp4",
                          "subtitle-overlap": "overlapping-cues-to-mkv", "alac-copy": "alac-to-mkv"}[regression]
                for encoder in encoders:
                    name = f"local-{encoder}-{suffix}"
                    self.report.case(name, lambda n=name, e=encoder: regression_case(n, e))
                    if regression == "subtitle-overlap":
                        filtered_name = f"local-{encoder}-filtered-cues-to-mp4"
                        self.report.case(filtered_name, lambda n=filtered_name, e=encoder: self.subtitle_overlap(n, primary, e, filtered=True))
                    if regression == "alac-copy":
                        for mode in ("native-mp4", "filtered", "remux-noop"):
                            extra_name = f"local-{encoder}-{mode}-alac-copy"
                            self.report.case(extra_name, lambda n=extra_name, e=encoder, m=mode: self.alac_copy(n, primary, e, mode=m))
                if tier == "fleet":
                    for name in missing_workers(self.workers, expected_workers):
                        self.report.case("required-worker-" + name, lambda n=name: (_ for _ in ()).throw(Blocked(f"Required worker {n} is unavailable")))
                    usable = [worker for worker in self.workers if worker["online"] and not worker["revokedAt"]]
                    if not usable:
                        self.report.case("fleet-workers", lambda: (_ for _ in ()).throw(Blocked("No workers paired")))
                    for worker in usable:
                        for encoder in worker["videoEncoders"]:
                            name = f"worker-{worker['id']}-{encoder}-{suffix}"
                            self.report.case(name, lambda n=name, e=encoder, w=worker: regression_case(n, e, w))
                            if regression == "subtitle-overlap":
                                filtered_name = f"worker-{worker['id']}-{encoder}-filtered-cues-to-mp4"
                                self.report.case(filtered_name, lambda n=filtered_name, e=encoder, w=worker: self.subtitle_overlap(n, primary, e, w, filtered=True))
                            if regression == "alac-copy":
                                for mode in ("native-mp4", "filtered", "remux-noop"):
                                    extra_name = f"worker-{worker['id']}-{encoder}-{mode}-alac-copy"
                                    self.report.case(extra_name, lambda n=extra_name, e=encoder, w=worker, m=mode: self.alac_copy(n, primary, e, w, mode=m))
                return self.report.exit_code
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
            self.report.case("local-mov-text-to-mkv", lambda: self.subtitle_mux("local-mov-text-to-mkv", primary))
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
                        name = f"worker-{worker['id']}-{encoder}-mov-text-to-mkv"
                        self.report.case(name, lambda n=name, e=encoder, w=worker: self.subtitle_mux(n, primary, e, w))
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
