namespace Optimisarr.Core.Queue;

/// <summary>The deterministic source window used by an interactive video preview.</summary>
public sealed record PreviewClipWindow(int StartSeconds, int DurationSeconds, bool IsClipped);

/// <summary>Keeps preview encoding and the comparison API on one source-relative timeline.</summary>
public static class PreviewClipPolicy
{
    public const int DurationSeconds = 60;

    public static PreviewClipWindow Plan(double? sourceDurationSeconds)
    {
        var isClipped = sourceDurationSeconds is { } duration && duration > DurationSeconds;
        var start = isClipped
            ? Math.Max(0, (int)(sourceDurationSeconds!.Value / 2 - DurationSeconds / 2.0))
            : 0;
        return new PreviewClipWindow(start, DurationSeconds, isClipped);
    }
}
