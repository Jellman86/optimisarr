using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class InputTimestampOffsetTests
{
    [Theory]
    [InlineData(0.431, "0.040\n0.080\n", 0.391)]
    [InlineData(0.081, "0.120\n0.040\n0.080\n", 0.041)]
    [InlineData(2.5, "2.5\n2.54\n", 0)]
    [InlineData(0, "N/A\n0.021\n", 0)]
    public void Only_pictures_before_the_declared_start_need_a_shared_input_correction(double start, string pts, double expected)
    {
        Assert.Equal(expected, InputTimestampOffset.Calculate(start, pts), 9);
    }

    [Theory]
    [InlineData("N/A\nNaN\nInfinity")]
    [InlineData("")]
    public void Missing_initial_timestamps_fail_closed(string pts) =>
        Assert.Throws<InvalidOperationException>(() => InputTimestampOffset.Calculate(0.431, pts));

    [Fact]
    public void Unbounded_or_nonfinite_corrections_are_refused()
    {
        Assert.Throws<ArgumentException>(() => InputTimestampOffset.Calculate(double.NaN, "0"));
        Assert.Throws<InvalidOperationException>(() => InputTimestampOffset.Calculate(86401, "0"));
    }

    [Fact]
    public void Head_read_is_bounded_and_reconstructs_only_the_source_timestamps()
    {
        var args = InputTimestampOffset.Arguments("source with spaces");
        Assert.Contains("%+#128", args);
        Assert.Contains("+genpts", args);
        Assert.Contains("V:0", args);
        Assert.Equal("source with spaces", args[^1]);
    }
}
