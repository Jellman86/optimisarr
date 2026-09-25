"""Adversarial wire-protocol acceptance against real queued media, without database edits."""
import hashlib
import json
import urllib.error
import urllib.request

from .core import require, sha256


def wire(api, path, *, token, method="GET", body=None, headers=None):
    request = urllib.request.Request(api.url + path, method=method, data=body,
        headers={"Authorization": "Bearer " + token, **(headers or {})})
    try:
        response = urllib.request.urlopen(request, timeout=120)
    except urllib.error.HTTPError as exc:
        response = exc
    with response:
        return response.status, dict(response.headers), response.read()


def worker_faults(harness, fixture):
    api = harness.api
    harness.select_worker(None)
    harness.configure(remoteWorkersEnabled=True)
    pin = api.post("/api/workers/pairing-code")["code"]
    pair = api.post("/api/workers/pair", {"code": pin, "name": "acceptance-protocol-faults",
        "operatingSystem": "test-protocol-client", "architecture": "test",
        "protocolMinimum": 1, "protocolMaximum": 2, "videoEncoders": ["libx265"],
        "audioEncoders": ["aac"], "hardwareDecoders": [], "vmaf": "Cpu",
        "freeScratchBytes": 10_000_000_000, "maxConcurrency": 1})
    token = pair["credential"]
    case = None
    try:
        code, _, _ = wire(api, "/api/workers/heartbeat", token=token, method="POST",
            body=json.dumps({"freeScratchBytes": 10_000_000_000, "maxConcurrency": 1,
                             "protocolMinimum": 1, "protocolMaximum": 2}).encode(),
            headers={"Content-Type": "application/json"})
        require(code == 200, "Protocol fixture heartbeat failed")
        case = harness.create_job("protocol-faults", fixture, worker=pair)
        code, _, body = wire(api, "/api/workers/claim", token=token, method="POST")
        require(code == 200, "Protocol fixture could not claim real queued media")
        assignment = json.loads(body)
        require(assignment["jobId"] == case["jobId"], "Claimed a different job")
        lease = f"/api/workers/leases/{assignment['leaseId']}"
        code, _, _ = wire(api, lease + "/source", token="invalid")
        require(code == 401, "Source transfer accepted a wrong credential")
        code, headers, data = wire(api, lease + "/source", token=token)
        require(code == 200 and hashlib.sha256(data).hexdigest() == case["sourceSha256"], "Source transfer changed bytes")
        code, _, part = wire(api, lease + "/source", token=token, headers={"Range": "bytes=3-12"})
        require(code == 206 and part == data[3:13], "Source range resume returned incorrect bytes")
        payload = b"deliberately truncated and invalid candidate"
        candidate_hash = hashlib.sha256(payload).hexdigest()
        base = {"Content-Type": "application/octet-stream", "X-Optimisarr-Source-Sha256": case["sourceSha256"],
                "X-Optimisarr-Candidate-Sha256": candidate_hash}
        code, _, body = wire(api, lease + "/result", token=token, method="POST", body=payload,
            headers={**base, "X-Optimisarr-Source-Sha256": "0" * 64})
        require(code == 409, "Result from wrong source was accepted")
        code, _, body = wire(api, lease + "/result", token=token, method="POST", body=payload[:4], headers=base)
        require(code == 409, "Truncated upload was accepted")
        code, _, _ = wire(api, lease + "/quality", token=token, method="POST",
            body=json.dumps({"sourceSha256": case["sourceSha256"], "candidateSha256": candidate_hash,
                             "logs": ["{broken-json"]}).encode(), headers={"Content-Type": "application/json"})
        require(code in (400, 409), "Malformed quality evidence was accepted")
        code, _, body = wire(api, lease + "/result", token=token, method="PATCH", body=payload[:4],
                             headers={"Content-Type": "application/octet-stream", "X-Optimisarr-Offset": "0"})
        require(code == 200 and json.loads(body)["bytes"] == 4, "Initial chunk failed")
        code, _, _ = wire(api, lease + "/result", token=token, method="PATCH", body=payload[:4],
                          headers={"Content-Type": "application/octet-stream", "X-Optimisarr-Offset": "0"})
        require(code == 409, "Repeated chunk was appended twice")
        code, _, body = wire(api, lease + "/result/offset", token=token)
        require(code == 200 and json.loads(body)["bytes"] == 4, "Resume offset corrupted")
        api.post(f"/api/jobs/{case['jobId']}/cancel")
        code, _, _ = wire(api, lease + "/result", token=token, method="POST", body=payload, headers=base)
        require(code == 409, "Stale result revived a cancelled job")
        require(sha256(case["source"]) == case["sourceSha256"], "Protocol fault changed source")
        require(not any(r["jobId"] == case["jobId"] for r in api.request("/api/replacements")), "Protocol fault allowed replacement")
        code, _, _ = wire(api, lease + "/release", token=token, method="POST")
        require(code == 204, "Cancelled lease could not release capacity")
        require(api.request(f"/api/jobs?libraryId={case['libraryId']}")[0]["status"] == "Cancelled",
                "Worker release revived cancelled work")
        # Valid transport hashes do not make a valid media file. Deliver garbage with honest
        # hashes and require the server's normal verifier to reject it before replacement.
        case = harness.create_job("protocol-corrupt-media", fixture, worker=pair)
        code, _, body = wire(api, "/api/workers/claim", token=token, method="POST")
        require(code == 200, "Could not claim corrupt-media scenario")
        assignment = json.loads(body)
        require(assignment["jobId"] == case["jobId"], "Claimed wrong corrupt-media scenario")
        lease = f"/api/workers/leases/{assignment['leaseId']}"
        code, _, data = wire(api, lease + "/source", token=token)
        require(code == 200 and hashlib.sha256(data).hexdigest() == case["sourceSha256"], "Source mismatch")
        code, _, _ = wire(api, lease + "/result", token=token, method="POST", body=payload,
                          headers={**base, "X-Optimisarr-Source-Sha256": case["sourceSha256"]})
        require(code == 202, "Corrupt-media test never reached application verification")
        job = harness.wait_job(case)
        require(job["status"] == "Failed" and job["verificationPassed"] is not True, "Application accepted corrupt media")
        verification = json.loads(job["verificationReportJson"] or "{}")
        if getattr(harness, "strict_worker_verification", False):
            require("no full verification evidence" in (job["errorMessage"] or ""),
                    "Missing sidecar evidence did not fail closed")
        else:
            require(any(c["name"] == "Decode health" and c["outcome"] in (1, "Failed") for c in verification.get("checks", [])),
                    "Corrupt media failed for a different reason")
        require(sha256(case["source"]) == case["sourceSha256"], "Corrupt delivery changed source")
        require(not any(r["jobId"] == case["jobId"] for r in api.request("/api/replacements")), "Corrupt media was replaced")
        return {"sourceRangeResume": True, "wrongCredentialRejected": True, "wrongSourceRejected": True,
                "truncatedUploadRejected": True, "malformedEvidenceRejected": True,
                "duplicateChunkRejected": True, "lateCancelledResultRejected": True,
                "validHashCorruptMediaRejected": True, "cancelledReleaseDidNotRequeue": True,
                "originalUnchanged": True}
    finally:
        if case:
            jobs = api.request(f"/api/jobs?libraryId={case['libraryId']}")
            if any(j["id"] == case["jobId"] and j["status"] not in ("Cancelled", "Failed", "ReadyToReplace", "Completed") for j in jobs):
                api.post(f"/api/jobs/{case['jobId']}/cancel")
        api.request(f"/api/workers/{pair['workerId']}", "DELETE")
