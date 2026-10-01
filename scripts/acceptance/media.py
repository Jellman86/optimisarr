"""Owned fixtures and a reference verifier independent of the application's command builders."""
from __future__ import annotations

import json
import math
from fractions import Fraction
from pathlib import Path
import re

from .core import Blocked, command, require, save, sha256, statistics


def vmaf_policy(reference_video, encoded_video, rate):
    """Independent viewing policy; CAMBI describes bytes before rescaling to reference size."""
    uhd = reference_video["width"] >= 3840 or reference_video["height"] >= 2160
    if rate >= 45:
        model = "vmaf_4k_v0.6.1" if uhd else "vmaf_v0.6.1"
        return model, "version=" + model, "yuv420p"
    model = "vmaf_v1.0.16_1d5h_2160" if uhd else "vmaf_v1.0.16_3d0h"
    pixel = encoded_video.get("pix_fmt", "")
    depths = {"yuv420p": 8, "yuvj420p": 8, "yuv422p": 8, "yuv444p": 8, "nv12": 8,
              "yuv420p10le": 10, "yuv422p10le": 10, "yuv444p10le": 10, "p010le": 10}
    depth = depths.get(pixel)
    require(depth in (8, 10) and encoded_video.get("width", 0) > 0 and encoded_video.get("height", 0) > 0,
            "V1 requires actual supported candidate format")
    raw = encoded_video.get("bits_per_raw_sample", "0")
    require(str(raw).isdigit() and int(raw) in (0, depth), "Inconsistent encoded bit depth")
    options = (f"'version={model}\\:cambi.enc_width={encoded_video['width']}"
               f"\\:cambi.enc_height={encoded_video['height']}\\:cambi.enc_bitdepth={depth}'")
    return model, options, "yuv420p10le"


def validate_shadow_report(report):
    shadow = report.get("shadowVmaf") or {}
    require(shadow.get("status") == "Measured", f"Research coverage incomplete: {shadow}")
    require(shadow.get("measurementLocation") == "Server", "Research location is missing")
    baseline = shadow.get("baselineModel")
    candidate = shadow.get("candidateModel")
    require(baseline in ("vmaf_v0.6.1", "vmaf_4k_v0.6.1"), "Research baseline changed")
    require(candidate in ("vmaf_v1.0.16_3d0h", "vmaf_v1.0.16_1d5h_2160"), "Research candidate changed")
    require((report.get("vmaf") or {}).get("scores", {}).get("modelVersion") == candidate,
            "Authoritative SDR verification stopped using the v1 model")
    windows = shadow.get("windows") or []
    require(0 < len(windows) <= 3, "Missing or unbounded research windows")
    for window in windows:
        before, after = window.get("baseline") or {}, window.get("candidate") or {}
        require(before.get("modelVersion") == baseline and after.get("modelVersion") == candidate, "Wrong measured models")
        require(isinstance(before.get("frameCount"), int) and before["frameCount"] > 0
                and before["frameCount"] == after.get("frameCount"), "Research frame counts differ")
        for scores in (before, after):
            require(all(isinstance(scores.get(metric), (float, int)) and math.isfinite(scores[metric])
                        for metric in ("vmafMean", "vmafHarmonicMean", "vmafMin", "vmafFifthPercentile")),
                    "Research scores are missing or non-finite")
    return {"pairs": len(windows), "baselineModel": baseline, "candidateModel": candidate}


class Tools:
    def __init__(self, ffmpeg, ffprobe, *, container=None, root=None, vmaf=None):
        self.ffmpeg, self.ffprobe = ffmpeg, ffprobe
        self.vmaf = vmaf or ffmpeg
        self.container, self.root = container, Path(root).resolve() if root else None

    def path(self, path):
        path = Path(path).resolve()
        return "/acceptance/" + str(path.relative_to(self.root)) if self.container else str(path)

    def run(self, executable, args, cwd=None, timeout=600, include_stderr=False):
        prefix = []
        if self.container:
            prefix = ["docker", "exec"]
            if cwd:
                prefix += ["-w", self.path(cwd)]
            prefix += [self.container]
        return command(prefix + [executable] + args,
                       cwd=None if self.container else cwd, timeout=timeout, include_stderr=include_stderr)

    def loudness(self, path):
        log = self.run(self.ffmpeg, ["-nostdin", "-hide_banner", "-i", self.path(path), "-af",
            "ebur128=peak=true", "-f", "null", "-"], include_stderr=True)
        integrated = re.findall(r"I:\s+(-?[\d.]+) LUFS", log)
        peaks = re.findall(r"Peak:\s+(-?[\d.]+) dBFS", log)
        require(integrated and peaks, "No measured loudness/true peak")
        return {"lufs": float(integrated[-1]), "truePeak": float(peaks[-1])}

    def encode(self, args, **kwargs):
        return self.run(self.ffmpeg, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y"] + args, **kwargs)

    def probe(self, path, frames=False):
        extra = ["-count_frames"] if frames else []
        return json.loads(self.run(self.ffprobe, ["-v", "error", *extra, "-show_streams", "-show_format",
                                                "-of", "json", self.path(path)]))

    def versions(self):
        return {name: self.run(exe, ["-version"]).splitlines()[0]
                for name, exe in (("ffmpeg", self.ffmpeg), ("ffprobe", self.ffprobe), ("vmaf", self.vmaf))}

    def fixture(self, path, variant="sdr", seconds=8, source=None, start=0):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        if source:
            inputs = ["-ss", str(start), "-i", self.path(source)]
            mapping = ["-map", "0:v:0", "-map", "0:a?"]
            filters = ["-vf", "scale=640:-2,format=yuv420p"]
        else:
            rate = "24000/1001" if variant == "fractional" else "12"
            inputs = ["-f", "lavfi", "-i", f"testsrc2=size=320x180:rate={rate}:duration={seconds}",
                      "-f", "lavfi", "-i", f"sine=frequency=880:sample_rate=48000:duration={seconds}"]
            mapping = ["-map", "0:v", "-map", "1:a"]
            filters = []
            if variant == "vfr":
                filters = ["-vf", "select='not(eq(mod(n,5),2))'", "-fps_mode", "vfr"]
            elif variant == "offset":
                filters = ["-output_ts_offset", "2.5"]
            elif variant == "ten-bit":
                filters = ["-pix_fmt", "yuv420p10le"]
            elif variant == "fractional":
                # A half-frame lead rounded to milliseconds reproduces collisions when a
                # nominally constant source is rounded again to the encoder's frame timebase.
                # A cue at zero pins the container start without introducing cross-container
                # audio priming/padding into this picture-timestamp regression.
                cue = path.with_suffix(".srt")
                cue.write_text("1\n00:00:00,000 --> 00:00:00,100\nTimestamp reference\n", encoding="utf-8")
                inputs = inputs[:4] + ["-i", self.path(cue)]
                mapping = ["-map", "0:v", "-map", "1:s"]
                filters = ["-vf", "settb=1/1000,setpts=PTS+21", "-fps_mode", "passthrough",
                           "-enc_time_base:v:0", "1/1000"]
        codecs = (["-c:v", "libx264", "-crf", "3", "-preset", "fast", "-bf", "3", "-c:s", "srt",
                   "-metadata:s:s:0", "language=eng"]
                  if variant == "fractional" else ["-c:v", "ffv1", "-level", "3", "-c:a", "flac"])
        self.encode(inputs + mapping + filters + ["-t", str(seconds), *codecs,
                    "-metadata:s:a:0", "language=eng", self.path(path)])
        return {"path": str(path), "sha256": sha256(path), "variant": variant,
                "seconds": seconds, "sourceSha256": sha256(source) if source else None,
                "start": start, "probe": self.probe(path, True)}

    def alac_fixture(self, path, source, *, mixed=False):
        path = Path(path)
        maps = ["-map", "0:v:0", "-map", "0:a:0"]
        audio = ["-c:a:0", "alac", "-metadata:s:a:0", "language=eng"]
        if mixed:
            maps += ["-map", "0:a:0"]
            audio = ["-c:a:0", "flac", "-c:a:1", "alac",
                     "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=fra"]
        self.encode(["-i", self.path(source), *maps, "-c:v", "libx264", "-crf", "3",
                     "-preset", "fast", *audio, self.path(path)])
        return path

    def subtitle_fixture(self, path, source):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        first, second = path.with_suffix(".eng.srt"), path.with_suffix(".fra.srt")
        first.write_text("1\n00:00:01,000 --> 00:00:02,500\nHello — subtitle regression\n\n2\n00:00:04,000 --> 00:00:05,500\nSecond cue\n", encoding="utf-8")
        second.write_text("1\n00:00:01,250 --> 00:00:03,000\nBonjour — deuxième piste\n", encoding="utf-8")
        self.encode(["-i", self.path(source), "-i", self.path(first), "-i", self.path(second),
            "-map", "0:v:0", "-map", "0:a:0", "-map", "1:s:0", "-map", "2:s:0",
            "-c:v", "libx264", "-crf", "0", "-c:a", "alac", "-c:s", "mov_text",
            "-metadata:s:a:0", "language=eng", "-metadata:s:s:0", "language=eng",
            "-metadata:s:s:1", "language=fra", self.path(path)])
        return {"sha256": sha256(path), "probe": self.probe(path)}

    def subtitle_cues(self, path, index):
        text = self.run(self.ffmpeg, ["-v", "error", "-i", self.path(path), "-map", f"0:s:{index}",
            "-c:s", "srt", "-f", "srt", "-"])
        return text.strip().replace("\r\n", "\n")

    def overlapping_subtitle_fixture(self, path, source):
        path = Path(path)
        tracks = [
            ("eng", "1\n00:00:01,000 --> 00:00:02,500\nAlpha\n\n2\n00:00:01,000 --> 00:00:03,000\nBeta\n"),
            ("fra", "1\n00:00:01,250 --> 00:00:03,000\nBonjour\n"),
            ("jpn", "1\n00:00:04,000 --> 00:00:06,000\nGamma\n\n2\n00:00:05,000 --> 00:00:07,000\nDelta\n"),
        ]
        inputs, maps, metadata = ["-i", self.path(source)], ["-map", "0:v:0"], []
        for index, (language, cues) in enumerate(tracks):
            track = path.with_suffix(f".{language}.srt")
            track.write_text(cues, encoding="utf-8")
            inputs += ["-i", self.path(track)]
            maps += ["-map", f"{index + 1}:s:0"]
            metadata += [f"-metadata:s:s:{index}", f"language={language}"]
        self.encode(inputs + maps + ["-c", "copy", "-c:s", "srt", *metadata, self.path(path)])
        return path

    def frame_times(self, path):
        result = json.loads(self.run(self.ffprobe, ["-v", "error", "-select_streams", "v:0",
            "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", self.path(path)]))
        times = [float(frame["best_effort_timestamp_time"]) for frame in result["frames"]]
        require(bool(times), "No decoded picture timestamps")
        require(all(b > a for a, b in zip(times, times[1:])), "Non-increasing picture timestamps")
        return [x - times[0] for x in times]

    def measure(self, reference, candidate, evidence_dir, *, kept_subtitle_indexes=None, kept_audio_indexes=None):
        evidence_dir = Path(evidence_dir)
        evidence_dir.mkdir(parents=True, exist_ok=True)
        ref, out = self.probe(reference, True), self.probe(candidate, True)
        save(evidence_dir / "reference-probe.json", ref)
        save(evidence_dir / "candidate-probe.json", out)
        rv = next(x for x in ref["streams"] if x["codec_type"] == "video")
        ov = next(x for x in out["streams"] if x["codec_type"] == "video")
        # A score must not erase lost pictures by blindly resetting, duplicating or trimming frames.
        rt, ot = self.frame_times(reference), self.frame_times(candidate)
        require(len(rt) == len(ot), f"Frame loss/duplication: {len(rt)} reference, {len(ot)} output")
        drift = max(abs(a - b) for a, b in zip(rt, ot))
        require(drift <= .003, f"Picture cadence drift {drift:.6f}s")
        require((rv["width"], rv["height"]) == (ov["width"], ov["height"]), "Unexpected dimensions")
        require(rv.get("pix_fmt") == ov.get("pix_fmt"), "Unexpected pixel format / bit depth")
        for key in ("color_range", "color_space", "color_transfer", "color_primaries", "sample_aspect_ratio"):
            if rv.get(key) not in (None, "unknown", "unspecified"):
                require(rv.get(key) == ov.get(key), f"Changed {key}")
        for kind in ("audio", "subtitle"):
            before = [x for x in ref["streams"] if x["codec_type"] == kind]
            if kind == "subtitle" and kept_subtitle_indexes is not None:
                before = [before[index] for index in kept_subtitle_indexes]
            if kind == "audio" and kept_audio_indexes is not None:
                before = [before[index] for index in kept_audio_indexes]
            after = [x for x in out["streams"] if x["codec_type"] == kind]
            require(len(before) == len(after), f"Lost {kind} streams")
            for a, b in zip(before, after):
                for key in ("channels", "channel_layout", "sample_rate") if kind == "audio" else ():
                    require(a.get(key) == b.get(key), f"Changed {kind} {key}")
                require(a.get("tags", {}).get("language") == b.get("tags", {}).get("language"),
                        f"Changed {kind} language")
        # Generated fixtures copy lossless audio. Decoded PCM hashes catch changed/lost samples.
        if any(x["codec_type"] == "audio" for x in ref["streams"]):
            def audio_hash(path, indexes=None):
                maps = ["-map", "0:a"] if indexes is None else [item for index in indexes for item in ("-map", f"0:a:{index}")]
                return self.run(self.ffmpeg, ["-v", "error", "-i", self.path(path), *maps,
                    "-c:a", "pcm_s32le", "-f", "hash", "-hash", "sha256", "-"]).strip()
            before_hash, after_hash = audio_hash(reference, kept_audio_indexes), audio_hash(candidate)
            save(evidence_dir / "decoded-audio.json", {"reference": before_hash, "candidate": after_hash,
                 "keptSourceAudioIndexes": kept_audio_indexes})
            require(before_hash == after_hash, "Decoded audio samples changed")
        self.run(self.ffmpeg, ["-nostdin", "-v", "error", "-xerror", "-i", self.path(candidate), "-f", "null", "-"])
        if rv.get("color_transfer") in ("smpte2084", "arib-std-b67"):
            raise Blocked("HDR needs an explicitly reviewed reference transform; SDR VMAF is not HDR certification")
        # Frame correspondence was checked above. Full sequential decode avoids seek/keyframe bugs.
        rate = Fraction(rv.get("avg_frame_rate", "0/1"))
        if rate <= 0:
            rate = Fraction(rv["r_frame_rate"])
        require(0 < rate <= 240, "Reference has no trustworthy picture cadence")
        model, options, pixel = vmaf_policy(rv, ov, float(rate))
        prep = (f"fps={rate}:start_time=0,scale={rv['width']}:{rv['height']}:"
                f"flags=bicubic:in_range=auto:out_range=tv,format={pixel}")
        graph = (f"[0:v]settb=AVTB,setpts=PTS-STARTPTS,{prep}[d];"
                 f"[1:v]settb=AVTB,setpts=PTS-STARTPTS,{prep}[r];"
                 f"[d][r]libvmaf=model={options}:n_threads=2:n_subsample=1:"
                 "log_fmt=json:log_path=vmaf.json:shortest=1:repeatlast=0")
        args = ["-nostdin", "-v", "error", "-i", self.path(candidate), "-i", self.path(reference),
                "-lavfi", graph, "-f", "null", "-"]
        save(evidence_dir / "measurement.json", {"args": args, "model": model,
             "referenceSha256": sha256(reference), "candidateSha256": sha256(candidate),
             "maxTimestampDriftSeconds": drift})
        self.run(self.vmaf, args, cwd=evidence_dir)
        scores = statistics(json.loads((evidence_dir / "vmaf.json").read_text()))
        # VFR pictures were checked one-for-one above; VMAF's documented common-cadence
        # comparison intentionally repeats them. Do not confuse that with encoder frame loss.
        expected_frames = round(rt[-1] * float(rate)) + 1
        require(scores["frames"] == expected_frames, "VMAF silently omitted pictures")
        return {**scores, "model": model, "candidateSha256": sha256(candidate)}

    def damaged(self, reference, path, damage):
        filters = {"black": "drawbox=color=black:t=fill", "frozen": "select=eq(n\\,0),loop=loop=95:size=1:start=0,setpts=N/12/TB",
                   "drop": "select='not(eq(n,12))'"}
        args = ["-i", self.path(reference)]
        if damage == "truncate":
            args += ["-t", "2"]
        elif damage in filters:
            args += ["-vf", filters[damage], "-fps_mode", "vfr"]
        else:
            raise ValueError(damage)
        self.encode(args + ["-c:v", "libx264", "-crf", "18", "-c:a", "copy", self.path(path)])


def compare_report(report, scores, tolerance=0.25):
    evidence = report.get("vmaf") or {}
    require(evidence.get("measured") is True, "Application omitted VMAF evidence")
    actual = evidence.get("scores") or {}
    require(actual.get("modelVersion") == scores["model"], "Application measured a different VMAF model")
    require(actual.get("frameCount") == scores["frames"], "Application measured a different number of frames")
    for field, key in (("vmafHarmonicMean", "harmonic"), ("vmafFifthPercentile", "p5"), ("vmafMin", "minimum")):
        value = actual.get(field)
        require(isinstance(value, (int, float)) and abs(value - scores[key]) <= tolerance,
                f"Independent {key} {scores[key]:.3f} disagrees with application {value}")
