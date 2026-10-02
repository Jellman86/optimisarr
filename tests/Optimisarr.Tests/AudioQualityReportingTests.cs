using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;
using Xunit;

namespace Optimisarr.Tests;

public sealed class AudioQualityReportingTests
{
    [Fact]
    public void Reporting_is_off_by_default_and_an_explicit_library_choice_is_resolved()
    {
        Assert.False(VerificationPolicy.Default.AudioQualityReportingEnabled);
        var overrides = new VerificationPolicyOverrides(null, null, null, null, null, null,
            AudioQualityReportingEnabled: true);
        Assert.True(VerificationPolicyResolver.Resolve(VerificationPolicy.Default, overrides).AudioQualityReportingEnabled);
    }

    [Theory]
    [InlineData(false, MediaKind.Audio, true, false, false)]
    [InlineData(true, MediaKind.Video, true, false, false)]
    [InlineData(true, MediaKind.Audio, false, false, false)]
    [InlineData(true, MediaKind.Audio, true, true, false)]
    [InlineData(true, MediaKind.Audio, true, false, true)]
    public void Only_enabled_healthy_complete_audio_jobs_launch_local_measurement(
        bool enabled, MediaKind kind, bool healthy, bool clip, bool expected)
    {
        Assert.Equal(expected, AudioQualityReporting.ShouldMeasureLocally(enabled, kind, healthy, clip, false));
        Assert.False(AudioQualityReporting.ShouldMeasureLocally(enabled, kind, healthy, clip, true));
    }

    [Fact]
    public void Unavailable_audio_reports_cannot_change_any_replacement_gate()
    {
        var report = new VerificationReport([new("Decode", CheckOutcome.Passed, "Healthy")]);
        report = report with { AudioQuality = new("Worker", null, "This worker returned no audio quality report.") };
        Assert.True(report.Passed);
        Assert.True(report.HasValidStructure());
    }

    [Fact]
    public void Complete_worker_reports_are_bound_to_files_windows_channels_and_the_pinned_metric()
    {
        var evidence = Evidence();
        Assert.Null(AudioQualityReporting.Validate(evidence, Hash('a'), Hash('b')));
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Revision = "unproved" }, Hash('a'), Hash('b')));
        Assert.NotNull(AudioQualityReporting.Validate(evidence, Hash('c'), Hash('b')));
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Assessment = evidence.Assessment with { Windows = [] } }, Hash('a'), Hash('b')));
        var window = evidence.Assessment.Windows[0];
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Assessment = evidence.Assessment with {
            Windows = [window with { Distances = new(48000, [double.NaN, 0]) }] } }, Hash('a'), Hash('b')));
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Assessment = evidence.Assessment with {
            Windows = [window with { Distances = new(48000, [0.1]) }] } }, Hash('a'), Hash('b')));
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Assessment = evidence.Assessment with {
            Windows = [window with { Window = new(100, 1) }] } }, Hash('a'), Hash('b')));
    }

    [Fact]
    public void Failed_or_malformed_worker_measurements_are_unavailable_without_server_fallback()
    {
        var evidence = Evidence();
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Assessment = evidence.Assessment with {
            Measured = false, Error = "Native tool missing" } }, Hash('a'), Hash('b')));
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Assessment = evidence.Assessment with {
            Reference = null } }, Hash('a'), Hash('b')));
        Assert.NotNull(AudioQualityReporting.Validate(evidence with { Assessment = evidence.Assessment with {
            MetricSha256 = "invalid" } }, Hash('a'), Hash('b')));
    }

    private static string Hash(char value) => new(value, 64);
    private static RemoteAudioQualityEvidence Evidence() => new("zimtohrli", AudioQualityResultParser.Revision,
        AudioQualityResultParser.Preparation, new(true, null, new(1, 2, 48000, "stereo"), new(1, 2, 48000, "stereo"),
            Hash('a'), Hash('b'), Hash('c'), Hash('d'), Hash('e'), [new(new(0, 1), new(48000, [0.01, 0.02]))], 1));
}
