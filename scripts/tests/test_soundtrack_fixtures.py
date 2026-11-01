import math
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.runner import soundtrack_source_audio, decoded_peak, mkvpropedit_metadata


class SoundtrackFixtureTests(unittest.TestCase):
    def test_lossy_source_uses_aac_and_overs_start_below_full_scale(self):
        self.assertEqual(["-c:a", "aac", "-b:a", "96k", "-ac", "2"],
                         soundtrack_source_audio(lossy=True))
        self.assertIn("pcm_s16le", soundtrack_source_audio(overs=True))
        self.assertIn("flac", soundtrack_source_audio(surround=True))
        self.assertEqual(["-c:a", "flac", "-ac:a:0", "1", "-ac:a:1", "2"], soundtrack_source_audio(mono=True))
        self.assertIn("pcm_s16le", soundtrack_source_audio())
        with self.assertRaises(AssertionError):
            soundtrack_source_audio(lossy=True, overs=True)

    def test_peak_requires_actual_finite_decoded_measurement(self):
        tools = Mock(ffmpeg="ffmpeg", path=Mock(return_value="/fixture.mkv"))
        tools.run.return_value = "[astats] Peak level dB: -12.0\n[astats] Peak level dB: 1.9382\n"
        self.assertAlmostEqual(1.25, decoded_peak(tools, Path("fixture.mkv")), places=4)
        argv = tools.run.call_args.args[1]
        self.assertIn("0:a:0", argv)
        self.assertIn("astats", argv)
        for log in ("", "Peak level dB: nan", "Peak level dB: inf"):
            tools.run.return_value = log
            with self.assertRaises(AssertionError):
                decoded_peak(tools, Path("fixture.mkv"))

    def test_mkvpropedit_emulation_has_only_suffixed_statistics_and_prevents_muxer_rewrites(self):
        argv = mkvpropedit_metadata(8, 2)
        self.assertEqual(["-live", "1"], argv[-2:])
        for index in range(2):
            self.assertIn(f"-metadata:s:a:{index}", argv)
        self.assertEqual(2, argv.count("DURATION="))
        self.assertEqual(2, argv.count("DURATION-eng=00:00:08.000000000"))
        self.assertEqual(2, argv.count("_STATISTICS_WRITING_APP-eng=mkvpropedit"))
