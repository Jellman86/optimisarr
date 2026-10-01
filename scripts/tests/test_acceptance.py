import json
import math
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.core import Blocked, Report, inside, quality_failures, statistics
from acceptance.media import compare_report, validate_shadow_report
from acceptance.corpus import import_corpus
from acceptance.runner import missing_workers, Harness
from media_acceptance import strict_worker_verification_for_run, signal_owned_process


def frames(values):
    return {"frames": [{"frameNum": i, "metrics": {"vmaf": x}} for i, x in enumerate(values)]}


class AcceptanceTests(unittest.TestCase):
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

    def test_shadow_acceptance_requires_complete_matching_pairs_and_baseline_authority(self):
        score = {"frameCount": 24, "vmafMean": 95, "vmafHarmonicMean": 94,
                 "vmafMin": 85, "vmafFifthPercentile": 90, "modelVersion": "vmaf_v0.6.1"}
        report = {"vmaf": {"scores": score}, "shadowVmaf": {"status": "Measured", "measurementLocation": "Server",
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
