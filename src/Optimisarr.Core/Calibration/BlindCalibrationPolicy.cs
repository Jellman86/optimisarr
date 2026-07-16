namespace Optimisarr.Core.Calibration;

using Optimisarr.Core.Domain;

public sealed record CalibrationSample(int Index, int StartSeconds, int DurationSeconds);

public sealed record BlindCalibrationPlan(
    IReadOnlyList<int> RequestedQualities,
    IReadOnlyList<CalibrationSample> Samples);

public sealed record AudioLevelMatch(double OriginalGainDb, double CandidateGainDb);

public enum CalibrationPreference
{
    Indistinguishable,
    Acceptable,
    VisiblyWorse
}

/// <summary>Pure planning and decision rules for a short, personal blind quality calibration.</summary>
public static class BlindCalibrationPolicy
{
    public const int SampleSeconds = 12;
    public const int AudioSampleSeconds = 15;

    public static bool CanCalibrateVideo(
        bool isHdr,
        bool isDolbyVision,
        HdrHandling hdrHandling,
        bool hdrPlaybackConfirmed)
    {
        if (isDolbyVision)
        {
            return false;
        }

        return !isHdr || hdrHandling == HdrHandling.Preserve && hdrPlaybackConfirmed;
    }

    public static BlindCalibrationPlan Plan(double durationSeconds, int currentQuality)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < SampleSeconds * 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "Blind calibration needs at least 48 seconds of SDR video.");
        }

        var boundedQuality = Math.Clamp(currentQuality, 14, 40);
        var qualities = new HashSet<int>();
        foreach (var offset in new[] { 6, 3, 0, -3, -6, 9, -9, 12, -12, 15, -15 })
        {
            qualities.Add(Math.Clamp(boundedQuality + offset, 14, 40));
            if (qualities.Count == 5) break;
        }
        var orderedQualities = qualities.OrderDescending().ToList();

        return new BlindCalibrationPlan(orderedQualities, RepresentativeSamples(durationSeconds, SampleSeconds));
    }

    public static BlindCalibrationPlan AudioPlan(
        double durationSeconds,
        string targetCodec)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < AudioSampleSeconds * 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "Blind audio calibration needs at least 60 seconds of audio.");
        }

        var ladder = targetCodec.ToLowerInvariant() switch
        {
            "opus" => new[] { 64, 80, 96, 128, 160 },
            "aac" => new[] { 96, 128, 160, 192, 256 },
            "mp3" => new[] { 128, 160, 192, 256, 320 },
            _ => throw new ArgumentOutOfRangeException(
                nameof(targetCodec),
                "Blind audio calibration supports Opus, AAC, and MP3 bitrate targets.")
        };

        return new BlindCalibrationPlan(
            ladder,
            RepresentativeSamples(durationSeconds, AudioSampleSeconds));
    }

    public static AudioLevelMatch MatchAudioLevels(double originalLufs, double candidateLufs)
    {
        var gains = MatchAudioGroupLevels([originalLufs, candidateLufs]);
        return new AudioLevelMatch(gains[0], gains[1]);
    }

    public static IReadOnlyList<double> MatchAudioGroupLevels(IReadOnlyList<double> measuredLufs)
    {
        if (measuredLufs.Count == 0 || measuredLufs.Any(level => !double.IsFinite(level)))
        {
            throw new ArgumentOutOfRangeException(nameof(measuredLufs), "Measured loudness must be finite.");
        }

        var target = measuredLufs.Min();
        return measuredLufs.Select(level => target - level).ToList();
    }

    public static BlindCalibrationPlan ImagePlan() => new(
        [40, 55, 70, 82, 92],
        [new CalibrationSample(0, 0, 0)]);

    private static IReadOnlyList<CalibrationSample> RepresentativeSamples(
        double durationSeconds,
        int sampleSeconds)
    {
        var lastStart = Math.Max(0, (int)Math.Floor(durationSeconds) - sampleSeconds);
        int Centred(double fraction) => Math.Clamp(
            (int)Math.Floor(durationSeconds * fraction) - (sampleSeconds / 2),
            0,
            lastStart);

        var starts = new[] { Centred(0.1), Centred(0.5), Centred(0.9) }
            .Distinct()
            .ToList();
        var samples = starts
            .Select((start, index) => new CalibrationSample(index, start, sampleSeconds))
            .ToList();
        return samples;
    }

    public static int? Recommend(
        BlindCalibrationPlan plan,
        IReadOnlyDictionary<int, CalibrationPreference> ratings)
    {
        foreach (var quality in plan.RequestedQualities)
        {
            if (ratings.TryGetValue(quality, out var rating)
                && rating is CalibrationPreference.Indistinguishable or CalibrationPreference.Acceptable)
            {
                return quality;
            }
        }

        return null;
    }
}
