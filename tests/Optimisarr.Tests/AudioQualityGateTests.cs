using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class AudioQualityGateTests
{
    [Fact]
    public void Gate_is_off_and_has_no_suggested_threshold_by_default()
    {
        Assert.False(VerificationPolicy.Default.AudioQualityGateEnabled);
        Assert.Null(VerificationPolicy.Default.MaximumAudioQualityDistance);
        Assert.False(VerificationPolicy.Default.RequiresAudioQuality(MediaKind.Audio));
        var policy = Policy(0.02);
        Assert.True(policy.RequiresAudioQuality(MediaKind.Audio));
        Assert.False(policy.RequiresAudioQuality(MediaKind.Video));
    }

    [Theory]
    [InlineData(0.01, true)]
    [InlineData(0.02, true)]
    [InlineData(0.020001, false)]
    public void Every_channel_and_sample_must_meet_the_inclusive_limit(double distance, bool passed)
    {
        var report = AudioQualityGate.Apply(Report(distance), MediaKind.Audio, Policy(0.02));
        Assert.Equal(passed, report.Passed);
        Assert.Equal(passed, report.AudioQuality!.GatePassed);
        Assert.Equal(0.02, report.AudioQuality.MaximumDistance);
        Assert.Contains("0.02", report.Checks.Last().Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void An_enabled_gate_with_an_invalid_limit_fails_closed(double? limit)
    {
        Assert.False(AudioQualityGate.Apply(Report(0), MediaKind.Audio, Policy(limit)).Passed);
    }

    [Fact]
    public void Missing_failed_mismatched_and_incomplete_measurements_block_replacement()
    {
        var report = Report(0.01);
        var evidence = report.AudioQuality!.Evidence!;
        foreach (var audio in new AudioQualityReport?[] {
            null, new("Worker", null, "No report"),
            report.AudioQuality with { Evidence = evidence with { Revision = "other" } },
            report.AudioQuality with { Evidence = evidence with { Assessment = evidence.Assessment with { Windows = [] } } },
            report.AudioQuality with { Evidence = evidence with { Assessment = evidence.Assessment with { Measured = false } } }
        })
            Assert.False(AudioQualityGate.Apply(report with { AudioQuality = audio }, MediaKind.Audio, Policy(0.02)).Passed);
    }

    [Fact]
    public void Report_only_and_non_audio_outputs_keep_their_existing_checks()
    {
        var report = Report(0.8);
        Assert.Same(report, AudioQualityGate.Apply(report, MediaKind.Audio, VerificationPolicy.Default));
        Assert.Same(report, AudioQualityGate.Apply(report, MediaKind.Video, Policy(0.02)));
        Assert.Same(report, AudioQualityGate.Apply(report, MediaKind.Image, Policy(0.02)));
    }

    [Fact]
    public void Disposable_previews_do_not_require_full_file_audio_measurements()
    {
        var report = Report(0.8) with { AudioQuality = null };
        Assert.Same(report, AudioQualityGate.Apply(report, MediaKind.Audio, Policy(0.01), preview: true));
        Assert.False(AudioQualityGate.Apply(report, MediaKind.Audio, Policy(0.01)).Passed);
    }

    [Fact]
    public void A_bad_last_sample_cannot_hide_behind_good_earlier_samples()
    {
        var report = Report(0);
        var evidence = report.AudioQuality!.Evidence!;
        var assessment = evidence.Assessment with
        {
            Reference = new(90, 2, 48000, "stereo"), Candidate = new(90, 2, 48000, "stereo"),
            Windows = [new(new(0, 30), new(1440000, [0, 0])), new(new(30, 30), new(1440000, [0, 0])),
                new(new(60, 30), new(1440000, [0, 0.021]))]
        };
        report = report with { AudioQuality = report.AudioQuality with { Evidence = evidence with { Assessment = assessment } } };
        Assert.False(AudioQualityGate.Apply(report, MediaKind.Audio, Policy(0.02)).Passed);
        Assert.True(AudioQualityGate.Apply(Report(0), MediaKind.Audio, Policy(0)).Passed);
    }

    [Fact]
    public void Gate_resolution_preserves_the_selected_limit_and_does_not_relax_invalid_values()
    {
        var overrides = new VerificationPolicyOverrides(null, null, null, null, null, null,
            AudioQualityGateEnabled: true, MaximumAudioQualityDistance: 0.01);
        var policy = VerificationPolicyResolver.Resolve(VerificationPolicy.Default, overrides);
        Assert.True(policy.RequiresAudioQuality(MediaKind.Audio));
        Assert.Equal(0.01, policy.MaximumAudioQualityDistance);
        policy = VerificationPolicyResolver.Resolve(VerificationPolicy.Default, overrides with { MaximumAudioQualityDistance = 2 });
        Assert.False(AudioQualityGate.Apply(Report(0.01), MediaKind.Audio, policy).Passed);
    }

    public static VerificationPolicy Policy(double? limit) => VerificationPolicy.Default with
        { AudioQualityGateEnabled = true, MaximumAudioQualityDistance = limit };

    private static VerificationReport Report(double distance)
    {
        static string Hash(char value) => new(value, 64);
        var evidence = RemoteAudioQualityEvidence.From(new(true, null, new(1, 2, 48000, "stereo"),
            new(1, 2, 48000, "stereo"), Hash('a'), Hash('b'), Hash('c'), Hash('d'), Hash('e'),
            [new(new(0, 1), new(48000, [0, distance]))], 1));
        return new([new("Decode", CheckOutcome.Passed, "Healthy")], AudioQuality: new("Worker", evidence, null));
    }
}
