from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.worker_group import WorkerGroup
from acceptance.core import Blocked, Report


class WorkerGroupTests(unittest.TestCase):
    def test_partial_startup_keeps_other_workers_and_cleanup_attempts_every_owned_launcher(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Report(Path(directory) / "report")
            group = WorkerGroup(Mock(), Path(directory), "http://localhost:12345", "ffmpeg", "ffprobe")
            first, second = Mock(observers={}), Mock(observers={"second": "observer"})
            first.start.side_effect = Blocked("missing image")
            first.stop.side_effect = RuntimeError("first cleanup failed")
            with patch("acceptance.worker_group.ContainerWorkers", return_value=first), \
                    patch("acceptance.worker_group.Workers", return_value=second):
                group.start([{"name": "first", "encoder": "libx265", "image": "worker:test"},
                             {"name": "second", "encoder": "hevc_videotoolbox", "command": ["adapter"], "remote": True}], report)
            self.assertEqual(2, len(group.workers))
            self.assertEqual({"second": "observer"}, group.observers)
            with self.assertRaises(AssertionError):
                group.stop()
            first.stop.assert_called_once()
            second.stop.assert_called_once()

    def test_duplicate_or_unsafe_names_are_refused_before_starting(self):
        with tempfile.TemporaryDirectory() as directory:
            group = WorkerGroup(Mock(), Path(directory), "http://localhost:12345", "ffmpeg", "ffprobe")
            for specs in ([{"name": "a"}, {"name": "a"}], [{"name": "../escape"}]):
                with self.assertRaises(AssertionError):
                    group.start(specs, Mock())
