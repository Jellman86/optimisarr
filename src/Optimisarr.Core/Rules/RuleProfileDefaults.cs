using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Rules;

/// <summary>
/// Maps each <see cref="RuleProfile"/> to its default settings. The container, codec, and CRF
/// pairings are opinionated and researched (see the notes on each profile) so an operator can
/// pick a profile and trust it without tuning; per-library overrides still layer on top.
///
/// The MP4 compatibility profiles pair video with channel-aware AAC so their playback promise
/// covers the complete file, not only its picture stream. Efficiency/remux profiles retain audio;
/// an explicit per-library <c>copy</c> override is always available. CRF values target
/// visually-transparent quality rather than the encoder's arbitrary built-in default.
/// </summary>
public static class RuleProfileDefaults
{
    private const long Megabyte = 1024L * 1024L;

    /// <summary>Re-encode profiles ignore files below this size by default.</summary>
    private const long DefaultMinReencodeSize = 200 * Megabyte;

    // Efficiency floors in bits per pixel-second (file bitrate ÷ width ÷ height). Below the floor a
    // source is already compressed enough that re-encoding to the profile's codec is unlikely to
    // save space. HEVC's break-even is ≈ 0.8 (a ~1.6 Mbps 1080p h264), so 1.0 leaves a safe margin;
    // H.264 is less efficient, so it needs a higher-bitrate source to win. AV1 sets no floor — it is
    // efficient enough to shrink even low-bitrate sources. Calibrated against real library data.
    private const double HevcEfficiencyFloor = 1.0;
    private const double H264EfficiencyFloor = 2.0;

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
            MinSourceBitsPerPixelSecond = HevcEfficiencyFloor,
            Hdr = HdrHandling.Exclude,
            VideoAudioCodec = "aac",
            VideoAudioBitrateKbps = 160
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
            MinSourceBitsPerPixelSecond = H264EfficiencyFloor,
            Hdr = HdrHandling.Exclude,
            VideoAudioCodec = "aac",
            VideoAudioBitrateKbps = 160
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
        // "Scott's Settings": the same conservative, broadly-compatible HEVC/MP4 base as
        // ConservativeHevc, but a complete bundle rather than video-only. HDR is preserved so a
        // library does not unexpectedly use the CPU-heavy software HDR-to-SDR tone-map path; audio
        // is re-encoded to AAC 96 kbps downmixed to stereo (both the audio track of a video job and
        // an audio-only/music file). 96 kbps stereo AAC is transparent enough for typical listening
        // while saving a lot over surround lossless. A music library set to this profile gets the
        // same AAC 96 kbps stereo target.
        RuleProfile.ScottsSettings => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = "hevc",
            TargetContainer = "mp4",
            DefaultCrf = 24,
            MinFileSizeBytes = DefaultMinReencodeSize,
            MinSourceBitsPerPixelSecond = HevcEfficiencyFloor,
            Hdr = HdrHandling.Preserve,
            VideoAudioCodec = "aac",
            VideoAudioBitrateKbps = 96,
            DownmixToStereo = true,
            TargetAudioCodec = "aac",
            AudioBitrateKbps = 96
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
