namespace Optimisarr.Core.Calibration;

using Optimisarr.Core.Domain;

public sealed record CalibrationSample(int Index, int StartSeconds, int DurationSeconds);

public sealed record BlindCalibrationPlan(
    IReadOnlyList<int> RequestedQualities,
    IReadOnlyList<CalibrationSample> Samples);

public sealed record AudioLevelMatch(double OriginalGainDb, double CandidateGainDb);

public enum CalibrationJudgement
{
    Continue,
    Distinguishable,
    NoReliableDifference
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
        var qualities = new[]
            {
                boundedQuality + 6,
                boundedQuality + 3,
                boundedQuality,
                boundedQuality - 3,
                boundedQuality - 6
            }
            .Select(quality => Math.Clamp(quality, 14, 40))
            .Distinct()
            .OrderDescending()
            .ToList();

        return new BlindCalibrationPlan(qualities, RepresentativeSamples(durationSeconds, SampleSeconds));
    }

    public static BlindCalibrationPlan AudioPlan(
        double durationSeconds,
        string targetCodec,
        int currentBitrateKbps)
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
        if (!double.IsFinite(originalLufs) || !double.IsFinite(candidateLufs))
        {
            throw new ArgumentOutOfRangeException(nameof(originalLufs), "Measured loudness must be finite.");
        }

        var target = Math.Min(originalLufs, candidateLufs);
        return new AudioLevelMatch(target - originalLufs, target - candidateLufs);
    }

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

    public static CalibrationJudgement JudgeScreening(int correctAnswers, int totalAnswers)
    {
        if (totalAnswers < 3)
        {
            return CalibrationJudgement.Continue;
        }

        return correctAnswers >= 2
            ? CalibrationJudgement.Distinguishable
            : CalibrationJudgement.NoReliableDifference;
    }

    public static CalibrationJudgement JudgeConfirmation(int correctAnswers, int totalAnswers)
    {
        if (totalAnswers < 10)
        {
            return CalibrationJudgement.Continue;
        }

        if (totalAnswers < 20)
        {
            // Under random guessing, 9/10 or better has a one-sided binomial probability of
            // about 1.1%. Seven or eight correct is deliberately treated as inconclusive rather
            // than either proof of a visible difference or proof of equivalence.
            if (correctAnswers >= 9)
            {
                return CalibrationJudgement.Distinguishable;
            }

            return correctAnswers <= 6
                ? CalibrationJudgement.NoReliableDifference
                : CalibrationJudgement.Continue;
        }

        // The extended threshold has the same conservative intent: 15/20 or better is about
        // 2.1% under random guessing. Falling short means only "no reliable difference found".
        return correctAnswers >= 15
            ? CalibrationJudgement.Distinguishable
            : CalibrationJudgement.NoReliableDifference;
    }
}
