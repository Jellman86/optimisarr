import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch
import tempfile
import json
import io
import re
from types import SimpleNamespace

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

    def test_candidate_offset_changes_only_its_absolute_frame_range(self):
        args = diagnose.command('ffmpeg', '/source.mkv', '/candidate.mp4', 60, 5, 50, True, 'scores.json', frame_offset=1)
        graph = args[args.index('-lavfi') + 1]
        self.assertIn('[0:v]trim=start_frame=3001:end_frame=3251', graph)
        self.assertIn('[1:v]trim=start_frame=3000:end_frame=3250', graph)
        self.assertNotIn('-ss', args)

    def test_negative_candidate_offset_does_not_move_the_reference(self):
        args = diagnose.command('ffmpeg', '/source.mkv', '/candidate.mp4', 60, 5, 50, True, 'scores.json', frame_offset=-1)
        graph = args[args.index('-lavfi') + 1]
        self.assertIn('[0:v]trim=start_frame=2999:end_frame=3249', graph)
        self.assertIn('[1:v]trim=start_frame=3000:end_frame=3250', graph)

    def test_offset_cannot_be_applied_to_an_input_sought_comparison(self):
        with self.assertRaises(ValueError):
            diagnose.command('ffmpeg', '/source.mkv', '/candidate.mp4', 60, 5, 50, False, 'scores.json', frame_offset=1)

    def test_offset_that_would_select_a_negative_frame_is_refused(self):
        with self.assertRaises(ValueError):
            diagnose.command('ffmpeg', '/source.mkv', '/candidate.mp4', 0, 5, 50, True, 'scores.json', frame_offset=-1)

    def test_cli_records_one_sought_baseline_and_all_sequential_offsets(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            source, candidate = base / 'source.mkv', base / 'candidate.mkv'
            source.touch(); candidate.touch()
            root = base / 'evidence'
            argv = ['diagnose', '--ffmpeg', 'ffmpeg', '--ffprobe', 'ffprobe',
                    '--source', str(source), '--candidate', str(candidate), '--root', str(root),
                    '--starts', '60', '--seconds', '5', '--frame-offsets', '-1', '0', '1']
            calls = []
            def measure(args, **kwargs):
                calls.append(args)
                graph = args[args.index('-lavfi') + 1]
                log = re.search(r'log_path=([^:]+):', graph).group(1)
                (Path(kwargs['cwd']) / log).write_text(json.dumps({
                    'pooled_metrics': {'vmaf': {'mean': 95}}, 'frames': [{}] * 250}))
                return SimpleNamespace(returncode=0, stderr='')
            stream = {'width': 640, 'height': 360, 'avg_frame_rate': '50/1'}
            with patch('sys.argv', argv), patch.object(diagnose, 'probe', return_value={'streams': [stream]}), \
                 patch.object(diagnose.subprocess, 'run', side_effect=measure), patch('sys.stdout', new=io.StringIO()):
                self.assertEqual(diagnose.main(), 0)
            report = json.loads((root / 'report.json').read_text())
            self.assertEqual([(w['mode'], w['offsetFrames']) for w in report['windows']],
                             [('seek', 0), ('sequential', -1), ('sequential', 0), ('sequential', 1)])
            self.assertEqual(sum('-ss' in call for call in calls), 1)
            self.assertEqual(len(list(root.glob('*-command.json'))), 4)
            self.assertTrue(all(w['frames'] == 250 for w in report['windows']))

    def test_cli_refuses_offset_sweep_without_the_zero_baseline(self):
        with patch('sys.argv', ['diagnose', '--ffmpeg', 'f', '--ffprobe', 'p', '--source', 's',
                              '--candidate', 'c', '--root', 'r', '--starts', '60', '--frame-offsets', '1']), \
             patch('sys.stderr', new=io.StringIO()):
            with self.assertRaises(SystemExit) as stopped:
                diagnose.main()
            self.assertEqual(stopped.exception.code, 2)
