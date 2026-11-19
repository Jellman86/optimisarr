import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.super_resolution import same_pixel_shape, validate_candidate


class SuperResolutionAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.source = {"streams": [
            {"codec_type": "video", "width": 128, "height": 72, "nb_read_frames": "96", "pix_fmt": "yuv420p10le", "color_range": "tv"},
            {"codec_type": "audio", "codec_name": "flac", "tags": {"language": "eng"}},
            {"codec_type": "subtitle", "codec_name": "subrip"},
            {"codec_type": "attachment", "tags": {"filename": "fixture.txt"}},
        ]}
        self.candidate = copy.deepcopy(self.source)
        self.candidate["streams"][0].update(width=256, height=144, codec_name="hevc")

    def test_a_complete_upscale_preserves_frame_count_depth_range_and_copied_streams(self):
        validate_candidate(self.source, self.candidate, "hevc_nvenc")

    def test_wrong_dimensions_missing_frames_lost_depth_and_changed_range_are_rejected(self):
        for field, value in (("width", 128), ("nb_read_frames", "95"), ("pix_fmt", "yuv420p"), ("color_range", "pc")):
            with self.subTest(field=field):
                candidate = copy.deepcopy(self.candidate)
                candidate["streams"][0][field] = value
                with self.assertRaises(AssertionError):
                    validate_candidate(self.source, candidate, "hevc_nvenc")

    def test_missing_audio_subtitles_or_attachments_are_rejected(self):
        for index in (1, 2, 3):
            with self.subTest(index=index):
                candidate = copy.deepcopy(self.candidate)
                del candidate["streams"][index]
                with self.assertRaises(AssertionError):
                    validate_candidate(self.source, candidate, "hevc_nvenc")

    def test_a_different_encoder_codec_is_rejected(self):
        with self.assertRaises(AssertionError):
            validate_candidate(self.source, self.candidate, "h264_nvenc")

    def test_an_anamorphic_upscale_must_keep_its_pixel_and_display_shape(self):
        source = copy.deepcopy(self.source)
        source["streams"][0].update(sample_aspect_ratio="64:45", display_aspect_ratio="16:9")
        candidate = copy.deepcopy(self.candidate)
        candidate["streams"][0].update(sample_aspect_ratio="64:45", display_aspect_ratio="16:9")
        validate_candidate(source, candidate, "hevc_nvenc")
        for change in ({"sample_aspect_ratio": "1:1"}, {"sample_aspect_ratio": None}, {"display_aspect_ratio": "4:3"}):
            with self.subTest(change=change):
                squashed = copy.deepcopy(candidate)
                squashed["streams"][0].update(change)
                squashed["streams"][0] = {k: v for k, v in squashed["streams"][0].items() if v is not None}
                with self.assertRaises(AssertionError):
                    validate_candidate(source, squashed, "hevc_nvenc")

    def test_square_and_unreported_pixels_are_the_same_shape(self):
        self.assertTrue(same_pixel_shape(None, "1:1"))
        self.assertTrue(same_pixel_shape("0:1", None))
        self.assertTrue(same_pixel_shape("64:45", "64:45"))
        self.assertFalse(same_pixel_shape("64:45", "1:1"))
        self.assertFalse(same_pixel_shape("64:45", None))
