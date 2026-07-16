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
            ReencodeSameCodecAboveBytes = overrides.ReencodeSameCodecAboveBytes ?? settings.ReencodeSameCodecAboveBytes,
            TargetVideoCodec = Normalise(overrides.TargetVideoCodec) ?? settings.TargetVideoCodec,
            // TrackCleanup's promise is "container unchanged", so a per-library container
            // override (e.g. left over from a previous profile) must not reintroduce a remux.
            TargetContainer = profile == Domain.RuleProfile.TrackCleanup
                ? null
                : Normalise(overrides.TargetContainer) ?? settings.TargetContainer,
            Hdr = overrides.Hdr ?? settings.Hdr,
            OptimiseDolbyVision = overrides.OptimiseDolbyVision ?? settings.OptimiseDolbyVision,
            ExcludePathSegments = overrides.ExcludePathSegments ?? settings.ExcludePathSegments,
            TargetAudioCodec = Normalise(overrides.TargetAudioCodec) ?? settings.TargetAudioCodec,
            AudioBitrateKbps = overrides.AudioBitrateKbps ?? settings.AudioBitrateKbps,
            VideoAudioCodec = ResolveVideoAudioCodec(overrides.VideoAudioCodec, settings.VideoAudioCodec),
            VideoAudioBitrateKbps = overrides.VideoAudioBitrateKbps ?? settings.VideoAudioBitrateKbps,
            DownmixToStereo = overrides.DownmixToStereo ?? settings.DownmixToStereo,
            KeepAudioLanguages = overrides.KeepAudioLanguages ?? settings.KeepAudioLanguages,
            KeepSubtitleLanguages = overrides.KeepSubtitleLanguages ?? settings.KeepSubtitleLanguages,
            ReencodeLossyAudio = overrides.ReencodeLossyAudio ?? settings.ReencodeLossyAudio,
            TargetImageFormat = Normalise(overrides.TargetImageFormat) ?? settings.TargetImageFormat,
            ImageQuality = overrides.ImageQuality ?? settings.ImageQuality,
            ReencodeLossyImages = overrides.ReencodeLossyImages ?? settings.ReencodeLossyImages,
            ImageDownscaleMode = overrides.ImageDownscaleMode ?? settings.ImageDownscaleMode,
            ImageDownscaleValue = overrides.ImageDownscaleValue ?? settings.ImageDownscaleValue
        };
    }

    private static string? Normalise(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? ResolveVideoAudioCodec(string? value, string? profileDefault)
    {
        var normalised = Normalise(value);
        return value is null
            ? profileDefault
            : string.Equals(normalised, "copy", StringComparison.OrdinalIgnoreCase)
                ? null
                : normalised ?? profileDefault;
    }
}
