using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafJobModelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_fresh_job_without_trustworthy_cadence_keeps_the_legacy_policy(double? rate)
    {
        Assert.Equal("vmaf_v0.6.1", QualityScoreCommandBuilder.ModelForJob(null, false,
            1920, 1080, false, rate, false));
    }

    [Fact]
    public void A_pre_upgrade_adaptive_choice_keeps_its_legacy_measurement()
    {
        Assert.Equal("vmaf_v0.6.1", QualityScoreCommandBuilder.ModelForJob(null, false, 1920, 1080,
            false, 24, false, qualityWasSelected: true));
    }

    [Theory]
    [InlineData(null, false, "vmaf_v1.0.16_3d0h")]
    [InlineData(null, true, "vmaf_v0.6.1")]
    [InlineData("vmaf_v0.6.1", false, "vmaf_v0.6.1")]
    [InlineData("vmaf_v1.0.16_3d0h", true, "vmaf_v1.0.16_3d0h")]
    public void Prepared_jobs_keep_their_model_and_unlabelled_existing_candidates_keep_legacy(string? recorded, bool candidateExists, string expected)
    {
        Assert.Equal(expected, QualityScoreCommandBuilder.ModelForJob(recorded, candidateExists, 1920, 1080, false, 24, false));
    }
}
