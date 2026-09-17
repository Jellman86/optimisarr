namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// The server's libvmaf command, checked before this machine will run it.
///
/// <para>The same contract as <see cref="AssignmentCommand"/>, for the same reason: the server
/// decides what to measure, never which files on this machine to read or write. The two inputs
/// must be the <c>{{distorted}}</c> and <c>{{reference}}</c> tokens in that order — libvmaf wants
/// distorted first — the filter must name <c>{{log}}</c> exactly once, the command must end in the
/// null muxer so nothing is written but the log, and no other value may look like a path.</para>
///
/// <para>The null-muxer rule is the one doing the most work. Without it a measurement command is
/// an encode command that happens to score something, and could be asked to write anywhere.</para>
/// </summary>
public static class MeasurementCommand
{
    private static readonly HashSet<string> Flags =
        new(StringComparer.Ordinal) { "-nostdin", "-stats", "-y", "-nostats" };

    /// <summary>
    /// From the server's own CPU measurement path. Device and hardware-decode options are absent
    /// on purpose: a worker's measurement decodes in software, and the server never sends them.
    /// </summary>
    private static readonly HashSet<string> Valued =
        new(StringComparer.Ordinal) { "-v", "-ss", "-t", "-i", "-lavfi", "-f", "-threads" };

    public static CommandRefusal? Refuse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 3)
        {
            return new CommandRefusal("The server sent an empty measurement command.");
        }

        if (arguments[^3] != "-f" || arguments[^2] != "null" || arguments[^1] != "-")
        {
            return new CommandRefusal(
                "A measurement must end in the null muxer so it writes nothing but its log.");
        }

        var inputs = new List<string>();
        var logPlaceholders = 0;

        for (var index = 0; index < arguments.Count - 3;)
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

            if (index + 1 >= arguments.Count - 3)
            {
                return new CommandRefusal($"'{token}' was sent with no value.");
            }

            var value = arguments[index + 1];
            switch (token)
            {
                case "-i":
                    inputs.Add(value);
                    break;
                case "-lavfi":
                    logPlaceholders += Occurrences(value, MeasurementPlaceholders.Log);
                    if (RefuseValue(value, allowingLog: true) is { } filterRefusal)
                    {
                        return filterRefusal;
                    }

                    break;
                default:
                    if (RefuseValue(value, allowingLog: false) is { } refusal)
                    {
                        return refusal;
                    }

                    break;
            }

            index += 2;
        }

        if (inputs is not [MeasurementPlaceholders.Distorted, MeasurementPlaceholders.Reference])
        {
            return new CommandRefusal(
                "A measurement reads exactly the candidate then the source, both as placeholders, "
                + $"but was sent [{string.Join(", ", inputs)}].");
        }

        return logPlaceholders == 1
            ? null
            : new CommandRefusal(
                $"The filter must name {MeasurementPlaceholders.Log} exactly once, but named it {logPlaceholders} times.");
    }

    private static CommandRefusal? RefuseValue(string value, bool allowingLog)
    {
        if (value.Contains(MeasurementPlaceholders.Distorted, StringComparison.Ordinal)
            || value.Contains(MeasurementPlaceholders.Reference, StringComparison.Ordinal))
        {
            return new CommandRefusal($"An input placeholder appears where a value belongs: '{value}'.");
        }

        if (!allowingLog
            && (value.Contains(MeasurementPlaceholders.Log, StringComparison.Ordinal)
                || value.Contains(MeasurementPlaceholders.DistortedShift, StringComparison.Ordinal)))
        {
            return new CommandRefusal($"A filter placeholder appears outside the filter: '{value}'.");
        }

        // The filter is the one value that may carry a path, because the log lives inside it. The
        // log is this machine's own scratch path, substituted after this check.
        if (allowingLog)
        {
            return null;
        }

        if (value.Contains('/', StringComparison.Ordinal)
            || value.StartsWith('~')
            || AssignmentCommand.HasPathLikeBackslash(value))
        {
            return new CommandRefusal($"A value names a path: '{value}'.");
        }

        return null;
    }

    private static int Occurrences(string value, string token)
    {
        var count = 0;
        var at = value.IndexOf(token, StringComparison.Ordinal);
        while (at >= 0)
        {
            count += 1;
            at = value.IndexOf(token, at + token.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
