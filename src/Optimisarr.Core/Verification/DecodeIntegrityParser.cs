namespace Optimisarr.Core.Verification;

/// <summary>The decoder errors found across a full decode of a file.</summary>
/// <param name="ErrorCount">How many error lines FFmpeg emitted (one per corrupt frame/packet).</param>
/// <param name="FirstError">The first error line, for display; null when there were none.</param>
public sealed record DecodeIntegrity(int ErrorCount, string? FirstError);

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

        var errors = stderr
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(IsDecodeError)
            .ToArray();

        return errors.Length == 0
            ? new DecodeIntegrity(0, null)
            : new DecodeIntegrity(errors.Length, errors[0]);
    }

    // A non-strictly-increasing DTS handed to the null muxer is a timestamp remark, not corruption;
    // it is the dominant false positive for hardware-encoded output and is filtered out here.
    private static bool IsDecodeError(string line) =>
        line.IndexOf("non monotonically increasing dts", StringComparison.OrdinalIgnoreCase) < 0;
}
