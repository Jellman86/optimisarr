import importlib.util
import json
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location("export_vmaf_shadow", Path(__file__).parents[1] / "export_vmaf_shadow.py")
MODULE = importlib.util.module_from_spec(SPEC)


class ShadowExportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        SPEC.loader.exec_module(MODULE)

    def test_old_jobs_are_ignored_and_research_status_is_preserved(self):
        shadow = {"status": "TimedOut", "windows": [], "detail": "budget expired"}
        jobs = [{"id": 1}, {"id": 2, "mediaFileId": 8, "relativePath": "private/title.mkv",
                            "verificationReportJson": json.dumps({"shadowVmaf": shadow})}]
        rows = MODULE.observations(jobs)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["jobId"], 2)
        self.assertEqual(rows[0]["mediaFileId"], 8)
        self.assertEqual(rows[0]["shadowVmaf"], shadow)
        self.assertNotIn("relativePath", rows[0])

    def test_damaged_reports_fail_instead_of_disappearing_from_the_dataset(self):
        with self.assertRaises(ValueError):
            MODULE.observations([{"id": 9, "verificationReportJson": "broken"}])

    def test_duplicate_job_ids_are_refused(self):
        with self.assertRaises(ValueError):
            MODULE.observations([{"id": 1}, {"id": 1}])

    def test_pagination_wrapper_is_not_mistaken_for_a_complete_job_list(self):
        with self.assertRaises(ValueError):
            MODULE.observations({"items": [], "total": 100})
