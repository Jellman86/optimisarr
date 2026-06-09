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
/// </summary>
public static class DecodeIntegrityParser
{
    public static DecodeIntegrity Parse(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return new DecodeIntegrity(0, null);
        }

        var lines = stderr
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return lines.Length == 0
            ? new DecodeIntegrity(0, null)
            : new DecodeIntegrity(lines.Length, lines[0]);
    }
}
