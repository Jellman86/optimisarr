using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class CandidateEvaluatorTests
{
    private static readonly RuleSettings Hevc = RuleProfileDefaults.For(RuleProfile.ConservativeHevc);

    private static MediaProperties File(
        string? videoCodec = "h264",
        string? container = "matroska,webm",
        int? width = 1920,
        int? height = 1080,
        long sizeBytes = 4L * 1024 * 1024 * 1024,
        bool isHdr = false,
        string relativePath = "Movies/Example (2020)/Example.mkv",
        string? optimisedMarker = null,
        MediaKind kind = MediaKind.Video,
        string? audioCodec = null,
        double? durationSeconds = null,
        bool isDolbyVision = false) =>
        new(container, videoCodec, width, height, sizeBytes, isHdr, relativePath, optimisedMarker, kind,
            audioCodec, DurationSeconds: durationSeconds, IsDolbyVision: isDolbyVision);

    private static MediaProperties AudioFile(
        string audioCodec,
        long sizeBytes = 40L * 1024 * 1024,
        int? audioBitrateKbps = null,
        int attachedPictureCount = 0,
        int subtitleTrackCount = 0,
        int maxAudioChannels = 2) =>
        new(null, null, null, null, sizeBytes, false, "Music/Album/Track.flac", null,
            MediaKind.Audio, audioCodec, audioBitrateKbps, AttachedPictureCount: attachedPictureCount,
            SubtitleTrackCount: subtitleTrackCount, MaxAudioChannels: maxAudioChannels);

    // An image's still-picture codec is captured as the file's VideoCodec by the probe.
    private static MediaProperties ImageFile(
        string imageCodec,
        long sizeBytes = 4L * 1024 * 1024,
        int? frameCount = null,
        string? pixelFormat = null,
        int? bitsPerRawSample = null) =>
        new("image2", imageCodec, 4000, 3000, sizeBytes, false, "Photos/2024/IMG_0001.jpg", null,
            MediaKind.Image, FrameCount: frameCount, PixelFormat: pixelFormat,
            BitsPerRawSample: bitsPerRawSample);

    [Fact]
    public void A_lossless_audio_file_is_eligible_for_re_encode_to_aac()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("flac"), Hevc);

        Assert.True(decision.IsEligible);
        Assert.Contains("flac", decision.Reason);
        Assert.Contains("aac", decision.Reason);
    }

    [Fact]
    public void A_lossy_audio_file_is_left_untouched_by_default()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("mp3", audioBitrateKbps: 320), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("lossy", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_high_bitrate_lossy_file_is_eligible_when_lossy_re_encode_is_opted_in()
    {
        var rules = Hevc with { ReencodeLossyAudio = true };

        // 320 kbps MP3 is well above the 128 kbps AAC target, so conversion genuinely saves space.
        var decision = CandidateEvaluator.Evaluate(AudioFile("mp3", audioBitrateKbps: 320), rules);

        Assert.True(decision.IsEligible);
        Assert.Contains("320", decision.Reason);
        Assert.Contains("aac", decision.Reason);
    }

    [Fact]
    public void A_lossy_file_near_the_target_bitrate_is_left_untouched_even_when_opted_in()
    {
        var rules = Hevc with { ReencodeLossyAudio = true };

        // 128 kbps MP3 against a 128 kbps Opus target: re-encoding would only add loss for no saving.
        var decision = CandidateEvaluator.Evaluate(AudioFile("mp3", audioBitrateKbps: 128), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("save space", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lossy_file_with_unknown_bitrate_is_left_untouched_even_when_opted_in()
    {
        var rules = Hevc with { ReencodeLossyAudio = true };

        var decision = CandidateEvaluator.Evaluate(AudioFile("mp3", audioBitrateKbps: null), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("bitrate unknown", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lossless_file_is_eligible_even_without_a_known_bitrate()
    {
        // Lossless eligibility never depends on the bitrate threshold, opted in or not.
        var rules = Hevc with { ReencodeLossyAudio = true };

        var decision = CandidateEvaluator.Evaluate(AudioFile("flac", audioBitrateKbps: null), rules);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void An_audio_file_already_in_aac_is_skipped()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("aac"), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("Already", decision.Reason);
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("opus")]
    public void Unsafe_audio_targets_reject_attached_cover_art_before_queueing(string targetCodec)
    {
        var rules = Hevc with { TargetAudioCodec = targetCodec };

        var decision = CandidateEvaluator.Evaluate(
            AudioFile("flac", attachedPictureCount: 1), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("cover art", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MP3", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aac_target_accepts_timed_lyrics_when_the_source_has_no_artwork()
    {
        var decision = CandidateEvaluator.Evaluate(
            AudioFile("flac", subtitleTrackCount: 1), Hevc);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Mp3_target_accepts_attached_cover_art()
    {
        var rules = Hevc with { TargetAudioCodec = "mp3" };

        var decision = CandidateEvaluator.Evaluate(
            AudioFile("flac", attachedPictureCount: 1), rules);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Mp3_target_rejects_timed_lyrics_before_queueing()
    {
        var rules = Hevc with { TargetAudioCodec = "mp3" };

        var decision = CandidateEvaluator.Evaluate(
            AudioFile("flac", subtitleTrackCount: 1), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("timed lyrics", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Multichannel_mp3_is_rejected_without_an_explicit_downmix()
    {
        var rules = Hevc with { TargetAudioCodec = "mp3" };

        var decision = CandidateEvaluator.Evaluate(AudioFile("flac", maxAudioChannels: 6), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("6-channel", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Multichannel_mp3_is_eligible_with_an_explicit_stereo_downmix()
    {
        var rules = Hevc with { TargetAudioCodec = "mp3", DownmixToStereo = true };

        var decision = CandidateEvaluator.Evaluate(AudioFile("flac", maxAudioChannels: 6), rules);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Lossy_surround_reencode_uses_the_channel_aware_bitrate_for_saving_decisions()
    {
        var rules = Hevc with { ReencodeLossyAudio = true, AudioBitrateKbps = 128 };

        var decision = CandidateEvaluator.Evaluate(
            AudioFile("ac3", audioBitrateKbps: 384, maxAudioChannels: 6), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("384 kbps target", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tiny_audio_file_is_below_the_minimum_size()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("flac", sizeBytes: 1024 * 1024), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("minimum size", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lossless_image_is_not_silently_converted_to_lossy_jpeg()
    {
        var decision = CandidateEvaluator.Evaluate(ImageFile("png", pixelFormat: "rgb24", bitsPerRawSample: 8), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("lossy", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_animated_image_is_left_untouched()
    {
        // A multi-frame GIF is really a short video; the still pipeline would flatten it.
        var decision = CandidateEvaluator.Evaluate(ImageFile("gif", frameCount: 36), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("Animated", decision.Reason);
    }

    [Fact]
    public void A_single_frame_lossless_image_is_eligible_for_lossless_webp()
    {
        var rules = Hevc with { TargetImageFormat = "webp" };
        var decision = CandidateEvaluator.Evaluate(
            ImageFile("png", frameCount: 1, pixelFormat: "rgba", bitsPerRawSample: 8), rules);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void An_animated_capable_image_with_unknown_frame_count_is_left_untouched()
    {
        var rules = Hevc with { ReencodeLossyImages = true };

        var decision = CandidateEvaluator.Evaluate(ImageFile("webp", frameCount: null), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("frame count", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rgba", 8, "alpha")]
    [InlineData("rgb48be", 16, "bit depth")]
    public void Jpeg_conversion_rejects_structural_image_loss_even_with_lossy_opt_in(
        string pixelFormat, int bitsPerRawSample, string expectedReason)
    {
        var rules = Hevc with { ReencodeLossyImages = true };

        var decision = CandidateEvaluator.Evaluate(
            ImageFile("png", pixelFormat: pixelFormat, bitsPerRawSample: bitsPerRawSample), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Webp_conversion_rejects_a_high_bit_depth_source()
    {
        var rules = Hevc with { TargetImageFormat = "webp" };

        var decision = CandidateEvaluator.Evaluate(
            ImageFile("png", frameCount: 1, pixelFormat: "rgb48be", bitsPerRawSample: 16), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("bit depth", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tiff_is_left_untouched_when_page_count_cannot_be_proven()
    {
        var rules = Hevc with { TargetImageFormat = "webp" };

        var decision = CandidateEvaluator.Evaluate(ImageFile("tiff", pixelFormat: "rgb24"), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("multi-page", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lossy_image_is_left_untouched_by_default()
    {
        // A WebP source is lossy and not the (JPEG) target, so re-encoding it risks generational
        // loss; the conservative default leaves it alone.
        var decision = CandidateEvaluator.Evaluate(ImageFile("webp", frameCount: 1), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("left untouched", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lossy_image_is_eligible_once_the_library_opts_into_re_encoding_it()
    {
        var rules = Hevc with { ReencodeLossyImages = true };

        var decision = CandidateEvaluator.Evaluate(
            ImageFile("webp", frameCount: 1, pixelFormat: "yuv420p", bitsPerRawSample: 8), rules);

        Assert.True(decision.IsEligible);
        Assert.Contains("jpeg", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_image_already_in_the_target_format_is_skipped()
    {
        // ffprobe reports a .jpg still as the "mjpeg" codec; the default target is JPEG.
        var decision = CandidateEvaluator.Evaluate(ImageFile("mjpeg"), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("Already", decision.Reason);
    }

    [Fact]
    public void A_tiny_image_is_below_the_minimum_size()
    {
        var decision = CandidateEvaluator.Evaluate(ImageFile("png", sizeBytes: 16 * 1024), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("minimum size", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_tagged_by_optimisarr_is_skipped_even_when_otherwise_eligible()
    {
        // h264 into an HEVC library would normally be eligible; the embedded mark wins.
        var decision = CandidateEvaluator.Evaluate(File(videoCodec: "h264", optimisedMarker: "0.4.2"), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("Already optimised", decision.Reason);
    }

    [Fact]
    public void Reencode_to_target_codec_is_eligible()
    {
        var decision = CandidateEvaluator.Evaluate(File(videoCodec: "h264"), Hevc);

        Assert.True(decision.IsEligible);
        Assert.Contains("h264", decision.Reason);
        Assert.Contains("hevc", decision.Reason);
    }

    // ~1.6 Mbps for 1080p ≈ 0.8 bits per pixel-second — well below the HEVC profile's 1.0 floor.
    // 570 MB over 2842 s = 1.61 Mbps; the file is already so compressed a re-encode won't beat it.
    private static MediaProperties AlreadyEfficientH264() =>
        File(videoCodec: "h264", width: 1920, height: 1080, sizeBytes: 570_000_000L, durationSeconds: 2842);

    [Fact]
    public void An_already_efficiently_encoded_source_is_skipped_before_transcoding()
    {
        var decision = CandidateEvaluator.Evaluate(AlreadyEfficientH264(), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("efficiently encoded", decision.Reason);
    }

    [Fact]
    public void A_healthy_bitrate_source_is_still_eligible()
    {
        // ~8 Mbps for 1080p (a typical Blu-ray h264) is well above the floor — re-encoding saves space.
        var decision = CandidateEvaluator.Evaluate(
            File(videoCodec: "h264", width: 1920, height: 1080, sizeBytes: 2_800_000_000L, durationSeconds: 2842),
            Hevc);

        Assert.True(decision.IsEligible);
        Assert.Contains("hevc", decision.Reason);
    }

    [Fact]
    public void A_source_with_unknown_duration_is_left_eligible_so_the_size_gate_decides()
    {
        // Without a duration the bitrate cannot be measured; stay conservative and let the encode run,
        // where the size-saving verification gate is the backstop.
        var decision = CandidateEvaluator.Evaluate(
            File(videoCodec: "h264", sizeBytes: 570_000_000L, durationSeconds: null), Hevc);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void A_profile_with_no_efficiency_floor_does_not_skip_a_low_bitrate_source()
    {
        // AV1 is efficient enough to shrink even a low-bitrate source, so its profile sets no floor.
        var av1 = RuleProfileDefaults.For(RuleProfile.ExperimentalAv1);

        var decision = CandidateEvaluator.Evaluate(AlreadyEfficientH264(), av1);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void File_already_in_target_codec_is_skipped()
    {
        var decision = CandidateEvaluator.Evaluate(File(videoCodec: "hevc"), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("Already", decision.Reason);
    }

    [Fact]
    public void An_oversized_same_codec_file_is_eligible_when_the_library_opts_in_by_size()
    {
        // Target is HEVC and the file is already HEVC, but it is a 30 GB remux and the library opted
        // in to re-encoding same-codec files above 20 GB — so it should be picked up to shrink.
        var rules = Hevc with { ReencodeSameCodecAboveBytes = 20L * 1024 * 1024 * 1024 };
        var huge = File(videoCodec: "hevc", sizeBytes: 30L * 1024 * 1024 * 1024);

        var decision = CandidateEvaluator.Evaluate(huge, rules);

        Assert.True(decision.IsEligible);
        Assert.Contains("re-encoding to shrink", decision.Reason);
    }

    [Fact]
    public void A_same_codec_file_below_the_opt_in_threshold_is_still_skipped()
    {
        // Opted in above 20 GB, but this HEVC file is only 5 GB — left untouched.
        var rules = Hevc with { ReencodeSameCodecAboveBytes = 20L * 1024 * 1024 * 1024 };
        var small = File(videoCodec: "hevc", sizeBytes: 5L * 1024 * 1024 * 1024);

        var decision = CandidateEvaluator.Evaluate(small, rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("Already", decision.Reason);
    }

    [Fact]
    public void File_without_a_video_stream_is_skipped()
    {
        var decision = CandidateEvaluator.Evaluate(File(videoCodec: null), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("video", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void File_below_minimum_size_is_skipped()
    {
        var decision = CandidateEvaluator.Evaluate(File(sizeBytes: 10 * 1024 * 1024), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("minimum size", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hdr_file_is_skipped_when_profile_excludes_hdr()
    {
        var decision = CandidateEvaluator.Evaluate(File(isHdr: true), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("HDR", decision.Reason);
    }

    [Fact]
    public void Hdr_file_is_eligible_when_profile_allows_hdr()
    {
        var av1 = RuleProfileDefaults.For(RuleProfile.ExperimentalAv1);

        var decision = CandidateEvaluator.Evaluate(File(videoCodec: "h264", isHdr: true), av1);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Dolby_vision_file_is_left_untouched_by_default_even_when_hdr_is_allowed()
    {
        // The AV1 profile allows HDR, so only the Dolby Vision guard can stop this — a re-encode
        // would drop the DV layer and a Profile 5 source comes out green/pink.
        var av1 = RuleProfileDefaults.For(RuleProfile.ExperimentalAv1);

        var decision = CandidateEvaluator.Evaluate(
            File(videoCodec: "h264", isHdr: true, isDolbyVision: true), av1);

        Assert.False(decision.IsEligible);
        Assert.Contains("Dolby Vision", decision.Reason);
    }

    [Fact]
    public void Dolby_vision_file_is_eligible_when_the_library_opts_in()
    {
        var av1 = RuleProfileDefaults.For(RuleProfile.ExperimentalAv1) with { OptimiseDolbyVision = true };

        var decision = CandidateEvaluator.Evaluate(
            File(videoCodec: "h264", isHdr: true, isDolbyVision: true), av1);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void File_above_resolution_limit_is_skipped()
    {
        var rules = Hevc with { MaxHeight = 1080 };

        var decision = CandidateEvaluator.Evaluate(File(height: 2160), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("2160", decision.Reason);
    }

    [Fact]
    public void File_under_an_excluded_path_is_skipped()
    {
        var rules = Hevc with { ExcludePathSegments = new[] { "Extras" } };

        var decision = CandidateEvaluator.Evaluate(File(relativePath: "Movies/Example/Extras/clip.mkv"), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("Extras", decision.Reason);
    }

    [Fact]
    public void Path_exclusion_is_checked_before_codec()
    {
        // An already-target file under an excluded path should report the path reason,
        // because exclusions are the operator's explicit intent.
        var rules = Hevc with { ExcludePathSegments = new[] { "Extras" } };

        var decision = CandidateEvaluator.Evaluate(
            File(videoCodec: "hevc", relativePath: "Movies/Extras/clip.mkv"),
            rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("Extras", decision.Reason);
    }

    [Fact]
    public void Remux_profile_is_eligible_for_a_non_matroska_container()
    {
        var remux = RuleProfileDefaults.For(RuleProfile.RemuxCleanup);

        var decision = CandidateEvaluator.Evaluate(
            File(videoCodec: "h264", container: "avi"),
            remux);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Remux_profile_skips_a_file_already_in_a_clean_container()
    {
        var remux = RuleProfileDefaults.For(RuleProfile.RemuxCleanup);

        var decision = CandidateEvaluator.Evaluate(
            File(videoCodec: "h264", container: "matroska,webm"),
            remux);

        Assert.False(decision.IsEligible);
        Assert.Contains("container", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
