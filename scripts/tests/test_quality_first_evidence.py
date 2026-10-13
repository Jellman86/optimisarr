import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.quality_first import validate_search, validate_image_command


class QualityFirstEvidenceTests(unittest.TestCase):
    def test_jpeg_and_webp_final_commands_use_their_actual_selected_controls(self):
        validate_image_command("jpeg", 60, "-c:v mjpeg -q:v 14 final.jpg")
        validate_image_command("webp", 60, "-c:v libwebp -quality 60 final.webp")
        for target, command in [("jpeg", "-q:v 2 final.jpg"), ("webp", "-q:v 14 final.webp"),
                                ("webp", "-quality 95 final.webp")]:
            with self.assertRaises(AssertionError):
                validate_image_command(target, 60, command)

    def report(self):
        return {"kind": "Audio", "encoder": "aac", "sourceSha256": "a" * 64,
                "audioWindows": [{"startSeconds": 0, "durationSeconds": 30}],
                "result": {"decision": {"selectedValue": 96, "fellBack": False, "reason": "Measured"},
                           "probes": [{"value": 128, "encodedBytes": 1000, "measurement": {"available": True, "meetsTarget": True}},
                                      {"value": 96, "encodedBytes": 800, "measurement": {"available": True, "meetsTarget": True}}]}}

    def test_valid_selection_is_bound_to_source_and_smallest_passing_candidate(self):
        self.assertEqual(96, validate_search(self.report(), "Audio", "a" * 64, 128)["selectedValue"])

    def test_wrong_source_missing_probes_or_unmeasured_choice_is_refused(self):
        for change in (lambda r: r.update(sourceSha256="b" * 64),
                       lambda r: r["result"].update(probes=[]),
                       lambda r: r["result"]["decision"].update(selectedValue=64),
                       lambda r: r["result"]["decision"].update(selectedValue=128),
                       lambda r: r["result"]["probes"][1]["measurement"].update(available=False),
                       lambda r: r["result"]["probes"].extend(copy.deepcopy(r["result"]["probes"]) * 2)):
            report = self.report()
            change(report)
            with self.assertRaises(AssertionError):
                validate_search(report, "Audio", "a" * 64, 128)

    def test_fallback_must_explicitly_choose_baseline_and_explain_it(self):
        report = self.report()
        report["result"]["decision"].update(selectedValue=128, fellBack=True, reason="No target pass")
        validate_search(report, "Audio", "a" * 64, 128)
        report["result"]["decision"]["reason"] = ""
        with self.assertRaises(AssertionError):
            validate_search(report, "Audio", "a" * 64, 128)

    def test_unavailable_encoder_can_record_zero_bytes_only_for_explicit_fallback(self):
        report = self.report()
        report["result"]["probes"] = [{"value": 128, "encodedBytes": 0, "measurement": {"available": False, "meetsTarget": False}}]
        report["result"]["decision"].update(selectedValue=128, fellBack=True, reason="Encoder unavailable")
        validate_search(report, "Audio", "a" * 64, 128)
        report["result"]["probes"][0]["measurement"]["available"] = True
        with self.assertRaises(AssertionError):
            validate_search(report, "Audio", "a" * 64, 128)

    def test_source_refusal_records_no_windows_or_candidates(self):
        report = self.report()
        report.update(preparationBlocked=True, audioWindows=[])
        report["result"]["probes"] = []
        report["result"]["decision"].update(selectedValue=128, fellBack=True, reason="Source unsupported")
        validate_search(report, "Audio", "a" * 64, 128)
        report["preparationBlocked"] = False
        with self.assertRaises(AssertionError):
            validate_search(report, "Audio", "a" * 64, 128)


if __name__ == "__main__":
    unittest.main()
