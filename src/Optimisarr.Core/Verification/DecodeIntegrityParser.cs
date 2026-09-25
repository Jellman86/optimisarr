namespace Optimisarr.Core.Verification;

/// <summary>The decoder errors found across a full decode of a file.</summary>
/// <param name="ErrorCount">How many error lines FFmpeg emitted (one per corrupt frame/packet).</param>
/// <param name="FirstError">The first error line, for display; null when there were none.</param>
public sealed record DecodeIntegrity(int ErrorCount, string? FirstError);

/// <summary>Counts decode diagnostics as they arrive, without retaining unbounded FFmpeg output.</summary>
public sealed class DecodeIntegrityAccumulator
{
    private int _count;
    private string? _first;
    private bool _lastCounted;

    public DecodeIntegrity Result => new(_count, _first);

    public void AddLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return;
        }

        if (DecodeIntegrityParser.RepeatCount(line) is { } repeats)
        {
            if (_lastCounted)
            {
                _count = (int)Math.Min(int.MaxValue, (long)_count + repeats);
            }
            return;
        }

        _lastCounted = DecodeIntegrityParser.IsDecodeError(line);
        if (_lastCounted)
        {
            _count = (int)Math.Min(int.MaxValue, (long)_count + 1);
            _first ??= line;
        }
    }
}

/// <summary>
/// Pure parser for the stderr of a full-file decode run at <c>-v error</c>. At that
/// log level FFmpeg prints one line per real decode problem, so the line count is a
/// faithful tally of corrupt frames and packet read errors over the whole file —
/// unlike a stop-at-first-error check, which only proves the file is bad somewhere.
/// Muxer-side timestamp notes are excluded: the decode pass writes to the null muxer,
/// which is stricter about timestamps than any player, and a hardware encoder (QSV/NVENC)
/// can emit equal/duplicate DTS the muxer flags as "non monotonically increasing dts to
/// muxer". That is a muxing remark about the throwaway output, not decoded-picture
/// corruption — genuine decode-order regressions are judged separately by the
/// timestamp-integrity gate — so it must not count as a decode error here.
/// </summary>
public static class DecodeIntegrityParser
{
    public static DecodeIntegrity Parse(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return new DecodeIntegrity(0, null);
        }

        var accumulator = new DecodeIntegrityAccumulator();

        foreach (var line in stderr.Split(
            '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            accumulator.AddLine(line);
        }

        return accumulator.Result;
    }

    // A non-strictly-increasing DTS handed to the null muxer is a timestamp remark, not corruption;
    // it is the dominant false positive for hardware-encoded output and is filtered out here.
    internal static bool IsDecodeError(string line) =>
        line.IndexOf("non monotonically increasing dts", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>
    /// How many further occurrences FFmpeg's own deduplication notice stands for, or null when the
    /// line is not one.
    ///
    /// <para>FFmpeg collapses consecutive identical messages into "Last message repeated N times".
    /// Counted as a line in its own right, it turned a file whose only remark was the muxer DTS
    /// note — already ignored here — into "1 decode error(s): Last message repeated 1 times", and
    /// twenty-four good encodes were thrown away on it. It is not an error; it is an account of
    /// how many of the previous one there were.</para>
    /// </summary>
    internal static int? RepeatCount(string line)
    {
        const string prefix = "Last message repeated ";
        var start = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var rest = line[(start + prefix.Length)..].AsSpan();
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        return digits > 0 && int.TryParse(rest[..digits], out var repeats) ? repeats : null;
    }
}
