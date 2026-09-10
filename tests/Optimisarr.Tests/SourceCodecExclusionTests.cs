using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

/// <summary>
/// "I don't mind AV1 — my devices play it — but I have no hardware that encodes it, so converting
/// it to HEVC costs me hours and gains nothing." Optimisarr already skips a file that is *already
/// the target*, and skips one encoded efficiently enough that shrinking is improbable, but neither
/// covers a source the operator is simply happy with.
///
/// This is that missing rule, and it is deliberately blunt: name a codec, and files in it are left
/// alone whatever else the profile would have done.
/// </summary>
public sealed class SourceCodecExclusionTests
{
    private static RuleSettings Skipping(params string[] codecs) =>
        RuleProfileDefaults.For(RuleProfile.ConservativeHevc) with { SkipSourceCodecs = codecs };

    private static MediaProperties VideoIn(string codec) =>
        new("matroska,webm", codec, 1920, 1080, 4L * 1024 * 1024 * 1024, false,
            "Movies/Example (2020)/Example.mkv", null, MediaKind.Video,
            PixelFormat: "yuv420p", BitsPerRawSample: 8);

    [Fact]
    public void A_source_in_an_excluded_codec_is_left_alone()
    {
        // The reporter's case exactly: an AV1 source under an HEVC target, on a machine with no
        // AV1 encoder. Without this it is eligible, because AV1 is not the target codec.
        var decision = CandidateEvaluator.Evaluate(VideoIn("av1"), Skipping("av1"));

        Assert.False(decision.IsEligible);
        Assert.Contains("av1", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_source_in_some_other_codec_is_unaffected()
    {
        var decision = CandidateEvaluator.Evaluate(VideoIn("h264"), Skipping("av1"));

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Nothing_is_excluded_when_no_codec_is_named()
    {
        // The default. An empty list must never read as "exclude everything".
        var decision = CandidateEvaluator.Evaluate(
            VideoIn("av1"), RuleProfileDefaults.For(RuleProfile.ConservativeHevc));

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Several_codecs_can_be_excluded_at_once()
    {
        Assert.False(CandidateEvaluator.Evaluate(VideoIn("av1"), Skipping("av1", "vp9")).IsEligible);
        Assert.False(CandidateEvaluator.Evaluate(VideoIn("vp9"), Skipping("av1", "vp9")).IsEligible);
        Assert.True(CandidateEvaluator.Evaluate(VideoIn("h264"), Skipping("av1", "vp9")).IsEligible);
    }

    [Fact]
    public void Codec_names_are_matched_regardless_of_case_or_padding()
    {
        // Operators type these by hand, and ffprobe's own spelling is lowercase.
        var decision = CandidateEvaluator.Evaluate(VideoIn("av1"), Skipping("  AV1 "));

        Assert.False(decision.IsEligible);
    }

    [Fact]
    public void An_audio_file_is_matched_on_its_audio_codec()
    {
        // "Leave my Opus alone." An audio file's eligibility is driven by its audio codec, so that
        // is what this rule has to compare against for a Music library.
        var opus = new MediaProperties(null, null, null, null, 40L * 1024 * 1024, false,
            "Music/Album/Track.opus", null, MediaKind.Audio, "opus");

        var decision = CandidateEvaluator.Evaluate(opus, Skipping("opus"));

        Assert.False(decision.IsEligible);
        Assert.Contains("opus", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_image_is_matched_on_its_still_picture_codec()
    {
        // The probe records an image's still-picture codec as the file's video codec.
        var jpeg = new MediaProperties("image2", "mjpeg", 4000, 3000, 4L * 1024 * 1024, false,
            "Photos/2024/IMG_0001.jpg", null, MediaKind.Image);

        var decision = CandidateEvaluator.Evaluate(jpeg, Skipping("mjpeg"));

        Assert.False(decision.IsEligible);
    }

    [Fact]
    public void A_file_with_no_detected_codec_is_not_excluded_by_this_rule()
    {
        // An unprobed file has no codec to compare. It already has its own clearer reason for being
        // skipped, and this rule must not steal it.
        var unprobed = new MediaProperties(null, null, null, null, 1024, false,
            "Movies/Example.mkv", null, MediaKind.Video);

        var decision = CandidateEvaluator.Evaluate(unprobed, Skipping("av1"));

        Assert.False(decision.IsEligible);
        Assert.DoesNotContain("excluded", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Excluding_the_target_codec_itself_reports_the_exclusion_rather_than_no_saving()
    {
        // Both rules would skip an HEVC source under an HEVC target, but they mean different
        // things. If the operator named it explicitly, say so — "no expected saving" would suggest
        // the setting had no effect.
        var decision = CandidateEvaluator.Evaluate(VideoIn("hevc"), Skipping("hevc"));

        Assert.False(decision.IsEligible);
        Assert.Contains("excluded", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Track_cleanup_honours_the_exclusion_too()
    {
        var cleanup = RuleProfileDefaults.For(RuleProfile.TrackCleanup) with { SkipSourceCodecs = ["av1"] };

        var decision = CandidateEvaluator.Evaluate(VideoIn("av1"), cleanup);

        Assert.False(decision.IsEligible);
        Assert.Contains("av1", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
