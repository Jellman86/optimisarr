from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch
from datetime import datetime, timezone

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.monitor import LinuxObserver
from acceptance.core import Blocked


class MonitorTests(unittest.TestCase):
    def observer(self, filesystem="tmpfs"):
        observer = LinuxObserver("http://unused", Mock(return_value=[]), lambda: filesystem, require_ram=True)
        observer.api = Mock()
        observer.api.request.return_value = {"jobs": []}
        observer.samples[7] = [{"observedAt": datetime.now(timezone.utc).isoformat(), "storage": {"kind": "RAM"}, "job": {"sourceMedia": {"width": 640}},
            "metrics": {"cpuPercent": 12, "gpuPercent": None, "sampledAt": datetime.now(timezone.utc).isoformat()},
            "files": [{"name": "source", "bytes": 100}, {"name": "candidate.mkv", "bytes": 80}]}]
        return observer

    def test_dashboard_ram_label_cannot_disguise_disk_filesystem(self):
        with self.assertRaisesRegex(AssertionError, "not RAM"):
            self.observer("ext4").finish(7)

    def test_missing_candidate_observation_is_blocked_not_passed(self):
        observer = self.observer()
        observer.samples[7][0]["files"].pop()
        with self.assertRaises(Blocked):
            observer.finish(7)

    def test_retained_scratch_fails_after_bounded_cleanup_wait(self):
        observer = self.observer()
        observer.files.return_value = [{"name": "source", "bytes": 100}]
        with patch("acceptance.monitor.time.monotonic", side_effect=[0, 31]), \
                self.assertRaisesRegex(AssertionError, "retained"):
            observer.finish(7)

    def test_valid_ram_and_cleanup_can_pass_without_claiming_gpu_load(self):
        evidence = self.observer().finish(7)
        self.assertTrue(evidence["cleanupConfirmed"])
        self.assertIsNone(evidence["samples"][0]["metrics"]["gpuPercent"])

    def test_cancelled_job_requires_cleanup_without_completed_candidate(self):
        observer = self.observer()
        observer.samples[7][0]["files"] = []
        self.assertTrue(observer.finish(7, completed=False)["cleanupConfirmed"])

    def test_frozen_metrics_do_not_count_as_live_utilization(self):
        observer = self.observer()
        observer.samples[7][0]["metrics"]["sampledAt"] = "2000-01-01T00:00:00Z"
        with self.assertRaisesRegex(AssertionError, "stale"):
            observer.finish(7)
