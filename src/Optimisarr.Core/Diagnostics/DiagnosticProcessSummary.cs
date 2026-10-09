namespace Optimisarr.Core.Diagnostics;

/// <summary>Canonical tool messages, never arbitrary log lines or path-bearing process output.</summary>
public sealed record DiagnosticProcessSummary(int CharactersInspected, bool Truncated, IReadOnlyList<string> KnownMessages)
{
    private static readonly string[] Messages =
    [
        "Invalid data found when processing input", "Non-monotonous DTS", "Non-monotonic DTS",
        "Application provided invalid, non monotonically increasing dts", "Error while opening encoder",
        "Error initializing output stream", "Error muxing a packet", "Conversion failed!", "No space left on device",
        "Permission denied", "Connection reset by peer", "corrupt decoded frame", "Error submitting packet"
    ];
    public static DiagnosticProcessSummary? Read(string? log)
    {
        if (string.IsNullOrEmpty(log)) return null;
        var bounded = log.Length > 65536 ? log[^65536..] : log;
        return new(bounded.Length, log.Length > 65536, Messages.Where(m => bounded.Contains(m, StringComparison.OrdinalIgnoreCase)).ToArray());
    }
    public static DiagnosticProcessSummary Sanitize(DiagnosticProcessSummary value) => new(
        Math.Clamp(value.CharactersInspected, 0, 65536), value.Truncated,
        (value.KnownMessages ?? []).Where(m => Messages.Contains(m, StringComparer.Ordinal)).Distinct().ToArray());
}
