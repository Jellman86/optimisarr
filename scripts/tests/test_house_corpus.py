import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.house_corpus import prepare_house, validate_expected, assert_outcome
from acceptance.corpus import import_corpus
from acceptance.core import sha256
from acceptance.runner import Harness


class HouseCorpusTests(unittest.TestCase):
    def test_house_job_enables_all_three_paths_and_keeps_expected_blocked_original(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "negative-opus.mkv"
            source.write_bytes(b"private clip")
            api, tools, report = Mock(), Mock(), Mock(root=root / "report")
            api.request.return_value = []
            harness = Harness(api, tools, root, report)
            harness.select_worker = Mock()
            case = {"source": source, "sourceSha256": sha256(source), "jobId": 17}
            harness.create_job = Mock(return_value=case)
            harness.configure = Mock()
            harness.wait_job = Mock(return_value={"status": "Failed", "errorMessage": "first window short",
                "verificationReportJson": "{}", "workerName": None})
            entry = {"id": "house-001", "path": source.name, "sha256": sha256(source),
                     "expected": {"outcome": "blocked", "reason": "window short"}}
            result = harness.house_clip(entry, root)
            self.assertTrue(result["originalUnchanged"])
            settings = harness.create_job.call_args.kwargs["overrides"]
            self.assertTrue(settings["soundtrackQualityGateEnabled"])
            self.assertTrue(settings["downmixToStereo"])
            self.assertTrue(settings["adaptiveSoundtrackQuality"])
            # Real library clips are often 10-bit, which H.264's compatibility profile refuses.
            self.assertEqual("hevc", settings["targetVideoCodec"])
            self.assertEqual("libx265", harness.create_job.call_args.kwargs["encoder"])
            self.assertEqual("Cpu", harness.configure.call_args.kwargs["encoderMode"])
            self.assertEqual("aac", settings["videoAudioCodec"])
            harness.wait_job.return_value["workerName"] = "mac"
            harness.house_clip(entry, root, {"id": 1, "name": "mac", "videoEncoders": ["hevc_videotoolbox"]})
            self.assertEqual("hevc_videotoolbox", harness.create_job.call_args.kwargs["encoder"])
            # CPU mode binds every machine to software (#425), so a hardware worker could never claim.
            self.assertEqual("Auto", harness.configure.call_args.kwargs["encoderMode"])
            self.assertEqual("hevc", harness.create_job.call_args.kwargs["overrides"]["targetVideoCodec"])
            harness.wait_job.return_value["workerName"] = None
            api.request.return_value = [{"jobId": 17}]
            with self.assertRaisesRegex(AssertionError, "replaced"):
                harness.house_clip(entry, root)

    def test_expected_outcomes_require_specific_refusal_and_reject_ambiguous_values(self):
        for expected in ("passes", "search-fallback", {"outcome": "blocked", "reason": "window"}):
            self.assertIn(validate_expected(expected)["outcome"], ("passes", "blocked", "search-fallback"))
        for expected in ("blocked", "skip", {}, {"outcome": "blocked", "reason": ""}):
            with self.assertRaises(AssertionError):
                validate_expected(expected)

    def test_outcome_never_accepts_unexpected_failure_or_silent_search_fallback(self):
        search = {"tracks": [{"result": {"probes": [1], "decision": {"fellBack": False}}}]}
        good = {"soundtrackSearch": search, "soundtrackQuality": {"gatePassed": True}}
        assert_outcome("passes", "ReadyToReplace", "", good)
        fallback = {**good, "soundtrackSearch": {"unavailable": "negative start"}}
        assert_outcome("search-fallback", "ReadyToReplace", "", fallback)
        assert_outcome({"outcome": "blocked", "reason": "46 ms"}, "Failed", "first window 46 ms short", {})
        # A per-track refusal is the specific reason; the job error only names the failed check.
        per_track = {"soundtrackQuality": {"tracks": [{"report": {"unavailableReason": "Prepared audio does not cover the requested channel/frame count."}}]}}
        assert_outcome({"outcome": "blocked", "reason": "does not cover"}, "Failed",
                       "Verification failed: Soundtrack 1: Perceptual audio quality (Zimtohrli)", per_track)
        with self.assertRaises(AssertionError):
            assert_outcome({"outcome": "blocked", "reason": "known-duration"}, "Failed", "Verification failed", per_track)
        for expected, status, error, report in [
            ("passes", "ReadyToReplace", "", fallback),
            ("search-fallback", "ReadyToReplace", "", good),
            ("passes", "Failed", "decode failed", good),
            ({"outcome": "blocked", "reason": "46 ms"}, "Failed", "unrelated", {}),
            ({"outcome": "blocked", "reason": "46 ms"}, "ReadyToReplace", "46 ms", good),
            ("passes", "ReadyToReplace", "", {}),
        ]:
            with self.subTest(expected=expected, status=status), self.assertRaises(AssertionError):
                assert_outcome(expected, status, error, report)

    def test_stream_copy_records_metadata_checksum_reason_and_expected_and_imports_as_private(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "private [source].mp4"
            source.write_bytes(b"original")
            tools = Mock()
            tools.path.side_effect = str
            tools.versions.return_value = {"ffmpeg": "test"}
            probe = {"streams": [{"codec_type": "video", "codec_name": "h264"},
                                 {"codec_type": "audio", "codec_name": "aac", "channel_layout": "stereo",
                                  "tags": {"language": "eng", "title": "Main"}}], "format": {"duration": "12"}}
            tools.probe.return_value = probe
            tools.encode.side_effect = lambda argv: Path(argv[-1]).write_bytes(b"clip")
            manifest = prepare_house(tools, root / "house", [{"source": str(source), "start": 2,
                "seconds": 8, "reason": "lossy AAC windows", "expected": "passes"}])
            argv = tools.encode.call_args.args[0]
            self.assertEqual("copy", argv[argv.index("-c") + 1])
            self.assertEqual("0", argv[argv.index("-map") + 1])
            data = json.loads(manifest.read_text())
            clip = data["clips"][0]
            self.assertEqual(probe, clip["probe"])
            self.assertEqual(probe, clip["sourceProbe"])
            self.assertEqual("lossy AAC windows", clip["reason"])
            self.assertEqual(sha256(manifest.parent / clip["path"]), clip["sha256"])
            imported = import_corpus(manifest, root / "imported")
            self.assertEqual(".mp4", Path(json.loads(imported.read_text())["clips"][0]["path"]).suffix)
            (manifest.parent / clip["path"]).write_bytes(b"changed")
            with self.assertRaisesRegex(AssertionError, "checksum"):
                import_corpus(manifest, root / "bad")

    def test_whole_file_preserves_bytes_and_refuses_large_sources_existing_roots_and_invalid_windows(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "opus.mkv"
            source.write_bytes(b"negative timestamps and legacy tags")
            tools = Mock()
            tools.probe.return_value = {"format": {"duration": "8"}, "streams": []}
            tools.versions.return_value = {}
            entry = {"source": str(source), "start": 0, "seconds": 8, "reason": "negative Opus",
                     "expected": {"outcome": "blocked", "reason": "window"}, "cut": "whole-file"}
            manifest = prepare_house(tools, root / "house", [entry])
            clip = json.loads(manifest.read_text())["clips"][0]
            self.assertEqual(sha256(source), clip["sha256"])
            tools.encode.assert_not_called()
            with self.assertRaises(FileExistsError):
                prepare_house(tools, root / "house", [entry])
            for index, changes in enumerate(({"start": -1}, {"seconds": 0}, {"seconds": 601},
                                             {"start": float("nan")}, {"reason": ""}, {"cut": "encode"})):
                with self.assertRaises(AssertionError):
                    prepare_house(tools, root / f"invalid-{index}", [{**entry, **changes}])
            tools.probe.return_value = {"format": {"duration": "3600"}, "streams": []}
            with self.assertRaises(AssertionError):
                prepare_house(tools, root / "large", [entry])


if __name__ == "__main__":
    unittest.main()
