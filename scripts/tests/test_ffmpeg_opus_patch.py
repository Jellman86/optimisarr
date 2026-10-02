import importlib.util
from pathlib import Path
import unittest

path = Path(__file__).resolve().parents[1] / 'patch_ffmpeg_opus.py'
spec = importlib.util.spec_from_file_location('opus_patch', path)
patch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(patch)

class OpusPatchTests(unittest.TestCase):
    def test_backport_skips_only_empty_flush_and_is_idempotent(self):
        source = 'if (set_frame_duration(ctx, avctx, buf, buf_size) < 0)\n    goto fail;'
        changed = patch.backport(source)
        self.assertIn('if (buf_size && set_frame_duration', changed)
        self.assertEqual(changed, patch.backport(changed))
    def test_unknown_source_refuses_to_create_an_unverified_build(self):
        with self.assertRaises(ValueError): patch.backport('changed upstream source')
