namespace Optimisarr.Core.Calibration;

public sealed record CalibrationSample(int Index, int StartSeconds, int DurationSeconds);

public sealed record BlindCalibrationPlan(
    IReadOnlyList<int> RequestedQualities,
    IReadOnlyList<CalibrationSample> Samples);

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

        var lastStart = Math.Max(0, (int)Math.Floor(durationSeconds) - SampleSeconds);
        int Centred(double fraction) => Math.Clamp(
            (int)Math.Floor(durationSeconds * fraction) - (SampleSeconds / 2),
            0,
            lastStart);

        var starts = new[] { Centred(0.1), Centred(0.5), Centred(0.9) }
            .Distinct()
            .ToList();
        var samples = starts
            .Select((start, index) => new CalibrationSample(index, start, SampleSeconds))
            .ToList();

        return new BlindCalibrationPlan(qualities, samples);
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
