"""Real preview/calibration workflows on isolated servers and disposable physical workers."""
from __future__ import annotations

import hashlib
import json
import sqlite3
import shutil
import time
import urllib.request
from pathlib import Path

from .core import Blocked, inside, require, save, sha256


def rows(harness, query, parameters=()):
    database = harness.root / "config" / "optimisarr.db"
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
        db.row_factory = sqlite3.Row
        return [dict(row) for row in db.execute(query, parameters)]


def binary(api, path):
    request = urllib.request.Request(api.url + path, headers={"Authorization": "Bearer " + api.token})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()


def wait(harness, read, ready):
    deadline = time.monotonic() + harness.timeout
    while time.monotonic() < deadline:
        value = read()
        if value.get("status") in ("Failed", "Cancelled"):
            raise AssertionError(f"Interactive work failed: {value}")
        if ready(value):
            return value
        time.sleep(.2)
    raise Blocked("Interactive work exceeded its acceptance deadline")


def check_job(harness, row, encoder, worker):
    require(row["Status"] == "Completed" and row["Type"] in ("Preview", "Calibration"),
            "A disposable job did not finish as disposable completed work")
    require(row["VideoEncoder"] == encoder, f"Wrong actual encoder: {row['VideoEncoder']}")
    report = json.loads(row["VerificationReportJson"])
    require(all(check["outcome"] not in (1, "Failed") for check in report["checks"]),
            "A sample failed structural verification")
    leases = rows(harness, 'SELECT * FROM JobLeases WHERE JobId = ?', (row["Id"],))
    if worker:
        require(leases and all(lease["WorkerId"] == worker["id"] for lease in leases), "Work ran on another worker")
        require(report.get("context", {}).get("verificationLocation") == "Worker", "Strict verification fell back to the server")
        require(all(lease["VerificationContractJson"] and lease["VerificationEvidenceJson"] for lease in leases),
                "Strict worker measurements are missing")
        frozen = json.loads(leases[-1]["VerificationWorkJson"])
        sample = frozen["workerSample"]
        reference = inside(harness.root / "work", Path(sample["path"]))
        candidate = inside(harness.root / "work", Path(row["WorkOutputPath"]))
        require(sha256(reference) == row["SourceSha256"], "The transferred reference identity changed")
        require(reference.stat().st_size == sample["bytes"], "Sample scratch accounting used another source size")
        # The independent oracle counts every decoded frame and checks lossless copied audio.
        oracle = harness.tools.measure(reference, candidate, harness.root / f"interactive-oracle-{row['Id']}")
    else:
        require(not leases, "Local-only work unexpectedly acquired a lease")
        oracle = None
    require(not any(item["jobId"] == row["Id"] for item in harness.api.request("/api/replacements")),
            "Disposable work acquired replacement history")
    return {"jobId": row["Id"], "type": row["Type"], "encoder": encoder,
            "verificationLocation": report.get("context", {}).get("verificationLocation"), "oracle": oracle}


def preserve_failed_media(harness, directory):
    # Keep reproducible disposable inputs/candidates before supported cleanup removes them.
    destination = directory / "failed-media"
    if not destination.exists():
        shutil.copytree(harness.root / "work", destination)


def interactive_video(harness, fixture, worker, encoder):
    harness.select_worker(worker)
    harness.configure(encoderMode="Cpu" if encoder.startswith("lib") else "Auto")
    name = "interactive-" + (worker["name"] if worker else "local") + "-" + encoder
    case = harness.create_job(name, fixture, worker=worker, encoder=encoder, enqueue=False)
    directory = harness.root / name
    directory.mkdir()
    source_hash = sha256(case["source"])
    worker_id = worker["id"] if worker else None
    if worker:
        harness.api.post(f"/api/workers/{worker_id}/drain")
    preview_id = harness.api.post(f"/api/media/{case['mediaId']}/preview")["jobId"]
    if worker:
        time.sleep(3)
        require(harness.api.request(f"/api/preview/{preview_id}")["status"] == "Queued",
                "WorkerOnly interactive work fell back to the server")
        harness.api.request(f"/api/workers/{worker_id}/drain", "DELETE")
    try:
        preview = wait(harness, lambda: harness.api.request(f"/api/preview/{preview_id}"),
                       lambda value: value["status"] == "Completed")
        preview_row = rows(harness, 'SELECT * FROM Jobs WHERE Id = ?', (preview_id,))[0]
        evidence = [check_job(harness, preview_row, encoder, worker)]
        require(preview["encoded"]["videoCodec"] == case["settings"]["targetVideoCodec"], "Preview used another codec")
        if worker:
            require(preview.get("originalUrl") and preview["clipStartSeconds"] == 0,
                    "Remote preview did not expose its matched bounded reference")
            content = binary(harness.api, preview["originalUrl"])
            require(hashlib.sha256(content).hexdigest() == preview_row["SourceSha256"], "Browser preview reference differs from worker source")
        save(directory / "preview.json", preview)
    except Exception:
        preserve_failed_media(harness, directory)
        raise
    finally:
        harness.api.request(f"/api/preview/{preview_id}", "DELETE")

    session = harness.api.post(f"/api/libraries/{case['libraryId']}/calibration",
        {"mediaFileId": case["mediaId"], "diagnosticsEnabled": True, "compareVideoPresets": False})
    session_id = session["id"]
    try:
        session = wait(harness, lambda: harness.api.request(f"/api/calibration/{session_id}"),
                       lambda value: value["status"] == "Comparing")
        samples = rows(harness, 'SELECT * FROM Jobs WHERE lower(CalibrationSessionId) = ?', (session_id.lower(),))
        require(len(samples) == 15 and all(row["RequestedRuleProfile"] is None for row in samples),
                "Default calibration compared codec presets instead of saved settings")
        for row in samples:
            evidence.append(check_job(harness, row, encoder, worker))
        candidates = [variant for variant in session["variants"] if not variant["isOriginal"]]
        require(all(variant["diagnostics"]["codec"] == case["settings"]["targetVideoCodec"] for variant in candidates),
                "Calibration used another codec")
        original = next(variant for variant in session["variants"] if variant["isOriginal"])
        for scene, sample in enumerate(original["samples"]):
            content = binary(harness.api, sample["url"])
            require(content, "Original comparison stream was not served")
            if worker:
                require(all(variant["samples"][scene]["startSeconds"] == sample["startSeconds"] for variant in candidates),
                        "Worker candidate and source have different playback clocks")
        result = harness.api.post(f"/api/calibration/{session_id}/classifications",
            {"classifications": {variant["name"]: "Acceptable" for variant in candidates}})
        require(result["result"]["recommendedProfile"] is None, "Selected-settings check recommended another codec preset")
        harness.api.post(f"/api/calibration/{session_id}/apply")
        library = next(item for item in harness.api.request("/api/libraries") if item["id"] == case["libraryId"])
        require(library["targetVideoCodec"] == case["settings"]["targetVideoCodec"]
                and library["targetContainer"] == case["settings"]["targetContainer"], "Apply changed codec/container")
        require(library["qualityCrf"] == result["result"]["recommendedQuality"], "Apply did not update only quality")
        save(directory / "calibration.json", result)
        save(directory / "jobs.json", evidence)
    except Exception:
        preserve_failed_media(harness, directory)
        raise
    finally:
        harness.api.request(f"/api/calibration/{session_id}", "DELETE")
    require(sha256(case["source"]) == source_hash, "Interactive work changed the original")
    require(not rows(harness, 'SELECT Id FROM Jobs WHERE LibraryId = ? AND Type = ?', (case["libraryId"], "Normal")),
            "Interactive work created a normal job")
    return {"workerId": worker_id, "encoder": encoder, "previewAndCalibration": evidence, "sourceUnchanged": True}


def interactive_cancellation(harness, fixture, worker, encoder):
    harness.select_worker(worker)
    harness.configure(encoderMode="Cpu" if encoder.startswith("lib") else "Auto")
    case = harness.create_job("cancel-" + worker["name"], fixture, worker=worker, encoder=encoder, enqueue=False)
    worker_id = worker["id"]
    harness.api.post(f"/api/workers/{worker_id}/drain")
    queued = harness.api.post(f"/api/media/{case['mediaId']}/preview")["jobId"]
    harness.api.request(f"/api/preview/{queued}", "DELETE")
    require(not rows(harness, 'SELECT Id FROM Jobs WHERE Id = ?', (queued,)), "Queued cancellation retained runnable work")
    harness.api.request(f"/api/workers/{worker_id}/drain", "DELETE")
    running = harness.api.post(f"/api/media/{case['mediaId']}/preview")["jobId"]
    deadline = time.monotonic() + harness.timeout
    while time.monotonic() < deadline:
        leases = rows(harness, 'SELECT Id FROM JobLeases WHERE JobId = ? AND State = ?', (running, "Held"))
        if leases:
            break
        time.sleep(.05)
    require(bool(leases), "Cancellation never observed an active worker lease")
    harness.api.request(f"/api/preview/{running}", "DELETE")
    while time.monotonic() < deadline:
        held = rows(harness, 'SELECT Id FROM JobLeases WHERE JobId = ? AND State = ?', (running, "Held"))
        if not held:
            break
        time.sleep(.1)
    require(not held, "Cancelled preview retained a held lease")
    require(not rows(harness, 'SELECT Id FROM Jobs WHERE Id = ?', (running,)), "Delivery resurrected a deleted preview")
    require(sha256(case["source"]) == case["sourceSha256"], "Cancellation changed the source")
    return {"queuedJobId": queued, "activeJobId": running, "workerId": worker_id, "sourceUnchanged": True}


def worker_quality_retry(harness, fixture, worker):
    result = harness.video("cpu-worker-quality-retry-" + worker["name"], fixture, "libx265", worker,
        reject=True, rule_overrides={"requireSizeReduction": False})
    job = next(job for job in harness.api.request("/api/jobs") if job["id"] == result["jobId"])
    leases = rows(harness, 'SELECT WorkerId, VerificationWorkJson FROM JobLeases WHERE JobId = ?', (result["jobId"],))
    require(job["qualityRetryCount"] == 1 and len(leases) == 2, "Higher quality retry was not exercised exactly once")
    require(all(lease["WorkerId"] == worker["id"] and json.loads(lease["VerificationWorkJson"])["videoEncoder"] == "libx265" for lease in leases),
            "WorkerOnly CPU retry moved to the server or a GPU encoder")
    return {**result, "retryCount": 1, "workerId": worker["id"], "leaseCount": len(leases)}


def run_interactive_matrix(harness, *, tier, expected_workers, lifecycle_only=False):
    if tier == "fleet":
        available = {worker["name"] for worker in harness.workers if worker["online"] and not worker["revokedAt"]}
        require(expected_workers and set(expected_workers) <= available, "Required physical workers are not all online")
    fixture = harness.root / "fixtures" / "interactive.mkv"
    fixture.parent.mkdir(exist_ok=True)
    harness.tools.encode(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24:duration=64",
        "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=64",
        "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "fast", "-crf", "16",
        "-g", "96", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", harness.tools.path(fixture)])
    if tier != "fleet":
        harness.report.case("interactive-local-saved-hevc", lambda: interactive_video(harness, fixture, None, "libx265"))
        return
    for worker in harness.workers:
        if worker["name"] not in expected_workers:
            continue
        encoder = worker["videoEncoders"][0]
        if not lifecycle_only:
            harness.report.case("interactive-" + worker["name"],
                lambda worker=worker, encoder=encoder: interactive_video(harness, fixture, worker, encoder))
        harness.report.case("cancel-" + worker["name"],
            lambda worker=worker, encoder=encoder: interactive_cancellation(harness, fixture, worker, encoder))
        if encoder == "libx265":
            harness.report.case("quality-retry-" + worker["name"],
                lambda worker=worker: worker_quality_retry(harness, fixture, worker))
    if lifecycle_only:
        harness.report.case("interactive-worker-reconnect", harness.reconnect_workers)
