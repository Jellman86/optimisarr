using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Rules;

/// <summary>
/// The probed facts about a media file that drive an eligibility decision. A pure
/// input record so <see cref="CandidateEvaluator"/> can be tested without a
/// database or live ffprobe.
/// </summary>
public sealed record MediaProperties(
    string? Container,
    string? VideoCodec,
    int? Width,
    int? Height,
    long SizeBytes,
    bool IsHdr,
    string RelativePath,
    string? OptimisedMarker = null,
    MediaKind Kind = MediaKind.Video,
    string? AudioCodec = null,
    int? AudioBitrateKbps = null,
    int? FrameCount = null,
    double? DurationSeconds = null,
    bool IsDolbyVision = false,
    string? PixelFormat = null,
    int? BitsPerRawSample = null,
    int AttachedPictureCount = 0,
    int SubtitleTrackCount = 0,
    int MaxAudioChannels = 0,
    // Per-track audio language tags in stream order; null when the probe predates
    // language capture, so language rules stay conservative.
    IReadOnlyList<string?>? AudioLanguages = null);
