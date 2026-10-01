import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('diagnose', Path(__file__).parents[1] / 'diagnose_frame_pairing.py')
diagnose = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnose)


class FrameDiagnosticTests(unittest.TestCase):
    def test_sequential_comparison_reads_from_start_and_cuts_matching_absolute_frames(self):
        args = diagnose.command('ffmpeg', '/source with spaces.mkv', '/candidate.mp4', 123, 10, 25, True, 'scores.json')
        self.assertNotIn('-ss', args)
        self.assertEqual(args.count('/source with spaces.mkv'), 1)
        self.assertEqual(args[args.index('-lavfi') + 1].count('start_frame=3075:end_frame=3325'), 2)
        self.assertNotIn('-y', args)
        self.assertEqual(args[-3:], ['-f', 'null', '-'])

    def test_seek_comparison_preserves_the_requested_window_on_each_input(self):
        args = diagnose.command('ffmpeg', '/source.mkv', '/candidate.mp4', 123, 10, 25, False, 'scores.json')
        self.assertEqual(args.count('-ss'), 2)
        self.assertEqual(args[args.index('-lavfi') + 1].count('start_frame=0:end_frame=250'), 2)
