using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Rules;

/// <summary>
/// Maps each <see cref="RuleProfile"/> to its default settings. The container, codec, and CRF
/// pairings are opinionated and researched (see the notes on each profile) so an operator can
/// pick a profile and trust it without tuning; per-library overrides still layer on top.
///
/// Audio defaults to <em>copy</em> (the source track is preserved bit-exact) on every profile;
/// the comments record the recommended audio codec to opt into, but a video re-encode never
/// silently downgrades the original audio. CRF values target visually-transparent quality for
/// each codec rather than the encoder's arbitrary built-in default.
/// </summary>
public static class RuleProfileDefaults
{
    private const long Megabyte = 1024L * 1024L;

    /// <summary>Re-encode profiles ignore files below this size by default.</summary>
    private const long DefaultMinReencodeSize = 200 * Megabyte;

    public static RuleSettings For(RuleProfile profile) => profile switch
    {
        // Broad compatibility + good efficiency: HEVC in MP4 plays on virtually all phones,
        // smart TVs, and Apple devices; pair with AAC audio. x265 CRF 24 is visually transparent
        // at 1080p (≈ x264 CRF 19).
        RuleProfile.ConservativeHevc => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = "hevc",
            TargetContainer = "mp4",
            DefaultCrf = 24,
            MinFileSizeBytes = DefaultMinReencodeSize,
            Hdr = HdrHandling.Exclude
        },
        // Maximum compatibility: H.264 + AAC in MP4 plays literally everywhere, at the cost of
        // larger files. x264 CRF 20 is visually transparent at 1080p.
        RuleProfile.CompatibilityH264 => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = "h264",
            TargetContainer = "mp4",
            DefaultCrf = 20,
            MinFileSizeBytes = DefaultMinReencodeSize,
            Hdr = HdrHandling.Exclude
        },
        // Maximum efficiency: AV1 (30–50% smaller than HEVC, royalty-free, slower to encode) with
        // Opus audio in MKV — MP4 + Opus has spotty player support, so MKV is the right home.
        // SVT-AV1 CRF 30 is a sane transparent-ish 1080p target.
        RuleProfile.ExperimentalAv1 => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = "av1",
            TargetContainer = "mkv",
            DefaultCrf = 30,
            MinFileSizeBytes = DefaultMinReencodeSize,
            Hdr = HdrHandling.Preserve
        },
        // Lossless container cleanup only: never re-encodes, so it has no codec or CRF. MKV is the
        // most permissive container for a remux.
        RuleProfile.RemuxCleanup => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = null,
            TargetContainer = "mkv",
            DefaultCrf = null,
            MinFileSizeBytes = 0,
            Hdr = HdrHandling.Preserve
        },
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown rule profile")
    };
}
