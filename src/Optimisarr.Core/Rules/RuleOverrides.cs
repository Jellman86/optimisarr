using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Rules;

/// <summary>
/// Per-library overrides layered on top of a <see cref="RuleProfile"/>'s defaults.
/// Every value is optional: a <c>null</c> means "use the profile default", so a
/// library only stores the knobs the operator actually changed.
/// </summary>
public sealed record RuleOverrides
{
    public long? MinFileSizeBytes { get; init; }
    public int? MaxHeight { get; init; }
    public string? TargetVideoCodec { get; init; }
    public string? TargetContainer { get; init; }
    public HdrHandling? Hdr { get; init; }
    public IReadOnlyList<string>? ExcludePathSegments { get; init; }
    public string? TargetAudioCodec { get; init; }
    public int? AudioBitrateKbps { get; init; }
    public string? VideoAudioCodec { get; init; }
    public int? VideoAudioBitrateKbps { get; init; }
    public bool? DownmixToStereo { get; init; }
    public bool? ReencodeLossyAudio { get; init; }
    public string? TargetImageFormat { get; init; }
    public int? ImageQuality { get; init; }
    public bool? ReencodeLossyImages { get; init; }

    public static readonly RuleOverrides None = new();
}
