"""Real Vulkan upscale, selected encoder, application verification, replacement and rollback."""
from __future__ import annotations

import json

from .core import require, save, sha256
from .runner import encoder_mode


def validate_candidate(source, candidate, encoder):
    original_video = next(s for s in source["streams"] if s["codec_type"] == "video")
    videos = [s for s in candidate["streams"] if s["codec_type"] == "video"]
    require(len(videos) == 1, "Upscale must have exactly one video stream")
    video = videos[0]
    codec = {"libx265": "hevc", "libx264": "h264", "libsvtav1": "av1"}.get(encoder, encoder.split("_")[0])
    require(video["codec_name"] == codec, "Upscale used a different codec")
    require((video["width"], video["height"]) == (original_video["width"] * 2, original_video["height"] * 2),
            "Candidate was not upscaled exactly 2x")
    require(int(video["nb_read_frames"]) == int(original_video["nb_read_frames"]), "Upscale lost or duplicated pictures")
    require(video["pix_fmt"] == original_video["pix_fmt"], "Upscale changed the bit depth or chroma layout")
    for tag in ("color_range", "color_space", "color_primaries", "color_transfer"):
        if original_video.get(tag):
            require(video.get(tag) == original_video[tag], f"Upscale changed {tag}")
    for kind in ("audio", "subtitle", "attachment"):
        original = [s for s in source["streams"] if s["codec_type"] == kind]
        copied = [s for s in candidate["streams"] if s["codec_type"] == kind]
        require(len(copied) == len(original), f"Upscale lost {kind} streams")
        for before, after in zip(original, copied):
            require(before.get("codec_name") == after.get("codec_name"), f"Upscale re-encoded {kind}")
            for tag in ("language", "filename", "mimetype"):
                if before.get("tags", {}).get(tag):
                    require(after.get("tags", {}).get(tag) == before["tags"][tag], f"Upscale changed {kind} {tag}")


def fixture(harness, variant, seconds):
    path = harness.root / "fixtures" / f"super-resolution-{variant}.mkv"
    cue = path.with_suffix(".srt")
    cue.write_text("1\n00:00:00,200 --> 00:00:01,800\nUpscale subtitle fixture\n", encoding="utf-8")
    attachment = path.with_suffix(".txt")
    attachment.write_text("Generated attachment retained through upscaling.\n", encoding="utf-8")
    layout = "yuv420p10le" if variant == "ten-bit" else "yuv420p"
    tools = harness.tools
    # The v1 model's CAMBI feature cannot assess tiny 128x72 pictures.
    tools.encode(["-f", "lavfi", "-i", f"testsrc2=size=320x180:rate=12:duration={seconds}",
        "-f", "lavfi", "-i", f"sine=frequency=880:sample_rate=48000:duration={seconds}",
        "-i", tools.path(cue), "-map", "0:v", "-map", "1:a", "-map", "2:s",
        "-vf", f"format={layout}", "-c:v", "ffv1", "-c:a", "flac", "-c:s", "srt",
        "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
        "-metadata:s:a:0", "language=eng", "-metadata:s:s:0", "language=eng",
        "-attach", tools.path(attachment), "-metadata:s:t:0", "mimetype=text/plain", tools.path(path)])
    return path


def run_case(harness, source, encoder, variant):
    name = f"super-resolution-{encoder}-{variant}"
    harness.configure(encoderMode=encoder_mode(encoder))
    case = harness.create_job(name, source, encoder=encoder, overrides={
        "superResolutionEnabled": True, "superResolutionEngine": "realesrgan",
        "superResolutionModel": "realesr-animevideov3", "superResolutionScale": 2,
        "superResolutionQualityCheck": "BrokenFramesOnly", "autoReplace": True})
    directory = harness.report.root / name
    directory.mkdir()
    job = harness.wait_job(case)
    save(directory / "job.json", job)
    require(sha256(case["source"]) == case["sourceSha256"], "Upscale changed its original before approval")
    require(job["status"] == "ReadyToReplace" and job["verificationPassed"] is True,
            f"Upscale failed application verification: {job['status']}: {job.get('errorMessage')}")
    require(job.get("superResolutionRun") == "realesrgan realesr-animevideov3 2x"
            and job["videoEncoder"] == encoder, "Requested engine/encoder was not used")
    require(not any(r["jobId"] == case["jobId"] for r in harness.api.request("/api/replacements")),
            "Upscale replaced its original automatically")
    candidate = harness.output(case)
    original_probe, candidate_probe = harness.tools.probe(source, True), harness.tools.probe(candidate, True)
    save(directory / "source-probe.json", original_probe)
    save(directory / "candidate-probe.json", candidate_probe)
    validate_candidate(original_probe, candidate_probe, encoder)
    harness.tools.encode(["-i", harness.tools.path(candidate), "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"])
    def audio_hash(path):
        return harness.tools.run(harness.tools.ffmpeg, ["-v", "error", "-i", harness.tools.path(path),
            "-map", "0:a:0", "-f", "hash", "-hash", "sha256", "-"]).strip()
    require(audio_hash(source) == audio_hash(candidate), "Upscale changed decoded audio")
    harness.tools.check_av_start_offset(source, candidate, directory)
    harness.replace_restore(case, candidate)
    return {"jobId": case["jobId"], "encoder": encoder, "variant": variant,
            "verification": json.loads(job["verificationReportJson"]), "originalRestored": True}


def run(harness, encoders, variants, seconds, binary=None, models=None):
    settings = harness.api.request("/api/settings")["superResolution"]
    settings.update(enabled=True, device="auto")
    if binary:
        settings["realEsrganBinaryPath"] = binary
    if models:
        settings["realEsrganModelsPath"] = models
    harness.configure(superResolution=settings)
    for variant in variants or ("sdr", "ten-bit"):
        require(variant in ("sdr", "ten-bit"), "Super-resolution regression supports SDR and ten-bit fixtures")
        source = fixture(harness, variant, seconds)
        for encoder in encoders or ("h264_nvenc", "hevc_nvenc", "av1_nvenc"):
            if variant == "ten-bit" and encoder in ("h264_nvenc", "libx264"):
                continue
            harness.report.case(f"super-resolution-{encoder}-{variant}",
                lambda e=encoder, v=variant, s=source: run_case(harness, s, e, v))
    return harness.report.exit_code
