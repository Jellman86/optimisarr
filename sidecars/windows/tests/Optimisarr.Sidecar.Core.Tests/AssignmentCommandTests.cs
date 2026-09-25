using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// What this machine will and will not run.
///
/// <para>The server decides what to encode; it does not get to name files here. This sidecar ran
/// whatever it was sent until now, as LocalSystem, while the macOS one has enforced this contract
/// since it was written.</para>
/// </summary>
public sealed class AssignmentCommandTests
{
    private static readonly string[] Ordinary =
    [
        "-y", "-progress", "pipe:1", "-nostats", "-fflags", "+genpts",
        "-i", "{{input}}", "-map", "0", "-c:v:0", "hevc_nvenc", "-cq", "24",
        "-c:a", "aac", "-c:s", "copy", "-metadata", "optimisarr=0.2.12", "{{output}}.mkv",
    ];

    private static CommandRefusal? Refuse(IReadOnlyList<string> arguments, string extension = "mkv") =>
        AssignmentCommand.Refuse(arguments, extension);

    [Fact]
    public void The_command_the_server_actually_sends_is_accepted()
    {
        Assert.Null(Refuse(Ordinary));
    }

    [Fact]
    public void An_extension_written_with_a_dot_is_the_same_extension()
    {
        // The assignment carries "mkv" and the command carries ".mkv"; this machine sees both.
        Assert.Null(Refuse(Ordinary, ".mkv"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"\\fileserver\share\secrets.txt")]
    [InlineData("/etc/shadow")]
    [InlineData("~/.ssh/id_ed25519")]
    public void An_input_that_names_a_file_on_this_machine_is_refused(string path)
    {
        // The reason the contract exists. A server that asked for this would be reading whatever
        // LocalSystem can read, which on Windows is everything.
        string[] arguments = ["-i", path, "-c:v:0", "hevc_nvenc", "{{output}}.mkv"];

        var refusal = Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("{{input}}", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_that_is_not_the_placeholder_is_refused()
    {
        // Writing over anything this machine can write. The output must be the token, last, with
        // the extension the server promised — the extension chooses the muxer and the subtitle
        // codec, so it is part of the contract rather than a worker's preference.
        string[] arguments = ["-i", "{{input}}", "-c:v:0", "hevc_nvenc", @"C:\Windows\System32\drivers\etc\hosts"];

        Assert.NotNull(Refuse(arguments));
    }

    [Fact]
    public void An_output_carrying_a_different_extension_than_promised_is_refused()
    {
        string[] arguments = ["-i", "{{input}}", "-c:v:0", "hevc_nvenc", "{{output}}.mp4"];

        var refusal = Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("{{output}}.mkv", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_this_sidecar_does_not_know_is_refused_by_name()
    {
        // Rather than run it and find out. The token is named because the operator reading the log
        // needs to know what the server sent, not merely that something was refused — and because
        // an option the server legitimately starts emitting has to be added here deliberately.
        string[] arguments = ["-i", "{{input}}", "-vsync", "0", "{{output}}.mkv"];

        var refusal = Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("-vsync", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_hides_a_windows_path_is_refused()
    {
        // The case this platform has and macOS does not: a backslash path is exactly what an
        // argument trying to name a file looks like here.
        string[] arguments =
            ["-i", "{{input}}", "-x265-params", @"stats=C:\Users\scott\pass.log", "{{output}}.mkv"];

        var refusal = Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("names a path", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filtergraph_escape_is_not_mistaken_for_a_path()
    {
        // The other half of that. A filter escapes a comma with a backslash, and the first real
        // capped encode on the other platform was refused wholesale for it.
        string[] arguments =
            ["-i", "{{input}}", "-filter:v:0", @"select=not(mod(round(t*60)\,2))", "{{output}}.mkv"];

        Assert.Null(Refuse(arguments));
    }

    [Theory]
    [InlineData(@"a\,b")]
    [InlineData(@"a\:b")]
    [InlineData(@"a\\b")]
    [InlineData(@"a\[b\]")]
    public void Every_character_a_filtergraph_escapes_stays_allowed(string value)
    {
        Assert.False(AssignmentCommand.HasPathLikeBackslash(value));
    }

    [Theory]
    [InlineData(@"C:\work")]
    [InlineData(@"..\..\secret")]
    [InlineData(@"ends with a trailing slash\")]
    public void Anything_else_after_a_backslash_reads_as_a_path(string value)
    {
        Assert.True(AssignmentCommand.HasPathLikeBackslash(value));
    }

    [Fact]
    public void A_hardware_decoder_this_platform_does_not_have_is_refused()
    {
        // The server names one only when this machine proved it. videotoolbox is the other
        // sidecar's, so a command carrying it was built for a different machine entirely.
        string[] arguments = ["-hwaccel", "videotoolbox", "-i", "{{input}}", "{{output}}.mkv"];

        var refusal = Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("videotoolbox", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_decoders_this_platform_does_have_are_allowed()
    {
        foreach (var decoder in new[] { "cuda", "qsv", "d3d11va", "dxva2" })
        {
            Assert.Null(Refuse(["-hwaccel", decoder, "-i", "{{input}}", "{{output}}.mkv"]));
        }
    }

    [Fact]
    public void Two_inputs_are_refused_because_one_of_them_is_not_the_source()
    {
        string[] arguments = ["-i", "{{input}}", "-i", "{{input}}", "{{output}}.mkv"];

        var refusal = Refuse(arguments);

        Assert.NotNull(refusal);
        Assert.Contains("exactly one input", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void No_input_at_all_is_refused()
    {
        Assert.NotNull(Refuse(["-c:v:0", "hevc_nvenc", "{{output}}.mkv"]));
    }

    [Fact]
    public void An_option_left_without_its_value_is_refused()
    {
        // "-i {{output}}.mkv" would otherwise read the output as the input's value and pass.
        var refusal = Refuse(["-i", "{{input}}", "-cq", "{{output}}.mkv"]);

        Assert.NotNull(refusal);
        Assert.Contains("-cq", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_command_is_refused_rather_than_run()
    {
        Assert.NotNull(Refuse([]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("mkv;rm -rf")]
    [InlineData(@"..\..\x")]
    [InlineData("averylongextension")]
    public void An_extension_that_is_not_a_plain_extension_is_refused(string extension)
    {
        Assert.NotNull(Refuse(Ordinary, extension));
    }
}
