using Optimisarr.Core.Domain;
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

    public static RuleSettings Resolve(Data.Library? library) =>
        RuleResolver.Resolve(ProfileOf(library), ToOverrides(library));

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
            TargetVideoCodec = library.TargetVideoCodec,
            TargetContainer = library.TargetContainer,
            Hdr = library.HdrHandling,
            ExcludePathSegments = ParseExcludePaths(library.ExcludePaths),
            TargetAudioCodec = library.AudioTargetCodec,
            AudioBitrateKbps = library.AudioBitrateKbps
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
