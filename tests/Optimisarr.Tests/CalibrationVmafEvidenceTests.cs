using System.Text.Json;
using Optimisarr.Api.Library;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class CalibrationVmafEvidenceTests
{
    [Fact]
    public void Reports_are_exposed_per_scene_with_a_conservative_preset_summary()
    {
        (int SampleNumber, string? ReportJson)[] reports =
        {
            (1, ReportJson(new QualityScores(91, 90, 50, null, null,
                ModelVersion: "vmaf_v0.6.1", VmafFifthPercentile: 80, FrameCount: 100))),
            (2, ReportJson(new QualityScores(97, 96, 60, null, null,
                ModelVersion: "vmaf_v0.6.1", VmafFifthPercentile: 85, FrameCount: 300))),
            (3, JsonSerializer.Serialize(new VerificationReport(
                [], Vmaf: new VmafEvidence(false, "libvmaf is unavailable.", null))))
        };

        var result = CalibrationVmafEvidence.FromReports(reports);

        Assert.NotNull(result);
        Assert.Equal(2, result.MeasuredSamples);
        Assert.Equal(3, result.TotalSamples);
        Assert.Equal(95.5, result.Mean);
        Assert.Equal(94.43, result.HarmonicMean);
        Assert.Equal(80, result.FifthPercentile);
        Assert.Equal(50, result.Minimum);
        Assert.Equal(400, result.FrameCount);
        Assert.Equal("vmaf_v0.6.1", result.ModelVersion);
        Assert.Equal(3, result.Samples.Count);
        Assert.False(result.Samples[2].Measured);
        Assert.Equal("libvmaf is unavailable.", result.Samples[2].Error);
    }

    private static string ReportJson(QualityScores scores) =>
        JsonSerializer.Serialize(new VerificationReport(
            [], Vmaf: new VmafEvidence(true, null, scores)));
}
