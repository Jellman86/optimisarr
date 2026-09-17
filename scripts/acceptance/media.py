"""Owned fixtures and a reference verifier independent of the application's command builders."""
from __future__ import annotations

import json
from fractions import Fraction
from pathlib import Path
import re

from .core import Blocked, command, require, save, sha256, statistics


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
            inputs = ["-f", "lavfi", "-i", f"testsrc2=size=320x180:rate=12:duration={seconds}",
                      "-f", "lavfi", "-i", f"sine=frequency=880:sample_rate=48000:duration={seconds}"]
            mapping = ["-map", "0:v", "-map", "1:a"]
            filters = []
            if variant == "vfr":
                filters = ["-vf", "select='not(eq(mod(n,5),2))'", "-fps_mode", "vfr"]
            elif variant == "offset":
                filters = ["-output_ts_offset", "2.5"]
            elif variant == "ten-bit":
                filters = ["-pix_fmt", "yuv420p10le"]
        self.encode(inputs + mapping + filters + ["-t", str(seconds), "-c:v", "ffv1", "-level", "3",
                    "-c:a", "flac", "-metadata:s:a:0", "language=eng", self.path(path)])
        return {"path": str(path), "sha256": sha256(path), "variant": variant,
                "seconds": seconds, "sourceSha256": sha256(source) if source else None,
                "start": start, "probe": self.probe(path, True)}

    def frame_times(self, path):
        result = json.loads(self.run(self.ffprobe, ["-v", "error", "-select_streams", "v:0",
            "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", self.path(path)]))
        times = [float(frame["best_effort_timestamp_time"]) for frame in result["frames"]]
        require(bool(times), "No decoded picture timestamps")
        require(all(b > a for a, b in zip(times, times[1:])), "Non-increasing picture timestamps")
        return [x - times[0] for x in times]

    def measure(self, reference, candidate, evidence_dir):
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
            after = [x for x in out["streams"] if x["codec_type"] == kind]
            require(len(before) == len(after), f"Lost {kind} streams")
            for a, b in zip(before, after):
                for key in ("channels", "channel_layout", "sample_rate") if kind == "audio" else ():
                    require(a.get(key) == b.get(key), f"Changed {kind} {key}")
                require(a.get("tags", {}).get("language") == b.get("tags", {}).get("language"),
                        f"Changed {kind} language")
        # Generated fixtures copy lossless audio. Decoded PCM hashes catch changed/lost samples.
        if any(x["codec_type"] == "audio" for x in ref["streams"]):
            def audio_hash(path):
                return self.run(self.ffmpeg, ["-v", "error", "-i", self.path(path), "-map", "0:a",
                    "-c:a", "pcm_s32le", "-f", "hash", "-hash", "sha256", "-"]).strip()
            require(audio_hash(reference) == audio_hash(candidate), "Decoded audio samples changed")
        self.run(self.ffmpeg, ["-nostdin", "-v", "error", "-xerror", "-i", self.path(candidate), "-f", "null", "-"])
        if rv.get("color_transfer") in ("smpte2084", "arib-std-b67"):
            raise Blocked("HDR needs an explicitly reviewed reference transform; SDR VMAF is not HDR certification")
        model = "vmaf_4k_v0.6.1" if rv["width"] >= 3840 or rv["height"] >= 2160 else "vmaf_v0.6.1"
        # Frame correspondence was checked above. Full sequential decode avoids seek/keyframe bugs.
        rate = Fraction(rv.get("avg_frame_rate", "0/1"))
        if rate <= 0:
            rate = Fraction(rv["r_frame_rate"])
        require(0 < rate <= 240, "Reference has no trustworthy picture cadence")
        graph = (f"[0:v]settb=AVTB,setpts=PTS-STARTPTS,fps={rate}:start_time=0,format=yuv420p[d];"
                 f"[1:v]settb=AVTB,setpts=PTS-STARTPTS,fps={rate}:start_time=0,format=yuv420p[r];"
                 f"[d][r]libvmaf=model=version={model}:n_threads=2:n_subsample=1:"
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
