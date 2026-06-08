using System.Globalization;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure verification: turns a gathered <see cref="VerificationInput"/> into a
/// <see cref="VerificationReport"/>. No I/O, no FFmpeg — every check is a
/// deterministic comparison so it can be unit tested without media on disk.
/// </summary>
public static class VerificationEvaluator
{
    public static VerificationReport Evaluate(VerificationInput input, VerificationPolicy policy)
    {
        var checks = new List<VerificationCheck>
        {
            DecodeHealth(input),
            OutputReadable(input),
            VideoStreamPresent(input),
            DurationWithinTolerance(input, policy),
            AudioRetained(input, policy),
            SubtitlesRetained(input, policy),
            SizeReduced(input, policy)
        };

        return new VerificationReport(checks);
    }

    private static VerificationCheck DecodeHealth(VerificationInput input) =>
        input.DecodeSucceeded
            ? Pass("Decode health", "Output decoded fully with no FFmpeg errors.")
            : Fail("Decode health", $"FFmpeg reported decode errors: {Describe(input.DecodeError)}");

    private static VerificationCheck OutputReadable(VerificationInput input) =>
        input.OutputProbeSucceeded
            ? Pass("Output readable", "ffprobe read the output container and streams.")
            : Fail("Output readable", $"ffprobe could not read the output: {Describe(input.OutputProbeError)}");

    private static VerificationCheck VideoStreamPresent(VerificationInput input) =>
        !string.IsNullOrWhiteSpace(input.OutputVideoCodec)
            ? Pass("Video stream", $"Output has a video stream ({input.OutputVideoCodec}).")
            : Fail("Video stream", "Output has no video stream.");

    private static VerificationCheck DurationWithinTolerance(VerificationInput input, VerificationPolicy policy)
    {
        if (input.OriginalDurationSeconds is not { } original || original <= 0)
        {
            return Fail("Duration", "Original duration is unknown, so the output cannot be compared.");
        }

        if (input.OutputDurationSeconds is not { } output || output <= 0)
        {
            return Fail("Duration", "Output duration is unknown.");
        }

        var driftSeconds = Math.Abs(original - output);
        var driftPercent = driftSeconds / original * 100.0;
        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Original {0:0.###}s, output {1:0.###}s ({2:0.##}% drift, tolerance {3:0.##}%).",
            original, output, driftPercent, policy.DurationTolerancePercent);

        return driftPercent <= policy.DurationTolerancePercent
            ? Pass("Duration", detail)
            : Fail("Duration", detail);
    }

    private static VerificationCheck AudioRetained(VerificationInput input, VerificationPolicy policy)
    {
        var detail = $"Original {input.OriginalAudioTrackCount} audio track(s), output {input.OutputAudioTrackCount}.";
        if (!policy.RequireAudioRetained)
        {
            return Pass("Audio tracks", $"{detail} Retention not required by policy.");
        }

        return input.OutputAudioTrackCount >= input.OriginalAudioTrackCount
            ? Pass("Audio tracks", detail)
            : Fail("Audio tracks", $"{detail} Audio tracks were lost.");
    }

    private static VerificationCheck SubtitlesRetained(VerificationInput input, VerificationPolicy policy)
    {
        var detail = $"Original {input.OriginalSubtitleTrackCount} subtitle track(s), output {input.OutputSubtitleTrackCount}.";
        if (!policy.RequireSubtitlesRetained)
        {
            return Pass("Subtitle tracks", $"{detail} Retention not required by policy.");
        }

        return input.OutputSubtitleTrackCount >= input.OriginalSubtitleTrackCount
            ? Pass("Subtitle tracks", detail)
            : Fail("Subtitle tracks", $"{detail} Subtitle tracks were lost.");
    }

    private static VerificationCheck SizeReduced(VerificationInput input, VerificationPolicy policy)
    {
        if (input.OutputSizeBytes <= 0)
        {
            return Fail("Size saving", "Output file is empty or missing.");
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Original {0:n0} bytes, output {1:n0} bytes ({2:+0.#;-0.#}% change).",
            input.OriginalSizeBytes,
            input.OutputSizeBytes,
            PercentChange(input.OriginalSizeBytes, input.OutputSizeBytes));

        if (!policy.RequireSizeReduction)
        {
            return Pass("Size saving", $"{detail} Reduction not required by policy.");
        }

        return input.OutputSizeBytes < input.OriginalSizeBytes
            ? Pass("Size saving", detail)
            : Fail("Size saving", $"{detail} Output is not smaller than the original.");
    }

    private static double PercentChange(long original, long output) =>
        original > 0 ? (output - original) / (double)original * 100.0 : 0;

    private static string Describe(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "no detail available" : error;

    private static VerificationCheck Pass(string name, string detail) =>
        new(name, CheckOutcome.Passed, detail);

    private static VerificationCheck Fail(string name, string detail) =>
        new(name, CheckOutcome.Failed, detail);
}
