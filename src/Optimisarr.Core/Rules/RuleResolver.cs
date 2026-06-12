namespace Optimisarr.Core.Rules;

/// <summary>
/// Resolves the effective <see cref="RuleSettings"/> for a library: start from its
/// profile's defaults, then apply any per-library overrides. Pure and deterministic
/// so the layering is fully unit tested.
/// </summary>
public static class RuleResolver
{
    public static RuleSettings Resolve(Domain.RuleProfile profile, RuleOverrides overrides)
    {
        var settings = RuleProfileDefaults.For(profile);

        return settings with
        {
            MinFileSizeBytes = overrides.MinFileSizeBytes ?? settings.MinFileSizeBytes,
            MaxHeight = overrides.MaxHeight ?? settings.MaxHeight,
            TargetVideoCodec = Normalise(overrides.TargetVideoCodec) ?? settings.TargetVideoCodec,
            TargetContainer = Normalise(overrides.TargetContainer) ?? settings.TargetContainer,
            Hdr = overrides.Hdr ?? settings.Hdr,
            ExcludePathSegments = overrides.ExcludePathSegments ?? settings.ExcludePathSegments,
            TargetAudioCodec = Normalise(overrides.TargetAudioCodec) ?? settings.TargetAudioCodec,
            AudioBitrateKbps = overrides.AudioBitrateKbps ?? settings.AudioBitrateKbps,
            VideoAudioCodec = Normalise(overrides.VideoAudioCodec) ?? settings.VideoAudioCodec,
            VideoAudioBitrateKbps = overrides.VideoAudioBitrateKbps ?? settings.VideoAudioBitrateKbps,
            DownmixToStereo = overrides.DownmixToStereo ?? settings.DownmixToStereo,
            ReencodeLossyAudio = overrides.ReencodeLossyAudio ?? settings.ReencodeLossyAudio
        };
    }

    private static string? Normalise(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
