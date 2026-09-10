using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

/// <summary>
/// A hardlinked file is one the library shares with somewhere else on the same filesystem — most
/// often a torrent client still seeding the exact bytes a *arr imported. Replacing it breaks that
/// other use, and Optimisarr cannot see the other side to ask, so the only honest answer is to let
/// an operator say "leave those alone".
///
/// The rule is off by default, because a link count above one is not by itself evidence that
/// anything is wrong. Once it is on, every uncertain case fails closed: the operator has said they
/// care, and "I could not tell" has to mean "do not touch it".
/// </summary>
public sealed class HardLinkExclusionTests
{
    private static readonly RuleSettings Off = RuleProfileDefaults.For(RuleProfile.ConservativeHevc);

    private static readonly RuleSettings On =
        RuleProfileDefaults.For(RuleProfile.ConservativeHevc) with { ExcludeHardLinkedFiles = true };

    private static MediaProperties VideoFile(int? hardLinkCount) =>
        new("matroska,webm", "h264", 1920, 1080, 4L * 1024 * 1024 * 1024, false,
            "Movies/Example (2020)/Example.mkv", null, MediaKind.Video,
            PixelFormat: "yuv420p", BitsPerRawSample: 8, HardLinkCount: hardLinkCount);

    [Fact]
    public void A_file_with_a_single_link_is_unaffected_by_the_rule()
    {
        var decision = CandidateEvaluator.Evaluate(VideoFile(hardLinkCount: 1), On);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void A_hardlinked_file_is_skipped_while_the_rule_is_on()
    {
        var decision = CandidateEvaluator.Evaluate(VideoFile(hardLinkCount: 2), On);

        Assert.False(decision.IsEligible);
        Assert.Contains("hardlink", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_skip_reason_names_how_many_links_the_file_has()
    {
        var decision = CandidateEvaluator.Evaluate(VideoFile(hardLinkCount: 3), On);

        Assert.Contains("3", decision.Reason);
    }

    [Fact]
    public void A_hardlinked_file_remains_a_candidate_while_the_rule_is_off()
    {
        // The default. A link count above one is not evidence of a problem on its own, and turning
        // this on for everyone would silently stop optimising whole libraries on upgrade.
        var decision = CandidateEvaluator.Evaluate(VideoFile(hardLinkCount: 2), Off);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void An_undeterminable_link_count_is_skipped_while_the_rule_is_on()
    {
        // Fails closed. The operator has said hardlinked files matter; an unreadable count is the
        // one case where guessing wrong costs someone their seed.
        var decision = CandidateEvaluator.Evaluate(VideoFile(hardLinkCount: null), On);

        Assert.False(decision.IsEligible);
        Assert.Contains("could not be determined", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_undeterminable_link_count_is_ignored_while_the_rule_is_off()
    {
        // Every file probed before this feature existed has a null count. They must stay eligible,
        // or upgrading would empty every candidate list until the libraries were rescanned.
        var decision = CandidateEvaluator.Evaluate(VideoFile(hardLinkCount: null), Off);

        Assert.True(decision.IsEligible);
    }

    [Fact]
    public void Audio_files_are_covered_by_the_rule_too()
    {
        var flac = new MediaProperties(null, null, null, null, 40L * 1024 * 1024, false,
            "Music/Album/Track.flac", null, MediaKind.Audio, "flac", HardLinkCount: 2);

        var decision = CandidateEvaluator.Evaluate(flac, On);

        Assert.False(decision.IsEligible);
        Assert.Contains("hardlink", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Images_are_covered_by_the_rule_too()
    {
        var jpeg = new MediaProperties("image2", "mjpeg", 4000, 3000, 4L * 1024 * 1024, false,
            "Photos/2024/IMG_0001.jpg", null, MediaKind.Image, HardLinkCount: 2);

        var decision = CandidateEvaluator.Evaluate(jpeg, On);

        Assert.False(decision.IsEligible);
        Assert.Contains("hardlink", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Track_cleanup_is_covered_because_it_replaces_the_file_as_well()
    {
        // Track cleanup only strips streams, but it still ends in a replacement, so it breaks a
        // hardlink exactly as a re-encode does.
        var cleanup = RuleProfileDefaults.For(RuleProfile.TrackCleanup) with { ExcludeHardLinkedFiles = true };

        var decision = CandidateEvaluator.Evaluate(VideoFile(hardLinkCount: 2), cleanup);

        Assert.False(decision.IsEligible);
        Assert.Contains("hardlink", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_already_optimised_file_reports_that_rather_than_its_link_count()
    {
        // Ordering matters for the reason an operator reads. "Already optimised" is terminal;
        // a link count can change tomorrow.
        var optimised = VideoFile(hardLinkCount: 2) with { OptimisedMarker = "optimisarr" };

        var decision = CandidateEvaluator.Evaluate(optimised, On);

        Assert.False(decision.IsEligible);
        Assert.Contains("Already optimised", decision.Reason);
    }
}
