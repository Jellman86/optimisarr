namespace Optimisarr.Core.Calibration;

using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

public sealed record CalibrationSample(int Index, int StartSeconds, int DurationSeconds);

public sealed record CalibrationSetting(
    string Key,
    int Quality,
    RuleProfile? VideoProfile = null);

public sealed record BlindCalibrationPlan(
    IReadOnlyList<CalibrationSetting> Settings,
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

    public static BlindCalibrationPlan VideoPlan(double durationSeconds, int? sourceBitDepth)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < SampleSeconds * 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "Blind calibration needs at least 48 seconds of SDR video.");
        }

        // These are the four concrete stops on the library's compatibility-to-efficiency slider.
        // Keep the recommendation order most space-efficient first; the UI randomises the labels.
        var profiles = new[]
        {
            RuleProfile.ExperimentalAv1,
            RuleProfile.ScottsSettings,
            RuleProfile.ConservativeHevc,
            RuleProfile.CompatibilityH264
        };
        var settings = profiles
            .Where(profile => profile != RuleProfile.CompatibilityH264 || sourceBitDepth is <= 8)
            .Select(profile =>
            {
                var rules = RuleProfileDefaults.For(profile);
                return new CalibrationSetting(profile.ToString(), rules.DefaultCrf!.Value, profile);
            })
            .ToList();

        return new BlindCalibrationPlan(settings, RepresentativeSamples(durationSeconds, SampleSeconds));
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
            ladder.Select(value => new CalibrationSetting(value.ToString(), value)).ToList(),
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
        [
            new CalibrationSetting("40", 40),
            new CalibrationSetting("55", 55),
            new CalibrationSetting("70", 70),
            new CalibrationSetting("82", 82),
            new CalibrationSetting("92", 92)
        ],
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

    public static CalibrationSetting? Recommend(
        BlindCalibrationPlan plan,
        IReadOnlyDictionary<string, CalibrationPreference> ratings)
    {
        foreach (var setting in plan.Settings)
        {
            if (ratings.TryGetValue(setting.Key, out var rating)
                && rating is CalibrationPreference.Indistinguishable or CalibrationPreference.Acceptable)
            {
                return setting;
            }
        }

        return null;
    }
}
