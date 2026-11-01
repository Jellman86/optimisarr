import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_check import ReleaseCheck, public_summary, package_receipt, REGRESSIONS, owned_command, same_version

DIGEST = "sha256:" + "a" * 64
COMMIT = "b" * 40


class ReleaseCheckTests(unittest.TestCase):
    def test_three_and_four_part_versions_preserve_zero_patch_versions(self):
        self.assertTrue(same_version("0.3.0", "0.3.0.0"))
        self.assertTrue(same_version("0.2.24.0", "0.2.24"))
        self.assertFalse(same_version("0.3.0.1", "0.3.0"))
        self.assertFalse(same_version(None, "0.3.0"))

    def test_missing_pinned_image_is_blocked_artifact_coverage(self):
        with tempfile.TemporaryDirectory() as directory:
            run = Mock(side_effect=subprocess.CalledProcessError(1, ["docker", "image", "inspect"]))
            check = ReleaseCheck(Path(directory) / "release", DIGEST, {}, run=run)
            check.artifacts_check(COMMIT)
            self.assertEqual(["blocked"] * 3, [s["status"] for s in check.steps])

    def test_timeout_gives_only_the_owned_harness_a_chance_to_clean_up(self):
        process = Mock(pid=1234, poll=Mock(return_value=None))
        process.communicate.side_effect = [subprocess.TimeoutExpired(["harness"], 1), (None, None)]
        with patch("release_check.subprocess.Popen", return_value=process), patch("release_check.os.killpg") as stop, \
                self.assertRaises(subprocess.TimeoutExpired):
            owned_command(["harness"], timeout=1)
        import signal
        stop.assert_called_once_with(1234, signal.SIGINT)
        process.kill.assert_not_called()

    def test_complete_fake_release_binds_all_artifacts_and_starts_mixed_workers_together(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt = {"sourceCommit": COMMIT, "version": "0.2.24", "ffmpegRevision": "c" * 40,
                       "nativeHelperRevision": "d" * 40, "sha256": "e" * 64}
            config = {"native": {"path": "/fake/server.dll"}, "requiredWorkers": ["linux", "macos", "windows"],
                "artifacts": {"image": receipt, "macos": receipt, "windows": receipt},
                "workers": {"linux": {**receipt, "image": "worker@" + DIGEST, "encoder": "libx265"},
                            "macos": {**receipt, "ssh": {"host": "fake-mac"}, "encoder": "hevc_videotoolbox"},
                            "windows": {**receipt, "ssh": {"host": "fake-windows"}, "encoder": "hevc_nvenc"}}}
            def fake(argv, **kwargs):
                if "inspect" in argv:
                    return subprocess.CompletedProcess(argv, 0, stdout=json.dumps({"Id": DIGEST}))
                if "/app/optimisarr-audio-quality" in argv:
                    return subprocess.CompletedProcess(argv, 0, stdout=json.dumps({"revision": "d" * 40}))
                if "media_acceptance.py" not in " ".join(argv):
                    return subprocess.CompletedProcess(argv, 0, stdout="ffmpeg version 7.1.4")
                result = self.fake_run(argv, **kwargs)
                root = Path(argv[argv.index("--root") + 1])
                path = root / "report/report.json"
                report = json.loads(path.read_text())
                report["environment"] = {"health": {"version": "0.2.24.0"}, "workers": [
                    {"name": name, "operatingSystem": name, "online": True, "protocolVersion": 13,
                     "sidecarVersion": "0.2.24", "videoEncoders": ["libx265"]} for name in config["requiredWorkers"]]}
                path.write_text(json.dumps(report))
                if "house-corpus" in argv:
                    report["results"] = [{"name": "house-clip-a", "status": "passed"}]
                    path.write_text(json.dumps(report))
                if "--worker-config" in argv:
                    launchers = json.loads(Path(argv[argv.index("--worker-config") + 1]).read_text())
                    self.assertEqual({"linux", "macos", "windows"}, {w["name"] for w in launchers})
                return result
            corpus = Path(directory) / "corpus.json"
            corpus.write_text(json.dumps({"kind": "house", "clips": [{"id": "clip-a", "property": "lossy-aac-window"}]}))
            check = ReleaseCheck(Path(directory) / "release", "registry.example/image@" + DIGEST, config,
                                 run=fake, config_path=Path(directory) / "config.json", corpus=corpus)
            check.artifacts_check(COMMIT)
            check.run_matrices()
            self.assertEqual(0, check.finish())
            summary = json.loads((check.root / "release-check.json").read_text())
            self.assertTrue(all(w["protocolVersion"] == 13 for w in summary["workers"]))
            self.assertEqual({"macos", "windows"}, {key for key in summary["artifacts"] if key in ("macos", "windows")})

    def test_shareable_report_cannot_pass_until_the_run_finishes(self):
        with tempfile.TemporaryDirectory() as directory:
            check = ReleaseCheck(Path(directory) / "release", DIGEST, {})
            check.record("smoke", "passed")
            self.assertEqual("blocked", json.loads((check.root / "release-check.json").read_text())["status"])
            self.assertEqual(0, check.finish())
            self.assertEqual("passed", json.loads((check.root / "release-check.json").read_text())["status"])

    def fake_run(self, argv, **kwargs):
        if "media_acceptance.py" in " ".join(argv):
            root = Path(argv[argv.index("--root") + 1])
            root.mkdir()
            (root / "report").mkdir()
            (root / "report" / "report.json").write_text(json.dumps({"summary": {"completed": True, "status": "passed"},
                "environment": {"workers": [{"name": "named-worker", "online": True, "protocolVersion": 13,
                    "sidecarVersion": "0.2.24", "videoEncoders": ["libx265"]}]},
                "results": [{"status": "passed"}]}))
        return subprocess.CompletedProcess(argv, 0, stdout="", stderr="")

    def test_every_matrix_runs_new_roots_and_native_only_coverage_cannot_disappear(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "release"
            run = Mock(side_effect=self.fake_run)
            check = ReleaseCheck(root, DIGEST, {"requiredWorkers": ["named-worker", "absent"],
                "workers": {"named-worker": {"image": "worker@" + DIGEST, "encoder": "libx265"}}}, run=run)
            check.run_matrices()
            calls = [call.args[0] for call in run.call_args_list]
            regressions = [argv[argv.index("--regression") + 1] for argv in calls if "--regression" in argv]
            self.assertEqual(set(REGRESSIONS) - {"quality-first-ownership"}, set(regressions))
            roots = [argv[argv.index("--root") + 1] for argv in calls]
            self.assertEqual(len(roots), len(set(roots)))
            fleet = next(argv for argv in calls if "--expected-worker" in argv and "--regression" not in argv)
            self.assertEqual("named-worker", fleet[fleet.index("--expected-worker") + 1])
            launchers = json.loads(Path(fleet[fleet.index("--worker-config") + 1]).read_text())
            self.assertEqual("named-worker", launchers[0]["name"])
            self.assertIn("absent", fleet)
            self.assertTrue(any(s["property"] == "quality-first-ownership" and s["status"] == "blocked" for s in check.steps))
            self.assertTrue(any(s["property"] == "fleet" and s["status"] == "blocked" for s in check.steps))
            self.assertNotEqual(0, check.finish())

    def test_native_ownership_and_house_corpus_are_explicit_and_worker_absence_is_blocked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "private.json"
            manifest.write_text(json.dumps({"kind": "house", "clips": [{"id": "private title", "property": "negative-opus-start"}]}))
            run = Mock(side_effect=self.fake_run)
            check = ReleaseCheck(root / "release", DIGEST, {"native": {"path": "/build/server.dll", "ffmpeg": "/tools/ffmpeg"}},
                                 corpus=manifest, run=run)
            check.run_matrices()
            commands = [c.args[0] for c in run.call_args_list]
            native = next(argv for argv in commands if "quality-first-ownership" in argv)
            self.assertIn("--native", native)
            self.assertNotIn("--image", native)
            house = next(argv for argv in commands if "house-corpus" in argv)
            self.assertEqual(str(manifest), house[house.index("--corpus") + 1])
            self.assertTrue(any(s["property"] == "fleet" and s["status"] == "blocked" for s in check.steps))

    def test_a_release_without_the_house_corpus_cannot_pass(self):
        # The roadmap requires the real-media corpus; leaving it out must be visible, not silent.
        with tempfile.TemporaryDirectory() as directory:
            check = ReleaseCheck(Path(directory) / "release", DIGEST, {}, run=Mock(side_effect=self.fake_run))
            check.run_matrices()
            house = [s for s in check.steps if s["property"] == "house-corpus"]
            self.assertEqual(["blocked"], [s["status"] for s in house])

    def test_a_corpus_clip_without_its_own_passing_result_blocks_the_step(self):
        # A clip that never ran must not hide behind an otherwise passing harness report.
        with tempfile.TemporaryDirectory() as directory:
            corpus = Path(directory) / "corpus.json"
            corpus.write_text(json.dumps({"kind": "house", "clips": [{"id": "ran", "property": "lossy-aac-window"},
                                                                      {"id": "missing", "property": "negative-opus-start"}]}))
            def run(argv, **kwargs):
                result = self.fake_run(argv, **kwargs)
                if "house-corpus" in argv:
                    path = Path(argv[argv.index("--root") + 1]) / "report/report.json"
                    report = json.loads(path.read_text())
                    report["results"] = [{"name": "house-ran", "status": "passed"}]
                    path.write_text(json.dumps(report))
                return result
            check = ReleaseCheck(Path(directory) / "release", DIGEST, {}, corpus=corpus, run=run)
            check.run_matrices()
            house = next(s for s in check.steps if s["property"] == "house-corpus")
            self.assertEqual("blocked", house["status"])
            self.assertEqual(["passed", "blocked"], [clip["status"] for clip in house["corpus"]])

    def test_an_interrupted_or_crashed_run_is_reported_as_the_release_check_not_a_matrix(self):
        summary = public_summary({"completed": True, "steps": [{"property": "release-check", "status": "failed"}]})
        self.assertEqual("release-check", summary["steps"][0]["property"])
        self.assertEqual("failed", summary["status"])

    def test_partial_missing_blocked_and_nonzero_child_reports_never_pass(self):
        for document, code, expected in [
            ({"summary": {"completed": False, "status": "running"}}, 0, "blocked"),
            ({"summary": {"completed": True, "status": "incomplete"}, "results": [{"status": "blocked"}]}, 2, "blocked"),
            ({"summary": {"completed": True, "status": "passed"}}, 1, "failed"),
            (None, 0, "blocked"),
        ]:
            with self.subTest(document=document), tempfile.TemporaryDirectory() as directory:
                def fake(argv, **kwargs):
                    root = Path(argv[argv.index("--root") + 1])
                    if document:
                        (root / "report").mkdir(parents=True)
                        (root / "report" / "report.json").write_text(json.dumps(document))
                    return subprocess.CompletedProcess(argv, code)
                check = ReleaseCheck(Path(directory) / "run", DIGEST, {}, run=fake)
                check.acceptance("smoke", [])
                self.assertEqual(expected, check.steps[0]["status"])

    def test_redaction_is_an_allowlist_even_when_private_fields_contain_secrets(self):
        private = "secret-title /private/source.mkv host.example LibraryName PIN123"
        summary = public_summary({"steps": [{"property": "house-corpus", "status": "failed", "detail": private,
            "corpus": [{"id": private, "property": "negative-opus-start", "reason": private}]}],
            "artifacts": {"imageDigest": DIGEST, "sourceCommit": COMMIT, "path": private},
            "environment": {"name": private}, "workers": [{"name": private, "protocolVersion": 13,
                "sidecarVersion": "0.2.24", "ffmpegRevision": "c" * 40, "nativeHelperRevision": "d" * 40}]})
        text = json.dumps(summary)
        for forbidden in ("secret-title", "/private", "host.example", "LibraryName", "PIN123"):
            self.assertNotIn(forbidden, text)
        self.assertIn("negative-opus-start", text)
        self.assertIn(DIGEST, text)
        self.assertEqual(13, summary["workers"][0]["protocolVersion"])

    def test_package_receipt_requires_hash_version_commit_and_tool_revisions(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "worker.msi"
            package.write_bytes(b"packaged worker")
            spec = {"path": str(package), "version": "0.2.24", "sourceCommit": COMMIT,
                    "ffmpegRevision": "c" * 40, "nativeHelperRevision": "d" * 40}
            receipt = package_receipt(spec, COMMIT)
            self.assertEqual(64, len(receipt["sha256"]))
            for changes in ({"path": "/missing"}, {"sourceCommit": "e" * 40}, {"version": None}, {"nativeHelperRevision": None}):
                with self.assertRaises(Exception):
                    package_receipt({**spec, **changes}, COMMIT)
            with self.assertRaises(AssertionError):
                package_receipt({**spec, "sha256": "f" * 64}, COMMIT)

    def test_image_digest_and_packaged_artifacts_are_required_and_helper_revision_is_observed(self):
        with tempfile.TemporaryDirectory() as directory:
            def fake(argv, **kwargs):
                if "inspect" in argv:
                    return subprocess.CompletedProcess(argv, 0, stdout=json.dumps({"Id": DIGEST}))
                if "/app/optimisarr-audio-quality" in argv:
                    return subprocess.CompletedProcess(argv, 0, stdout=json.dumps({"revision": "d" * 40}))
                return subprocess.CompletedProcess(argv, 0, stdout="ffmpeg version 7.1.4-Jellyfin")
            receipt = {"sourceCommit": COMMIT, "version": "0.2.24", "ffmpegRevision": "c" * 40,
                       "nativeHelperRevision": "d" * 40}
            check = ReleaseCheck(Path(directory) / "release", "registry.example/image@" + DIGEST,
                                 {"artifacts": {"image": receipt}}, run=fake)
            check.artifacts_check(COMMIT)
            self.assertEqual("passed", check.steps[0]["status"])
            self.assertEqual(["blocked", "blocked"], [s["status"] for s in check.steps[1:]])
            self.assertEqual(DIGEST, check.artifacts["imageDigest"])
            self.assertEqual("7.1.4", check.artifacts["ffmpegVersion"])
            self.assertEqual("d" * 40, check.artifacts["nativeHelperRevision"])

    def test_regression_list_contains_all_ci_commands_and_all_harness_choices(self):
        import re
        from release_check import REPOSITORY
        workflow_text = "\n".join(path.read_text() for path in (REPOSITORY / ".github/workflows").glob("*.yml"))
        self.assertTrue(set(re.findall(r"--regression\s+([a-z-]+)", workflow_text)) <= set(REGRESSIONS))
        cli = (REPOSITORY / "scripts/media_acceptance.py").read_text()
        choices = cli.split('choices=("house-corpus", ', 1)[1].split('), help=', 1)[0]
        self.assertEqual(set(REGRESSIONS), set(re.findall(r'"([a-z-]+)"', choices)))


if __name__ == "__main__":
    unittest.main()
