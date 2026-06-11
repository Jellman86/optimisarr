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
        string? audioCodec = null) =>
        new(container, videoCodec, width, height, sizeBytes, isHdr, relativePath, optimisedMarker, kind, audioCodec);

    private static MediaProperties AudioFile(string audioCodec, long sizeBytes = 40L * 1024 * 1024) =>
        File(videoCodec: null, sizeBytes: sizeBytes, relativePath: "Music/Album/Track.flac",
            kind: MediaKind.Audio, audioCodec: audioCodec);

    [Fact]
    public void A_lossless_audio_file_is_eligible_for_re_encode_to_opus()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("flac"), Hevc);

        Assert.True(decision.IsEligible);
        Assert.Contains("flac", decision.Reason);
        Assert.Contains("opus", decision.Reason);
    }

    [Fact]
    public void A_lossy_audio_file_is_left_untouched()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("mp3"), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("lossy", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_audio_file_already_in_opus_is_skipped()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("opus"), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("Already", decision.Reason);
    }

    [Fact]
    public void A_tiny_audio_file_is_below_the_minimum_size()
    {
        var decision = CandidateEvaluator.Evaluate(AudioFile("flac", sizeBytes: 1024 * 1024), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("minimum size", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_image_file_is_skipped_with_a_clear_not_yet_supported_reason()
    {
        var decision = CandidateEvaluator.Evaluate(File(videoCodec: null, kind: MediaKind.Image), Hevc);

        Assert.False(decision.IsEligible);
        Assert.Contains("Image", decision.Reason, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void File_already_in_target_codec_is_skipped()
    {
        var decision = CandidateEvaluator.Evaluate(File(videoCodec: "hevc"), Hevc);

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
