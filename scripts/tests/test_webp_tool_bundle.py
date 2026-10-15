import importlib.util
import json
from pathlib import Path
import subprocess
import unittest

SPEC = importlib.util.spec_from_file_location('media_bundle', Path(__file__).resolve().parents[1] / 'check_media_tool_bundle.py')
bundle = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bundle)


class WebpToolBundleTests(unittest.TestCase):
    def run_tools(self, *, alpha_damage=False, rgb_damage=False, codec='webp', encode_exit=0):
        def run(args, **kwargs):
            if str(args[0]) == 'probe':
                return subprocess.CompletedProcess(args, 0, json.dumps({'streams': [
                    {'codec_name': codec, 'width': 64, 'height': 64}]}).encode(), b'')
            if '-c:v' in args:
                return subprocess.CompletedProcess(args, encode_exit, b'', b'encoder failed' if encode_exit else b'')
            data = bytearray(bundle.webp_fixture())
            if alpha_damage:
                data[3] ^= 1
            if rgb_damage:
                data[0] ^= 1
            return subprocess.CompletedProcess(args, 0, bytes(data), b'')
        return run

    def test_lossless_and_lossy_webp_with_exact_alpha_pass_the_bundle_gate(self):
        self.assertEqual([], bundle.check_webp('encode', 'probe', run=self.run_tools()))

    def test_a_changed_alpha_byte_blocks_both_modes(self):
        problems = bundle.check_webp('encode', 'probe', run=self.run_tools(alpha_damage=True))
        self.assertEqual(2, len(problems))

    def test_lossless_rgb_damage_is_blocked_while_lossy_rgb_changes_are_expected(self):
        problems = bundle.check_webp('encode', 'probe', run=self.run_tools(rgb_damage=True))
        self.assertEqual(1, len(problems))
        self.assertIn('lossless', problems[0])

    def test_missing_encoder_and_a_misidentified_codec_cannot_qualify(self):
        for operations in [self.run_tools(encode_exit=1), self.run_tools(codec='png')]:
            self.assertEqual(2, len(bundle.check_webp('encode', 'probe', run=operations)))
