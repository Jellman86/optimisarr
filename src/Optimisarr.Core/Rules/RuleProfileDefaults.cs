using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Rules;

/// <summary>Maps each <see cref="RuleProfile"/> to its default eligibility settings.</summary>
public static class RuleProfileDefaults
{
    private const long Megabyte = 1024L * 1024L;

    /// <summary>Re-encode profiles ignore files below this size by default.</summary>
    private const long DefaultMinReencodeSize = 200 * Megabyte;

    public static RuleSettings For(RuleProfile profile) => profile switch
    {
        RuleProfile.ConservativeHevc => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = "hevc",
            MinFileSizeBytes = DefaultMinReencodeSize,
            Hdr = HdrHandling.Exclude
        },
        RuleProfile.CompatibilityH264 => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = "h264",
            MinFileSizeBytes = DefaultMinReencodeSize,
            Hdr = HdrHandling.Exclude
        },
        RuleProfile.ExperimentalAv1 => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = "av1",
            MinFileSizeBytes = DefaultMinReencodeSize,
            Hdr = HdrHandling.Preserve
        },
        RuleProfile.RemuxCleanup => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = null,
            MinFileSizeBytes = 0,
            Hdr = HdrHandling.Preserve
        },
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown rule profile")
    };
}
