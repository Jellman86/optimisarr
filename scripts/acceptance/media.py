"""Owned fixtures and a reference verifier independent of the application's command builders."""
from __future__ import annotations

import json
import math
from fractions import Fraction
from pathlib import Path
import re

from .dts_fixture import vfw_matroska
from .picture_identity import parse_picture_ids, validate_picture_ids
from .core import Blocked, command, require, save, sha256, statistics


def validate_uneven_timing_fixture(probe, times):
    """Prove the fixture hides close frame pairs behind apparently constant rate metadata."""
    pictures = [stream for stream in probe.get("streams", []) if stream.get("codec_type") == "video"
                and not stream.get("disposition", {}).get("attached_pic")]
    require(pictures, "Uneven timing fixture has no moving picture stream")
    try:
        nominal = Fraction(pictures[0].get("r_frame_rate", "0/1"))
        average = Fraction(pictures[0].get("avg_frame_rate", "0/1"))
    except (ValueError, ZeroDivisionError, TypeError):
        raise AssertionError("Uneven timing fixture has invalid rate metadata") from None
    require(0 < nominal <= 240 and nominal == average,
            "Uneven timing fixture no longer appears constant rate")
    require(len(times) >= 4 and all(math.isfinite(value) for value in times),
            "Uneven timing fixture lacks complete finite picture timestamps")
    gaps = [after - before for before, after in zip(times, times[1:])]
    require(all(gap > 0 for gap in gaps), "Uneven timing fixture has unordered pictures")
    period = 1 / float(nominal)
    require(min(gaps) < period / 2 and max(gaps) > max(1, period * 1.5),
            "Uneven timing fixture no longer contains tight frame pairs and a long pause")
    return {"frames": len(times), "declaredFramesPerSecond": float(nominal),
            "minimumGapSeconds": min(gaps), "maximumGapSeconds": max(gaps)}


def validate_soundtrack_report(report, source_indexes, location, limit, *, passes=True):
    """Check retained-track coverage and every channel independently of the server verdict."""
    require(report and not report.get("unavailableReason"), "Soundtrack assessment unavailable")
    tracks = report.get("tracks") or []
    require(len(tracks) == len(source_indexes) and tracks, "Missing retained soundtrack evidence")
    measured_passes = []
    for index, (item, source_index) in enumerate(zip(tracks, source_indexes)):
        mapping, quality = item.get("track") or {}, item.get("report") or {}
        require(mapping.get("sourceAudioIndex") == source_index and mapping.get("candidateAudioIndex") == index,
                "Incorrect retained soundtrack mapping")
        require(quality.get("measurementLocation") == location and not quality.get("unavailableReason"),
                "Soundtrack measurement ran on the wrong host or was unavailable")
        evidence = quality.get("evidence") or {}
        assessment = evidence.get("assessment") or {}
        require(evidence.get("preparation") == "audio-f32le-48k-video-timeline-v1" and assessment.get("measured"),
                "Missing versioned soundtrack evidence")
        require(assessment.get("referenceAudioIndex") == source_index and assessment.get("candidateAudioIndex") == index,
                "Score belongs to a different soundtrack")
        windows = assessment.get("windows") or []
        require(0 < len(windows) <= 3, "Missing or unbounded soundtrack windows")
        values = [value for window in windows for value in (window.get("distances") or {}).get("channelDistances", [])]
        require(values and all(isinstance(value, (int, float)) and math.isfinite(value) and 0 <= value <= 1 for value in values),
                "Invalid channel distances")
        passed = all(value <= limit for value in values)
        require(quality.get("gateEnabled") is True and quality.get("gatePassed") == passed, "Incorrect per-track gate verdict")
        measured_passes.append(passed)
    require(report.get("gateEnabled") is True and report.get("gatePassed") == all(measured_passes) == passes,
            "Incorrect aggregate soundtrack gate verdict")


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


def quality_picture_preparation(rate, width, height, pixel):
    # The oracle first checks counts and each picture's timing. A cadence grid can
    # omit tightly spaced pictures or repeat a long-held picture and hide damage.
    return (f"settb=AVTB,setpts=N*{rate.denominator}/{rate.numerator}/TB,"
            f"scale={width}:{height}:flags=bicubic:in_range=auto:out_range=tv,format={pixel}")


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
        if variant in ("dts-only", "dts-only-no-subtitles", "dts-repeated"):
            return self.dts_only_fixture(path, seconds, subtitles=variant != "dts-only-no-subtitles",
                                         numbered_repeated_fixture=variant == "dts-repeated")
        if source:
            inputs = ["-ss", str(start), "-i", self.path(source)]
            mapping = ["-map", "0:v:0", "-map", "0:a?"]
            filters = ["-vf", "scale=640:-2,format=yuv420p"]
        else:
            rate = "24000/1001" if variant == "fractional" else "24" if variant == "uneven" else "12"
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
            elif variant in ("fractional", "uneven"):
                # Millisecond frame pairs and a long pause exercise reordered packet durations;
                # the fractional variant instead stresses rounding of a half-frame lead.
                # A cue at zero pins the origin without audio priming/padding differences.
                cue = path.with_suffix(".srt")
                cue.write_text("1\n00:00:00,000 --> 00:00:00,100\nTimestamp reference\n", encoding="utf-8")
                inputs = inputs[:4] + ["-i", self.path(cue)]
                mapping = ["-map", "0:v", "-map", "1:s"]
                pause_frame, pause_ms = int(seconds * 6), int(seconds * 500)
                timing = ("PTS+21" if variant == "fractional" else
                          f"floor(N/2)*84+mod(N\\,2)*1+gte(N\\,{pause_frame})*{pause_ms}")
                filters = ["-vf", "settb=1/1000,setpts=" + timing, "-fps_mode", "passthrough",
                           "-enc_time_base:v:0", "1/1000"]
        codecs = (["-c:v", "libx264", "-crf", "3", "-preset", "fast", "-bf", "3", "-c:s", "srt",
                   "-metadata:s:s:0", "language=eng"]
                  if variant in ("fractional", "uneven") else ["-c:v", "ffv1", "-level", "3", "-c:a", "flac"])
        self.encode(inputs + mapping + filters + ["-t", str(seconds), *codecs,
                    "-metadata:s:a:0", "language=eng", self.path(path)])
        evidence = {"path": str(path), "sha256": sha256(path), "variant": variant,
                "seconds": seconds, "sourceSha256": sha256(source) if source else None,
                "start": start, "probe": self.probe(path, True)}
        if variant == "uneven":
            evidence["timing"] = validate_uneven_timing_fixture(evidence["probe"], self.frame_times(path))
        return evidence

    def dts_only_fixture(self, path, seconds, *, subtitles=True, numbered_repeated_fixture=False):
        avi, flac = path.with_suffix(".avi"), path.with_suffix(".flac")
        markers = []
        if numbered_repeated_fixture:
            require(8 <= seconds <= 40, "Numbered DTS fixture requires 8 to 40 seconds")
            markers = ["-vf", "geq=lum='if(lt(Y,48),16+219*mod(floor(N/pow(2,floor(X/32))),2),p(X,Y))':cb='cb(X,Y)':cr='cr(X,Y)'"]
        self.encode(["-f", "lavfi", "-i", f"testsrc2=size=320x180:rate=25:duration={seconds}",
                     *markers, "-c:v", "mpeg4", "-bf", "2", "-q:v", "3", self.path(avi)])
        self.encode(["-f", "lavfi", "-i", f"sine=frequency=880:sample_rate=48000:duration={seconds}",
                     "-c:a", "flac", self.path(flac)])
        def packets(file):
            return json.loads(self.run(self.ffprobe, ["-v", "error", "-show_packets", "-show_entries",
                "packet=pts_time,dts_time,pos,size,flags", "-of", "json", self.path(file)]))["packets"]
        video, audio = packets(avi), packets(flac)
        if numbered_repeated_fixture:
            # Adjacent B pictures repeat a decode time without changing packet content/order.
            video[6]["dts_time"] = video[5]["dts_time"]
        path.write_bytes(vfw_matroska(avi.read_bytes(), video, flac.read_bytes(), audio, seconds, subtitles=subtitles))
        probe = self.probe(path, True)
        moving = next(stream for stream in probe["streams"] if stream["codec_type"] == "video")
        source_packets = json.loads(self.run(self.ffprobe, ["-v", "error", "-select_streams", "V:0",
            "-show_entries", "packet=pts_time,dts_time", "-of", "json", self.path(path)]))["packets"]
        require(source_packets and "pts_time" not in source_packets[0] and all("dts_time" in packet for packet in source_packets),
                "DTS-only fixture no longer carries decode timestamps alone")
        require(int(moving["nb_read_frames"]) == round(seconds * 25), "Generated fixture lost pictures")
        require(sum(stream["codec_type"] == "subtitle" for stream in probe["streams"]) == int(subtitles),
                "Generated fixture has the wrong subtitle coverage")
        first_picture = json.loads(self.run(self.ffprobe, ["-v", "error", "-fflags", "+genpts", "-select_streams", "V:0",
            "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", self.path(path)]))["frames"][0]
        require(float(first_picture["best_effort_timestamp_time"]) < float(probe["format"]["start_time"]),
                "Generated picture no longer precedes the declared input start")
        if numbered_repeated_fixture:
            times = self.frame_times(path, generate_pts=True, numbered_repeated_fixture=True)
            expected = [i / 25 for i in range(round(seconds * 25))]
            expected[5] = expected[4]
            require(len(times) == len(expected) and all(abs(a - b) < .0005 for a, b in zip(times, expected)),
                    "Numbered source no longer contains the expected repeated initial timestamp")
            identities = self.picture_ids(path, path.parent / (path.stem + "-markers.gray"), generate_pts=True)
            validate_picture_ids(identities, identities)
        return {"path": str(path), "sha256": sha256(path),
                "variant": "dts-repeated" if numbered_repeated_fixture else "dts-only", "seconds": seconds, "probe": probe}

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

    def subtitle_cues(self, path, index, *, picture_origin=False, generate_pts=False):
        origin = self.first_av_timestamps(path, generate_pts=generate_pts)["video"] if picture_origin else None
        flags = ["-copyts", "-itsoffset", str(-origin)] if origin is not None else []
        text = self.run(self.ffmpeg, ["-v", "error", *flags, "-i", self.path(path), "-map", f"0:s:{index}",
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

    def first_av_timestamps(self, path, *, generate_pts=False):
        frames = json.loads(self.run(self.ffprobe, ["-v", "error", *(["-fflags", "+genpts"] if generate_pts else []),
            "-read_intervals", "%+#32", "-show_entries", "frame=media_type,best_effort_timestamp_time", "-of", "json",
            self.path(path)]))["frames"]
        starts = {}
        for frame in frames:
            kind = frame.get("media_type")
            if kind in ("video", "audio") and kind not in starts and "best_effort_timestamp_time" in frame:
                starts[kind] = float(frame["best_effort_timestamp_time"])
        require(set(starts) == {"video", "audio"}, "Missing decoded A/V start evidence")
        return starts

    def check_av_start_offset(self, source, candidate, directory):
        before = self.first_av_timestamps(source, generate_pts=True)
        after = self.first_av_timestamps(candidate)
        delta = abs((after["video"] - after["audio"]) - (before["video"] - before["audio"]))
        save(Path(directory) / "decoded-av-offset.json", {"source": before, "candidate": after, "offsetChangeSeconds": delta})
        require(delta <= .003, "Changed decoded picture/audio alignment")

    def picture_ids(self, path, raw_path, *, generate_pts=False):
        self.encode([*(["-fflags", "+genpts"] if generate_pts else []), "-i", self.path(path),
            "-map", "0:V:0", "-vf", "crop=320:48:0:0,format=gray", "-fps_mode", "passthrough",
            "-frames:v", "1001", "-pix_fmt", "gray", "-f", "rawvideo", self.path(raw_path)])
        return parse_picture_ids(Path(raw_path).read_bytes())

    def frame_times(self, path, *, generate_pts=False, numbered_repeated_fixture=False, evidence_path=None):
        entries = "frame=pts_time,best_effort_timestamp_time" if numbered_repeated_fixture else "frame=best_effort_timestamp_time"
        result = json.loads(self.run(self.ffprobe, ["-v", "error", *(["-fflags", "+genpts"] if generate_pts else []), "-select_streams", "V:0",
            "-show_frames", "-show_entries", entries, "-of", "json", self.path(path)]))
        field = "pts_time" if numbered_repeated_fixture and not generate_pts else "best_effort_timestamp_time"
        if evidence_path is not None:
            save(evidence_path, {"selectedField": field, **result})
        require(all(field in frame for frame in result["frames"]), "Missing required picture timestamp field")
        times = [float(frame[field]) for frame in result["frames"]]
        require(times and all(math.isfinite(value) for value in times), "Missing or non-finite decoded picture timestamps")
        require(all(b >= a if numbered_repeated_fixture else b > a for a, b in zip(times, times[1:])),
                "Non-increasing picture timestamps")
        return [x - times[0] for x in times]

    def measure(self, reference, candidate, evidence_dir, *, kept_subtitle_indexes=None, kept_audio_indexes=None,
                numbered_repeated_fixture=False):
        evidence_dir = Path(evidence_dir)
        evidence_dir.mkdir(parents=True, exist_ok=True)
        ref, out = self.probe(reference, True), self.probe(candidate, True)
        save(evidence_dir / "reference-probe.json", ref)
        save(evidence_dir / "candidate-probe.json", out)
        rv = next(x for x in ref["streams"] if x["codec_type"] == "video")
        ov = next(x for x in out["streams"] if x["codec_type"] == "video")
        # A score must not erase lost pictures by blindly resetting, duplicating or trimming frames.
        if numbered_repeated_fixture:
            require((rv["width"], rv["height"]) == (320, 180) == (ov["width"], ov["height"]),
                    "Numbered picture fixture dimensions changed")
            before = self.picture_ids(reference, evidence_dir / "reference-markers.gray", generate_pts=True)
            after = self.picture_ids(candidate, evidence_dir / "candidate-markers.gray")
            save(evidence_dir / "picture-identities.json", {"reference": before, "candidate": after})
            validate_picture_ids(before, after)
        rt = self.frame_times(reference, generate_pts=True, numbered_repeated_fixture=numbered_repeated_fixture,
            evidence_path=evidence_dir / "reference-frame-timestamps.json" if numbered_repeated_fixture else None)
        ot = self.frame_times(candidate, numbered_repeated_fixture=numbered_repeated_fixture,
            evidence_path=evidence_dir / "candidate-frame-timestamps.json" if numbered_repeated_fixture else None)
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
        prep = quality_picture_preparation(rate, rv['width'], rv['height'], pixel)
        graph = (f"[0:v]{prep}[d];"
                 f"[1:v]{prep}[r];"
                 f"[d][r]libvmaf=model={options}:n_threads=2:n_subsample=1:"
                 "log_fmt=json:log_path=vmaf.json:shortest=1:repeatlast=0")
        args = ["-nostdin", "-v", "error", "-i", self.path(candidate), "-i", self.path(reference),
                "-lavfi", graph, "-f", "null", "-"]
        save(evidence_dir / "measurement.json", {"args": args, "model": model,
             "referenceSha256": sha256(reference), "candidateSha256": sha256(candidate),
             "maxTimestampDriftSeconds": drift})
        self.run(self.vmaf, args, cwd=evidence_dir)
        scores = statistics(json.loads((evidence_dir / "vmaf.json").read_text()))
        require(scores["frames"] == len(rt), "VMAF silently omitted or repeated pictures")
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
