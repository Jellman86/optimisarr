using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafSoftwareConfirmationTests
{
    private static readonly VerificationPolicy Policy = VerificationPolicy.Default with
    {
        QualityGateEnabled = true,
        MinimumVmafHarmonicMean = 80,
        MinimumVmafMin = 60,
        MinimumVmafCatastrophicMin = 30
    };

    [Fact]
    public void Failed_accelerated_score_requires_software_confirmation()
    {
        var result = QualityResult.Ok(Scores(harmonic: 14, fifthPercentile: 0, minimum: 0)) with
        {
            Acceleration = VmafAcceleration.Qsv
        };

        Assert.True(VmafSoftwareConfirmation.IsRequired(result, Policy));
    }

    [Fact]
    public void Passing_accelerated_score_does_not_require_confirmation()
    {
        var result = QualityResult.Ok(Scores(harmonic: 92, fifthPercentile: 75, minimum: 45)) with
        {
            Acceleration = VmafAcceleration.Qsv
        };

        Assert.False(VmafSoftwareConfirmation.IsRequired(result, Policy));
    }

    [Fact]
    public void Failed_software_score_is_already_authoritative()
    {
        var result = QualityResult.Ok(Scores(harmonic: 14, fifthPercentile: 0, minimum: 0));

        Assert.False(VmafSoftwareConfirmation.IsRequired(result, Policy));
    }

    [Fact]
    public async Task Failed_accelerated_score_is_replaced_by_software_measurement()
    {
        var accelerated = QualityResult.Ok(Scores(harmonic: 14, fifthPercentile: 0, minimum: 0)) with
        {
            Acceleration = VmafAcceleration.Qsv
        };
        var software = QualityResult.Ok(Scores(harmonic: 92, fifthPercentile: 75, minimum: 45));
        var confirmations = 0;

        var result = await VmafSoftwareConfirmation.ConfirmAsync(accelerated, Policy, () =>
        {
            confirmations++;
            return Task.FromResult(software);
        });

        Assert.Same(software, result);
        Assert.Equal(1, confirmations);
    }

    [Fact]
    public async Task Passing_accelerated_score_does_not_run_software_measurement()
    {
        var accelerated = QualityResult.Ok(Scores(harmonic: 92, fifthPercentile: 75, minimum: 45)) with
        {
            Acceleration = VmafAcceleration.Qsv
        };
        var confirmations = 0;

        var result = await VmafSoftwareConfirmation.ConfirmAsync(accelerated, Policy, () =>
        {
            confirmations++;
            return Task.FromResult(QualityResult.Failed("should not run"));
        });

        Assert.Same(accelerated, result);
        Assert.Equal(0, confirmations);
    }

    private static QualityScores Scores(double harmonic, double fifthPercentile, double minimum) =>
        new(
            VmafMean: harmonic,
            VmafHarmonicMean: harmonic,
            VmafMin: minimum,
            PsnrYMean: null,
            SsimMean: null,
            VmafFifthPercentile: fifthPercentile);
}
