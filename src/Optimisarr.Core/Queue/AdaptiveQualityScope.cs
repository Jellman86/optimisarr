namespace Optimisarr.Core.Queue;

/// <summary>
/// A per-title quality selected by adaptive VMAF is a value on one encoder's own scale, measured
/// on that encoder's samples. Retries and recovered attempts can be claimed by a different
/// machine, so the value is reused only by the encoder that proved it. Any other encoder searches
/// again rather than encoding at a number that means something different there.
/// </summary>
public static class AdaptiveQualityScope
{
    public static int? For(int? selectedQuality, string? selectedOnEncoder, string? encoder) =>
        selectedQuality is not null
        && !string.IsNullOrWhiteSpace(selectedOnEncoder)
        && string.Equals(selectedOnEncoder, encoder, StringComparison.OrdinalIgnoreCase)
            ? selectedQuality
            : null;
}
