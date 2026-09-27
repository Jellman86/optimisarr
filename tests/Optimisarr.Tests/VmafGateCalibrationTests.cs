using Optimisarr.Core.Calibration;

namespace Optimisarr.Tests;

public sealed class VmafGateCalibrationTests
{
    [Fact]
    public void Calibration_requires_both_sides_of_the_gate_and_independent_sources()
    {
        Assert.Null(VmafGateCalibration.Validate([new("one", 80, 82), new("one", 95, 97)], 90));
        Assert.Null(VmafGateCalibration.Validate([new("one", 95, 97), new("two", 96, 98), new("three", 94, 96)], 90));
    }

    [Fact]
    public void Held_out_sources_cannot_influence_the_threshold_used_to_validate_them()
    {
        var rows = new[]
        {
            new CalibrationScore("one", 80, 70), new CalibrationScore("one", 95, 95),
            new CalibrationScore("two", 80, 70), new CalibrationScore("two", 95, 95),
            new CalibrationScore("three", 80, 94), new CalibrationScore("three", 95, 98)
        };
        var result = VmafGateCalibration.Validate(rows, 90)!;
        Assert.Equal(1, result.UnsafeAccepts);
        Assert.Equal(3, result.HeldOutSources);
        Assert.False(result.Passed);
    }

    [Fact]
    public void A_monotonic_shift_passes_when_each_source_contains_passes_and_failures()
    {
        var rows = new[] { "one", "two", "three" }
            .SelectMany(source => new[] { new CalibrationScore(source, 80, 82), new CalibrationScore(source, 95, 97) }).ToList();
        var result = VmafGateCalibration.Validate(rows, 90)!;
        Assert.True(result.Passed);
        Assert.Equal(0, result.UnsafeAccepts);
        Assert.Equal(0, result.RejectedBaselinePasses);
        Assert.True(result.ProposedThreshold > 82);
    }

    [Fact]
    public void Rejecting_everything_cannot_pass_validation()
    {
        var rows = new[] { "one", "two", "three" }
            .SelectMany(source => new[] { new CalibrationScore(source, 80, 98), new CalibrationScore(source, 95, 97) }).ToList();
        Assert.False(VmafGateCalibration.Validate(rows, 90)!.Passed);
    }

    [Fact]
    public void A_separating_gap_protects_against_normal_variation_in_held_out_failures()
    {
        CalibrationScore[] rows =
        [
            new("one", 80, 82), new("one", 95, 97),
            new("two", 85, 87), new("two", 95, 97),
            new("three", 88, 90), new("three", 95, 97)
        ];
        Assert.True(VmafGateCalibration.Validate(rows, 90)!.Passed);
    }
}
