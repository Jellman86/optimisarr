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

/// <param name="Version">The ID3v2 version, 3 or 4; null when the file has no ID3v2 tag.</param>
/// <param name="HasId3v1">The file also ends with an ID3v1 tag.</param>
public sealed record Id3Layout(int? Version, bool HasId3v1);

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

    /// <param name="tagRefusal">
    /// Why the file's tags would not survive a trim unchanged (see <see cref="TrimOnlyTags.Refusal"/>),
    /// as last read from the file; null when nothing stands in the way or the tags are not read yet.
    /// </param>
    public static AudioTrimOnlyDecision Decide(string? codec, string relativePath, int attachedPictureCount, string? tagRefusal = null)
    {
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        var normalised = codec?.Trim().ToLowerInvariant();
        // A converted M4A keeps its iTunes atoms (#593), but neither a stream-copied AAC cut, with its
        // edit list and priming, nor the native comparison of MP4 tags has been measured for a trim.
        if (extension is ".m4a")
            return new(null, "M4A files are not yet trimmed without converting: it has not been proved that their audio and tags come through a trim unchanged.");
        var plan = (normalised, extension) switch
        {
            ("flac", ".flac") => Lossless("flac"),
            (_, ".wav") when normalised is not null && WavPcmCodecs.Contains(normalised) => Lossless(normalised),
            // MP3 records its encoder delay, which FFmpeg writes again for the cut.
            ("mp3", ".mp3") => Copy(normalised),
            // Ogg Opus records its pre-skip and its end in the page granule positions.
            ("opus", ".opus" or ".ogg") => Copy(normalised),
            _ => null,
        };
        if (plan is null)
            return new(null, $"{codec ?? "Unknown"} audio in a {extension} file cannot be trimmed without converting it.");
        // Neither FFmpeg's Ogg writer nor its WAV writer can carry an embedded picture.
        if (attachedPictureCount > 0 && extension is ".opus" or ".ogg" or ".wav")
            return new(null, $"The cover art in a {extension} file could not be kept, so it is not trimmed without converting it.");
        if (tagRefusal is not null) return new(null, tagRefusal);
        return new(plan, plan.Method == AudioTrimOnlyMethod.StreamCopy
            ? $"Trims silence only, copying the {plan.Codec} audio unchanged."
            : $"Trims silence only, keeping the {plan.Codec} audio lossless.");
    }

    /// <summary>Whether a file in this format could be trimmed without converting, so its tags are worth reading.</summary>
    public static bool ReadsTags(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() is ".mp3" or ".flac" or ".opus" or ".ogg" or ".wav";

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
