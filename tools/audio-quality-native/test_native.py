"""Real native qualification tests; generated PCM stays in an owned temporary directory."""
import json
import math
from pathlib import Path
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

    def test_version_reports_the_pinned_metric(self):
        result = subprocess.run([EXECUTABLE, "--version"], capture_output=True, text=True, check=True)
        self.assertEqual(json.loads(result.stdout)["revision"], "f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3")


if __name__ == "__main__":
    unittest.main()
