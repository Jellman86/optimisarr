using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PerceptualAuxiliaryTargetsTests
{
    private static readonly PerceptualSearchMeasurement Passing = new(true, true, 0.005, null);
    private static readonly VerificationPolicy Policy = VerificationPolicy.Default with
        { AudioLoudnessGateEnabled = true, MaxLoudnessDriftLufs = 1, AudioClippingGateEnabled = true, MaxTruePeakDbtp = 0 };

    [Theory]
    [InlineData(-20, -20.5, true)]
    [InlineData(-20, -21, true)]
    [InlineData(-20, -21.1, false)]
    public void Loudness_drift_guides_selection_without_overriding_the_perceptual_target(double before, double after, bool passes)
    {
        var result = PerceptualAuxiliaryTargets.Audio(Passing, LoudnessResult.Ok(before, -1), LoudnessResult.Ok(after, -1), Policy);
        Assert.True(result.Available);
        Assert.Equal(passes, result.MeetsTarget);
        Assert.Equal(Math.Abs(before - after), result.LoudnessDriftLufs);
        Assert.False(PerceptualAuxiliaryTargets.Audio(Passing with { MeetsTarget = false }, LoudnessResult.Ok(before, -1), LoudnessResult.Ok(after, -1), Policy).MeetsTarget);
    }

    [Theory]
    [InlineData(-1, 0.1, false)]
    [InlineData(-1, 0, true)]
    [InlineData(0.1, 0.2, true)]
    [InlineData(0.1, 0.5, false)]
    public void Introduced_clipping_uses_the_existing_ceiling_policy(double source, double candidate, bool passes)
    {
        var result = PerceptualAuxiliaryTargets.Audio(Passing, LoudnessResult.Ok(-20, source), LoudnessResult.Ok(-20, candidate), Policy);
        Assert.True(result.Available);
        Assert.Equal(passes, result.MeetsTarget);
        Assert.Equal(candidate, result.CandidateTruePeakDbtp);
    }

    [Fact]
    public void Missing_or_non_finite_auxiliary_measurements_are_unavailable_when_required()
    {
        Assert.False(PerceptualAuxiliaryTargets.Audio(Passing, LoudnessResult.Failed("no summary"), LoudnessResult.Ok(-20, -1), Policy).Available);
        Assert.False(PerceptualAuxiliaryTargets.Audio(Passing, LoudnessResult.Ok(double.NaN, -1), LoudnessResult.Ok(-20, -1), Policy).Available);
        Assert.False(PerceptualAuxiliaryTargets.Audio(Passing, LoudnessResult.Ok(-20, null), LoudnessResult.Ok(-20, -1), Policy).Available);
    }

    [Fact]
    public void Disabled_auxiliary_gates_do_not_create_new_requirements()
    {
        Assert.Equal(Passing, PerceptualAuxiliaryTargets.Audio(Passing, null, null, VerificationPolicy.Default));
    }

    [Fact]
    public void Silent_samples_remain_usable_but_cannot_hide_introduced_clipping()
    {
        var silence = new LoudnessResult(true, -70, null, null, TruePeakIsSilent: true);
        Assert.True(PerceptualAuxiliaryTargets.Audio(Passing, silence, silence, Policy).MeetsTarget);
        Assert.False(PerceptualAuxiliaryTargets.Audio(Passing, silence, LoudnessResult.Ok(-70, 0.5), Policy).MeetsTarget);
    }
}
