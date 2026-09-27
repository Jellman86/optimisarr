import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.core import Blocked, Report
from acceptance.workers import Workers
from acceptance.runner import Harness
from acceptance.container_workers import ContainerWorkers


class WorkerAvailabilityTests(unittest.TestCase):
    def test_ram_scratch_is_separate_from_durable_configuration_and_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "durable"
            scratch = Path(directory) / "ram"
            workers = Workers(Mock(), root, "http://localhost:1234", "ffmpeg", "ffprobe", scratch_root=scratch)
            self.assertEqual(str(scratch / "discovery"), workers.env["OPTIMISARR_SIDECAR_WORK"])
            self.assertEqual(str(root / "discovery-config"), workers.env["OPTIMISARR_CONFIG_DIR"])

    def test_missing_docker_is_blocked_without_aborting_other_targets(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Report(Path(directory) / "report")
            workers = ContainerWorkers(Mock(), directory, "http://localhost:1234", "sidecar:test")
            with patch("acceptance.container_workers.shutil.which", return_value=None):
                workers.start([], report=report)
            self.assertEqual(2, report.exit_code)
            self.assertEqual([], workers.containers)

    def test_one_cleanup_failure_does_not_leave_other_owned_containers_unattempted(self):
        with tempfile.TemporaryDirectory() as directory:
            workers = ContainerWorkers(Mock(), directory, "http://localhost:1234", "sidecar:test")
            workers.containers = ["first", "second"]
            with patch.object(workers, "remove", side_effect=[RuntimeError("first failed"), None]) as remove, \
                    self.assertRaises(AssertionError):
                workers.stop()
            self.assertEqual(["first", "second"], [call.args[0] for call in remove.call_args_list])

    def test_container_pairs_without_putting_the_pin_in_command_arguments(self):
        with tempfile.TemporaryDirectory() as directory:
            api = Mock()
            api.request.return_value = {}
            api.post.return_value = {"code": "secret-test-pin"}
            workers = ContainerWorkers(api, directory, "http://localhost:1234", "sidecar:test")
            with patch("acceptance.container_workers.subprocess.run") as run, patch.object(workers, "wait_online"):
                workers.launch([], {}, "libx265")
            argv = run.call_args.args[0]
            self.assertNotIn("secret-test-pin", " ".join(argv))
            self.assertEqual("secret-test-pin", run.call_args.kwargs["env"]["OPTIMISARR_PAIRING_CODE"])
            self.assertIn("/work:size=1g,mode=0700,uid=1000,gid=1000", argv)
            self.assertTrue(workers.observers["acceptance-linux-libx265"].require_ram)

    def test_remote_video_enables_requested_gpu_decode_on_the_test_server(self):
        harness = Harness(Mock(), Mock(), "/tmp/unused", Mock())
        with patch.object(harness, "select_worker"), patch.object(harness, "configure") as configure, \
                patch.object(harness, "create_job", side_effect=Blocked("fixture")), self.assertRaises(Blocked):
            harness.video("gpu", Path("source.mkv"), "hevc_qsv", {"id": 1}, hardware_decode=True)
        configure.assert_called_once_with(encoderMode="Cpu", hardwareDecode=True)

    def test_missing_encoder_does_not_prevent_available_encoder_startup(self):
        with tempfile.TemporaryDirectory() as directory:
            workers = Workers(Mock(), directory, "http://localhost:1234", "ffmpeg", "ffprobe")
            report = Report(Path(directory) / "report")
            with patch.object(workers, "discover", return_value={"videoEncoders": ["libx265"], "operatingSystem": "linux"}), \
                    patch.object(workers, "launch", return_value={"online": True}) as launch:
                workers.start(["worker"], ["hevc_qsv", "libx265"], report=report)
            self.assertEqual(["libx265"], [call.args[2] for call in launch.call_args_list])
            self.assertEqual(["passed", "blocked", "passed"], [r["status"] for r in report.results])
            self.assertEqual(2, report.exit_code)

    def test_failed_start_does_not_hide_remaining_encoders(self):
        with tempfile.TemporaryDirectory() as directory:
            workers = Workers(Mock(), directory, "http://localhost:1234", "ffmpeg", "ffprobe")
            report = Report(Path(directory) / "report")
            with patch.object(workers, "discover", return_value={"videoEncoders": ["libx264", "libx265"], "operatingSystem": "linux"}), \
                    patch.object(workers, "launch", side_effect=[RuntimeError("crash"), {"online": True}]) as launch:
                workers.start(["worker"], report=report)
            self.assertEqual(2, launch.call_count)
            self.assertEqual(["passed", "failed", "passed"], [r["status"] for r in report.results])

    def test_missing_executable_records_incomplete_coverage_without_raising(self):
        with tempfile.TemporaryDirectory() as directory:
            workers = Workers(Mock(), directory, "http://localhost:1234", "ffmpeg", "ffprobe")
            report = Report(Path(directory) / "report")
            workers.start([str(Path(directory) / "absent")], report=report)
            self.assertEqual(2, report.exit_code)

    def test_stale_online_snapshot_cannot_dispatch_to_disconnected_worker(self):
        worker = {"id": 1, "name": "Quark", "online": True, "revokedAt": None}
        api = Mock()
        api.request.return_value = [{**worker, "online": False}]
        harness = Harness(api, Mock(), "/tmp/unused", Mock())
        harness.workers = [worker]
        with self.assertRaises(Blocked):
            harness.select_worker(worker)
        api.request.assert_called_once_with("/api/workers")

    def test_disconnected_job_is_cancelled_promptly_and_reported_blocked(self):
        api = Mock()
        api.request.side_effect = [[{"id": 4, "status": "Queued"}], [{"id": 1, "online": False}]]
        harness = Harness(api, Mock(), "/tmp/unused", Mock(), timeout=5)
        with self.assertRaisesRegex(Blocked, "offline"):
            harness.wait_job({"libraryId": 3, "jobId": 4, "workerId": 1})
        api.post.assert_called_once_with("/api/jobs/4/cancel")
