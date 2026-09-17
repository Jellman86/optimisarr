namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Why an assignment's command was refused, naming the token the server sent.</summary>
public sealed record CommandRefusal(string Reason)
{
    public override string ToString() => Reason;
}

/// <summary>
/// The server's argument array, checked before this machine will run it.
///
/// <para>The server is trusted to decide <em>what</em> to encode; it is not trusted to name
/// <em>files</em> on this machine. A compromised or merely buggy server could otherwise point
/// FFmpeg at anything this worker can read, or write over anything it can write — and here that is
/// LocalSystem. So the command must satisfy a small, explicit contract: every option is one the
/// server's own command builder is known to emit, the only input is the <c>{{input}}</c> token, the
/// only output is the <c>{{output}}</c> token in last position carrying the promised extension, and
/// no other value looks like a path. The worker then substitutes only its own scratch paths.</para>
///
/// <para>Anything outside the contract is refused whole rather than repaired, because a repaired
/// command is one nobody wrote.</para>
///
/// <para>The same contract the macOS sidecar enforces, which had it from the start while this one
/// ran whatever it was sent. The option list is shared by being written out again rather than
/// derived: an option the server begins emitting must be added here deliberately, and the failure
/// is a refused job naming the token rather than a silently executed one.</para>
/// </summary>
public static class AssignmentCommand
{
    /// <summary>Options that take no value.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "-y", "-nostats" };

    /// <summary>
    /// Options that take exactly one value, drawn from the server's FfmpegCommandBuilder and
    /// EncoderTuningPolicy. Device options are absent on purpose: a remote command decodes in
    /// software and the server never sends them to a worker.
    /// </summary>
    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "-progress", "-threads", "-fflags", "-i", "-map", "-map_metadata", "-metadata",
        "-disposition:v:0", "-c", "-c:v", "-c:v:0", "-c:a", "-c:s", "-filter:v:0", "-vf",
        "-crf", "-preset", "-q:v", "-qp", "-cq", "-rc", "-b:v", "-b:a", "-ac",
        "-global_quality", "-rc_mode", "-quality", "-lossless",
        "-tune", "-maxrate", "-minrate", "-bufsize", "-x264-params", "-x265-params",
        "-spatial-aq", "-temporal-aq", "-fps_mode", "-enc_time_base:v:0",
        "-ss", "-t", "-movflags", "-hwaccel",
    };

    /// <summary>
    /// The hardware decoders this platform has. The server names one only when this machine proved
    /// it by a real decode; anything else would be a command meant for another machine.
    /// </summary>
    private static readonly HashSet<string> HardwareDecoders =
        new(StringComparer.Ordinal) { "cuda", "d3d11va", "qsv", "dxva2" };

    /// <summary>
    /// Checks the array against the contract. Returns the reason on refusal rather than throwing,
    /// because refusing a command is an ordinary outcome this machine hands the job back for.
    /// </summary>
    public static CommandRefusal? Refuse(IReadOnlyList<string> arguments, string outputExtension)
    {
        if (arguments.Count == 0)
        {
            return new CommandRefusal("The server sent an empty command.");
        }

        var extension = outputExtension.TrimStart('.');
        if (!IsPlainExtension(extension))
        {
            return new CommandRefusal($"'{outputExtension}' is not a plain output extension.");
        }

        var expectedOutput = $"{AssignmentPlaceholders.Output}.{extension}";
        if (arguments[^1] != expectedOutput)
        {
            return new CommandRefusal(
                $"The command must end with {expectedOutput}, but ended with '{arguments[^1]}'.");
        }

        var inputs = 0;
        for (var index = 0; index < arguments.Count - 1;)
        {
            var token = arguments[index];
            if (Flags.Contains(token))
            {
                index += 1;
                continue;
            }

            if (!Valued.Contains(token))
            {
                return new CommandRefusal($"The server sent an option this sidecar does not know: '{token}'.");
            }

            if (index + 1 >= arguments.Count - 1)
            {
                return new CommandRefusal($"'{token}' was sent with no value.");
            }

            var value = arguments[index + 1];
            switch (token)
            {
                case "-i" when value != AssignmentPlaceholders.Input:
                    return new CommandRefusal($"The only input may be {AssignmentPlaceholders.Input}, not '{value}'.");
                case "-i":
                    inputs += 1;
                    break;
                case "-hwaccel" when !HardwareDecoders.Contains(value):
                    return new CommandRefusal($"'{value}' is not a hardware decoder this platform has.");
                case "-hwaccel":
                    break;
                default:
                    if (RefuseValue(value) is { } refusal)
                    {
                        return refusal;
                    }

                    break;
            }

            index += 2;
        }

        return inputs == 1
            ? null
            : new CommandRefusal($"The command must name exactly one input, but named {inputs}.");
    }

    private static CommandRefusal? RefuseValue(string value)
    {
        if (value.Contains(AssignmentPlaceholders.Input, StringComparison.Ordinal)
            || value.Contains(AssignmentPlaceholders.Output, StringComparison.Ordinal))
        {
            return new CommandRefusal($"A placeholder appears where a value belongs: '{value}'.");
        }

        // A filter chain, a metadata marker or a rate-control value never needs a path separator or
        // a home shorthand; an argument that has one is trying to name a file.
        if (value.Contains('/', StringComparison.Ordinal)
            || value.StartsWith('~')
            || HasPathLikeBackslash(value))
        {
            return new CommandRefusal($"A value names a path: '{value}'.");
        }

        return null;
    }

    /// <summary>
    /// A backslash is how a filtergraph escapes a comma, colon or quote inside one filter's
    /// arguments — <c>select=not(mod(round(t*60)\,2))</c> — and how Windows separates path
    /// segments. They are told apart by what follows: an escape precedes one of the few characters
    /// filtergraphs escape, a path segment precedes anything else.
    ///
    /// <para>This matters far more here than on macOS. On that platform a stray backslash is
    /// almost always an escape; on this one <c>C:\Windows\System32</c> is what a command trying to
    /// name a file actually looks like.</para>
    /// </summary>
    internal static bool HasPathLikeBackslash(string value)
    {
        const string escapable = @",:'\[];";
        var previousWasBackslash = false;
        foreach (var character in value)
        {
            if (previousWasBackslash)
            {
                if (!escapable.Contains(character, StringComparison.Ordinal))
                {
                    return true;
                }

                previousWasBackslash = false;
                continue;
            }

            previousWasBackslash = character == '\\';
        }

        return previousWasBackslash;
    }

    private static bool IsPlainExtension(string extension) =>
        extension.Length is > 0 and <= 8 && extension.All(char.IsAsciiLetterOrDigit);
}
