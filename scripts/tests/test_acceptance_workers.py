import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch, call

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.core import Blocked, Report
from acceptance.workers import Workers
from acceptance.runner import Harness
from acceptance.container_workers import ContainerWorkers


class WorkerAvailabilityTests(unittest.TestCase):
    def test_focused_early_return_still_reports_missing_required_workers(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Report(Path(directory) / "report")
            api = Mock()
            api.request.return_value = []
            harness = Harness(api, Mock(), Path(directory), report)
            harness.preflight = Mock(return_value={})
            harness.configure, harness.restore = Mock(), Mock(return_value=None)
            harness.image_perceptual = Mock(return_value={})
            harness.run(tier="fleet", regression="quality-first-source", expected_workers=["missing"])
            self.assertTrue(any(row["status"] == "blocked" for row in report.results))

    def test_named_native_worker_pairs_under_the_exact_required_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            api = Mock()
            api.request.return_value = {}
            api.post.return_value = {"code": "test-pin"}
            workers = Workers(api, directory, "http://localhost:1234", "ffmpeg", "ffprobe",
                              observe_local=False, name="required-worker")
            process = Mock()
            with patch("acceptance.workers.subprocess.Popen", return_value=process) as launch, patch.object(workers, "wait_online"):
                result = workers.launch(["launcher"], {"operatingSystem": "windows"}, "libx265")
            self.assertEqual("required-worker", result["name"])
            self.assertEqual("required-worker", launch.call_args.kwargs["env"]["OPTIMISARR_ACCEPTANCE_NAME"])
            workers.processes[0][1].close()

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
        configure.assert_called_once_with(encoderMode="IntelQsv", hardwareDecode=True)

    def test_remote_hardware_video_uses_the_worker_encoder_mode(self):
        # CPU mode binds every machine to software encoding (#425), so a hardware worker could never claim.
        harness = Harness(Mock(), Mock(), "/tmp/unused", Mock())
        for encoder, mode in (("hevc_videotoolbox", "Auto"), ("hevc_nvenc", "NvidiaNvenc"), ("libx265", "Cpu")):
            with self.subTest(encoder=encoder), patch.object(harness, "select_worker"), \
                    patch.object(harness, "configure") as configure, \
                    patch.object(harness, "create_job", side_effect=Blocked("fixture")), self.assertRaises(Blocked):
                harness.video("remote", Path("source.mkv"), encoder, {"id": 2})
            configure.assert_called_once_with(encoderMode=mode, hardwareDecode=False)

    def test_worker_encoder_without_a_mode_is_blocked_rather_than_crashing(self):
        harness = Harness(Mock(), Mock(), "/tmp/unused", Mock())
        with patch.object(harness, "select_worker"), patch.object(harness, "configure") as configure, \
                patch.object(harness, "create_job") as create_job:
            with self.assertRaisesRegex(Blocked, "hevc_amf"):
                harness.video("remote", Path("source.mkv"), "hevc_amf", {"id": 3})
        configure.assert_not_called()
        create_job.assert_not_called()

    def test_soundtrack_cases_select_the_proved_workers_encoder_policy_before_encoding(self):
        harness = Harness(Mock(), Mock(), "/tmp/unused", Mock())
        for encoder, mode in (("hevc_videotoolbox", "Auto"), ("hevc_nvenc", "NvidiaNvenc"), ("libx265", "Cpu")):
            worker = {"id": 2, "videoEncoders": [encoder]}
            with self.subTest(encoder=encoder), patch.object(harness, "select_worker"), \
                    patch.object(harness, "configure") as configure, \
                    patch.object(harness.tools, "encode", side_effect=Blocked("fixture")), self.assertRaises(Blocked):
                harness.soundtrack(worker=worker)
            configure.assert_called_once_with(encoderMode=mode, hardwareDecode=False)

    def test_local_soundtrack_restores_cpu_policy_after_a_hardware_case(self):
        harness = Harness(Mock(), Mock(), "/tmp/unused", Mock())
        with patch.object(harness, "select_worker"), patch.object(harness, "configure") as configure, \
                patch.object(harness.tools, "encode", side_effect=Blocked("fixture")), self.assertRaises(Blocked):
            harness.soundtrack()
        configure.assert_called_once_with(encoderMode="Cpu", hardwareDecode=False)

    def test_soundtrack_job_uses_worker_encoder_defaults_instead_of_a_cpu_preset(self):
        for encoder in ("hevc_videotoolbox", "hevc_nvenc", "libx265"):
            with self.subTest(encoder=encoder), tempfile.TemporaryDirectory() as directory:
                report = Mock(root=Path(directory) / "report")
                tools = Mock()
                tools.probe.return_value = {"streams": []}
                harness = Harness(Mock(), tools, directory, report)
                with patch.object(harness, "select_worker"), patch.object(harness, "configure"), \
                        patch.object(harness, "create_job", side_effect=Blocked("fixture")) as create_job, self.assertRaises(Blocked):
                    harness.soundtrack(worker={"id": 2, "videoEncoders": [encoder]})
                self.assertEqual(encoder, create_job.call_args.kwargs["encoder"])

    def test_soundtrack_fixture_video_meets_hardware_encoder_minimum_size(self):
        # NVENC refuses frames below its minimum size (64x64 failed on a physical NVIDIA worker).
        harness = Harness(Mock(), Mock(), "/tmp/unused", Mock())
        with patch.object(harness, "select_worker"), patch.object(harness, "configure"), \
                patch.object(harness.tools, "encode", side_effect=Blocked("fixture")) as encode, self.assertRaises(Blocked):
            harness.soundtrack(worker={"id": 2, "videoEncoders": ["hevc_nvenc"]})
        source = next(arg for arg in encode.call_args.args[0] if arg.startswith("testsrc2="))
        width, height = (int(v) for v in source.split("size=")[1].split(":")[0].split("x"))
        self.assertGreaterEqual(width, 160)
        self.assertGreaterEqual(height, 128)

    def test_remote_running_cancel_uses_the_worker_encoder_mode(self):
        harness = Harness(Mock(), Mock(), "/tmp/unused", Mock())
        with patch.object(harness, "select_worker"), patch.object(harness, "configure") as configure, \
                patch.object(harness, "create_job", side_effect=Blocked("fixture")), self.assertRaises(Blocked):
            harness.cancel_running({"id": 2, "name": "mac"}, "hevc_videotoolbox")
        configure.assert_called_once_with(encoderMode="Auto", hardwareDecode=False)

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

    def test_selection_skips_workers_revoked_since_preflight(self):
        api = Mock()
        old = {"id": 1, "name": "retired", "revokedAt": None}
        live = {"id": 2, "name": "live", "online": True, "revokedAt": None}
        api.request.return_value = [{**old, "revokedAt": "now"}, live]
        harness = Harness(api, Mock(), "/tmp/unused", Mock())
        harness.workers = [old, live]
        harness.settings = {"remoteWorkersAvailable": True}
        with patch.object(harness, "configure"):
            harness.select_worker(live)
        self.assertEqual([call("/api/workers"), call("/api/workers/2/drain", "DELETE")], api.request.call_args_list)

    def test_restore_skips_revoked_and_missing_workers_and_continues_after_failure(self):
        api = Mock()
        workers = [{"id": i, "revokedAt": None, "drainRequestedAt": None} for i in range(1, 5)]
        def request(path, *args):
            if path == "/api/jobs": return [{"id": 9, "status": "Encoding"}]
            if path == "/api/workers": return [{**workers[0], "revokedAt": "now"}, workers[2], workers[3]]
            if path == "/api/workers/3/drain": raise RuntimeError("drain unavailable")
        api.request.side_effect = request
        harness = Harness(api, Mock(), "/tmp/unused", Mock())
        harness.workers, harness.job_ids, harness.settings = workers, {9}, {"original": True}
        with self.assertRaisesRegex(AssertionError, "drain unavailable"):
            harness.restore()
        api.post.assert_called_once_with("/api/jobs/9/cancel")
        self.assertNotIn(call("/api/workers/1/drain", "DELETE"), api.request.call_args_list)
        self.assertNotIn(call("/api/workers/2/drain", "DELETE"), api.request.call_args_list)
        self.assertIn(call("/api/workers/4/drain", "DELETE"), api.request.call_args_list)
        self.assertEqual(call("/api/settings", "PUT", {"original": True}), api.request.call_args_list[-1])
