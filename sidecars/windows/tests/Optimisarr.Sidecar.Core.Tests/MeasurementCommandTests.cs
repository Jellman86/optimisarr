using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// What this machine will measure with.
///
/// <para>The same contract as the encode's, for the same reason. The null-muxer rule is the one
/// doing the most work: without it a measurement command is an encode command that happens to
/// score something, and could be asked to write anywhere.</para>
/// </summary>
public sealed class MeasurementCommandTests
{
    private static readonly string[] Ordinary =
    [
        "-nostdin", "-v", "error", "-stats",
        "-i", "{{distorted}}", "-ss", "115.99", "-i", "{{reference}}",
        "-lavfi", "[0:v]settb=AVTB[d];[1:v]settb=AVTB[r];[d][r]libvmaf=log_path={{log}}:shortest=1",
        "-t", "40", "-f", "null", "-",
    ];

    [Fact]
    public void The_command_the_server_actually_sends_is_accepted()
    {
        Assert.Null(MeasurementCommand.Refuse(Ordinary));
    }

    [Fact]
    public void A_command_that_writes_a_file_instead_of_scoring_is_refused()
    {
        // The rule that keeps a measurement a measurement. Anything not ending in the null muxer
        // is an encode with a score attached, and its output is a file this machine would write.
        string[] arguments =
        [
            "-i", "{{distorted}}", "-i", "{{reference}}",
            "-lavfi", "[d][r]libvmaf=log_path={{log}}",
            "-f", "matroska", @"C:\Windows\System32\drivers\etc\hosts",
        ];

        var refusal = MeasurementCommand.Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("null muxer", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Inputs_that_are_not_the_two_placeholders_are_refused()
    {
        string[] arguments =
        [
            "-i", @"C:\Users\scott\Documents\private.mkv", "-i", "{{reference}}",
            "-lavfi", "[d][r]libvmaf=log_path={{log}}", "-f", "null", "-",
        ];

        Assert.NotNull(MeasurementCommand.Refuse(arguments));
    }

    [Fact]
    public void The_two_inputs_must_be_in_the_order_libvmaf_expects()
    {
        // Distorted first, reference second. Reversed, every score would be measured against the
        // wrong file and still look like a number.
        string[] arguments =
        [
            "-i", "{{reference}}", "-i", "{{distorted}}",
            "-lavfi", "[d][r]libvmaf=log_path={{log}}", "-f", "null", "-",
        ];

        Assert.NotNull(MeasurementCommand.Refuse(arguments));
    }

    [Fact]
    public void A_filter_that_never_names_the_log_is_refused()
    {
        // It would run, score nothing this machine could read back, and report a measurement that
        // could not be made — which the server would then have to distinguish from a real failure.
        string[] arguments =
        [
            "-i", "{{distorted}}", "-i", "{{reference}}",
            "-lavfi", "[d][r]libvmaf=shortest=1", "-f", "null", "-",
        ];

        var refusal = MeasurementCommand.Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("{{log}}", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filter_naming_the_log_twice_is_refused()
    {
        // Two log paths in one graph is two files written where one was agreed.
        string[] arguments =
        [
            "-i", "{{distorted}}", "-i", "{{reference}}",
            "-lavfi", "[a]libvmaf=log_path={{log}}[x];[b]libvmaf=log_path={{log}}", "-f", "null", "-",
        ];

        Assert.NotNull(MeasurementCommand.Refuse(arguments));
    }

    [Fact]
    public void An_option_this_sidecar_does_not_know_is_refused_by_name()
    {
        string[] arguments =
        [
            "-i", "{{distorted}}", "-i", "{{reference}}", "-hwaccel", "cuda",
            "-lavfi", "[d][r]libvmaf=log_path={{log}}", "-f", "null", "-",
        ];

        var refusal = MeasurementCommand.Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("-hwaccel", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_outside_the_filter_may_not_name_a_path()
    {
        string[] arguments =
        [
            "-i", "{{distorted}}", "-i", "{{reference}}", "-t", @"C:\x",
            "-lavfi", "[d][r]libvmaf=log_path={{log}}", "-f", "null", "-",
        ];

        Assert.NotNull(MeasurementCommand.Refuse(arguments));
    }

    [Fact]
    public void The_filter_may_carry_a_path_because_the_log_lives_in_it()
    {
        // The one exception, and it is why the log is checked by count rather than by shape: the
        // filter legitimately contains a path, and it is this machine's own scratch path,
        // substituted after this check rather than before it.
        Assert.Null(MeasurementCommand.Refuse(Ordinary));
    }

    [Fact]
    public void A_shift_token_outside_the_filter_is_refused()
    {
        // It is filled in with a number this machine measures. Anywhere but the filter, that is a
        // value being smuggled into a place nobody checks.
        string[] arguments =
        [
            "-i", "{{distorted}}", "-i", "{{reference}}", "-ss", "{{distortedShift}}",
            "-lavfi", "[d][r]libvmaf=log_path={{log}}", "-f", "null", "-",
        ];

        Assert.NotNull(MeasurementCommand.Refuse(arguments));
    }

    [Fact]
    public void An_empty_command_is_refused_rather_than_run()
    {
        Assert.NotNull(MeasurementCommand.Refuse([]));
        Assert.NotNull(MeasurementCommand.Refuse(["-f", "null", "-"]));
    }
}
