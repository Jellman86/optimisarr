namespace Optimisarr.Core.Queue;

/// <summary>How a music file that is not converted has its silence trimmed.</summary>
public enum AudioTrimOnlyMethod
{
    /// <summary>
    /// Decoded, cut to the exact sample and encoded again with the same lossless codec, so every
    /// kept sample is the source's own.
    /// </summary>
    LosslessReencode,

    /// <summary>
    /// The encoded audio frames inside the window are copied unchanged. A lossy file is never
    /// encoded again for a trim: that would lose quality to save nothing. The cut falls on a frame,
    /// a few hundredths of a second, inside the silence the trim keeps.
    /// </summary>
    StreamCopy,
}

/// <param name="Codec">The source's codec, which the trimmed file keeps.</param>
/// <param name="Encoder">The lossless encoder that writes it again; null for a stream copy.</param>
public sealed record AudioTrimOnlyPlan(AudioTrimOnlyMethod Method, string Codec, string? Encoder);

/// <summary>A way to trim a file without converting it, or why there is none.</summary>
public sealed record AudioTrimOnlyDecision(AudioTrimOnlyPlan? Plan, string Reason);

/// <summary>
/// Which music files can have silence trimmed while keeping their format, measured with the shipped
/// FFmpeg on generated signals (#535; see docs/setup/configuration.md). Every format not listed is left
/// alone with its reason.
/// </summary>
public static class AudioTrimOnly
{
    /// <summary>
    /// A stream copy starts this far before the planned start. A copy can only begin on an audio
    /// frame (26 ms for MP3 at 44.1 kHz, 23 ms for AAC, 20 ms for Opus) and its seek lands up to a
    /// frame late, so without it the cut could reach into the silence the trim keeps.
    /// </summary>
    public const double StreamCopyLeadSeconds = 0.05;

    // PCM variants FFmpeg writes back to WAV bit for bit.
    private static readonly HashSet<string> WavPcmCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le",
    };

    public static AudioTrimOnlyDecision Decide(string? codec, string relativePath, int attachedPictureCount)
    {
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        var normalised = codec?.Trim().ToLowerInvariant();
        var plan = (normalised, extension) switch
        {
            ("flac", ".flac") => Lossless("flac"),
            ("alac", ".m4a") => Lossless("alac"),
            (_, ".wav") when normalised is not null && WavPcmCodecs.Contains(normalised) => Lossless(normalised),
            // MP3 and MP4 record their encoder delay, which FFmpeg writes again for the cut.
            ("mp3", ".mp3") or ("aac", ".m4a") => Copy(normalised),
            // Ogg Opus records its pre-skip and its end in the page granule positions.
            ("opus", ".opus" or ".ogg") => Copy(normalised),
            _ => null,
        };
        if (plan is null)
            return new(null, $"{codec ?? "Unknown"} audio in a {extension} file cannot be trimmed without converting it.");
        // Neither FFmpeg's Ogg writer nor its WAV writer can carry an embedded picture, and its M4A
        // writer drops one when it keeps Optimisarr's marker (use_metadata_tags), in 6.1 and 7.1 alike.
        if (attachedPictureCount > 0 && extension is ".opus" or ".ogg" or ".wav" or ".m4a")
            return new(null, $"The cover art in a {extension} file could not be kept, so it is not trimmed without converting it.");
        return new(plan, plan.Method == AudioTrimOnlyMethod.StreamCopy
            ? $"Trims silence only, copying the {plan.Codec} audio unchanged."
            : $"Trims silence only, keeping the {plan.Codec} audio lossless.");
    }

    /// <summary>
    /// The window a stream copy keeps: from <see cref="StreamCopyLeadSeconds"/> before the planned
    /// start, so the kept silence is never cut into, to the planned end, which the copy reaches by
    /// keeping the whole frame it falls in. Verification expects exactly this window.
    /// </summary>
    public static AudioTrimPlan ForStreamCopy(AudioTrimPlan plan)
    {
        if (!plan.TrimsStart) return plan;
        var lead = (long)Math.Round(StreamCopyLeadSeconds * plan.SampleRate);
        return plan with { StartSample = plan.StartSample > lead ? plan.StartSample - lead : 0 };
    }

    private static AudioTrimOnlyPlan Lossless(string codec) => new(AudioTrimOnlyMethod.LosslessReencode, codec, codec);

    private static AudioTrimOnlyPlan Copy(string codec) => new(AudioTrimOnlyMethod.StreamCopy, codec, null);
}
