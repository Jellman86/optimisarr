import json
import math
import subprocess
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.core import Blocked, Report, inside, quality_failures, statistics
from acceptance.media import compare_report, validate_shadow_report, vmaf_policy, validate_soundtrack_report
from acceptance.corpus import import_corpus
from acceptance.runner import missing_workers, Harness
from media_acceptance import strict_worker_verification_for_run, signal_owned_process


def frames(values):
    return {"frames": [{"frameNum": i, "metrics": {"vmaf": x}} for i, x in enumerate(values)]}


class AcceptanceTests(unittest.TestCase):

    def test_dts_fixture_preserves_packet_order_when_decode_timestamps_repeat(self):
        import struct
        from acceptance.dts_fixture import vfw_matroska
        header = bytearray(40)
        struct.pack_into('<ii', header, 4, 320, 180)
        prefix = b'strf' + struct.pack('<I', 40) + header
        avi = prefix + b'ZfirstAsecond'
        packets = [{'pos': len(prefix), 'size': 6, 'dts_time': '.2', 'flags': ''},
                   {'pos': len(prefix) + 6, 'size': 7, 'dts_time': '.2', 'flags': ''}]
        flac = b'fLaC\x80\x00\x00\x22' + bytes(34)
        result = vfw_matroska(avi, packets, flac, [], 8, subtitles=False)
        self.assertLess(result.index(b'Zfirst'), result.index(b'Asecond'))

    def test_numbered_picture_oracle_rejects_lost_repeated_and_reordered_pictures(self):
        from acceptance.picture_identity import validate_picture_ids
        self.assertEqual(4, validate_picture_ids([0, 1, 2, 3], [0, 1, 2, 3]))
        for changed in [[1, 2, 3], [0, 1, 1, 3], [0, 2, 1, 3], [0, 1, 2, 3, 4], []]:
            with self.assertRaises(AssertionError):
                validate_picture_ids([0, 1, 2, 3], changed)
        with self.assertRaises(AssertionError):
            validate_picture_ids([0, 1, 1, 3], [0, 1, 1, 3])

    def test_numbered_picture_oracle_refuses_partial_ambiguous_and_unbounded_markers(self):
        from acceptance.picture_identity import parse_picture_ids
        def picture(number):
            row = b''.join(bytes([240 if number & (1 << i) else 16]) * 32 for i in range(10))
            return row * 48
        self.assertEqual([0, 1, 511, 1023], parse_picture_ids(b''.join(picture(i) for i in [0, 1, 511, 1023])))
        for raw in [b'', picture(0)[:-1], bytes([128]) * 320 * 48, picture(0) * 1001]:
            with self.assertRaises(AssertionError):
                parse_picture_ids(raw)

    def test_repeated_picture_times_are_allowed_only_for_the_numbered_fixture(self):
        from acceptance.media import Tools
        tools = Tools('ffmpeg', 'ffprobe')
        values = [0, .04, .08, .12, .16, .16, .24]
        tools.run = Mock(return_value=json.dumps({'frames': [
            {'best_effort_timestamp_time': value, 'pts_time': value} for value in values]}))
        with self.assertRaisesRegex(AssertionError, 'Non-increasing'):
            tools.frame_times('/fixture.mkv')
        self.assertEqual(values, tools.frame_times('/fixture.mkv', numbered_repeated_fixture=True))
        for bad in [[0, .04, .03], [0, float('nan')], [float('inf')], []]:
            tools.run.return_value = json.dumps({'frames': [
                {'best_effort_timestamp_time': value, 'pts_time': value} for value in bad]})
            with self.assertRaises(AssertionError):
                tools.frame_times('/fixture.mkv', numbered_repeated_fixture=True)

    def test_repeated_picture_quality_compares_every_picture_after_identity_and_timing_checks(self):
        from acceptance.media import quality_picture_preparation
        from fractions import Fraction
        ordinary = quality_picture_preparation(Fraction(25), 320, 180, 'yuv420p10le')
        repeated = quality_picture_preparation(Fraction(25), 320, 180, 'yuv420p10le', numbered_repeated_fixture=True)
        self.assertIn('fps=25', ordinary)
        self.assertNotIn('fps=', repeated)
        self.assertIn('setpts=N*1/25/TB', repeated)

    def test_numbered_candidate_requires_written_presentation_times_and_retains_both_fields(self):
        from acceptance.media import Tools
        tools = Tools('ffmpeg', 'ffprobe')
        frames = [{'pts_time': 0, 'best_effort_timestamp_time': 0},
                  {'pts_time': .04, 'best_effort_timestamp_time': .000062},
                  {'pts_time': .08, 'best_effort_timestamp_time': .04}]
        tools.run = Mock(return_value=json.dumps({'frames': frames}))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'timestamps.json'
            self.assertEqual([0, .04, .08], tools.frame_times('/candidate.mp4',
                numbered_repeated_fixture=True, evidence_path=path))
            self.assertEqual(frames, json.loads(path.read_text())['frames'])
        tools.run.return_value = json.dumps({'frames': [{'best_effort_timestamp_time': 0}]})
        with self.assertRaises(AssertionError):
            tools.frame_times('/candidate.mp4', numbered_repeated_fixture=True)

    def test_numbered_fixture_cannot_be_requested_outside_its_focused_regression(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'must-not-be-created'
            result = subprocess.run([sys.executable, str(Path(__file__).resolve().parents[1] / 'media_acceptance.py'),
                '--native', str(Path(directory) / 'missing.dll'), '--root', str(root),
                '--fixture-variant', 'dts-repeated'], capture_output=True, text=True, timeout=10)
            self.assertEqual(2, result.returncode)
            self.assertIn('invalid choice', result.stderr)
            self.assertFalse(root.exists())

    def test_uneven_timing_fixture_requires_misleading_rates_and_tight_frame_pairs(self):
        from acceptance.media import validate_uneven_timing_fixture
        probe = {"streams": [{"codec_type": "video", "r_frame_rate": "24/1", "avg_frame_rate": "24/1"}]}
        irregular = [0, .001, 8.084, 8.085, 8.168, 8.169]
        result = validate_uneven_timing_fixture(probe, irregular)
        self.assertEqual(6, result["frames"])
        self.assertAlmostEqual(.001, result["minimumGapSeconds"])
        self.assertAlmostEqual(8.083, result["maximumGapSeconds"])
        for changed in [
            {"streams": [{"codec_type": "video", "r_frame_rate": "24/1", "avg_frame_rate": "12/1"}]},
            {"streams": [{"codec_type": "video", "r_frame_rate": "0/0", "avg_frame_rate": "24/1"}]},
            {"streams": []},
        ]:
            with self.assertRaises(AssertionError):
                validate_uneven_timing_fixture(changed, irregular)
        for changed in [[i / 24 for i in range(6)], [0, .001, .042, .043, .084, .085],
                        [0, .001, .084, float("nan"), .168, .169], [0, .001, .084, .084, .168, .169], [], [0]]:
            with self.assertRaises(AssertionError):
                validate_uneven_timing_fixture(probe, changed)

    def test_soundtrack_report_checks_each_channel_track_mapping_and_measurement_host(self):
        def track(index, distance=0.01):
            return {"track": {"sourceAudioIndex": index, "candidateAudioIndex": index, "language": "eng"},
                    "report": {"measurementLocation": "Worker", "gateEnabled": True, "gatePassed": distance <= 0.1,
                        "evidence": {"preparation": "audio-f32le-48k-video-timeline-v1", "assessment": {
                            "measured": True, "referenceAudioIndex": index, "candidateAudioIndex": index,
                            "windows": [{"distances": {"channelDistances": [distance, distance]}}]}}}}
        report = {"gateEnabled": True, "gatePassed": True, "tracks": [track(0), track(1)]}
        validate_soundtrack_report(report, [0, 1], "Worker", 0.1)
        for changed in [{**report, "tracks": [track(0)]}, {**report, "tracks": [track(1), track(0)]},
                        {**report, "tracks": [track(0), track(1, 0.2)]}]:
            with self.assertRaises(AssertionError): validate_soundtrack_report(changed, [0, 1], "Worker", 0.1)
        with self.assertRaises(AssertionError): validate_soundtrack_report(report, [0, 1], "Server", 0.1)
        bad = {**report, "gatePassed": False, "tracks": [track(0), track(1, 0.2)]}
        validate_soundtrack_report(bad, [0, 1], "Worker", 0.1, passes=False)

    def test_v1_oracle_uses_candidate_format_and_legacy_only_for_high_frame_rates(self):
        source = {"width": 3840, "height": 1600}
        encoded = {"width": 1280, "height": 720, "pix_fmt": "yuv420p", "bits_per_raw_sample": "0"}
        model, options, pixel = vmaf_policy(source, encoded, 24)
        self.assertEqual("vmaf_v1.0.16_1d5h_2160", model)
        self.assertIn("cambi.enc_width=1280", options)
        self.assertIn("cambi.enc_bitdepth=8", options)
        self.assertEqual("yuv420p10le", pixel)
        self.assertEqual("vmaf_4k_v0.6.1", vmaf_policy(source, encoded, 60)[0])
        with self.assertRaises(AssertionError):
            vmaf_policy(source, {**encoded, "pix_fmt": "yuv420p12le"}, 24)

    def test_native_cleanup_targets_only_the_owned_pid_on_windows(self):
        process = Mock(pid=417, poll=Mock(return_value=None))
        with patch("media_acceptance.os.name", "nt"), patch("media_acceptance.subprocess.run") as run:
            signal_owned_process(process)
            self.assertEqual(["taskkill", "/PID", "417", "/T", "/F"], run.call_args.args[0])
        process.poll.return_value = 0
        with patch("media_acceptance.subprocess.run") as run:
            signal_owned_process(process)
            run.assert_not_called()

    def test_opus_candidate_is_discovered_as_an_audio_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            candidate = root / "work" / "1" / "candidate.opus"
            candidate.parent.mkdir(parents=True)
            candidate.write_bytes(b"audio")
            harness = Harness(None, None, root, None)
            self.assertEqual(candidate.resolve(), harness.output({"mediaId": 1}))

    def test_shadow_acceptance_requires_complete_matching_pairs_and_v1_authority(self):
        score = {"frameCount": 24, "vmafMean": 95, "vmafHarmonicMean": 94,
                 "vmafMin": 85, "vmafFifthPercentile": 90, "modelVersion": "vmaf_v0.6.1"}
        report = {"vmaf": {"scores": {**score, "modelVersion": "vmaf_v1.0.16_3d0h"}}, "shadowVmaf": {"status": "Measured", "measurementLocation": "Server",
                  "baselineModel": "vmaf_v0.6.1", "candidateModel": "vmaf_v1.0.16_3d0h",
                  "windows": [{"baseline": score, "candidate": {**score, "modelVersion": "vmaf_v1.0.16_3d0h"}}]}}
        self.assertEqual(1, validate_shadow_report(report)["pairs"])
        report["shadowVmaf"]["windows"][0]["candidate"]["frameCount"] = 23
        with self.assertRaises(AssertionError):
            validate_shadow_report(report)
        report["shadowVmaf"]["status"] = "TimedOut"
        with self.assertRaises(AssertionError):
            validate_shadow_report(report)

    def test_empty_report_cannot_claim_success(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Report(Path(directory) / "report")
            report.write()
            self.assertEqual(2, report.exit_code)
            self.assertEqual("incomplete", json.loads((report.root / "report.json").read_text())["summary"]["status"])

    def test_partial_success_cannot_be_mistaken_for_a_completed_run(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Report(Path(directory) / "report")
            report.case("first test", lambda: {"passed": True})
            self.assertEqual(2, report.exit_code)
            summary = json.loads((report.root / "report.json").read_text())["summary"]
            self.assertEqual("running", summary["status"])
            self.assertFalse(summary["completed"])
            self.assertIn("Run has not finished", (report.root / "junit.xml").read_text())
            report.finish()
            self.assertEqual(0, report.exit_code)
            summary = json.loads((report.root / "report.json").read_text())["summary"]
            self.assertEqual("passed", summary["status"])
            self.assertTrue(summary["completed"])
            self.assertNotIn("<error", (report.root / "junit.xml").read_text())

    def test_fleet_defaults_to_complete_sidecar_verification_with_explicit_opt_out(self):
        self.assertTrue(strict_worker_verification_for_run("fleet", server_verification=False))
        self.assertFalse(strict_worker_verification_for_run("fleet", server_verification=True))
        self.assertFalse(strict_worker_verification_for_run("smoke", server_verification=False))

    def test_expected_offline_revoked_and_empty_workers_cannot_disappear_from_coverage(self):
        workers = [{"name": "online", "online": True, "videoEncoders": ["libx265"]},
                   {"name": "offline", "online": False, "videoEncoders": ["libx265"]},
                   {"name": "revoked", "online": True, "revokedAt": "today", "videoEncoders": ["libx265"]},
                   {"name": "empty", "online": True, "videoEncoders": []}]
        self.assertEqual(["offline", "revoked", "empty", "missing"],
                         missing_workers(workers, ["online", "offline", "revoked", "empty", "missing"]))

    def test_raw_scores_override_dishonest_pooled_summary(self):
        data = frames([0, 100])
        data["pooled_metrics"] = {"vmaf": {"harmonic_mean": 100}}
        result = statistics(data)
        self.assertAlmostEqual(2 / (1 + 1 / 101) - 1, result["harmonic"])
        self.assertEqual(5, result["p5"])
        self.assertEqual(0, result["minimum"])

    def test_invalid_or_partial_measurement_never_passes(self):
        for values in ([], [math.nan], [math.inf], [-1], [101], [True]):
            with self.subTest(values=values), self.assertRaises(AssertionError):
                statistics(frames(values))
        data = frames([93, 94])
        data["frames"][1]["frameNum"] = 0
        with self.assertRaises(AssertionError):
            statistics(data)

    def test_quality_boundary_and_individual_gate_failures(self):
        thresholds = {"harmonic": 93, "p5": 80, "minimum": 50}
        self.assertEqual([], quality_failures(thresholds, thresholds))
        for key in thresholds:
            self.assertEqual([key], quality_failures({**thresholds, key: thresholds[key] - .001}, thresholds))

    def test_symlink_escape_and_root_itself_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "owned"
            root.mkdir()
            (root / "escape").symlink_to(Path(directory))
            for candidate in (root, root / ".." / "other", root / "escape" / "other"):
                with self.assertRaises(AssertionError):
                    inside(root, candidate)

    def test_blocked_is_nonzero_and_not_skipped_in_junit(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Report(Path(directory) / "report")
            report.case("missing <worker>", lambda: (_ for _ in ()).throw(Blocked("offline")))
            self.assertEqual(2, report.exit_code)
            self.assertIn('<error', (report.root / "junit.xml").read_text())
            self.assertNotIn('<worker>', (report.root / "index.html").read_text())
            self.assertEqual("blocked", json.loads((report.root / "report.json").read_text())["results"][0]["status"])

    def test_worker_evidence_must_match_model_frame_count_and_every_gate(self):
        measured = {"model": "vmaf_v0.6.1", "frames": 3, "harmonic": 94, "p5": 85, "minimum": 65}
        good = {"vmaf": {"measured": True, "scores": {"modelVersion": "vmaf_v0.6.1", "frameCount": 3,
            "vmafHarmonicMean": 94, "vmafFifthPercentile": 85, "vmafMin": 65}}}
        compare_report(good, measured)
        for key, value in (("modelVersion", "different"), ("frameCount", 2), ("vmafHarmonicMean", 99),
                           ("vmafFifthPercentile", 99), ("vmafMin", None)):
            bad = json.loads(json.dumps(good))
            bad["vmaf"]["scores"][key] = value
            with self.subTest(key=key), self.assertRaises(AssertionError):
                compare_report(bad, measured)

    def test_corpus_import_rejects_paths_outside_manifest_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "corpus").mkdir()
            manifest = root / "corpus" / "corpus.json"
            manifest.write_text(json.dumps({"version": 1, "clips": [{"id": "escape", "path": "../secret"}]}))
            with self.assertRaisesRegex(AssertionError, "escapes"):
                import_corpus(manifest, root / "import")


if __name__ == "__main__":
    unittest.main()
