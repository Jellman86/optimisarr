"""Real native qualification tests; generated PCM stays in an owned temporary directory."""
import json
import math
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

EXECUTABLE = sys.argv.pop(1)


class NativeAssessmentTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.reference = self.root / "reference [,] 日本語.raw"
        self.candidate = self.root / "candidate.raw"
        self.samples = [value for n in range(48000 * 2) for value in
                        (0.3 * math.sin(n * 2 * math.pi * 440 / 48000),
                         0.3 * math.sin(n * 2 * math.pi * 900 / 48000))]
        self.write(self.reference, self.samples)

    @staticmethod
    def write(path, values):
        path.write_bytes(struct.pack("<" + "f" * len(values), *values))

    def run_metric(self):
        return subprocess.run([EXECUTABLE, str(self.reference), str(self.candidate), "2"],
                              capture_output=True, text=True, timeout=30)

    def test_identity_and_channel_swap_remain_visible_per_channel(self):
        self.write(self.candidate, self.samples)
        identity = self.run_metric()
        self.assertEqual(identity.returncode, 0, identity.stderr)
        data = json.loads(identity.stdout)
        self.assertEqual(data["distances"], [0, 0])
        self.assertEqual(data["frames"], 96000)
        swapped = [value for n in range(0, len(self.samples), 2)
                   for value in (self.samples[n + 1], self.samples[n])]
        self.write(self.candidate, swapped)
        result = self.run_metric()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(all(value > 0 for value in json.loads(result.stdout)["distances"]))

    def test_nonfinite_clipped_truncated_short_or_oversized_inputs_fail(self):
        for values in ([float("nan"), 0] * 48000, [1.1, 0] * 48000,
                       self.samples[:-2], [0.0, 0.0] * 100):
            self.write(self.candidate, values)
            result = self.run_metric()
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, "")
        with self.candidate.open("wb") as output:
            output.truncate(48000 * 31 * 2 * 4)
        self.assertNotEqual(self.run_metric().returncode, 0)

    def test_owned_report_file_is_written_once_without_overwriting_inputs(self):
        self.write(self.candidate, self.samples)
        report = self.root / "report 日本語.json"
        args = [EXECUTABLE, str(self.reference), str(self.candidate), "2", "--report", str(report)]
        first = subprocess.run(args, capture_output=True, text=True, timeout=30)
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertEqual(json.loads(report.read_text()), json.loads(first.stdout))
        before = report.read_bytes()
        self.assertNotEqual(subprocess.run(args, capture_output=True, timeout=30).returncode, 0)
        self.assertEqual(report.read_bytes(), before)
        original = self.reference.read_bytes()
        args[-1] = str(self.reference)
        self.assertNotEqual(subprocess.run(args, capture_output=True, timeout=30).returncode, 0)
        self.assertEqual(self.reference.read_bytes(), original)

    @unittest.skipUnless(sys.platform == "win32", "Windows extended-length paths")
    def test_long_unicode_inputs_and_reports_preserve_identity_and_exclusive_creation(self):
        directory = self.root
        while len(str(directory.relative_to(self.root))) < 300:
            directory /= "quality assessment 日本語 " + "x" * 24
        extended = Path("\\\\?\\" + str(directory))
        extended.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, Path("\\\\?\\" + str(self.root)))
        reference = extended / "reference 日本語.raw"
        candidate = extended / "candidate.raw"
        self.write(reference, self.samples)
        self.write(candidate, self.samples)
        original = reference.read_bytes()

        for form in ("absolute", "relative", "extended", "unc", "extended-unc"):
            with self.subTest(form=form):
                share = "\\\\localhost\\" + directory.drive[0] + "$"
                if form in ("unc", "extended-unc") and not Path(share).is_dir():
                    self.skipTest("Loopback administrative share is unavailable")

                def argument(name):
                    if form == "extended":
                        return str(extended / name)
                    if form in ("unc", "extended-unc"):
                        path = share + str(directory)[len(directory.drive):] + "\\" + name
                        return "\\\\?\\UNC\\" + path[2:] if form == "extended-unc" else path
                    path = directory / name
                    return str(path.relative_to(self.root) if form == "relative" else path)

                report_name = f"report 日本語-{form}.json"
                args = [EXECUTABLE, argument(reference.name), argument(candidate.name), "2",
                        "--report", argument(report_name)]
                result = subprocess.run(args, cwd=self.root, capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(json.loads(result.stdout)["distances"], [0, 0])
                report = extended / report_name
                self.assertEqual(json.loads(report.read_text()), json.loads(result.stdout))
                saved = report.read_bytes()
                self.assertNotEqual(subprocess.run(args, cwd=self.root, capture_output=True,
                                                   timeout=30).returncode, 0)
                self.assertEqual(report.read_bytes(), saved)
                args[-1] = argument(reference.name)
                self.assertNotEqual(subprocess.run(args, cwd=self.root, capture_output=True,
                                                   timeout=30).returncode, 0)
                self.assertEqual(reference.read_bytes(), original)
                self.assertEqual(candidate.read_bytes(), original)

    @unittest.skipUnless(sys.platform == "darwin", "Mach-O deployment metadata")
    def test_macos_binary_matches_the_sidecars_supported_deployment_floor(self):
        import re
        info = subprocess.run(["otool", "-l", EXECUTABLE], capture_output=True, text=True, check=True).stdout
        versions = re.findall(r"\bminos ([0-9.]+)", info)
        self.assertTrue(versions)
        self.assertTrue(all(int(value.split(".")[0]) <= 14 for value in versions))

    def test_version_reports_the_pinned_metric(self):
        result = subprocess.run([EXECUTABLE, "--version"], capture_output=True, text=True, check=True)
        self.assertEqual(json.loads(result.stdout)["revision"], "f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3")


if __name__ == "__main__":
    unittest.main()
