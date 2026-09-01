namespace Optimisarr.Core.Queue;

/// <summary>
/// What kind of picture the encoder should optimise for. Deliberately a small, portable vocabulary
/// rather than the encoder's own tune list: those lists disagree about what a "tune" even is —
/// x264/x265 name content types, NVENC names latency profiles, SVT-AV1 names metrics.
/// </summary>
public enum ContentTune
{
    /// <summary>The encoder's own default. Nothing is passed.</summary>
    None,

    /// <summary>Flat colour, hard edges, low grain — animation and cartoons.</summary>
    Animation,

    /// <summary>Preserve film grain rather than smoothing it into a flat, plastic-looking image.</summary>
    Grain
}

/// <summary>The portable advanced-encoder intent stored on a library.</summary>
public sealed record EncoderTuning(
    ContentTune Tune = ContentTune.None,

    /// <summary>
    /// A ceiling on the output's video bitrate in kbps, on top of the constant-quality target.
    /// Null or non-positive means no cap. Capping can only make an output smaller, so it cannot
    /// weaken the size-saving gate.
    /// </summary>
    int? MaxBitrateKbps = null,

    /// <summary>
    /// Spend more bits on the areas an eye notices — flat gradients, dark scenes — at the cost of
    /// detail elsewhere. Each family spells this differently; some cannot express it at all.
    /// </summary>
    bool StrongerAdaptiveQuantisation = false)
{
    public static readonly EncoderTuning None = new();

    /// <summary>Whether anything at all was asked for, so callers can skip the resolve entirely.</summary>
    public bool IsEmpty =>
        Tune == ContentTune.None && MaxBitrateKbps is null or <= 0 && !StrongerAdaptiveQuantisation;
}

/// <summary>
/// Maps the portable tuning intent onto the vocabulary of the exact encoder chosen at dispatch,
/// following <see cref="EncoderPresetPolicy"/>: the library stores what the operator wants, and
/// each encoder receives only arguments it actually supports.
///
/// This matters more than it looks. In Auto mode a library cannot know whether its next job lands
/// on libx265 or NVENC, so a setting that named one encoder's flags directly would silently do
/// nothing half the time. Where a family has no equivalent the answer is no arguments — never an
/// approximation, because a knob that quietly did something else would leave the operator believing
/// the encode honoured a request it did not.
/// </summary>
public static class EncoderTuningPolicy
{
    /// <summary>
    /// The extra FFmpeg arguments for this encoder, in a stable order. Empty when nothing was
    /// asked for, or when nothing asked for can be expressed by this encoder.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? encoder, EncoderTuning tuning)
    {
        if (tuning.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var name = encoder?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
        {
            return Array.Empty<string>();
        }

        var isX26x = name is "libx264" or "libx265";
        var isNvenc = name.EndsWith("_nvenc", StringComparison.Ordinal);
        var isQsv = name.EndsWith("_qsv", StringComparison.Ordinal);
        var isVaapi = name.EndsWith("_vaapi", StringComparison.Ordinal);
        var isSvtAv1 = name == "libsvtav1";

        // A family this policy has not been taught cannot be assumed to share anyone else's
        // spelling, so it receives nothing at all.
        if (!isX26x && !isNvenc && !isQsv && !isVaapi && !isSvtAv1)
        {
            return Array.Empty<string>();
        }

        var args = new List<string>();

        // Content tuning exists only on x264/x265. NVENC's own -tune selects a latency profile
        // (hq/ll/ull), which is a different question rather than a coarser answer, and passing a
        // content name there fails the encode outright.
        if (isX26x && tuning.Tune != ContentTune.None)
        {
            args.Add("-tune");
            args.Add(tuning.Tune == ContentTune.Animation ? "animation" : "grain");
        }

        // Capped constant quality, and only where that is a real documented mode: x264/x265 pair
        // -crf with -maxrate/-bufsize, and NVENC is already on VBR with no target bitrate, so a cap
        // is exactly the ceiling it is missing.
        //
        // Not QSV or VAAPI. Those run constant-quantiser modes here (-global_quality and
        // -rc_mode CQP), where a rate cap is at best ignored and at worst quietly moves the encoder
        // onto a different rate-control mode than the one the quality setting chose. Nor SVT-AV1,
        // whose capped-CRF control is its own parameter rather than -maxrate. An ignored argument
        // is the failure this policy exists to prevent, so those families are left alone and the
        // library form says so.
        if ((isX26x || isNvenc) && tuning.MaxBitrateKbps is { } cap && cap > 0)
        {
            args.Add("-maxrate");
            args.Add($"{cap}k");
            args.Add("-bufsize");
            args.Add($"{cap * 2}k");
        }

        // The same intent in two vocabularies. x264/x265 take an aq-mode through their params
        // string; NVENC has two independent switches. QSV, VAAPI and SVT-AV1 expose no equivalent
        // Optimisarr can set safely, so they are left on their own defaults.
        if (tuning.StrongerAdaptiveQuantisation)
        {
            if (isX26x)
            {
                args.Add(name == "libx265" ? "-x265-params" : "-x264-params");
                args.Add("aq-mode=3");
            }
            else if (isNvenc)
            {
                args.AddRange(["-spatial-aq", "1", "-temporal-aq", "1"]);
            }
        }

        return args;
    }
}
