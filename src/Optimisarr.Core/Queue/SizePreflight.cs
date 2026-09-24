using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>
/// What the source spent on the adaptive sample windows, reduced to the facts a size forecast needs.
///
/// <para><see cref="SourceVideoBytesPerWindow"/> is the source's primary picture over exactly the
/// frames each sample encodes, so a sample's bytes divided by it is a same-scene ratio.
/// <see cref="VideoShare"/> and <see cref="CarriedShare"/> split the sampled source bytes into the
/// picture being re-encoded and the tracks the output copies unchanged, which lets the ratio be
/// applied to the part of the file it describes rather than to audio it never touched.</para>
/// </summary>
public sealed record SizeForecastBasis(
    long SourceBytes,
    IReadOnlyList<long> SourceVideoBytesPerWindow,
    double VideoShare,
    double CarriedShare,
    long ReencodedAudioBytes)
{
    /// <summary>
    /// Null when the windows carry no picture bytes to compare with. Tracks the output drops count
    /// towards neither share; re-encoded audio is estimated from its target bitrate when one is
    /// set and otherwise left out, which errs towards a smaller forecast and so towards not holding.
    /// </summary>
    public static SizeForecastBasis? From(
        long sourceBytes,
        IReadOnlyList<IReadOnlyList<SampledStreamBytes>> windows,
        TranscodeSpec spec,
        double sourceDurationSeconds)
    {
        if (sourceBytes <= 0 || windows.Count == 0)
        {
            return null;
        }

        var perWindow = windows
            .Select(window => window.Where(stream => stream.IsPrimaryVideo).Sum(stream => stream.Bytes))
            .ToList();
        var sampled = windows.Sum(window => window.Sum(stream => stream.Bytes));
        if (perWindow.Any(bytes => bytes <= 0) || sampled <= 0)
        {
            return null;
        }

        var removedAudio = spec.RemoveAudioStreamIndexes ?? [];
        var removedSubtitles = spec.RemoveSubtitleStreamIndexes ?? [];
        bool Carried(SampledStreamBytes stream) => stream.CodecType switch
        {
            _ when stream.IsPrimaryVideo => false,
            "audio" => spec.AudioEncoder is null && !removedAudio.Contains(stream.TypeIndex),
            "subtitle" => !removedSubtitles.Contains(stream.TypeIndex),
            _ => true
        };
        var carried = windows.Sum(window => window.Where(Carried).Sum(stream => stream.Bytes));

        long reencodedAudio = 0;
        if (spec.AudioEncoder is not null && spec.AudioBitrateKbps is { } kbps and > 0
            && double.IsFinite(sourceDurationSeconds) && sourceDurationSeconds > 0)
        {
            var keptTracks = windows
                .SelectMany(window => window)
                .Where(stream => stream.CodecType == "audio" && !removedAudio.Contains(stream.TypeIndex))
                .Select(stream => stream.TypeIndex)
                .Distinct()
                .Count();
            reencodedAudio = (long)(keptTracks * kbps * 125.0 * sourceDurationSeconds);
        }

        return new SizeForecastBasis(
            sourceBytes,
            perWindow,
            (double)perWindow.Sum() / sampled,
            (double)carried / sampled,
            reencodedAudio);
    }
}

/// <summary>
/// A prediction of the finished file's size, made from the sample encodes the adaptive quality
/// search has already paid for. The samples are compared with the source's own bytes over the same
/// scenes, so what is projected is how much this encoder changes this title's picture, not how the
/// sampled minutes happen to compare with the film's average.
///
/// <para>It is a forecast, not a proof: scenes outside the windows can behave differently. A
/// predicted miss pauses the job for review and nothing more. Only the final size gate decides
/// whether an output may replace the original.</para>
/// </summary>
public static class SizePreflight
{
    /// <summary>
    /// Decides, after each measurement of the search, whether to stop and ask the operator.
    ///
    /// <para>Once the search has chosen a quality, the question is whether that quality's samples
    /// fit. Before then, a candidate that <em>missed</em> the VMAF target and still does not fit
    /// settles it early: every quality that clears the target is a higher-quality setting, which
    /// encoders spend more bytes on, so measuring more samples would only buy a larger file.</para>
    /// </summary>
    public static SizePreflightAssessment Review(
        SizeForecastBasis? basis,
        IReadOnlyList<AdaptiveQualityProbe> probes,
        AdaptiveQualityProbe? latest,
        AdaptiveQualityDecision decision,
        long? maximumCandidateBytes,
        bool bypass)
    {
        if (decision.Complete)
        {
            var selected = probes.LastOrDefault(probe => probe.Quality == decision.SelectedQuality);
            return Assess(basis, selected, maximumCandidateBytes, bypass);
        }

        return latest is { MeetsTarget: false }
            && Assess(basis, latest, maximumCandidateBytes, bypass) is { ShouldHold: true } early
                ? early
                : SizePreflightAssessment.Inconclusive;
    }

    public static SizePreflightAssessment Assess(
        SizeForecastBasis? basis,
        AdaptiveQualityProbe? probe,
        long? maximumCandidateBytes,
        bool bypass)
    {
        if (bypass || basis is null || probe is null || maximumCandidateBytes is not { } maximum
            || probe.EncodedBytes <= 0 || basis.SourceBytes <= 0)
        {
            return SizePreflightAssessment.Inconclusive;
        }

        var sourceVideo = basis.SourceVideoBytesPerWindow.Sum();
        if (sourceVideo <= 0)
        {
            return SizePreflightAssessment.Inconclusive;
        }

        var ratio = (double)probe.EncodedBytes / sourceVideo;
        var projected = basis.SourceBytes * (ratio * basis.VideoShare + basis.CarriedShare)
            + basis.ReencodedAudioBytes;
        if (!double.IsFinite(projected) || projected <= 0)
        {
            return SizePreflightAssessment.Inconclusive;
        }

        var windowRatios = probe.WindowEncodedBytes is { } windows
            && windows.Count == basis.SourceVideoBytesPerWindow.Count
                ? windows.Select((bytes, index) => (double)bytes / basis.SourceVideoBytesPerWindow[index]).ToList()
                : null;
        var projectedBytes = projected >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(projected);
        var percentChange = (projected / basis.SourceBytes - 1) * 100;
        var shouldHold = projected > maximum;
        return new SizePreflightAssessment(
            shouldHold,
            projectedBytes,
            percentChange,
            ratio,
            windowRatios,
            shouldHold ? Explain(probe, ratio, windowRatios, projected, maximum, basis.SourceBytes) : null);
    }

    private static string Explain(
        AdaptiveQualityProbe probe,
        double ratio,
        IReadOnlyList<double>? windowRatios,
        double projected,
        long maximum,
        long sourceBytes)
    {
        static string Percent(double value) =>
            (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

        var scenes = windowRatios is { Count: > 1 }
            ? $" ({string.Join(", ", windowRatios.Select(Percent))} across the {windowRatios.Count} samples)"
            : "";
        var allowed = maximum >= sourceBytes - 1
            ? "an output smaller than the source"
            : $"at most {Percent((double)maximum / sourceBytes)} of the source";
        var projection = $"That projects the finished file at about {Percent(projected / sourceBytes)} of the source, "
            + $"where the library allows {allowed}.";
        var measured = probe.MeetsTarget
            ? $"At quality {probe.Quality} the samples cleared the VMAF target, but their video came out at "
              + $"{Percent(ratio)} of the source's own video over the same scenes{scenes}. {projection}"
            : $"At quality {probe.Quality} the samples missed the VMAF target and their video still came out at "
              + $"{Percent(ratio)} of the source's own video over the same scenes{scenes}. {projection} "
              + "A quality that clears the target would normally be larger still, so the search stopped here.";
        return measured
            + " The full encode is held until you choose to run it. This is an estimate from sampled scenes; "
            + "the original has not changed, and the final size and quality checks still apply.";
    }
}

public sealed record SizePreflightAssessment(
    bool ShouldHold,
    long? ProjectedBytes,
    double? ProjectedPercentChange,
    double? VideoRatio,
    IReadOnlyList<double>? WindowRatios,
    string? Reason)
{
    public static SizePreflightAssessment Inconclusive { get; } = new(false, null, null, null, null, null);
}
