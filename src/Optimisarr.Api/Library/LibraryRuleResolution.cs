using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;

namespace Optimisarr.Api.Library;

/// <summary>
/// Bridges a persisted <see cref="Data.Library"/> to the pure rules engine: maps its
/// override columns to <see cref="RuleOverrides"/> and resolves the effective
/// <see cref="RuleSettings"/>. Shared by candidate evaluation and the transcode queue.
/// </summary>
internal static class LibraryRuleResolution
{
    public static RuleProfile ProfileOf(Data.Library? library) =>
        library?.RuleProfile ?? RuleProfile.ConservativeHevc;

    public static RuleSettings Resolve(Data.Library? library)
    {
        var rules = RuleResolver.Resolve(ProfileOf(library), ToOverrides(library));

        // A library can opt out of the profile's "skip already-efficient sources" floor, sending every
        // eligible source to the encoder (the size-saving gate still guards the original).
        return library is { SkipEfficientSources: false }
            ? rules with { MinSourceBitsPerPixelSecond = null }
            : rules;
    }

    /// <summary>
    /// Resolves one of the four video preset-slider stops while retaining unrelated library
    /// overrides. Codec/container overrides are deliberately cleared because selecting a named
    /// slider stop does the same. Scott's stop also applies the bundle fields set by the UI.
    /// </summary>
    public static RuleSettings ResolveVideoPreset(Data.Library library, RuleProfile profile)
    {
        var overrides = ToOverrides(library) with
        {
            TargetVideoCodec = null,
            TargetContainer = null
        };
        if (profile == RuleProfile.ScottsSettings)
        {
            overrides = overrides with
            {
                Hdr = HdrHandling.Preserve,
                VideoAudioCodec = "aac",
                VideoAudioBitrateKbps = 96,
                DownmixToStereo = true
            };
        }

        var rules = RuleResolver.Resolve(profile, overrides);
        return library.SkipEfficientSources
            ? rules
            : rules with { MinSourceBitsPerPixelSecond = null };
    }

    public static RuleOverrides ToOverrides(Data.Library? library)
    {
        if (library is null)
        {
            return RuleOverrides.None;
        }

        return new RuleOverrides
        {
            MinFileSizeBytes = library.MinFileSizeBytes,
            MaxHeight = library.MaxHeight,
            ReencodeSameCodecAboveBytes = library.ReencodeSameCodecAboveBytes,
            TargetVideoCodec = library.TargetVideoCodec,
            TargetContainer = library.TargetContainer,
            Hdr = library.HdrHandling,
            OptimiseDolbyVision = library.OptimiseDolbyVision,
            ExcludePathSegments = ParseExcludePaths(library.ExcludePaths),
            TargetAudioCodec = library.AudioTargetCodec,
            AudioBitrateKbps = library.AudioBitrateKbps,
            VideoAudioCodec = library.VideoAudioCodec,
            VideoAudioBitrateKbps = library.VideoAudioBitrateKbps,
            DownmixToStereo = library.DownmixToStereo,
            KeepAudioLanguages = string.IsNullOrWhiteSpace(library.KeepAudioLanguages)
                ? null
                : TrackLanguages.ParseLanguageList(library.KeepAudioLanguages),
            KeepSubtitleLanguages = string.IsNullOrWhiteSpace(library.KeepSubtitleLanguages)
                ? null
                : TrackLanguages.ParseLanguageList(library.KeepSubtitleLanguages),
            ReencodeLossyAudio = library.ReencodeLossyAudio,
            TargetImageFormat = library.TargetImageFormat,
            ImageQuality = library.ImageQuality,
            ReencodeLossyImages = library.ReencodeLossyImages,
            ImageDownscaleMode = library.ImageDownscaleMode,
            ImageDownscaleValue = library.ImageDownscaleValue
        };
    }

    // Operators enter one path substring per line; blank lines are ignored.
    private static IReadOnlyList<string>? ParseExcludePaths(string? excludePaths)
    {
        if (string.IsNullOrWhiteSpace(excludePaths))
        {
            return null;
        }

        var segments = excludePaths
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        return segments.Length > 0 ? segments : null;
    }
}
