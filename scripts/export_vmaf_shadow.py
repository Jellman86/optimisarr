#!/usr/bin/env python3
"""Export recorded research observations from a saved GET /api/jobs response."""

import argparse
import json
from pathlib import Path


def observations(jobs):
    if not isinstance(jobs, list):
        raise ValueError("Expected the job array returned by GET /api/jobs.")
    rows, seen = [], set()
    for job in jobs:
        if not isinstance(job, dict) or not isinstance(job.get("id"), int):
            raise ValueError("Every job must have a numeric id.")
        if job["id"] in seen:
            raise ValueError(f"Duplicate job id {job['id']}; combine snapshots by job id before exporting.")
        seen.add(job["id"])
        raw = job.get("verificationReportJson")
        if not raw:
            continue
        try:
            report = json.loads(raw)
        except (ValueError, TypeError) as error:
            raise ValueError(f"Job {job['id']} has an unreadable verification report.") from error
        if not isinstance(report, dict):
            raise ValueError(f"Job {job['id']} has an invalid verification report.")
        if report.get("shadowVmaf") is None:
            continue
        rows.append({
            "jobId": job["id"], "mediaFileId": job.get("mediaFileId"),
            "libraryId": job.get("libraryId"), "workerId": job.get("workerId"),
            "verifiedAt": job.get("verifiedAt"), "verificationPassed": job.get("verificationPassed"),
            "context": report.get("context"), "authoritativeVmaf": report.get("vmaf"),
            "shadowVmaf": report["shadowVmaf"],
        })
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="Saved /api/jobs JSON; export before job history is pruned")
    parser.add_argument("output", type=Path, help="New JSONL file; existing evidence is never overwritten")
    args = parser.parse_args()
    try:
        rows = observations(json.loads(args.input.read_text()))
        payload = "".join(json.dumps(row, allow_nan=False) + "\n" for row in rows)
        with args.output.open("x") as output:
            output.write(payload)
    except (OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")
    print(f"Exported {len(rows)} research observations. Skips and incomplete results are retained.")


if __name__ == "__main__":
    main()
